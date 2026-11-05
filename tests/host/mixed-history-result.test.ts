import test from 'node:test';
import assert from 'node:assert/strict';
import type { NativeHistoryCaptureV2, NativeHistoryRequestV2, NativeHistoryQuery } from '../../contracts/ts/index.ts';
import { GameClientError } from '../../src/host/src/game-client/game-client.ts';
import { requireCaptureV2, requireCaptureV3 } from '../../src/host/src/game-client/mixed-history-result.ts';
import { historyCapture } from './history-values.ts';

const reader = 'a'.repeat(32), historicalOwner = 'c'.repeat(32);
const source: NativeHistoryCaptureV2['source'] = { ...historyCapture.source, formatVersion: 2,
  journalSchemaRevision: 3, selection: 'confirmedTaskMetadataAndOperationTerminals', throughSequence: 8, throughBytes: 1000 };
const request = (query: NativeHistoryQuery): NativeHistoryRequestV2 => ({ formatVersion: 2, environmentId: 'fixture', profileId: 'reader',
  expectedAuthorityEpoch: 2, expectedGrantEpoch: 3, expectedProfileInstanceId: reader, query });
const capture = (query: NativeHistoryQuery, result: NativeHistoryCaptureV2['result']): NativeHistoryCaptureV2 => ({
  formatVersion: 2, environmentId: 'fixture', profileId: 'reader', profileInstanceId: reader,
  authorizationRecordRevision: 3, authorityEpoch: 2, grantEpoch: 3, source, query, result,
});
const rejects = (value: unknown, input: NativeHistoryRequestV2) => assert.throws(() => requireCaptureV2(value, input),
  (error: unknown) => error instanceof GameClientError && error.code === 'INVALID_HISTORY_RESULT');

test('Native2有限选择关联原operation ID/key、false事实及本次来源，不伪造CP1', () => {
  const record = { ...historyCapture.record!, state: 'failed' as const, load: { ...historyCapture.record!.load,
    physicalTaskState: 'ranToCompletion' as const, loadReturnValue: false, historicalCompletionProven: false,
    correlatedTargetReady: false, targetMatch: 'unknown' as const } };
  for (const selector of [{ kind: 'operationId', operationId: record.operationId } as const,
    { kind: 'submissionKey', submissionKey: record.submissionKey } as const]) {
    const query = { kind: 'operation' as const, selector }, input = request(query), value = capture(query, { kind: 'operation', record });
    assert.equal(requireCaptureV2(value, input), value);
    assert.equal(value.result.kind === 'operation' && value.result.record.load.loadReturnValue, false);
    rejects({ ...value, result: { kind: 'operation', record: { ...record, operationId: 'other', submissionKey: { ...record.submissionKey, requestId: 'other' } } } }, input);
    rejects({ ...value, result: { kind: 'operation', record: { ...record, terminalSequence: 9 } } }, input);
    rejects({ ...value, source: { ...source, selection: 'durableTerminalOnly', journalSchemaRevision: 2 } }, input);
  }
});

test('Native2固定当前读者三值与echo，历史owner/epochs不变且不取得控制权', () => {
  const query = { kind: 'task' as const, taskId: 'task-one' }, input = request(query);
  const summary = { taskId: query.taskId, cityId: 'city-old', owner: { profileId: 'old-owner', profileInstanceId: historicalOwner,
    authorityEpoch: 1, grantEpoch: 1 }, title: 'Current goal', goalRevision: 2, controlVersion: 2, isStopped: true,
    createdSequence: 2, lastDecisionSequence: 8 };
  const value = capture(query, { kind: 'task', summary });
  assert.equal(requireCaptureV2(value, input).profileInstanceId, reader);
  assert.deepEqual(value.result.kind === 'task' && value.result.summary.owner, summary.owner);
  for (const changed of [
    { ...value, environmentId: 'other' }, { ...value, profileId: 'other' }, { ...value, profileInstanceId: historicalOwner },
    { ...value, grantEpoch: 2 }, { ...value, authorityEpoch: 1 }, { ...value, authorizationRecordRevision: 1 },
    { ...value, query: { ...query, taskId: 'other' } }, { ...value, source: { ...source, environmentId: 'other' } },
    { ...value, source: { ...source, throughBytes: 45 } }, { ...value, source: { ...source, producerCoreManifestSha256: 'B'.repeat(64) } },
    { ...value, result: { kind: 'task', summary: { ...summary, taskId: 'other' } } },
    { ...value, result: { kind: 'task', summary: { ...summary, lastDecisionSequence: 1 } } },
    { ...value, result: { kind: 'task', summary: { ...summary, owner: { ...summary.owner, profileInstanceId: 'bad' } } } },
    { ...value, result: { kind: 'notCovered', hidden: true } },
  ]) rejects(changed, input);
  rejects(value, { ...input, expectedProfileInstanceId: null });
  const unbound = { ...input, expectedAuthorityEpoch: null, expectedGrantEpoch: null, expectedProfileInstanceId: null };
  assert.equal(requireCaptureV2(value, unbound), value);
});

test('Native2原决定回执关联原key与sequence，Stop不借用当前Goal或当前城市', () => {
  const query = { kind: 'taskDecision' as const, submissionKey: { writeContextId: 'task-control', requestId: 'stop-one' } };
  const input = request(query), decision = { submissionKey: query.submissionKey,
    actor: { profileId: 'old-owner', profileInstanceId: historicalOwner, authorityEpoch: 1, grantEpoch: 1 },
    source: { instanceId: 'old-instance', generation: '10', gameState: 'unavailable' as const, cityId: null }, commandHash: 'd'.repeat(64) };
  const receipt = { taskId: 'task-one', decisionKind: 'stop' as const, goalRevision: null, controlVersion: 2, sequence: 8, decision };
  const value = capture(query, { kind: 'taskDecision', receipt });
  assert.equal(requireCaptureV2(value, input), value);
  const readyUnknown = capture(query, { kind: 'taskDecision', receipt: { ...receipt,
    decision: { ...decision, source: { ...decision.source, gameState: 'ready', cityId: null } } } });
  assert.equal(requireCaptureV2(readyUnknown, input), readyUnknown);
  const version3 = { ...readyUnknown, formatVersion: 3 as const };
  assert.equal(requireCaptureV3(version3, { ...input, formatVersion: 3 }), version3);
  for (const decisionKind of ['create', 'reviseGoal'] as const) {
    const knownCity = capture(query, { kind: 'taskDecision', receipt: { ...receipt, decisionKind,
      goalRevision: decisionKind === 'create' ? 1 : 2,
      decision: { ...decision, source: { ...decision.source, gameState: 'ready', cityId: 'historical-city' } } } });
    assert.equal(requireCaptureV2(knownCity, input), knownCity);
    if (knownCity.result.kind !== 'taskDecision') throw new Error('Expected decision selection.');
    const knownReceipt = knownCity.result.receipt;
    const unknown = capture(query, { kind: 'taskDecision', receipt: { ...knownReceipt,
      decision: { ...knownReceipt.decision, source: { ...knownReceipt.decision.source, cityId: null } } } });
    rejects(unknown, input);
    assert.throws(() => requireCaptureV3({ ...unknown, formatVersion: 3 }, { ...input, formatVersion: 3 }),
      (error: unknown) => error instanceof GameClientError && error.code === 'INVALID_HISTORY_RESULT');
  }
  for (const changed of [
    { ...receipt, goalRevision: 2 }, { ...receipt, sequence: 9 },
    { ...receipt, decision: { ...decision, submissionKey: { ...query.submissionKey, requestId: 'other' } } },
    { ...receipt, decision: { ...decision, source: { ...decision.source, generation: '01' } } },
    { ...receipt, decision: { ...decision, source: { ...decision.source, cityId: 'wrong' } } },
    { ...receipt, decisionKind: 'create', goalRevision: 1 },
  ]) rejects({ ...value, result: { kind: 'taskDecision', receipt: changed } }, input);
});

test('Native2页按标量/项核本次revision和窗口；未覆盖也不能放过非法title请求', () => {
  const query = { kind: 'taskGoal' as const, taskId: 'task-one', goalRevision: 1, section: 'title' as const, offset: 0, limit: 2 };
  const input = request(query), page = { kind: 'text' as const, taskId: query.taskId, goalRevision: 1,
    section: 'title' as const, offset: 0, nextOffset: 2, text: '😀🚆', sequence: 2 };
  const value = capture(query, { kind: 'taskGoal', page });
  assert.equal(requireCaptureV2(value, input), value);
  for (const changed of [{ ...page, goalRevision: 2 }, { ...page, section: 'requestText' }, { ...page, offset: 1 },
    { ...page, taskId: 'other' }, { ...page, text: '😀🚆x' }, { ...page, text: 'x' }, { ...page, nextOffset: 3 },
    { ...page, text: '\ud800' }, { ...page, sequence: 9 }]) rejects({ ...value, result: { kind: 'taskGoal', page: changed } }, input);
  const listQuery = { ...query, section: 'constraintsText' as const };
  const listPage = { kind: 'items' as const, taskId: query.taskId, goalRevision: 1, section: 'constraintsText' as const,
    offset: 0, nextOffset: 2, items: ['one', 'two'], sequence: 2 };
  const list = capture(listQuery, { kind: 'taskGoal', page: listPage });
  assert.equal(requireCaptureV2(list, request(listQuery)), list);
  rejects({ ...list, result: { kind: 'taskGoal', page: { ...listPage, items: ['one', 'two', 'three'] } } }, request(listQuery));
  rejects({ ...list, result: value.result }, request(listQuery));
  const missingQuery = { ...query, taskId: 'not-covered', offset: 129 };
  rejects(capture(missingQuery, { kind: 'notCovered' }), request(missingQuery));
  const validMissing = { ...missingQuery, offset: 128 };
  assert.equal(requireCaptureV2(capture(validMissing, { kind: 'notCovered' }), request(validMissing)).result.kind, 'notCovered');
});
