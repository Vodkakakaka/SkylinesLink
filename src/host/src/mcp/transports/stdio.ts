import { Transform, Writable, type Readable, type TransformCallback } from 'node:stream';
import { finished } from 'node:stream/promises';
import { StdioServerTransport, serveStdio, type StdioServerHandle } from '@modelcontextprotocol/server/stdio';
import { deserializeMessage } from '@modelcontextprotocol/server';
import { createMcpServer, WorkBudget, type HostOptions } from '../server.ts';
import { BoundedTransport } from './bounded-transport.ts';
import { checkRawJson } from '../../shared/json-budget.ts';
import { probeLimits, TransportBudgetError, validateLimits, type TransportLimits } from '../../shared/limits.ts';
import { ControlClassifier, MessageRate } from '../../shared/control-admission.ts';

export class JsonLineBudget extends Transform {
  readonly limits: TransportLimits;
  #buffer: Buffer;
  #length = 0;
  #deadline: NodeJS.Timeout | undefined;
  #rate: MessageRate;

  constructor(limits: TransportLimits, readonly classifier = new ControlClassifier()) {
    validateLimits(limits);
    super({ readableHighWaterMark: limits.messageBytes, writableHighWaterMark: limits.messageBytes });
    this.limits = limits;
    this.#rate = new MessageRate(limits, classifier.binding !== undefined);
    this.#buffer = Buffer.allocUnsafe(limits.messageBytes);
  }

  override _transform(chunk: Buffer, _encoding: BufferEncoding, callback: TransformCallback): void {
    try {
      if (chunk.length > this.limits.messageBytes * 2) throw new TransportBudgetError('单次输入块超过上限。');
      for (let offset = 0; offset < chunk.length && !this.destroyed; offset++) {
        if (!this.#deadline) this.#deadline = setTimeout(() => this.destroy(new TransportBudgetError('接收完整消息超时。')), this.limits.receiveDeadlineMs);
        const byte = chunk[offset]!;
        if (byte === 10) {
          const frame = this.#buffer.subarray(0, this.#length);
          const text = checkRawJson(frame, this.limits);
          // Official public decoder, bounded before parsing. The original raw
          // line length (including whitespace/escapes), not a re-stringify.
          let control = false;
          try { control = this.classifier.raw(deserializeMessage(text), frame.length) !== undefined; }
          catch (error) {
            if (error instanceof TransportBudgetError) throw error;
            // Ordinary malformed RPC still reaches the official SDK's existing
            // rejection path; it cannot acquire protected Stop capacity.
          }
          this.#rate.accept(control);
          // 固定scratch不跨消息借出，SDK持有的是已核预算的独立帧。
          this.push(Buffer.concat([frame, Buffer.from('\n')]));
          this.#length = 0;
          clearTimeout(this.#deadline);
          this.#deadline = undefined;
        } else {
          if (this.#length === this.#buffer.length) throw new TransportBudgetError('输入消息超过上限。');
          this.#buffer[this.#length++] = byte;
        }
      }
      callback();
    } catch (error) { callback(error instanceof Error ? error : new TransportBudgetError()); }
  }

  override _flush(callback: TransformCallback): void {
    callback(this.#length === 0 ? undefined : new TransportBudgetError('输入在消息完成前关闭。'));
  }

  override _destroy(error: Error | null, callback: (error: Error | null) => void): void {
    clearTimeout(this.#deadline);
    this.#buffer.fill(0);
    this.#length = 0;
    callback(error);
  }
}

/** 使用标准流公开回调判断实际发送结束；SDK write() 为 true 不等于完成。 */
class OwnedOutput extends Writable {
  readonly destination: Writable;
  #pending = new Set<Promise<void>>();
  #finishers = new Set<() => void>();

  constructor(destination: Writable) {
    super({ highWaterMark: 1 });
    this.destination = destination;
    this.on('error', () => {});
    destination.on('error', () => this.destroy(new Error('输出连接关闭。')));
    destination.once('close', () => { for (const finish of this.#finishers) finish(); });
  }

  override _write(chunk: Buffer, encoding: BufferEncoding, callback: (error?: Error | null) => void): void {
    let resolve!: () => void;
    const done = new Promise<void>((r) => { resolve = r; });
    this.#pending.add(done);
    let complete = false;
    const finish = (error?: Error | null) => {
      if (complete) return;
      complete = true;
      this.#finishers.delete(closed);
      this.#pending.delete(done);
      resolve();
      callback(error ? new Error('输出连接关闭。') : undefined);
    };
    const closed = () => finish(new Error('输出连接关闭。'));
    this.#finishers.add(closed);
    this.destination.write(chunk, encoding, finish);
  }

  async settled(): Promise<void> { await Promise.all(this.#pending); }
}

export interface StdioHost {
  handle: StdioServerHandle;
  transport: BoundedTransport;
  closed: Promise<void>;
  close(): Promise<void>;
}

/** Caller transfers both streams; output must support asynchronous writes and actual close. */
export function startStdioHost(options: HostOptions, input: Readable, output: Writable): StdioHost {
  const limits = options.limits ?? probeLimits;
  const classifier = new ControlClassifier(options.capabilities);
  const lines = new JsonLineBudget(limits, classifier);
  const writer = new OwnedOutput(output);
  const workBudget = options.workBudget ?? new WorkBudget(limits.activeRequests);
  let ownerClose: Promise<void> | undefined;
  const owner = {
    settled: () => writer.settled(),
    close(): Promise<void> {
      ownerClose ??= (async () => {
        input.unpipe(lines);
        lines.destroy();
        input.destroy();
        output.destroy();
        writer.destroy(new Error('MCP 连接关闭。'));
        await Promise.allSettled([finished(input), finished(output), finished(lines), finished(writer)]);
        await writer.settled();
        await options.game.close();
      })();
      return ownerClose;
    },
  };
  const inner = new StdioServerTransport(lines, writer, { maxBufferSize: limits.messageBytes + 1 });
  const transport = new BoundedTransport(inner, limits, owner, classifier);
  const handle = serveStdio(() => createMcpServer({ ...options, workBudget, lifecycle: transport.lifecycle }), { transport, maxSubscriptions: 1, onerror: () => {} });
  let closed: Promise<void> | undefined;
  let resolved!: () => void, rejected!: (error: unknown) => void;
  const completion = new Promise<void>((resolve, reject) => { resolved = resolve; rejected = reject; });
  // Library callers may only await close(); the CLI also observes failures after natural EOF.
  void completion.catch(() => {});
  const close = (): Promise<void> => {
    if (!closed) {
      closed = (async () => {
        const cleanup = await Promise.allSettled([handle.close(), owner.close(), workBudget.waitIdle()]);
        if (cleanup.some(result => result.status === 'rejected')) throw new Error('stdio 连接清理未全部成功。');
      })();
      void closed.then(resolved, rejected);
    }
    return closed;
  };
  const closeAfterEvent = () => { void close().catch(() => {}); };
  lines.on('error', closeAfterEvent);
  input.on('error', closeAfterEvent);
  input.on('end', closeAfterEvent);
  input.on('close', closeAfterEvent);
  output.on('error', closeAfterEvent);
  output.on('close', closeAfterEvent);
  input.pipe(lines);
  return { handle, transport, closed: completion, close };
}
