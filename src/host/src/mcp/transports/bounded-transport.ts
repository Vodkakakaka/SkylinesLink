import type { Transport, JSONRPCMessage, TransportSendOptions, MessageExtraInfo } from '@modelcontextprotocol/server';
import { measureJson } from '../../shared/json-budget.ts';
import { TransportBudgetError, validateLimits, type TransportLimits } from '../../shared/limits.ts';
import { ControlClassifier, MessageRate, RequestLifecycle, controlTransportProfile, type OutputLane } from '../../shared/control-admission.ts';

export interface TransportOwner {
  /** 必须真正关闭自有流并等待 close；不能只解除 SDK 的事件订阅。 */
  close(): Promise<void>;
  /** 对应流实际 write callback / finish 已结束。 */
  settled(): Promise<void>;
}
interface Output {
  json: string;
  options?: TransportSendOptions;
  lane: OutputLane;
  reservation: number;
  timer: NodeJS.Timeout;
  resolve(): void;
  reject(error: unknown): void;
  released: boolean;
}

/** One owned queue/pump before the SDK; no SDK private queues or maps. */
export class BoundedTransport implements Transport {
  readonly hasPerRequestStream?: boolean;
  onclose?: (() => void) | undefined;
  onerror?: ((error: Error) => void) | undefined;
  onmessage?: (<T extends JSONRPCMessage>(message: T, extra?: MessageExtraInfo) => void) | undefined;
  readonly lifecycle: RequestLifecycle;
  readonly classifier: ControlClassifier;
  #rate: MessageRate;
  #closing: Promise<void> | undefined;
  #closed = false;
  #sendBytes = 0;
  #ordinaryBytes = 0;
  #ordinary = 0;
  #control = 0;
  #setup = 0;
  #normalQueue: Output[] = [];
  #controlQueue: Output[] = [];
  #pumping = false;
  #pumpTask: Promise<void> | undefined;
  #physicalClosed!: () => void;
  #closedEvent = new Promise<void>(resolve => { this.#physicalClosed = resolve; });

  constructor(readonly inner: Transport, readonly limits: TransportLimits, readonly owner: TransportOwner,
    classifier = new ControlClassifier()) {
    validateLimits(limits);
    this.classifier = classifier;
    this.#rate = new MessageRate(limits, classifier.binding !== undefined);
    this.lifecycle = new RequestLifecycle(limits, () => this.#closeAfterEvent());
    if (inner.hasPerRequestStream !== undefined) this.hasPerRequestStream = inner.hasPerRequestStream;
  }
  get sessionId(): string | undefined { return this.inner.sessionId; }
  setProtocolVersion(version: string): void { this.inner.setProtocolVersion?.(version); }
  setSupportedProtocolVersions(versions: string[]): void { this.inner.setSupportedProtocolVersions?.(versions); }
  get usage() { return { activeRequests: this.lifecycle.active, sendBytes: this.#sendBytes,
    sendCount: this.#ordinary + this.#control + this.#setup, closed: this.#closed }; }

  async start(): Promise<void> {
    this.inner.onclose = () => this.#closeAfterEvent();
    this.inner.onerror = () => { this.onerror?.(new Error('MCP 输入或输出格式错误。')); this.#closeAfterEvent(); };
    this.inner.onmessage = (message, extra) => {
      if (this.#closed || this.#closing) return;
      try {
        const key = this.classifier.message(message);
        this.#rate.accept(key !== undefined);
        if ('method' in message && 'id' in message) this.lifecycle.admit(message.id, key, message.method);
        if ('method' in message && message.method === 'notifications/cancelled') {
          const id = message.params?.requestId;
          if (typeof id === 'number' || typeof id === 'string') this.lifecycle.cancel(id);
        }
        this.onmessage?.(message, extra);
      } catch { this.onerror?.(new TransportBudgetError()); this.#closeAfterEvent(); }
    };
    await this.inner.start();
  }

  send(message: JSONRPCMessage, options?: TransportSendOptions): Promise<void> {
    if (this.#closed || this.#closing) return Promise.reject(new Error('MCP 连接已关闭。'));
    // This is output ownership, not physicalWriteStarted. Late cancellation
    // cannot refund a response already handed to this queue by the SDK.
    const classification = this.lifecycle.claimOutput(message);
    const { lane } = classification;
    const control = lane === 'control';
    try {
      const limits = control || classification.setup ? { ...this.limits, messageBytes: control
        ? controlTransportProfile.maximumResponseBytes : controlTransportProfile.maximumSetupResponseBytes } : this.limits;
      const bytes = measureJson(message, limits);
      if ((control || classification.setup) && 'result' in message && typeof message.result === 'object' && message.result !== null && '_meta' in message.result)
        measureJson(message.result._meta, { ...limits, messageBytes: controlTransportProfile.maximumOutputMetadataBytes });
      const reservation = bytes * 3;
      const controlReserve = this.classifier.binding ? controlTransportProfile.maximumControlRequests
        * controlTransportProfile.maximumResponseBytes * 3 : 0;
      const setupReserve = controlTransportProfile.maximumSetupRequests * controlTransportProfile.maximumSetupResponseBytes * 3;
      const ordinaryMaximum = Math.min(this.limits.activeRequests, this.limits.sendCount);
      if (this.#ordinary + this.#control + this.#setup >= this.limits.sendCount + controlTransportProfile.maximumSetupRequests || (control
        ? this.#control >= controlTransportProfile.maximumControlRequests : lane === 'setup'
          ? this.#setup >= controlTransportProfile.maximumSetupRequests
          : this.#ordinary >= ordinaryMaximum || this.#ordinaryBytes + reservation > this.limits.sendBytes - controlReserve - setupReserve)
        || this.#sendBytes + reservation > this.limits.sendBytes) throw new TransportBudgetError();
      const json = JSON.stringify(message); // immutable, bounded, owned until actual callback/close
      this.#sendBytes += reservation;
      if (control) this.#control++; else if (lane === 'setup') this.#setup++; else { this.#ordinary++; this.#ordinaryBytes += reservation; }
      const result = new Promise<void>((resolve, reject) => {
        const output: Output = { json, ...(options ? { options } : {}), lane, reservation, resolve, reject, released: false,
          timer: setTimeout(() => this.#closeAfterEvent(), this.limits.sendDeadlineMs) };
        (control ? this.#controlQueue : this.#normalQueue).push(output);
      });
      this.#pump();
      return result;
    } catch (error) { this.#closeAfterEvent(); return Promise.reject(error); }
  }

  #pump(): void {
    if (this.#pumping || this.#closed || this.#closing) return;
    this.#pumping = true;
    this.#pumpTask = (async () => {
      while (!this.#closed && !this.#closing) {
        const output = this.#controlQueue.shift() ?? this.#normalQueue.shift();
        if (!output) break;
        const message = JSON.parse(output.json) as JSONRPCMessage;
        try {
          const written = (async () => { await this.inner.send(message, output.options); await this.owner.settled(); })();
          await Promise.race([written, this.#closedEvent.then(() => { throw new Error('MCP 连接已关闭。'); })]);
          this.lifecycle.outputDone(message);
          this.#finish(output);
        } catch (error) { this.#finish(output, error); this.#closeAfterEvent(); break; }
      }
    })().finally(() => { this.#pumping = false; if (!this.#closed && !this.#closing && (this.#controlQueue.length || this.#normalQueue.length)) this.#pump(); });
  }
  #finish(output: Output, error?: unknown): void {
    if (output.released) return;
    output.released = true;
    clearTimeout(output.timer);
    this.#sendBytes -= output.reservation;
    if (output.lane === 'control') this.#control--; else if (output.lane === 'setup') this.#setup--;
    else { this.#ordinary--; this.#ordinaryBytes -= output.reservation; }
    output.json = '';
    if (error === undefined) output.resolve(); else output.reject(error);
  }
  close(): Promise<void> {
    if (this.#closing) return this.#closing;
    this.#closing = Promise.resolve().then(async () => {
      const closed = await Promise.allSettled([Promise.resolve().then(() => this.owner.close()), Promise.resolve().then(() => this.inner.close())]);
      this.#closed = true;
      this.#physicalClosed();
      for (const output of [...this.#normalQueue, ...this.#controlQueue]) this.#finish(output, new Error('MCP 连接已关闭。'));
      this.#normalQueue = []; this.#controlQueue = [];
      this.lifecycle.close();
      await this.#pumpTask;
      this.onclose?.();
      if (closed.some(result => result.status === 'rejected')) throw new Error('MCP 传输清理未全部成功。');
    });
    return this.#closing;
  }
  #closeAfterEvent(): void { void this.close().catch(() => this.onerror?.(new Error('MCP 传输清理未全部成功。'))); }
}
