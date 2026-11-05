import { controlTransportProfile } from '@skylineslink/contracts';
import type { JSONRPCMessage } from '@modelcontextprotocol/server';
import { productCapabilities, type Capability } from '../catalog/catalog.ts';
import { GameClientError } from '../game-client/game-client.ts';
import { TransportBudgetError, type TransportLimits } from './limits.ts';

export { controlTransportProfile };
export type RequestId = string | number;
export type OutputLane = 'ordinary' | 'control' | 'setup';
export interface OutputClassification { lane: OutputLane; setup: boolean }

/** Only the SDK's two connection setup RPCs get the small setup envelope. */
export function setupMethod(message: JSONRPCMessage): 'initialize' | 'server/discover' | undefined {
  if (!('method' in message) || !('id' in message) || message.jsonrpc !== '2.0'
    || typeof message.id !== 'string' && (typeof message.id !== 'number' || !Number.isSafeInteger(message.id))) return undefined;
  return message.method === 'initialize' || message.method === 'server/discover' ? message.method : undefined;
}

/** A transport class is derived from this connection's registered, validated binding. */
export class ControlClassifier {
  readonly binding: Capability | undefined;
  constructor(catalog: readonly Capability[] = productCapabilities) {
    this.binding = catalog.find(value => value.id === controlTransportProfile.capabilityId
      && value.schemaRevision === controlTransportProfile.schemaRevision && value.effect === 'write');
  }
  capability(id: string, revision: string, args: unknown): string | undefined {
    const binding = this.binding;
    if (!binding || binding.id !== id || binding.schemaRevision !== revision || !binding.validateInput(args).valid) return undefined;
    const value = args as { taskId: string; expectedControlVersion: number };
    return JSON.stringify([value.taskId, value.expectedControlVersion]);
  }
  message(message: JSONRPCMessage): string | undefined {
    const binding = this.binding;
    if (!binding) return undefined;
    if (!('method' in message) || !('id' in message) || message.jsonrpc !== '2.0' || message.method !== 'tools/call'
      || typeof message.id !== 'string' && (typeof message.id !== 'number' || !Number.isSafeInteger(message.id))) return undefined;
    const params = message.params;
    if (!params || params.name !== binding.toolName) return undefined;
    return this.capability(binding.id, binding.schemaRevision, params.arguments);
  }
  raw(message: JSONRPCMessage, bytes: number): string | undefined {
    if (setupMethod(message) !== undefined && bytes > controlTransportProfile.maximumSetupRequestBytes)
      throw new TransportBudgetError('连接初始化完整消息超过上限。');
    const key = this.message(message);
    if (key !== undefined && bytes > controlTransportProfile.maximumRequestBytes)
      throw new TransportBudgetError('停止请求完整消息超过上限。');
    return key;
  }
}

/** Separate physical and class rates, on the same process monotonic window. */
export class MessageRate {
  #window = -1;
  #ordinary = 0;
  #control = 0;
  constructor(private readonly limits: TransportLimits, private readonly controls: boolean) {}
  accept(control: boolean): void {
    const window = Math.floor(performance.now() / this.limits.windowMs);
    if (window !== this.#window) { this.#window = window; this.#ordinary = 0; this.#control = 0; }
    const maximumControl = this.controls ? controlTransportProfile.maximumControlMessagesPerWindow : 0;
    if (this.#ordinary + this.#control >= this.limits.messagesPerWindow + maximumControl)
      throw new TransportBudgetError('完整消息总速率超过上限。');
    if (control) {
      if (++this.#control > maximumControl) throw new TransportBudgetError('停止消息速率超过上限。');
    } else if (++this.#ordinary > this.limits.messagesPerWindow) throw new TransportBudgetError('普通消息速率超过上限。');
  }
}

/** No queue: each lease owns one actual RPC/work/output obligation. */
export class LaneBudget {
  #ordinary = 0;
  #controls = new Set<string>();
  constructor(readonly ordinaryMaximum: number, readonly controlMaximum = controlTransportProfile.maximumControlRequests) {}
  get active(): number { return this.#ordinary + this.#controls.size; }
  acquire(controlKey?: string): () => void {
    if (controlKey === undefined) {
      if (this.#ordinary >= this.ordinaryMaximum) throw new GameClientError('BUSY', '普通请求额度已满。');
      this.#ordinary++;
    } else {
      if (this.#controls.has(controlKey) || this.#controls.size >= this.controlMaximum)
        throw new GameClientError('BUSY', '停止请求正在收尾或停止额度已满，请按原身份查询。', true);
      this.#controls.add(controlKey);
    }
    let owned = true;
    return () => {
      if (!owned) return;
      owned = false;
      if (controlKey === undefined) this.#ordinary--; else this.#controls.delete(controlKey);
    };
  }
}

interface Ticket {
  controlKey?: string;
  lane: OutputLane;
  setup: boolean;
  cancelled: boolean;
  handlerDone: boolean;
  outputClaimed: boolean;
  signal?: AbortSignal;
  release(): void;
  timer: NodeJS.Timeout;
  removeAbort?: () => void;
}

/** Public context/Transport handoff; no SDK private maps or cancellation-as-IO-completion. */
export class RequestLifecycle {
  #tickets = new Map<RequestId, Ticket>();
  #budget: LaneBudget;
  #setupActive = 0;
  #setupSeen = new Set<string>();
  #setupSealed = false;
  constructor(private readonly limits: TransportLimits, private readonly expire: () => void) {
    this.#budget = new LaneBudget(limits.activeRequests);
  }
  get active(): number { return this.#tickets.size; }
  admit(id: RequestId, controlKey?: string, method?: string): void {
    if (this.#tickets.has(id)) throw new TransportBudgetError('请求标识仍在使用。');
    const setup = method === 'initialize' || method === 'server/discover';
    if (setup && (this.#setupSealed || this.#setupSeen.has(method)))
      throw new TransportBudgetError('此连接的初始化入口已经使用或关闭。');
    if (setup) this.#setupSeen.add(method); else this.#setupSealed = true;
    // A legacy fallback may overlap the modern probe's last physical write.
    // It owns an ordinary slot rather than a second dedicated setup allowance.
    const dedicated = setup && this.#setupActive < controlTransportProfile.maximumSetupRequests;
    const lane: OutputLane = dedicated ? 'setup' : controlKey === undefined ? 'ordinary' : 'control';
    let release: () => void;
    if (dedicated) { this.#setupActive++; let owned = true; release = () => { if (owned) { owned = false; this.#setupActive--; } }; }
    else release = this.#budget.acquire(controlKey);
    const ticket: Ticket = { ...(controlKey === undefined ? {} : { controlKey }), cancelled: false,
      lane, setup, handlerDone: false, outputClaimed: false, release,
      timer: setTimeout(this.expire, this.limits.receiveDeadlineMs + this.limits.sendDeadlineMs) };
    this.#tickets.set(id, ticket);
  }
  cancel(id: RequestId): void {
    const ticket = this.#tickets.get(id);
    if (!ticket) return;
    // Raw cancellation is not SDK acceptance of that notification. Only the
    // public handler signal establishes suppression, and its actual handler
    // must settle before either an ordinary or a Stop id can be reused.
    if (ticket.signal?.aborted) ticket.cancelled = true;
    if (!ticket.outputClaimed && ticket.cancelled && ticket.handlerDone) this.#finish(id, ticket);
  }
  handlerStarted(id: RequestId, signal: AbortSignal): void {
    const ticket = this.#tickets.get(id);
    if (!ticket) return;
    if (ticket.signal && ticket.signal !== signal) { this.expire(); return; }
    ticket.signal = signal;
    const abort = () => this.cancel(id);
    signal.addEventListener('abort', abort, { once: true });
    ticket.removeAbort = () => signal.removeEventListener('abort', abort);
    if (signal.aborted) abort();
  }
  handlerDone(id: RequestId, signal: AbortSignal): void {
    const ticket = this.#tickets.get(id);
    if (!ticket) return;
    if (ticket.signal !== signal) { this.expire(); return; }
    ticket.handlerDone = true;
    if (signal.aborted) ticket.cancelled = true;
    if (ticket.cancelled && !ticket.outputClaimed) this.#finish(id, ticket);
  }
  claimOutput(message: JSONRPCMessage): OutputClassification {
    if (!('id' in message) || message.id === undefined || 'method' in message) return { lane: 'ordinary', setup: false };
    const ticket = this.#tickets.get(message.id);
    if (!ticket) return { lane: 'ordinary', setup: false };
    // Synchronous on Transport.send entry, BEFORE waiting in the owned queue.
    ticket.outputClaimed = true;
    return { lane: ticket.lane, setup: ticket.setup };
  }
  outputDone(message: JSONRPCMessage): void {
    if (!('id' in message) || message.id === undefined || 'method' in message) return;
    const ticket = this.#tickets.get(message.id);
    if (ticket) this.#finish(message.id, ticket);
  }
  close(): void { for (const [id, ticket] of this.#tickets) this.#finish(id, ticket); }
  #finish(id: RequestId, ticket: Ticket): void {
    if (this.#tickets.get(id) !== ticket) return;
    this.#tickets.delete(id);
    clearTimeout(ticket.timer);
    ticket.removeAbort?.();
    ticket.release();
  }
}
