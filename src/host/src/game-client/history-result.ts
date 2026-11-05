import { requireContract, schemaIds, type HistoryRecord, type HistorySource, type NativeHistoryCapture,
  type OperationLookupV3, type OperationLookupV4, type OperationQueryV3, type OperationSelector } from '@skylineslink/contracts';
import { GameClientError } from './game-client.ts';
import { isLowerHex256 } from './auth-proof.ts';

export interface ConnectionSubject {
  readonly environmentId: string;
  readonly profileId: string;
  readonly authorityEpoch: number;
  readonly grantEpoch: number;
  /** Native1 has no incarnation field; only current trusted captures may supply it. */
  readonly profileInstanceId?: string;
}

/** Legacy epochs must match; a missing incarnation is not proof of its value. */
export function compatibleSubject(a: ConnectionSubject, b: ConnectionSubject): boolean {
  return a.environmentId === b.environmentId && a.profileId === b.profileId
    && a.authorityEpoch === b.authorityEpoch && a.grantEpoch === b.grantEpoch
    && (a.profileInstanceId === undefined || b.profileInstanceId === undefined || a.profileInstanceId === b.profileInstanceId);
}

export function copySelector(value: OperationSelector): OperationSelector {
  return value.kind === 'operationId' ? { kind: 'operationId', operationId: value.operationId }
    : { kind: 'submissionKey', submissionKey: { ...value.submissionKey } };
}

export function sameSelector(a: OperationSelector, b: OperationSelector): boolean {
  return a.kind === 'operationId' ? b.kind === 'operationId' && a.operationId === b.operationId
    : b.kind === 'submissionKey' && a.submissionKey.writeContextId === b.submissionKey.writeContextId
      && a.submissionKey.requestId === b.submissionKey.requestId;
}

const invalid = () => new GameClientError('INVALID_HISTORY_RESULT', '历史来源或查询关联未满足契约。');
const generation = (value: string): boolean => /^(0|[1-9][0-9]{0,18})$/.test(value) && BigInt(value) <= 9223372036854775807n;

/** Validate retained facts and associations; never derive a new operation outcome. */
export function requireHistorySelection(source: Pick<HistorySource, 'environmentId' | 'producerCoreManifestSha256' | 'throughSequence'>,
  record: HistoryRecord | null, selector: OperationSelector, environment?: string): void {
  if (environment !== undefined && source.environmentId !== environment || !isLowerHex256(source.producerCoreManifestSha256)) throw invalid();
  if (!record) return;
  if (record.terminalSequence > source.throughSequence || record.load.isObservationCurrent
    || record.sourceContext.instanceId !== record.originInstanceId || !generation(record.sourceContext.generation)
    || !(record.sourceContext.gameState === 'ready'
      || record.sourceContext.gameState === 'menu' && record.sourceContext.cityId === null)) throw invalid();
  const observed = record.load.observationContext;
  if (observed !== null && (observed.instanceId !== record.originInstanceId || observed.gameState !== 'ready'
    || !generation(observed.generation))) throw invalid();
  if (selector.kind === 'operationId' ? record.operationId !== selector.operationId
    : record.submissionKey.writeContextId !== selector.submissionKey.writeContextId
      || record.submissionKey.requestId !== selector.submissionKey.requestId) throw invalid();
}

export function requireCapture(value: unknown, selection: { environment: string; profile: string }, selector: OperationSelector): NativeHistoryCapture {
  try { requireContract(schemaIds.NativeHistoryCapture, value); } catch { throw invalid(); }
  const capture = value as NativeHistoryCapture;
  if (capture.environmentId !== selection.environment || capture.profileId !== selection.profile
    || capture.authorityEpoch > capture.authorizationRecordRevision || capture.grantEpoch > capture.authorizationRecordRevision) throw invalid();
  requireHistorySelection(capture.source, capture.record, selector, selection.environment);
  return capture;
}

export function historyAge(publishedAtUnixMs: number, now: number): number | null {
  const age = now - publishedAtUnixMs;
  return Number.isSafeInteger(now) && Number.isSafeInteger(age) && age >= 0 ? age : null;
}

export function offlineLookup(query: OperationQueryV3, capture: NativeHistoryCapture, now: number): OperationLookupV3 {
  return { selector: copySelector(query.selector), result: { kind: 'history', access: 'offlineLease', source: capture.source,
    ageMs: historyAge(capture.source.publishedAtUnixMs, now), integrity: 'sha256Verified', lookup: capture.record === null
      ? { kind: 'unavailable', reason: 'HISTORY_NOT_COVERED' } : { kind: 'found', record: capture.record } } };
}

interface LookupExpectation { environment?: string; instanceId?: string; access?: 'activeAuthority' | 'offlineLease' }

export function requireOperationLookup(query: OperationQueryV3, value: unknown, expected: LookupExpectation = {}): OperationLookupV3 {
  return requireLookup(schemaIds.OperationLookupV3, query, value, expected) as OperationLookupV3;
}

export function requireOperationLookupV4(query: OperationQueryV3, value: unknown, expected: LookupExpectation = {}): OperationLookupV4 {
  return requireLookup(schemaIds.OperationLookupV4, query, value, expected);
}

function requireLookup(schemaId: string, query: OperationQueryV3, value: unknown, expected: LookupExpectation): OperationLookupV4 {
  const invalidLookup = () => new GameClientError('INVALID_GAME_RESULT', '操作查询响应或关联未满足当前契约。');
  try { requireContract(schemaId, value); } catch { throw invalidLookup(); }
  const result = value as OperationLookupV4;
  if (!sameSelector(query.selector, result.selector) || result.result.kind !== (query.source ?? 'live')) throw invalidLookup();
  if (result.result.kind === 'history') {
    if (expected.access !== undefined && result.result.access !== expected.access) throw invalid();
    requireHistorySelection(result.result.source, result.result.lookup.kind === 'found' ? result.result.lookup.record : null, query.selector, expected.environment);
  } else {
    if (expected.instanceId !== undefined && result.result.instanceId !== expected.instanceId) throw invalidLookup();
    const lookup = result.result.lookup;
    if (query.selector.kind === 'operationId' && (lookup.kind === 'expired' || lookup.kind === 'unavailable' && lookup.reason === 'CONTEXT_UNKNOWN'
      || (lookup.kind === 'found' || lookup.kind === 'pending') && lookup.operationId !== query.selector.operationId)) throw invalidLookup();
  }
  return result;
}
