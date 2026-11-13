import { createConnection, type Socket } from 'node:net';
import { randomUUID, randomBytes } from 'node:crypto';
import { schemaIds, requireContract, parseContractJson, capabilities, type RegisteredBinding, type WireAuthenticateV2, type WireChallengeV2, type WireAuthenticatedV2, type WireError, type WireResult } from '@skylineslink/contracts';
import { checkRawJson, measureJson } from '../shared/json-budget.ts';
import { probeLimits, validateLimits, type TransportLimits } from '../shared/limits.ts';
import { GameClientError, type GameClient } from './game-client.ts';
import { createHandshakeProof, equalProof, isLowerHex256 } from './auth-proof.ts';
import { ControlClassifier, LaneBudget, MessageRate, controlTransportProfile } from '../shared/control-admission.ts';
import { copyStopSelector, requireStopResult, requireStopDecisionLookup } from './task-stop-result.ts';
import type { TaskStopDecisionQuery } from '@skylineslink/contracts';
import type { ModuleRuntimeQuery } from '@skylineslink/contracts';
import { copyModuleRuntimeQuery, requireModuleRuntimePage } from './module-runtime-result.ts';
import { copyBuildingInspectionQuery, requireBuildingInspection } from './building-inspection-result.ts';
import type { BuildingInspectionQuery } from '@skylineslink/contracts';

export interface PipeConnection {
  pipeName: string;
  profileId: string;
  profileInstanceId: string;
  token: string;
  instanceId: string;
  environmentId: string;
  authorityEpoch: number;
  grantEpoch: number;
  coreManifestSha256: string;
  productVersion: string;
  contractVersion: string;
}

interface Pending {
  binding?: RegisteredBinding;
  resolve(value: unknown): void;
  reject(reason: Error): void;
  timer: NodeJS.Timeout;
  stopAbort?: () => void;
  mayHaveApplied: boolean;
  cancelled: boolean;
  release(): void;
  query?: TaskStopDecisionQuery;
  moduleQuery?: ModuleRuntimeQuery;
  buildingQuery?: BuildingInspectionQuery;
}

interface Sending { json: string; reservation: number; control: boolean; onWriteStart?: () => void; onWriteDone?: () => void }

interface Handshake {
  auth: WireAuthenticateV2;
  key: Buffer;
  phase: 'challenge' | 'proofQueued' | 'authenticated';
  proofSettled: boolean;
  ackValidated: boolean;
  resolve(): void;
  reject(reason: Error): void;
}

export function encodePipeFrame(value: unknown, limits: TransportLimits = probeLimits): Buffer {
  measureJson(value, limits);
  const body = Buffer.from(JSON.stringify(value), 'utf8');
  if (body.length > limits.messageBytes) throw new GameClientError('BUDGET_EXCEEDED', '内部消息超过上限。');
  const frame = Buffer.allocUnsafe(12 + body.length);
  frame.write('SLNK', 0, 4, 'ascii');
  frame.writeUInt16LE(1, 4);
  frame.writeUInt16LE(0, 6);
  frame.writeUInt32LE(body.length, 8);
  body.copy(frame, 12);
  body.fill(0);
  return frame;
}

// Frame admission only; UTF-8, JSON and the current Wire phase are checked after the bounded read.
export function decodePipeFrameLength(header: Buffer, maximumBytes: number): number {
  if (header.length !== 12 || header[0] !== 0x53 || header[1] !== 0x4c || header[2] !== 0x4e || header[3] !== 0x4b ||
      header.readUInt16LE(4) !== 1 || header.readUInt16LE(6) !== 0) throw new Error('无效帧头。');
  const length = header.readUInt32LE(8);
  if (length === 0 || length > maximumBytes) throw new Error('帧长度越界。');
  return length;
}

export class NamedPipeGameClient implements GameClient {
  readonly connection: Readonly<Omit<PipeConnection, 'token'>>;
  readonly limits: TransportLimits;
  #socket: Socket;
  #pending = new Map<string, Pending>();
  #pendingBytes = 0;
  #sending = 0;
  #ready = false;
  #closed = false;
  #closePromise: Promise<void>;
  #header = Buffer.alloc(12);
  #headerBytes = 0;
  #body: Buffer | undefined;
  #bodyBytes = 0;
  #frameTimer: NodeJS.Timeout | undefined;
  #pumpTask: Promise<void> | undefined;
  #pumping = false;
  #ordinaryQueue: Sending[] = [];
  #controlQueue: Sending[] = [];
  #ordinarySending = 0;
  #controlSending = 0;
  #ordinaryBytes = 0;
  #classifier = new ControlClassifier();
  #pendingBudget: LaneBudget;
  #rate: MessageRate;
  #handshake: Handshake | undefined;
  #terminalError: GameClientError | undefined;

  private constructor(connection: PipeConnection, limits: TransportLimits) {
    const { token: _token, ...identity } = connection;
    this.connection = Object.freeze(identity);
    this.limits = Object.freeze({ ...limits });
    this.#pendingBudget = new LaneBudget(limits.activeRequests);
    this.#rate = new MessageRate(limits, this.#classifier.binding !== undefined);
    this.#socket = createConnection({ path: `\\\\.\\pipe\\${connection.pipeName}` });
    this.#closePromise = new Promise<void>((resolve) => this.#socket.once('close', () => {
      this.#closed = true;
      clearTimeout(this.#frameTimer);
      this.#body?.fill(0);
      this.#body = undefined;
      this.#handshake?.key.fill(0);
      this.#handshake?.reject(this.#terminalError ?? new GameClientError('GAME_UNAVAILABLE', '游戏连接认证未完成。'));
      this.#handshake = undefined;
      for (const pending of this.#pending.values()) {
        clearTimeout(pending.timer);
        pending.stopAbort?.();
        pending.release();
        pending.reject(pending.cancelled
          ? new GameClientError('CANCELLED', '本次等待已取消；已受理操作仍须按原身份查询。', pending.mayHaveApplied)
          : new GameClientError(pending.mayHaveApplied ? 'OUTCOME_UNKNOWN' : 'GAME_DISCONNECTED', '游戏连接已关闭；请按原操作身份查询。', pending.mayHaveApplied));
      }
      this.#pending.clear();
      for (const item of [...this.#ordinaryQueue, ...this.#controlQueue]) this.#finishSending(item);
      this.#ordinaryQueue = []; this.#controlQueue = [];
      resolve();
    }));
    this.#socket.on('error', () => this.#socket.destroy());
    this.#socket.on('data', (chunk) => {
      try { if (typeof chunk === 'string') throw new Error('内部流编码不合法。'); this.#receive(chunk); }
      catch (error) { this.#fail(error instanceof GameClientError ? error : new GameClientError('INVALID_GAME_RESPONSE', '游戏连接返回了不符合当前阶段的消息。')); }
    });
  }

  static async connect(connection: PipeConnection, limits: TransportLimits = probeLimits, signal?: AbortSignal): Promise<NamedPipeGameClient> {
    validateLimits(limits);
    if (signal?.aborted) throw new GameClientError('CANCELLED', '游戏连接认证已取消。');
    if (!/^skylineslink-[a-z0-9-]{1,107}$/.test(connection.pipeName)) throw new GameClientError('INVALID_ENDPOINT', '内部端点必须为本机 SkylinesLink 管道名。');
    if (connection.contractVersion !== '2' || typeof connection.profileInstanceId !== 'string' || !/^[a-f0-9]{32}$/.test(connection.profileInstanceId)
      || !isLowerHex256(connection.token) || !isLowerHex256(connection.coreManifestSha256)) throw new GameClientError('INCOMPATIBLE_COMPONENT', '连接身份或认证材料不符合当前契约。');
    const auth: WireAuthenticateV2 = { kind: 'authenticate', requestId: randomUUID(), profileId: connection.profileId,
      environmentId: connection.environmentId, expectedInstanceId: connection.instanceId, productVersion: connection.productVersion,
      contractVersion: '2', coreManifestSha256: connection.coreManifestSha256, expectedAuthorityEpoch: connection.authorityEpoch,
      expectedGrantEpoch: connection.grantEpoch, clientNonce: randomBytes(32).toString('hex') };
    requireContract(schemaIds.WireAuthenticateV2, auth);
    const client = new NamedPipeGameClient(connection, limits);
    const abort = () => client.#fail(new GameClientError('CANCELLED', '游戏连接认证已取消。'));
    signal?.addEventListener('abort', abort, { once: true });
    // One deadline covers connect plus all four handshake frames; progress cannot extend it.
    const timer = setTimeout(() => client.#fail(new GameClientError('AUTHENTICATION_TIMEOUT', '游戏连接认证超时。')), limits.receiveDeadlineMs);
    try {
      await new Promise<void>((resolve, reject) => {
        client.#socket.once('connect', resolve);
        client.#socket.once('close', () => reject(new GameClientError('GAME_UNAVAILABLE', '未能连接游戏实例。')));
      });
      await new Promise<void>((resolve, reject) => {
        client.#handshake = { auth, key: Buffer.from(connection.token, 'hex'), phase: 'challenge', proofSettled: false,
          ackValidated: false, resolve, reject };
        try { client.#queueFrame(auth); } catch (error) { client.#fail(error instanceof GameClientError ? error : new GameClientError('AUTHENTICATION_FAILED', '游戏连接认证失败。')); }
      });
      return client;
    } catch (error) {
      await client.close();
      throw error;
    } finally { clearTimeout(timer); signal?.removeEventListener('abort', abort); }
  }

  async call(capabilityId: string, schemaRevision: string, args: unknown, signal: AbortSignal): Promise<unknown> {
    const buildingQuery = capabilityId === 'buildings.inspect' && schemaRevision === '1' ? copyBuildingInspectionQuery(args) : undefined;
    if (buildingQuery) args = copyBuildingInspectionQuery(buildingQuery);
    const moduleQuery = capabilityId === 'system.modules.get' && schemaRevision === '1'
      ? (requireContract(schemaIds.ModuleRuntimeQuery, args), copyModuleRuntimeQuery(args as ModuleRuntimeQuery)) : undefined;
    if (moduleQuery) args = copyModuleRuntimeQuery(moduleQuery);
    if (!this.#ready || this.#closed || this.#socket.destroyed) throw new GameClientError('GAME_UNAVAILABLE', '游戏连接尚未就绪或已经关闭。');
    let query: TaskStopDecisionQuery | undefined;
    if ((capabilityId === 'tasks.stop' || capabilityId === 'tasks.stop.get') && schemaRevision === '1') {
      const input = args as TaskStopDecisionQuery;
      query = { ...copyStopSelector(input), ...(capabilityId === 'tasks.stop.get' && input.source !== undefined ? { source: input.source } : {}) };
    }
    const requestId = randomUUID();
    const request = { kind: 'request', requestId, capabilityId, schemaRevision, arguments: args };
    requireContract(schemaIds.WireRequest, request);
    const capability = capabilities.find(c => c.id === capabilityId && c.schemaRevision === schemaRevision);
    const reply = await this.#send(requestId, request, { capabilityId, schemaRevision }, signal, capability?.effect === 'write', query, moduleQuery, buildingQuery) as WireResult;
    return reply.result;
  }

  #send(requestId: string, value: unknown, binding: RegisteredBinding | undefined, signal: AbortSignal, mayHaveApplied: boolean, query?: TaskStopDecisionQuery, moduleQuery?: ModuleRuntimeQuery, buildingQuery?: BuildingInspectionQuery): Promise<unknown> {
    if (signal.aborted) return Promise.reject(new GameClientError('CANCELLED', '本次等待已取消。'));
    if (this.#closed) return Promise.reject(new GameClientError('GAME_UNAVAILABLE', '内部连接已经关闭。'));
    const controlKey = binding ? this.#classifier.capability(binding.capabilityId, binding.schemaRevision, query) : undefined;
    let release: () => void;
    try { release = this.#pendingBudget.acquire(controlKey); } catch (error) { return Promise.reject(error); }
    return new Promise((resolve, reject) => {
      // A caller stops waiting; it does not cancel the accepted RPC or the
      // other calls sharing this socket. Keep this promise and slot until the
      // original validated reply or actual connection close drains the owner.
      const abort = () => { entry.cancelled = true; };
      const timer = setTimeout(() => this.#socket.destroy(), this.limits.receiveDeadlineMs + this.limits.sendDeadlineMs);
      const entry: Pending = { resolve, reject, timer, mayHaveApplied, cancelled: false, release,
        ...(query ? { query } : {}),
        ...(moduleQuery ? { moduleQuery } : {}),
        ...(buildingQuery ? { buildingQuery } : {}),
        ...(binding ? { binding } : {}), stopAbort: () => signal.removeEventListener('abort', abort) };
      this.#pending.set(requestId, entry);
      signal.addEventListener('abort', abort, { once: true });
      if (signal.aborted) abort();
      try { this.#queueFrame(value, undefined, controlKey); }
      catch (error) {
        this.#pending.delete(requestId); clearTimeout(timer); entry.stopAbort?.(); release(); reject(error);
      }
    });
  }

  #queueFrame(value: unknown, onWriteStart?: () => void, controlKey?: string, onWriteDone?: () => void): void {
    if (this.#closed || this.#socket.destroyed) throw new GameClientError('GAME_UNAVAILABLE', '游戏连接已经关闭。');
    const control = controlKey !== undefined;
    const maximum = control ? controlTransportProfile.maximumRequestBytes - 12 : this.limits.messageBytes;
    const reservation = (measureJson(value, { ...this.limits, messageBytes: maximum }) + 12) * 3;
    const controlReserve = this.#classifier.binding ? controlTransportProfile.maximumControlRequests * controlTransportProfile.maximumRequestBytes * 3 : 0;
    if (this.#sending >= this.limits.sendCount || this.#pendingBytes + reservation > this.limits.sendBytes
      || (control ? this.#controlSending >= controlTransportProfile.maximumControlRequests :
        this.#ordinarySending >= this.limits.activeRequests || this.#ordinaryBytes + reservation > this.limits.sendBytes - controlReserve))
      throw new GameClientError('BUDGET_EXCEEDED', '内部连接字节额度不足。');
    const json = JSON.stringify(value);
    this.#pendingBytes += reservation; this.#sending++;
    if (control) this.#controlSending++; else { this.#ordinarySending++; this.#ordinaryBytes += reservation; }
    (control ? this.#controlQueue : this.#ordinaryQueue).push({ json, reservation, control,
      ...(onWriteStart ? { onWriteStart } : {}), ...(onWriteDone ? { onWriteDone } : {}) });
    this.#pump();
  }

  #finishSending(item: Sending): void {
    item.json = ''; this.#pendingBytes -= item.reservation; this.#sending--;
    if (item.control) this.#controlSending--; else { this.#ordinarySending--; this.#ordinaryBytes -= item.reservation; }
  }
  #pump(): void {
    if (this.#pumping || this.#closed || this.#socket.destroyed) return;
    this.#pumping = true;
    // Drain the current receive callback before a newly queued proof can
    // physically start; a coalesced ACK is still invalid in proofQueued.
    this.#pumpTask = Promise.resolve().then(async () => {
      while (!this.#closed && !this.#socket.destroyed) {
        const item = this.#controlQueue.shift() ?? this.#ordinaryQueue.shift();
        if (!item) break;
        let frame: Buffer | undefined;
        let sent = false;
        try {
          frame = encodePipeFrame(JSON.parse(item.json), this.limits);
          await new Promise<void>((written, failed) => {
            const sendTimer = setTimeout(() => this.#socket.destroy(), this.limits.sendDeadlineMs);
            const closed = () => { clearTimeout(sendTimer); failed(new Error('连接已关闭。')); };
            this.#socket.once('close', closed);
            item.onWriteStart?.();
            this.#socket.write(frame!, error => {
              this.#socket.off('close', closed); clearTimeout(sendTimer);
              if (error) failed(error); else written();
            });
          });
          sent = true;
        } catch { this.#socket.destroy(); }
        finally { frame?.fill(0); this.#finishSending(item); }
        if (sent) item.onWriteDone?.();
      }
    }).finally(() => { this.#pumping = false; if (!this.#closed && !this.#socket.destroyed && (this.#ordinaryQueue.length || this.#controlQueue.length)) this.#pump(); });
  }

  #fail(error: GameClientError): void { this.#terminalError ??= error; this.#socket.destroy(); }

  #receive(chunk: Buffer): void {
    let offset = 0;
    while (offset < chunk.length && !this.#socket.destroyed) {
      this.#frameTimer ??= setTimeout(() => this.#socket.destroy(), this.limits.receiveDeadlineMs);
      if (this.#headerBytes < 12) {
        const n = Math.min(12 - this.#headerBytes, chunk.length - offset);
        chunk.copy(this.#header, this.#headerBytes, offset, offset + n);
        this.#headerBytes += n; offset += n;
        if (this.#headerBytes < 12) continue;
        const length = decodePipeFrameLength(this.#header, this.limits.messageBytes);
        this.#body = Buffer.allocUnsafe(length);
      }
      const body = this.#body!;
      const n = Math.min(body.length - this.#bodyBytes, chunk.length - offset);
      chunk.copy(body, this.#bodyBytes, offset, offset + n);
      this.#bodyBytes += n; offset += n;
      if (this.#bodyBytes === body.length) {
        const value = parseContractJson(checkRawJson(body, this.limits));
        const pending = typeof value === 'object' && value !== null && 'requestId' in value ? this.#pending.get(String(value.requestId)) : undefined;
        const control = pending?.binding?.capabilityId === controlTransportProfile.capabilityId;
        if (control && body.length > controlTransportProfile.maximumResponseBytes) throw new Error('停止回执超过上限。');
        this.#rate.accept(control);
        this.#accept(value);
        body.fill(0);
        this.#body = undefined; this.#bodyBytes = 0; this.#headerBytes = 0;
        clearTimeout(this.#frameTimer); this.#frameTimer = undefined;
      }
    }
  }

  #accept(value: unknown): void {
    if (typeof value !== 'object' || value === null || !('requestId' in value) || typeof value.requestId !== 'string') throw new Error('响应身份缺失。');
    if (this.#handshake) { this.#acceptHandshake(value as object & { requestId: string }); return; }
    if (!this.#ready) throw new Error('握手尚未开始。');
    const pending = this.#pending.get(value.requestId);
    if (!pending) throw new Error('响应与当前请求无关联。');
    const kind = 'kind' in value ? value.kind : undefined;
    if (kind === 'error') requireContract(schemaIds.WireError, value);
    else if (pending.binding) requireContract(schemaIds.WireResult, value, pending.binding);
    else throw new Error('缺少结果绑定。');
    if (kind === 'result' && pending.query) {
      const result = (value as WireResult).result;
      if (pending.binding?.capabilityId === 'tasks.stop') requireStopResult(pending.query, result);
      else requireStopDecisionLookup(pending.query, result, { environment: this.connection.environmentId,
        instanceId: this.connection.instanceId, ...(pending.query.source === 'history' ? { access: 'activeAuthority' as const } : {}) });
    }
    if (kind === 'result' && pending.moduleQuery)
      requireModuleRuntimePage(pending.moduleQuery, (value as WireResult).result, this.connection.instanceId);
    if (kind === 'result' && pending.buildingQuery)
      requireBuildingInspection(pending.buildingQuery, (value as WireResult).result, this.connection.instanceId);
    this.#pending.delete(value.requestId);
    clearTimeout(pending.timer); pending.stopAbort?.();
    pending.release();
    if (pending.cancelled) pending.reject(new GameClientError('CANCELLED', '本次等待已取消；已受理操作仍须按原身份查询。', pending.mayHaveApplied));
    else if (kind === 'error') pending.reject(new GameClientError((value as WireError).code, '游戏接口拒绝本次请求。', pending.mayHaveApplied));
    else pending.resolve(value);
  }

  #acceptHandshake(value: object & { requestId: string }): void {
    const handshake = this.#handshake!;
    const auth = handshake.auth;
    if (value.requestId !== auth.requestId) throw new Error('握手身份不匹配。');
    if ('kind' in value && value.kind === 'error') {
      requireContract(schemaIds.WireError, value);
      const code = (value as WireError).code;
      throw new GameClientError(['UNAUTHORIZED', 'STALE_DISCOVERY', 'INCOMPATIBLE_COMPONENT'].includes(code) ? code : 'AUTHENTICATION_FAILED', '游戏连接认证被拒绝。');
    }
    if (handshake.phase === 'challenge') {
      requireContract(schemaIds.WireChallengeV2, value);
      const challenge = value as WireChallengeV2;
      this.#checkBinding(challenge, auth);
      if (challenge.clientNonce !== auth.clientNonce || !isLowerHex256(challenge.serverNonce)
        || !equalProof(challenge.serverProof, createHandshakeProof(handshake.key, 'server', auth, challenge.serverNonce))) throw new GameClientError('AUTHENTICATION_FAILED', '游戏端身份未通过认证。');
      const proof = { kind: 'proof', requestId: auth.requestId, clientProof: createHandshakeProof(handshake.key, 'client', auth, challenge.serverNonce) };
      requireContract(schemaIds.WireProofV2, proof);
      handshake.phase = 'proofQueued';
      this.#queueFrame(proof, () => { handshake.phase = 'authenticated'; }, undefined, () => {
        handshake.proofSettled = true; this.#completeHandshake(handshake);
      });
      return;
    }
    if (handshake.phase !== 'authenticated' || handshake.ackValidated) throw new Error('握手阶段不匹配。');
    requireContract(schemaIds.WireAuthenticatedV2, value);
    this.#checkBinding(value as WireAuthenticatedV2, auth);
    handshake.key.fill(0);
    handshake.ackValidated = true;
    this.#completeHandshake(handshake);
  }

  #completeHandshake(handshake: Handshake): void {
    // ACK receipt proves peer acceptance, not completion of our proof's write.
    // No RPC may consume the ordinary eight until both owners really finish.
    if (this.#handshake !== handshake || !handshake.ackValidated || !handshake.proofSettled
      || this.#closed || this.#socket.destroyed) return;
    this.#handshake = undefined;
    this.#ready = true;
    handshake.resolve();
  }

  #checkBinding(reply: Omit<WireAuthenticatedV2, 'kind'>, auth: WireAuthenticateV2): void {
    if (reply.productVersion !== auth.productVersion || reply.contractVersion !== auth.contractVersion || reply.coreManifestSha256 !== auth.coreManifestSha256) throw new GameClientError('INCOMPATIBLE_COMPONENT', '游戏与当前 Host 的发行不匹配。');
    if (reply.environmentId !== auth.environmentId || reply.instanceId !== auth.expectedInstanceId || reply.authorityEpoch !== auth.expectedAuthorityEpoch) throw new GameClientError('STALE_DISCOVERY', '游戏接入实例已经变化。');
    if (reply.profileId !== auth.profileId || reply.grantEpoch !== auth.expectedGrantEpoch) throw new GameClientError('UNAUTHORIZED', '连接授权已经变化。');
  }

  get usage() { return { pending: this.#pending.size, pendingBytes: this.#pendingBytes, frameBytes: this.#body?.length ?? 0, closed: this.#closed }; }

  async close(): Promise<void> {
    this.#socket.destroy();
    await this.#closePromise;
    await this.#pumpTask;
  }
}
