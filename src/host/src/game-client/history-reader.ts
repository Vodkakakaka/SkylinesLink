import { spawn, type ChildProcess } from 'node:child_process';
import { isAbsolute } from 'node:path';
import { parseContractJson, requireContract, schemaIds, type NativeHistoryCapture, type NativeHistoryRequest,
  type NativeHistoryCaptureV2, type NativeHistoryRequestV2, type NativeHistoryCaptureV3, type NativeHistoryRequestV3,
  nativeHistoryLimits } from '@skylineslink/contracts';
import { checkRawJson } from '../shared/json-budget.ts';
import { probeLimits } from '../shared/limits.ts';
import { GameClientError } from './game-client.ts';
import { requireCaptureV2, requireCaptureV3 } from './mixed-history-result.ts';

export interface HistoryReader {
  read(request: NativeHistoryRequest, signal: AbortSignal, deadline: number): Promise<NativeHistoryCapture>;
  close(): Promise<void>;
}

/** Explicit opt-in port; existing CP1-only readers do not fabricate V2 support. */
export interface HistoryReaderV2 extends HistoryReader {
  readV2(request: NativeHistoryRequestV2, signal: AbortSignal, deadline: number): Promise<NativeHistoryCaptureV2>;
}
export interface HistoryReaderV3 extends HistoryReaderV2 {
  readV3(request: NativeHistoryRequestV3, signal: AbortSignal, deadline: number): Promise<NativeHistoryCaptureV3>;
}

type HistorySpawner = (executable: string, args: string[], options: {
  windowsHide: true; shell: false; stdio: ['overlapped', 'overlapped', 'ignore'];
}) => ChildProcess;

const inputLimit = nativeHistoryLimits.MaximumRequestBytes;
const profiles = Object.freeze({
  1: Object.freeze({ request: schemaIds.NativeHistoryRequest, capture: schemaIds.NativeHistoryCapture, argument: '--read-history', outputBytes: nativeHistoryLimits.Version1MaximumResponseBytes }),
  2: Object.freeze({ request: schemaIds.NativeHistoryRequestV2, capture: schemaIds.NativeHistoryCaptureV2, argument: '--read-history-v2', outputBytes: nativeHistoryLimits.Version2MaximumResponseBytes }),
  3: Object.freeze({ request: schemaIds.NativeHistoryRequestV3, capture: schemaIds.NativeHistoryCaptureV3, argument: '--read-history-v3', outputBytes: nativeHistoryLimits.Version3MaximumResponseBytes }),
});
const errors: Readonly<Record<number, readonly [string, string]>> = Object.freeze({
  80: ['HISTORY_READER_REJECTED', '历史读取入口拒绝了请求或管道。'],
  81: ['CONNECTION_STORE_UNAVAILABLE', '历史读取所需连接存储不存在。'],
  82: ['HISTORY_AUTHORITY_BUSY', '当前环境权威正在使用连接租约。'],
  83: ['UNAUTHORIZED', '当前连接没有历史读取权限。'],
  84: ['SUBJECT_CHANGED', '连接授权主体已改变，请重新建立客户端会话。'],
  85: ['HISTORY_UNAVAILABLE', '当前没有可读取的历史检查点。'],
  86: ['HISTORY_INCOMPATIBLE', '历史读取依赖的授权或检查点格式不兼容。'],
  87: ['INVALID_HISTORY_SOURCE', '历史来源无法通过完整性或存储身份检查。'],
  88: ['HISTORY_IO_UNAVAILABLE', '历史读取未在期限内完成。'],
});

/** One owned short-lived child; the path is injected by the pinned Launcher composition. */
export class NativeHistoryReader implements HistoryReaderV3 {
  #closed = false;
  #active: { cancel(): void; done: Promise<void>; reservedPayloadBytes: number } | undefined;
  constructor(private readonly executable: string, private readonly start: HistorySpawner = spawn) {
    if (!isAbsolute(executable) || executable.length > 1024 || executable.includes('\0')) throw new Error('历史读取依赖无效。');
  }
  get usage() { return { readers: this.#active ? 1 : 0, reservedPayloadBytes: this.#active?.reservedPayloadBytes ?? 0 }; }

  async read(request: NativeHistoryRequest, signal: AbortSignal, deadline: number): Promise<NativeHistoryCapture> {
    return this.#capture(request, signal, deadline, 1);
  }

  async readV2(request: NativeHistoryRequestV2, signal: AbortSignal, deadline: number): Promise<NativeHistoryCaptureV2> {
    return this.#capture(request, signal, deadline, 2);
  }
  async readV3(request: NativeHistoryRequestV3, signal: AbortSignal, deadline: number): Promise<NativeHistoryCaptureV3> {
    return this.#capture(request, signal, deadline, 3);
  }

  #capture(request: NativeHistoryRequest, signal: AbortSignal, deadline: number, version: 1): Promise<NativeHistoryCapture>;
  #capture(request: NativeHistoryRequestV2, signal: AbortSignal, deadline: number, version: 2): Promise<NativeHistoryCaptureV2>;
  #capture(request: NativeHistoryRequestV3, signal: AbortSignal, deadline: number, version: 3): Promise<NativeHistoryCaptureV3>;
  async #capture(request: NativeHistoryRequest | NativeHistoryRequestV2 | NativeHistoryRequestV3, signal: AbortSignal, deadline: number, version: 1 | 2 | 3): Promise<NativeHistoryCapture | NativeHistoryCaptureV2 | NativeHistoryCaptureV3> {
    if (this.#closed || signal.aborted) throw new GameClientError('CANCELLED', '历史读取已取消。');
    if (this.#active) throw new GameClientError('BUSY', '此 Host 已有一个历史读取请求正在收尾。');
    if (!Number.isFinite(deadline) || deadline <= performance.now()) throw new GameClientError('HISTORY_TIMEOUT', '历史读取期限已到。');
    const profile = profiles[version], requestSchema = profile.request, captureSchema = profile.capture, outputLimit = profile.outputBytes;
    const limits = { ...probeLimits, messageBytes: outputLimit };
    try { requireContract(requestSchema, request); } catch { throw new GameClientError('INVALID_HISTORY_REQUEST', '历史读取请求未满足契约。'); }
    const json = JSON.stringify(request);
    if (Buffer.byteLength(json, 'utf8') > inputLimit) throw new GameClientError('BUDGET_EXCEEDED', '历史读取请求超过上限。');
    // Correlate with the exact request sent, rather than a caller-owned object
    // that can be mutated while this child is awaiting physical completion.
    const sent = version === 1 ? undefined : JSON.parse(json) as NativeHistoryRequestV2 | NativeHistoryRequestV3;
    const input = Buffer.from(json, 'utf8'), output = Buffer.alloc(outputLimit);
    let finish!: () => void;
    const done = new Promise<void>(resolve => { finish = resolve; });
    let child: ChildProcess | undefined, failure: GameClientError | undefined, ended = false, bytes = 0;
    let closed: Promise<{ code: number | null; signal: NodeJS.Signals | null }> | undefined;
    let inputCompleted = Promise.resolve();
    let streamsClosed = Promise.resolve();
    const stop = (error: GameClientError) => {
      failure ??= error;
      if (child && !ended) { try { child.kill(); } catch { /* Retain ownership until actual close, even when termination fails. */ } }
    };
    this.#active = { done, reservedPayloadBytes: inputLimit + outputLimit, cancel: () => stop(new GameClientError('CANCELLED', '历史读取已取消。')) };
    const abort = () => stop(new GameClientError('CANCELLED', '历史读取已取消。'));
    signal.addEventListener('abort', abort, { once: true });
    const timer = setTimeout(() => stop(new GameClientError('HISTORY_TIMEOUT', '历史读取期限已到。')), Math.max(0, deadline - performance.now()));
    try {
      child = this.start(this.executable, [profile.argument], { windowsHide: true, shell: false, stdio: ['overlapped', 'overlapped', 'ignore'] });
      closed = new Promise<{ code: number | null; signal: NodeJS.Signals | null }>(resolve => child!.once('close', (code, terminated) => {
        ended = true; resolve({ code, signal: terminated });
      }));
      child.on('error', () => stop(new GameClientError('HISTORY_READER_UNAVAILABLE', '历史读取进程未能完成。')));
      const incoming = child.stdout, outgoing = child.stdin;
      streamsClosed = Promise.all([incoming, outgoing].map(stream => !stream || stream.closed ? Promise.resolve()
        : new Promise<void>(resolve => { stream.once('close', resolve); }))).then(() => {});
      let stdoutEnded = false;
      incoming?.once('end', () => { stdoutEnded = true; });
      incoming?.on('error', () => stop(new GameClientError('INVALID_HISTORY_RESPONSE', '历史读取响应未完整结束。')));
      incoming?.on('data', (chunk: Buffer | string) => {
        if (failure) return;
        if (typeof chunk === 'string' || bytes + chunk.length > outputLimit) {
          incoming.pause(); stop(new GameClientError('BUDGET_EXCEEDED', '历史读取响应超过上限。')); return;
        }
        chunk.copy(output, bytes); bytes += chunk.length;
      });
      outgoing?.on('error', () => stop(new GameClientError('HISTORY_IO_UNAVAILABLE', '历史读取输入未能完成。')));
      if (!incoming || !outgoing) stop(new GameClientError('HISTORY_READER_UNAVAILABLE', '历史读取管道不可用。'));
      else inputCompleted = new Promise<void>(resolve => outgoing.end(input, (error?: Error | null) => {
        if (error) stop(new GameClientError('HISTORY_IO_UNAVAILABLE', '历史读取输入未能完成。'));
        resolve();
      }));
      if (signal.aborted) abort();
      const result = await closed;
      await inputCompleted;
      await streamsClosed;
      if (failure) throw failure;
      if (result.signal !== null) throw new GameClientError('HISTORY_READER_UNAVAILABLE', '历史读取进程未正常完成。');
      if (result.code !== 0) {
        const known = result.code === null ? undefined : errors[result.code];
        throw new GameClientError(known?.[0] ?? 'HISTORY_READER_UNAVAILABLE', known?.[1] ?? '历史读取进程返回了未知结果。');
      }
      if (!stdoutEnded || bytes === 0) throw new GameClientError('INVALID_HISTORY_RESPONSE', '历史读取响应未完整结束。');
      let value: unknown;
      try {
        value = parseContractJson(checkRawJson(output.subarray(0, bytes), limits));
        requireContract(captureSchema, value);
      } catch { throw new GameClientError('INVALID_HISTORY_RESPONSE', '历史读取响应未满足当前契约。'); }
      return version === 3 ? requireCaptureV3(value, sent as NativeHistoryRequestV3)
        : version === 2 ? requireCaptureV2(value, sent as NativeHistoryRequestV2) : value as NativeHistoryCapture;
    } catch (error) {
      if (child && !ended) { stop(new GameClientError('HISTORY_READER_UNAVAILABLE', '历史读取进程未能完成。')); await closed; }
      await inputCompleted.catch(() => {});
      await streamsClosed;
      if (error instanceof GameClientError) throw error;
      throw new GameClientError('HISTORY_READER_UNAVAILABLE', '历史读取进程未能完成。');
    } finally {
      clearTimeout(timer); signal.removeEventListener('abort', abort);
      input.fill(0); output.fill(0);
      this.#active = undefined; finish();
    }
  }

  async close(): Promise<void> {
    this.#closed = true;
    const active = this.#active;
    if (active) { active.cancel(); await active.done; }
  }
}
