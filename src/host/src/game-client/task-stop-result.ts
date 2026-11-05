import { requireContract, schemaIds, type TaskStopSelector, type TaskStopResult, type TaskStopDecisionQuery,
  type TaskStopDecisionLookup, type NativeHistoryCaptureV3 } from '@skylineslink/contracts';
import { GameClientError } from './game-client.ts';
import { historyAge } from './history-result.ts';
import { isDeploymentIdentifier } from '../shared/identifiers.ts';
import { isLowerHex256 } from './auth-proof.ts';

export function copyStopSelector(value: TaskStopSelector): TaskStopSelector {
  return { taskId: value.taskId, expectedControlVersion: value.expectedControlVersion };
}
function same(a: TaskStopSelector, b: TaskStopSelector): boolean {
  return a.taskId === b.taskId && a.expectedControlVersion === b.expectedControlVersion;
}
const invalid = (write = false) => new GameClientError('INVALID_GAME_RESULT', '停止决定响应与原任务或控制版本不一致，请按原身份查询。', write);

export function requireStopResult(query: TaskStopSelector, value: unknown): TaskStopResult {
  try { requireContract(schemaIds.TaskStopResult, value); } catch { throw invalid(true); }
  const result = value as TaskStopResult;
  if (!same(query, result.selector)) throw invalid(true);
  const lookup = result.lookup, receipt = lookup.receipt;
  if (receipt && (receipt.controlVersion !== query.expectedControlVersion + 1 || !receipt.stopRequested
    || receipt.journal !== null && !receipt.persistenceAttempted
    || lookup.kind === 'pending' && (receipt.journal !== null || !receipt.persistenceAttempted)
    || lookup.kind === 'found' && (receipt.journal === null || !receipt.persistenceAttempted))) throw invalid(true);
  return result;
}

export interface StopLookupExpectation {
  environment?: string;
  instanceId?: string;
  access?: 'activeAuthority' | 'offlineLease';
}

export function requireStopDecisionLookup(query: TaskStopDecisionQuery, value: unknown, expected: StopLookupExpectation = {}): TaskStopDecisionLookup {
  try { requireContract(schemaIds.TaskStopDecisionLookup, value); } catch { throw invalid(); }
  const result = value as TaskStopDecisionLookup;
  if (!same(query, result.selector) || result.result.kind !== (query.source ?? 'live')) throw invalid();
  const body = result.result;
  if (body.kind === 'live') {
    if (expected.instanceId !== undefined && body.instanceId !== expected.instanceId) throw invalid();
    const receipt = body.lookup.receipt;
    if (receipt) {
      validateReceipt(query, receipt, Number.MAX_SAFE_INTEGER);
      if (receipt.journal !== null && !receipt.persistenceAttempted
        || body.lookup.kind === 'found' && (receipt.journal === null || !receipt.persistenceAttempted)
        || body.lookup.kind === 'pending' && receipt.journal !== null) throw invalid();
    }
  } else {
    if (expected.access !== undefined && body.access !== expected.access || expected.environment !== undefined && body.source.environmentId !== expected.environment
      || !isLowerHex256(body.source.producerCoreManifestSha256) || body.source.throughSequence > 129
      || body.source.throughBytes < body.source.throughSequence * 45) throw invalid();
    if (body.lookup.kind === 'found') validateReceipt(query, body.lookup.receipt, body.source.throughSequence);
  }
  return result;
}

function validateReceipt(query: TaskStopSelector, receipt: { taskId: string; decisionKind: string; controlVersion: number;
  goalRevision: number | null; sequence?: number; decision: { commandHash: string; actor: { profileId: string; profileInstanceId: string };
    source: { generation: string; gameState: string; cityId: string | null } } }, maximumSequence: number): void {
  const decision = receipt.decision, source = decision.source;
  if (receipt.taskId !== query.taskId || receipt.decisionKind !== 'stop' || receipt.goalRevision !== null
    || receipt.controlVersion !== query.expectedControlVersion + 1 || !isLowerHex256(decision.commandHash)
    || !isDeploymentIdentifier(decision.actor.profileId) || !/^[a-f0-9]{32}$/.test(decision.actor.profileInstanceId)
    || !/^(0|[1-9][0-9]{0,18})$/.test(source.generation) || BigInt(source.generation) > 9223372036854775807n
    || source.gameState !== 'ready' && source.cityId !== null
    || receipt.sequence !== undefined && (receipt.sequence <= 1 || receipt.sequence > maximumSequence)) throw invalid();
}

export function offlineStopLookup(query: TaskStopDecisionQuery, capture: NativeHistoryCaptureV3, now: number): TaskStopDecisionLookup {
  const selected = capture.result;
  if (selected.kind !== 'taskDecision' && selected.kind !== 'notCovered') throw invalid();
  return requireStopDecisionLookup(query, { selector: copyStopSelector(query), result: { kind: 'history', access: 'offlineLease',
    source: capture.source, ageMs: historyAge(capture.source.publishedAtUnixMs, now), integrity: 'sha256Verified', lookup:
      selected.kind === 'notCovered' ? { kind: 'unavailable', reason: 'HISTORY_NOT_COVERED' } : { kind: 'found', receipt: selected.receipt } } },
  { environment: capture.environmentId, access: 'offlineLease' });
}
