import test from 'node:test';
import assert from 'node:assert/strict';
import { resolve } from 'node:path';
import { setImmediate as immediate } from 'node:timers/promises';
import type { NativeHistoryCaptureV3, NativeHistoryRequestV3, TaskStopDecisionLookup, TaskStopDecisionQuery } from '../../contracts/ts/index.ts';
import { DiscoveredGameClient, type LiveGameConnection } from '../../src/host/src/game-client/discovered-game-client.ts';
import type { PipeConnection } from '../../src/host/src/game-client/named-pipe-client.ts';
import { GameClientError } from '../../src/host/src/game-client/game-client.ts';
import { offlineStopLookup, requireStopDecisionLookup } from '../../src/host/src/game-client/task-stop-result.ts';
import { historyCapture, historyCaptureV3, historyQuery } from './history-values.ts';

const selection = { environment: 'fixture', profile: 'reader', coreManifestSha256: 'a'.repeat(64), userProfile: resolve('.') };
const connection: PipeConnection = { environmentId: 'fixture', profileId: 'reader', profileInstanceId: 'a'.repeat(32),
  authorityEpoch: 2, grantEpoch: 3, instanceId: 'live-instance', coreManifestSha256: selection.coreManifestSha256,
  productVersion: '0.1.0-dev.0', contractVersion: '2', pipeName: 'skylineslink-fixture', token: 'f'.repeat(64) };
const query: TaskStopDecisionQuery = { taskId: 'task-1', expectedControlVersion: 1, source: 'history' };
const waiting = () => new AbortController().signal;
const code = (name: string) => (error: unknown) => error instanceof GameClientError && error.code === name;
const capture = (request: NativeHistoryRequestV3): NativeHistoryCaptureV3 => ({ ...structuredClone(historyCaptureV3), query: request.query,
  result: { kind: 'taskDecision', receipt: { taskId: query.taskId, decisionKind: 'stop', goalRevision: null,
    controlVersion: 2, sequence: 5, decision: { submissionKey: { writeContextId: 'old-context', requestId: 'original noncanonical stop' },
      commandHash: 'c'.repeat(64), source: { instanceId: 'origin', generation: '1', gameState: 'menu', cityId: null },
      actor: { profileId: 'former-planner', profileInstanceId: 'b'.repeat(32), authorityEpoch: 1, grantEpoch: 1 } } } } });

test('Stop来源只记录观察，ready未知城市仍可查询；非ready不能携带城市身份', () => {
  const original = capture({ formatVersion: 3, environmentId: 'fixture', profileId: 'reader', expectedAuthorityEpoch: null,
    expectedGrantEpoch: null, expectedProfileInstanceId: null,
    query: { kind: 'taskStopDecision', taskId: query.taskId, expectedControlVersion: query.expectedControlVersion } });
  if (original.result.kind !== 'taskDecision') throw new Error('Expected decision selection.');
  const prior = original.result.receipt;
  const receipt = { ...prior, decision: { ...prior.decision, source: { ...prior.decision.source, gameState: 'ready' as const } } };
  const value: NativeHistoryCaptureV3 = { ...original, result: { kind: 'taskDecision', receipt } };
  const historical = offlineStopLookup(query, value, 200);
  assert.equal(historical.result.kind, 'history');
  const liveReceipt = { taskId: receipt.taskId, decisionKind: 'stop' as const, goalRevision: null,
    controlVersion: receipt.controlVersion, decision: receipt.decision, persistenceAttempted: true,
    journal: { sequence: receipt.sequence, storeId: value.source.storeId } };
  const live: TaskStopDecisionLookup = { selector: { taskId: query.taskId, expectedControlVersion: query.expectedControlVersion },
    result: { kind: 'live', instanceId: connection.instanceId, lookup: { kind: 'found', receipt: liveReceipt } } };
  assert.equal(requireStopDecisionLookup({ ...query, source: 'live' }, live), live);
  const invalidDecision = { ...receipt.decision,
    source: { ...receipt.decision.source, gameState: 'menu' as const, cityId: 'unexpected-menu-city' } };
  assert.throws(() => offlineStopLookup(query, { ...value,
    result: { kind: 'taskDecision', receipt: { ...receipt, decision: invalidDecision } } }, 200), code('INVALID_GAME_RESULT'));
  assert.throws(() => requireStopDecisionLookup({ ...query, source: 'live' }, { ...live,
    result: { kind: 'live', instanceId: connection.instanceId,
      lookup: { kind: 'found', receipt: { ...liveReceipt, decision: invalidDecision } } } }), code('INVALID_GAME_RESULT'));
});

test('Stop显式离线history复用同一Native3 owner，保原停止键/生产者且不自动发现或回退', async () => {
  const requests: NativeHistoryRequestV3[] = []; let closed = 0, discoveries = 0;
  let fail: GameClientError | undefined;
  const reader = { read: async () => structuredClone(historyCapture), readV3: async (request: NativeHistoryRequestV3) => {
    requests.push(structuredClone(request)); if (fail) throw fail; return capture(request);
  }, close: async () => { closed++; } };
  const game = new DiscoveredGameClient(selection, { historyReader: reader,
    discover: async () => { discoveries++; throw new GameClientError('GAME_UNAVAILABLE', 'fixture'); }, now: () => 200 });
  try {
    const result = await game.call('tasks.stop.get', '1', query, waiting()) as TaskStopDecisionLookup;
    assert.equal(result.result.kind, 'history');
    if (result.result.kind !== 'history' || result.result.lookup.kind !== 'found') throw new Error('Expected history selection.');
    assert.equal(result.result.access, 'offlineLease'); assert.equal(result.result.ageMs, 100);
    assert.equal(result.result.source.producerInstanceId, historyCaptureV3.source.producerInstanceId);
    assert.deepEqual(result.result.lookup.receipt.decision.submissionKey, { writeContextId: 'old-context', requestId: 'original noncanonical stop' });
    assert.deepEqual(requests[0], { formatVersion: 3, environmentId: 'fixture', profileId: 'reader', expectedAuthorityEpoch: null,
      expectedGrantEpoch: null, expectedProfileInstanceId: null, query: { kind: 'taskStopDecision', taskId: query.taskId, expectedControlVersion: 1 } });
    assert.equal(game.subject?.profileInstanceId, connection.profileInstanceId);
    fail = new GameClientError('HISTORY_AUTHORITY_BUSY', 'fixture');
    await assert.rejects(game.call('tasks.stop.get', '1', query, waiting()), code('HISTORY_AUTHORITY_BUSY'));
    assert.equal(discoveries, 0, 'a busy Native3 selection does not switch route');
    assert.equal(requests.at(-1)?.expectedProfileInstanceId, connection.profileInstanceId);
  } finally { await game.close(); }
  assert.equal(closed, 1);
});

test('Native1已绑定四值升级：Auth2仅提供候选，Native3全expected/CAS成功后才补PID，失败不绑定', async () => {
  let finish!: (value: NativeHistoryCaptureV3) => void; const requests: NativeHistoryRequestV3[] = [];
  let candidates = 0;
  const reader = { read: async () => structuredClone(historyCapture), readV3: (request: NativeHistoryRequestV3) => {
    requests.push(structuredClone(request)); return new Promise<NativeHistoryCaptureV3>(resolve => { finish = resolve; });
  }, close: async () => {} };
  const game = new DiscoveredGameClient(selection, { historyReader: reader, authorizationCandidate: async (_selection, expected) => {
    candidates++; assert.equal(expected.profileInstanceId, undefined); return { ...expected, profileInstanceId: connection.profileInstanceId };
  } });
  try {
    await game.call('operations.get', '5', historyQuery, waiting()); const legacy = game.subject!;
    const pending = game.call('tasks.stop.get', '1', query, waiting()); await immediate();
    assert.equal(game.subject, legacy); assert.equal(game.subject?.profileInstanceId, undefined);
    assert.equal(requests[0]?.expectedAuthorityEpoch, legacy.authorityEpoch);
    assert.equal(requests[0]?.expectedGrantEpoch, legacy.grantEpoch);
    assert.equal(requests[0]?.expectedProfileInstanceId, connection.profileInstanceId);
    finish({ ...capture(requests[0]!), profileInstanceId: 'c'.repeat(32) });
    await assert.rejects(pending, code('INVALID_HISTORY_RESULT'));
    assert.equal(game.subject, legacy, 'a rejected capture cannot install the local candidate');
    const retry = game.call('tasks.stop.get', '1', query, waiting()); await immediate(); finish(capture(requests[1]!)); await retry;
    assert.equal(game.subject?.profileInstanceId, connection.profileInstanceId); assert.equal(candidates, 2);
    const bound = game.subject;
    const next = game.call('tasks.stop.get', '1', query, waiting()); await immediate(); finish(capture(requests[2]!)); await next;
    assert.equal(candidates, 2, 'known PID does not get reread or replaced'); assert.equal(game.subject, bound);
  } finally { await game.close(); }
});

test('Native1升级候选四值不符在Native3之前拒绝，已知主体不被清空', async () => {
  let nativeReads = 0;
  const game = new DiscoveredGameClient(selection, { historyReader: { read: async () => structuredClone(historyCapture),
    readV3: async request => { nativeReads++; return capture(request); }, close: async () => {} },
  authorizationCandidate: async (_selection, expected) => ({ ...expected, grantEpoch: expected.grantEpoch + 1, profileInstanceId: connection.profileInstanceId }) });
  try {
    await game.call('operations.get', '5', historyQuery, waiting()); const original = game.subject;
    await assert.rejects(game.call('tasks.stop.get', '1', query, waiting()), code('SUBJECT_CHANGED'));
    assert.equal(nativeReads, 0); assert.equal(game.subject, original);
  } finally { await game.close(); }
});

test('Stop history绝对期限包含候选IO；计时到期仍持有未结束读取，读取结束才拒绝且不启动Native3', async t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  let release!: () => void, nativeReads = 0, settled = false;
  const game = new DiscoveredGameClient(selection, { historyReader: { read: async () => structuredClone(historyCapture),
    readV3: async request => { nativeReads++; return capture(request); }, close: async () => {} },
  authorizationCandidate: async (_selection, expected) => {
    await new Promise<void>(r => { release = r; }); return { ...expected, profileInstanceId: connection.profileInstanceId };
  } });
  try {
    await game.call('operations.get', '5', historyQuery, waiting());
    const pending = game.call('tasks.stop.get', '1', query, waiting()).finally(() => { settled = true; });
    const rejected = assert.rejects(pending, code('HISTORY_TIMEOUT'));
    await immediate(); t.mock.timers.tick(5001); await immediate();
    assert.equal(settled, false, 'timeout is not IO completion'); assert.equal(nativeReads, 0);
    release(); await rejected; assert.equal(nativeReads, 0); assert.equal(game.subject?.profileInstanceId, undefined);
  } finally { release?.(); await game.close(); }
});

test('Stop缺省仅live；已有live的显式history仍走同权威，失败不换Native3', async () => {
  let diskReads = 0; const calls: TaskStopDecisionQuery[] = [];
  const live: LiveGameConnection = { connection, usage: { closed: false }, close: async () => {}, call: async (_id, _rev, args) => {
    const input = args as TaskStopDecisionQuery; calls.push(structuredClone(input));
    if (input.source === 'history') throw new GameClientError('UNAUTHORIZED', 'fixture');
    return { selector: { taskId: input.taskId, expectedControlVersion: input.expectedControlVersion },
      result: { kind: 'live', instanceId: connection.instanceId, lookup: { kind: 'unavailable', receipt: null, reason: 'TASKS_UNAVAILABLE' } } };
  } };
  const game = new DiscoveredGameClient(selection, { discover: async () => connection, connect: async () => live,
    historyReader: { read: async () => structuredClone(historyCapture), readV3: async request => { diskReads++; return capture(request); }, close: async () => {} } });
  try {
    const result = await game.call('tasks.stop.get', '1', { taskId: query.taskId, expectedControlVersion: 1 }, waiting()) as TaskStopDecisionLookup;
    assert.equal(result.result.kind, 'live');
    await assert.rejects(game.call('tasks.stop.get', '1', query, waiting()), code('UNAUTHORIZED'));
    assert.equal(diskReads, 0); assert.equal(calls[0]?.source, undefined); assert.equal(calls[1]?.source, 'history');
  } finally { await game.close(); }
});
