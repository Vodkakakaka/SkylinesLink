import { discoverConnection, readAuthorizationSubjectCandidate, type DiscoverySelection } from './discovery.ts';
import { NamedPipeGameClient, type PipeConnection } from './named-pipe-client.ts';
import { GameClientError, type GameClient } from './game-client.ts';
import { requireContract, schemaIds, type OperationQueryV3, type TaskStopDecisionQuery, type TaskStopSelector, type NativeHistoryRequestV3, type ModuleRuntimeQuery } from '@skylineslink/contracts';
import type { HistoryReader, HistoryReaderV3 } from './history-reader.ts';
import { offlineLookup, requireCapture, requireOperationLookup, requireOperationLookupV4, compatibleSubject, type ConnectionSubject } from './history-result.ts';
import { copyStopSelector, requireStopResult, requireStopDecisionLookup, offlineStopLookup } from './task-stop-result.ts';
import { requireCaptureV3 } from './mixed-history-result.ts';
import { copyModuleRuntimeQuery, requireModuleRuntimePage } from './module-runtime-result.ts';
import { requireCityOverview } from './city-overview-result.ts';
import { copyBuildingInspectionQuery, requireBuildingInspection } from './building-inspection-result.ts';

export interface LiveGameConnection extends GameClient {
  readonly connection: Readonly<Omit<PipeConnection, 'token'>>;
  readonly usage: { closed: boolean };
}

export interface DiscoveredGameDependencies {
  historyReader?: HistoryReader & Partial<Pick<HistoryReaderV3, 'readV3'>>;
  discover?: typeof discoverConnection;
  connect?: (connection: PipeConnection, signal: AbortSignal) => Promise<LiveGameConnection>;
  now?: () => number;
  authorizationCandidate?: typeof readAuthorizationSubjectCandidate;
}

/** One explicit Host session resolves at most one instance, without background polling or replay. */
export class DiscoveredGameClient implements GameClient {
  #attempt: Promise<LiveGameConnection> | undefined;
  #attemptSettled = false;
  #game: LiveGameConnection | undefined;
  #subject: Readonly<ConnectionSubject> | undefined;
  #closing: Promise<void> | undefined;
  #closed = false;
  #lifetime = new AbortController();
  constructor(private readonly selection: DiscoverySelection, private readonly dependencies: DiscoveredGameDependencies = {}) {}

  get subject(): Readonly<ConnectionSubject> | undefined { return this.#subject; }

  async call(id: string, revision: string, args: unknown, signal: AbortSignal): Promise<unknown> {
    const combined = AbortSignal.any([signal, this.#lifetime.signal]);
    this.#checkOpen(combined);
    if (id === 'buildings.inspect' && revision === '1') {
      const query = copyBuildingInspectionQuery(args);
      const game = await this.#live(combined); this.#checkOpen(combined);
      const result = await game.call(id, revision, copyBuildingInspectionQuery(query), combined);
      this.#checkOpen(combined);
      return requireBuildingInspection(query, result, game.connection.instanceId);
    }
    if (id === 'city.overview' && revision === '2') {
      requireContract(schemaIds.Empty, args);
      const game = await this.#live(combined); this.#checkOpen(combined);
      const result = await game.call(id, revision, {}, combined);
      this.#checkOpen(combined);
      return requireCityOverview(result, game.connection.instanceId);
    }
    if (id === 'system.modules.get' && revision === '1') {
      requireContract(schemaIds.ModuleRuntimeQuery, args);
      const query = copyModuleRuntimeQuery(args as ModuleRuntimeQuery);
      const game = await this.#live(combined); this.#checkOpen(combined);
      const result = await game.call(id, revision, copyModuleRuntimeQuery(query), combined);
      this.#checkOpen(combined);
      return requireModuleRuntimePage(query, result, game.connection.instanceId);
    }
    if (id === 'tasks.stop' && revision === '1') {
      requireContract(schemaIds.TaskStopQuery, args);
      const query = copyStopSelector(args as TaskStopSelector);
      const game = await this.#live(combined); this.#checkOpen(combined);
      return requireStopResult(query, await game.call(id, revision, query, combined));
    }
    if (id === 'tasks.stop.get' && revision === '1') {
      requireContract(schemaIds.TaskStopDecisionQuery, args);
      const input = args as TaskStopDecisionQuery;
      const query: TaskStopDecisionQuery = { ...copyStopSelector(input), ...(input.source === undefined ? {} : { source: input.source }) };
      return this.#stopDecision(query, combined);
    }
    if (id !== 'operations.get' || revision !== '4' && revision !== '5') return (await this.#live(combined)).call(id, revision, args, combined);
    requireContract(schemaIds.OperationQueryV3, args);
    const query = args as OperationQueryV3;
    if ((query.source ?? 'live') === 'live') return this.#queryLive(query, revision, combined);
    // One deadline covers Native capture and the single definite-busy live route.
    const deadline = performance.now() + 5000;
    const timeout = new AbortController();
    const timer = setTimeout(() => timeout.abort(), Math.max(0, deadline - performance.now()));
    const waiting = AbortSignal.any([combined, timeout.signal]);
    try {
      // A request that chose live never changes to disk after that call fails.
      if (this.#game && !this.#game.usage.closed || this.#attempt && !this.#attemptSettled) return await this.#queryLive(query, revision, waiting);
      const reader = this.dependencies.historyReader;
      if (!reader) throw new GameClientError('HISTORY_READER_UNAVAILABLE', '此连接没有由固定发行入口提供的历史读取依赖。');
      let capture;
      try {
        capture = await reader.read({ formatVersion: 1, environmentId: this.selection.environment, profileId: this.selection.profile,
          expectedAuthorityEpoch: this.#subject?.authorityEpoch ?? null, expectedGrantEpoch: this.#subject?.grantEpoch ?? null,
          selector: query.selector }, waiting, deadline);
      } catch (error) {
        if (!(error instanceof GameClientError) || error.code !== 'HISTORY_AUTHORITY_BUSY') throw error;
        this.#checkOpen(waiting);
        // #attempt is never reset, including an earlier failed discovery or a closed instance.
        return await this.#queryLive(query, revision, waiting);
      }
      this.#checkOpen(waiting);
      capture = requireCapture(capture, this.selection, query.selector);
      this.#bind(capture);
      const verify = revision === '5' ? requireOperationLookupV4 : requireOperationLookup;
      return verify(query, offlineLookup(query, capture, (this.dependencies.now ?? Date.now)()),
        { environment: this.selection.environment, access: 'offlineLease' });
    } catch (error) {
      if (timeout.signal.aborted && !combined.aborted) throw new GameClientError('HISTORY_TIMEOUT', '历史读取期限已到。');
      throw error;
    } finally { clearTimeout(timer); }
  }

  async #stopDecision(query: TaskStopDecisionQuery, signal: AbortSignal): Promise<unknown> {
    if ((query.source ?? 'live') === 'live' || this.#game && !this.#game.usage.closed || this.#attempt && !this.#attemptSettled) {
      const game = await this.#live(signal); this.#checkOpen(signal);
      return requireStopDecisionLookup(query, await game.call('tasks.stop.get', '1', query, signal), {
        environment: this.selection.environment, instanceId: game.connection.instanceId,
        ...(query.source === 'history' ? { access: 'activeAuthority' as const } : {}),
      });
    }
    const reader = this.dependencies.historyReader;
    if (!reader?.readV3) throw new GameClientError('HISTORY_READER_UNAVAILABLE', '此连接没有固定发行入口的Task历史读取依赖。');
    const deadline = performance.now() + 5000;
    const timeout = new AbortController();
    const timer = setTimeout(() => timeout.abort(), Math.max(0, deadline - performance.now()));
    const waiting = AbortSignal.any([signal, timeout.signal]);
    try {
      let expected = this.#subject;
      if (expected && expected.profileInstanceId === undefined) {
        const candidate = await (this.dependencies.authorizationCandidate ?? readAuthorizationSubjectCandidate)(this.selection, expected);
        // The original deadline includes candidate IO. Do not race/refund a
        // read that is still physically pending; check it when it settles.
        this.#checkOpen(waiting);
        if (!compatibleSubject(expected, candidate) || candidate.profileInstanceId === undefined)
          throw new GameClientError('SUBJECT_CHANGED', '当前历史主体没有相符的完整身份，请重新建立客户端会话。');
        expected = candidate; // candidate is NOT installed until Native3 lease/CAS succeeds
      }
      this.#checkOpen(waiting);
      const request: NativeHistoryRequestV3 = { formatVersion: 3, environmentId: this.selection.environment,
        profileId: this.selection.profile, expectedAuthorityEpoch: expected?.authorityEpoch ?? null,
        expectedGrantEpoch: expected?.grantEpoch ?? null, expectedProfileInstanceId: expected?.profileInstanceId ?? null,
        query: { kind: 'taskStopDecision', ...copyStopSelector(query) } };
      const capture = requireCaptureV3(await reader.readV3(request, waiting, deadline), request);
      this.#checkOpen(waiting); this.#bind(capture);
      return offlineStopLookup(query, capture, (this.dependencies.now ?? Date.now)());
    } catch (error) {
      if (timeout.signal.aborted && !signal.aborted) throw new GameClientError('HISTORY_TIMEOUT', '历史读取期限已到。');
      throw error;
    } finally { clearTimeout(timer); }
  }

  #checkOpen(signal: AbortSignal): void {
    if (this.#closed || signal.aborted) throw new GameClientError('CANCELLED', '本次等待已取消。');
  }

  #bind(subject: ConnectionSubject): void {
    if (subject.environmentId !== this.selection.environment || subject.profileId !== this.selection.profile
      || this.#subject && !compatibleSubject(this.#subject, subject))
      throw new GameClientError('SUBJECT_CHANGED', '连接授权主体已改变，请重新建立客户端会话。');
    // Upgrade only missing knowledge from a current authenticated value. A
    // Native1 response cannot erase an incarnation already fixed by live IO.
    if (!this.#subject || this.#subject.profileInstanceId === undefined && subject.profileInstanceId !== undefined)
      this.#subject = Object.freeze({ environmentId: subject.environmentId, profileId: subject.profileId,
        authorityEpoch: subject.authorityEpoch, grantEpoch: subject.grantEpoch,
        ...(subject.profileInstanceId === undefined ? {} : { profileInstanceId: subject.profileInstanceId }) });
  }

  #live(signal: AbortSignal): Promise<LiveGameConnection> {
    this.#checkOpen(signal);
    return this.#attempt ??= (async () => {
      const connection = await (this.dependencies.discover ?? discoverConnection)(this.selection);
      this.#checkOpen(signal);
      if (this.#subject && !compatibleSubject(this.#subject, connection))
        throw new GameClientError('SUBJECT_CHANGED', '连接授权主体已改变，请重新建立客户端会话。');
      const game = await (this.dependencies.connect ?? ((value, stop) => NamedPipeGameClient.connect(value, undefined, stop)))(connection, signal);
      try { this.#checkOpen(signal); this.#bind(game.connection); }
      catch (error) { await game.close(); throw error; }
      this.#game = game;
      return game;
    })().finally(() => { this.#attemptSettled = true; });
  }

  async #queryLive(query: OperationQueryV3, revision: '4' | '5', signal: AbortSignal): Promise<unknown> {
    const game = await this.#live(signal);
    this.#checkOpen(signal);
    const value = await game.call('operations.get', revision, query, signal);
    const verify = revision === '5' ? requireOperationLookupV4 : requireOperationLookup;
    return verify(query, value, { environment: this.selection.environment, instanceId: game.connection.instanceId,
      ...((query.source ?? 'live') === 'history' ? { access: 'activeAuthority' as const } : {}) });
  }

  close(): Promise<void> {
    this.#closed = true;
    this.#lifetime.abort();
    return this.#closing ??= (async () => {
      await Promise.all([this.dependencies.historyReader?.close(),
        (async () => { try { await (await this.#attempt)?.close(); } catch { /* Failed discovery has no live transport. */ } })()]);
    })();
  }
}
