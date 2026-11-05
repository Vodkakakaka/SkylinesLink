import test from 'node:test';
import assert from 'node:assert/strict';
import type { NativeHistoryCaptureV3, NativeHistoryRequestV3, NativeHistoryQueryV3, TaskHistorySummary } from '../../contracts/ts/index.ts';
import { requireCaptureV2, requireCaptureV3 } from '../../src/host/src/game-client/mixed-history-result.ts';
import { GameClientError } from '../../src/host/src/game-client/game-client.ts';
import { historyCapture } from './history-values.ts';
import { measureJson, checkRawJson } from '../../src/host/src/shared/json-budget.ts';
import { probeLimits } from '../../src/host/src/shared/limits.ts';
import { nativeHistoryLimits } from '../../contracts/ts/index.ts';

const reader = 'a'.repeat(32), historicalOwner = 'c'.repeat(32);
const source: NativeHistoryCaptureV3['source'] = { ...historyCapture.source, formatVersion: 2,
  journalSchemaRevision: 3, selection: 'confirmedTaskMetadataAndOperationTerminals', throughSequence: 129, throughBytes: 2097152 };
const request = (query: NativeHistoryQueryV3): NativeHistoryRequestV3 => ({ formatVersion: 3, environmentId: 'fixture', profileId: 'reader',
  expectedAuthorityEpoch: 2, expectedGrantEpoch: 3, expectedProfileInstanceId: reader, query });
const capture = (query: NativeHistoryQueryV3, result: NativeHistoryCaptureV3['result']): NativeHistoryCaptureV3 => ({
  formatVersion: 3, environmentId: 'fixture', profileId: 'reader', profileInstanceId: reader,
  authorizationRecordRevision: 3, authorityEpoch: 2, grantEpoch: 3, source, query, result,
});
function summary(taskId: string, sequence = 2): TaskHistorySummary {
  return { taskId, cityId: 'prior-city', owner: { profileId: 'past-owner', profileInstanceId: historicalOwner, authorityEpoch: 1, grantEpoch: 1 },
    title: 'A city task', goalRevision: 1, controlVersion: 1, isStopped: false, createdSequence: sequence, lastDecisionSequence: sequence };
}
function rejects(value: unknown, input: NativeHistoryRequestV3) {
  assert.throws(() => requireCaptureV3(value, input), (error: unknown) => error instanceof GameClientError && error.code === 'INVALID_HISTORY_RESULT');
}

test('Native3名单只表示同CP2保留集合，空/满8项保历史owner与当前reader', () => {
  const query = { kind: 'tasks' as const }, input = request(query);
  const empty = capture(query, { kind: 'tasks', tasks: [] });
  assert.equal(requireCaptureV3(empty, input), empty);
  const tasks = Array.from({ length: 8 }, (_, index) => summary(`task-${index}`, index + 2));
  const full = capture(query, { kind: 'tasks', tasks });
  assert.equal(requireCaptureV3(full, input), full);
  assert.equal(full.profileInstanceId, reader); assert.equal(tasks[0]!.owner.profileInstanceId, historicalOwner);
  rejects(capture(query, { kind: 'notCovered' }), input);
  rejects({ ...full, result: { kind: 'tasks', tasks: [...tasks, summary('task-8', 10)] } }, input);
  rejects({ ...full, result: { kind: 'tasks', tasks: [...tasks].reverse() } }, input);
  rejects({ ...full, result: { kind: 'tasks', tasks: [tasks[0], tasks[0]] } }, input);
  rejects({ ...full, result: { kind: 'tasks', tasks: [summary('a', 2), summary('b', 2)] } }, input);
  rejects(capture(query, { kind: 'tasks', tasks: [{ ...summary('a', 2), lastDecisionSequence: 3 }, { ...summary('b', 3), lastDecisionSequence: 4 }] }), input);
  rejects({ ...full, result: { kind: 'tasks', tasks: [{ ...tasks[0], owner: { ...tasks[0]!.owner, profileInstanceId: 'bad' } }] } }, input);
  rejects({ ...full, result: { kind: 'tasks', tasks: [{ ...tasks[0], lastDecisionSequence: 130 }] } }, input);
  rejects({ ...full, profileInstanceId: historicalOwner }, input);
  rejects({ ...full, query: { kind: 'task', taskId: 'task-0' } }, input);
  assert.throws(() => requireCaptureV2(full, { ...input, formatVersion: 2 } as never));
});

test('Native3按UTF16 Ordinal而非locale/Unicode scalar排序，并核混合expected和完整source', () => {
  const query = { kind: 'tasks' as const }, input = request(query);
  const tasks = [summary('\u{10000}', 2), summary('\uE000', 3)];
  const value = capture(query, { kind: 'tasks', tasks });
  assert.equal(requireCaptureV3(value, input), value);
  rejects(capture(query, { kind: 'tasks', tasks: [...tasks].reverse() }), input);
  rejects(value, { ...input, expectedProfileInstanceId: null });
  rejects({ ...value, authorityEpoch: 3 }, input);
  rejects({ ...value, source: { ...source, formatVersion: 1 } }, input);
  rejects({ ...value, source: { ...source, throughBytes: 129 } }, input);
  const initial = { ...input, expectedAuthorityEpoch: null, expectedGrantEpoch: null, expectedProfileInstanceId: null };
  assert.equal(requireCaptureV3(value, initial), value);
});

test('Native3 Stop按原任务和控制版本关联，保非canonical原key/历史actor和menu source', () => {
  const query = { kind: 'taskStopDecision' as const, taskId: 'retained-task', expectedControlVersion: 7 }, input = request(query);
  const receipt = { taskId: query.taskId, decisionKind: 'stop' as const, goalRevision: null, controlVersion: 8, sequence: 10,
    decision: { submissionKey: { writeContextId: 'an-original-imported-key', requestId: 'not-a-canonical-stop-key' },
      actor: { profileId: 'old-controller', profileInstanceId: historicalOwner, authorityEpoch: 1, grantEpoch: 1 },
      source: { instanceId: 'prior-game', generation: '19', gameState: 'menu' as const, cityId: null }, commandHash: 'f'.repeat(64) } };
  const value = capture(query, { kind: 'taskDecision', receipt });
  assert.equal(requireCaptureV3(value, input), value);
  assert.equal(value.result.kind === 'taskDecision' && value.result.receipt.decision.submissionKey.requestId, 'not-a-canonical-stop-key');
  for (const patch of [{ taskId: 'other' }, { controlVersion: 7 }, { decisionKind: 'reviseGoal', goalRevision: 2 }, { goalRevision: 1 }])
    rejects({ ...value, result: { kind: 'taskDecision', receipt: { ...receipt, ...patch } } }, input);
  rejects({ ...value, query: { ...query, expectedControlVersion: 6 } }, input);
  const miss = capture(query, { kind: 'notCovered' }); assert.equal(requireCaptureV3(miss, input), miss);
  rejects(value, request({ ...query, expectedControlVersion: Number.MAX_SAFE_INTEGER }));
});

test('Native3旧单任务/决定/分页语义复用同一结果守卫，仍不把notCovered视为absent', () => {
  const query = { kind: 'task' as const, taskId: 'task-one' }, input = request(query);
  const value = capture(query, { kind: 'task', summary: summary(query.taskId) });
  assert.equal(requireCaptureV3(value, input), value);
  rejects(capture(query, { kind: 'task', summary: summary('wrong') }), input);
  const miss = capture(query, { kind: 'notCovered' }); assert.equal(requireCaptureV3(miss, input), miss);
  const page = request({ kind: 'taskGoal', taskId: query.taskId, goalRevision: 1, section: 'title', offset: 129, limit: 1 });
  rejects(capture(page.query, { kind: 'notCovered' }), page);
});

test('Native3完整8摘要的Unicode/控制字元包络和MCP双投影保持独立有限预算', () => {
  for (const character of ['\u{1F680}', '\0', '\\', '"', '\n']) {
    const text = character.repeat(128), query = { kind: 'tasks' as const };
    const input = { ...request(query), environmentId: 'e'.repeat(64), profileId: 'p'.repeat(64),
      expectedAuthorityEpoch: Number.MAX_SAFE_INTEGER, expectedGrantEpoch: Number.MAX_SAFE_INTEGER };
    const value: NativeHistoryCaptureV3 = { ...capture(query, { kind: 'tasks', tasks: [] }), environmentId: input.environmentId,
      profileId: input.profileId, authorizationRecordRevision: Number.MAX_SAFE_INTEGER,
      authorityEpoch: Number.MAX_SAFE_INTEGER, grantEpoch: Number.MAX_SAFE_INTEGER,
      source: { ...source, environmentId: input.environmentId, storeId: text, producerInstanceId: text, producerVersion: character.repeat(64) },
      result: { kind: 'tasks', tasks: Array.from({ length: 8 }, (_, index) => ({ ...summary(String(index) + character.repeat(127), index + 2),
        cityId: text, title: text, goalRevision: Number.MAX_SAFE_INTEGER, controlVersion: Number.MAX_SAFE_INTEGER,
        owner: { profileId: 'o'.repeat(64), profileInstanceId: historicalOwner, authorityEpoch: Number.MAX_SAFE_INTEGER, grantEpoch: Number.MAX_SAFE_INTEGER } })) } };
    assert.equal(requireCaptureV3(value, input), value);
    // This is a conservative schema-bound example, not a claim that a CP2
    // reducer could reach maximum revisions with only eight creation rows.
    const encoded = JSON.stringify(value).replace(/[\u0080-\uFFFF]/g, item => '\\u' + item.charCodeAt(0).toString(16).padStart(4, '0'));
    assert.ok(Buffer.byteLength(encoded) <= nativeHistoryLimits.Version3MaximumResponseBytes);
    if (character === '\u{1F680}') assert.ok(Buffer.byteLength(encoded) > nativeHistoryLimits.Version2MaximumResponseBytes);
    const envelope = { jsonrpc: '2.0', id: '\0'.repeat(128), result: {
      structuredContent: value, content: [{ type: 'text', text: JSON.stringify(value) }], _meta: { note: '\\'.repeat(2048) },
    } };
    measureJson(envelope, probeLimits);
    const bytes = Buffer.from(JSON.stringify(envelope));
    assert.ok(bytes.length <= 65536); checkRawJson(bytes, probeLimits);
  }
  assert.equal(nativeHistoryLimits.Version1MaximumResponseBytes, 32768);
  assert.equal(nativeHistoryLimits.Version2MaximumResponseBytes, 32768);
  assert.equal(nativeHistoryLimits.MaximumRequestBytes, 4096);
});
