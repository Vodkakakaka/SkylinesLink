import type { NativeHistoryCapture, NativeHistoryCaptureV2, NativeHistoryRequestV2, NativeHistoryCaptureV3, NativeHistoryRequestV3, OperationQueryV3, OperationLookupV3 } from '../../contracts/ts/index.ts';

export const historyCapture: NativeHistoryCapture = {
  formatVersion: 1, environmentId: 'fixture', profileId: 'reader', authorizationRecordRevision: 3, authorityEpoch: 2, grantEpoch: 3,
  source: { formatVersion: 1, productId: 'skylineslink', environmentId: 'fixture', storeId: 'store-1', throughSequence: 5,
    producerVersion: '0.1.0-dev.0', producerInstanceId: 'previous-producer', producerCoreManifestSha256: 'b'.repeat(64),
    journalSchemaRevision: 2, publishedAtUnixMs: 100, selection: 'durableTerminalOnly' },
  record: { terminalSequence: 5, submissionKey: { writeContextId: 'ctx-1', requestId: 'request-1' }, operationId: 'operation-1',
    originInstanceId: 'origin-1', sourceContext: { instanceId: 'origin-1', generation: '1', gameState: 'menu', cityId: null },
    state: 'succeeded', mayHaveApplied: true, durability: 'durable', load: { physicalTaskState: 'ranToCompletion', loadReturnValue: true,
      targetMatch: 'different', isObservationCurrent: false, correlatedTargetReady: true, historicalCompletionProven: true,
      observationEnd: 'ownerStopped', observationContext: { instanceId: 'origin-1', generation: '2', gameState: 'ready', cityId: 'city-1' } } },
};
export const historyQuery: OperationQueryV3 = { source: 'history', selector: { kind: 'operationId', operationId: 'operation-1' } };
export const historyCaptureV2: NativeHistoryCaptureV2 = {
  formatVersion: 2, environmentId: 'fixture', profileId: 'reader', profileInstanceId: 'a'.repeat(32),
  authorizationRecordRevision: 3, authorityEpoch: 2, grantEpoch: 3,
  source: { ...historyCapture.source, formatVersion: 2, storeId: 'd'.repeat(32), journalSchemaRevision: 3,
    selection: 'confirmedTaskMetadataAndOperationTerminals', throughBytes: 2048 },
  query: { kind: 'task', taskId: 'task-1' },
  result: { kind: 'task', summary: { taskId: 'task-1', cityId: 'city-1', title: 'Stated goal', goalRevision: 2, controlVersion: 2,
    isStopped: true, createdSequence: 2, lastDecisionSequence: 5,
    owner: { profileId: 'planner', profileInstanceId: 'b'.repeat(32), authorityEpoch: 1, grantEpoch: 1 } } },
};
export const historyRequestV2: NativeHistoryRequestV2 = { formatVersion: 2, environmentId: 'fixture', profileId: 'reader',
  expectedAuthorityEpoch: null, expectedGrantEpoch: null, expectedProfileInstanceId: null, query: historyCaptureV2.query };
export const historyCaptureV3: NativeHistoryCaptureV3 = { ...historyCaptureV2, formatVersion: 3, query: { kind: 'tasks' }, result: { kind: 'tasks', tasks: [] } };
export const historyRequestV3: NativeHistoryRequestV3 = { ...historyRequestV2, formatVersion: 3, query: historyCaptureV3.query };
export function activeLookup(query: OperationQueryV3): OperationLookupV3 {
  return query.source === 'history' ? { selector: query.selector, result: { kind: 'history', access: 'activeAuthority', source: historyCapture.source,
    ageMs: 100, integrity: 'sha256Verified', lookup: { kind: 'found', record: historyCapture.record! } } }
    : { selector: query.selector, result: { kind: 'live', instanceId: 'live-instance', lookup: { kind: 'absent' } } };
}
