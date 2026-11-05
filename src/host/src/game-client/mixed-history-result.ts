import { requireContract, schemaIds, type NativeHistoryCaptureV2, type NativeHistoryRequestV2,
  type NativeHistoryCaptureV3, type NativeHistoryRequestV3, type NativeHistoryQueryV3,
  type TaskHistorySummary, type TaskHistoryDecisionReceipt } from '@skylineslink/contracts';
import { isDeploymentIdentifier, canonicalEnvironment } from '../shared/identifiers.ts';
import { isLowerHex256 } from './auth-proof.ts';
import { GameClientError } from './game-client.ts';
import { requireHistorySelection, sameSelector } from './history-result.ts';

const invalid = () => new GameClientError('INVALID_HISTORY_RESULT', '历史来源或查询关联未满足契约。');
const incarnation = (value: string): boolean => /^[a-f0-9]{32}$/.test(value);
const generation = (value: string): boolean => /^(0|[1-9][0-9]{0,18})$/.test(value) && BigInt(value) <= 9223372036854775807n;

function sameKey(a: { writeContextId: string; requestId: string }, b: { writeContextId: string; requestId: string }): boolean {
  return a.writeContextId === b.writeContextId && a.requestId === b.requestId;
}

function sameQuery(a: NativeHistoryQueryV3, b: NativeHistoryQueryV3): boolean {
  switch (a.kind) {
    case 'operation': return b.kind === 'operation' && sameSelector(a.selector, b.selector);
    case 'task': return b.kind === 'task' && a.taskId === b.taskId;
    case 'taskDecision': return b.kind === 'taskDecision' && sameKey(a.submissionKey, b.submissionKey);
    case 'taskGoal': return b.kind === 'taskGoal' && a.taskId === b.taskId && a.goalRevision === b.goalRevision
      && a.section === b.section && a.offset === b.offset && a.limit === b.limit;
    case 'tasks': return b.kind === 'tasks';
    case 'taskStopDecision': return b.kind === 'taskStopDecision' && a.taskId === b.taskId
      && a.expectedControlVersion === b.expectedControlVersion;
  }
}

/** Check one bounded Native selection; the Native owner validates the complete CP2. */
export function requireCaptureV2(value: unknown, request: NativeHistoryRequestV2): NativeHistoryCaptureV2 {
  try {
    requireContract(schemaIds.NativeHistoryRequestV2, request);
    requireContract(schemaIds.NativeHistoryCaptureV2, value);
    if (canonicalEnvironment(request.environmentId) !== request.environmentId || !isDeploymentIdentifier(request.profileId)) throw invalid();
  } catch { throw invalid(); }
  return requireMixedSelection(value as NativeHistoryCaptureV2, request);
}

export function requireCaptureV3(value: unknown, request: NativeHistoryRequestV3): NativeHistoryCaptureV3 {
  try {
    requireContract(schemaIds.NativeHistoryRequestV3, request);
    requireContract(schemaIds.NativeHistoryCaptureV3, value);
    if (canonicalEnvironment(request.environmentId) !== request.environmentId || !isDeploymentIdentifier(request.profileId)) throw invalid();
  } catch { throw invalid(); }
  return requireMixedSelection(value as NativeHistoryCaptureV3, request);
}

function requireSummary(summary: TaskHistorySummary, sequence: (number: number) => boolean): void {
  if (!isDeploymentIdentifier(summary.owner.profileId) || !incarnation(summary.owner.profileInstanceId) ||
    !sequence(summary.createdSequence) || !sequence(summary.lastDecisionSequence) || summary.lastDecisionSequence < summary.createdSequence)
    throw invalid();
}

function requireReceipt(receipt: TaskHistoryDecisionReceipt, sequence: (number: number) => boolean): void {
  const decision = receipt.decision, context = decision.source;
  if (!sequence(receipt.sequence) || !isDeploymentIdentifier(decision.actor.profileId) || !incarnation(decision.actor.profileInstanceId) ||
    !isLowerHex256(decision.commandHash) || !generation(context.generation) ||
    context.gameState !== 'ready' && context.cityId !== null ||
    (receipt.decisionKind === 'stop' ? receipt.goalRevision !== null
      : receipt.goalRevision === null || context.gameState !== 'ready' || context.cityId === null) ||
    receipt.decisionKind === 'create' && receipt.goalRevision !== 1) throw invalid();
}

function requireMixedSelection<T extends NativeHistoryCaptureV2 | NativeHistoryCaptureV3>(capture: T,
  request: NativeHistoryRequestV2 | NativeHistoryRequestV3): T {
  const query = request.query;
  const expectations = [request.expectedAuthorityEpoch, request.expectedGrantEpoch, request.expectedProfileInstanceId];
  if (!(expectations.every(item => item === null) || expectations.every(item => item !== null)) ||
    request.expectedProfileInstanceId !== null && !incarnation(request.expectedProfileInstanceId) ||
    capture.environmentId !== request.environmentId || capture.profileId !== request.profileId ||
    !incarnation(capture.profileInstanceId) || capture.authorityEpoch > capture.authorizationRecordRevision ||
    capture.grantEpoch > capture.authorizationRecordRevision ||
    request.expectedAuthorityEpoch !== null && (capture.authorityEpoch !== request.expectedAuthorityEpoch ||
      capture.grantEpoch !== request.expectedGrantEpoch || capture.profileInstanceId !== request.expectedProfileInstanceId) ||
    !sameQuery(query, capture.query)) throw invalid();
  const source = capture.source;
  if (source.environmentId !== request.environmentId || !isLowerHex256(source.producerCoreManifestSha256) ||
    source.throughSequence > 129 || source.throughBytes < 45 * source.throughSequence) throw invalid();
  // The schema bounds each section jointly; enforce its narrower semantic
  // limits even for notCovered so an invalid page cannot pass via a miss.
  if (query.kind === 'taskGoal') {
    const items = query.section === 'constraintsText' || query.section === 'acceptanceText';
    const maximum = items ? 16 : query.section === 'title' ? 128 : 2048;
    if (query.offset > maximum || query.limit > (items ? 4 : 512)) throw invalid();
  }
  const result = capture.result;
  if (result.kind === 'notCovered') { if (query.kind === 'tasks') throw invalid(); return capture; }
  const sequence = (number: number): boolean => number > 1 && number <= source.throughSequence;
  if (query.kind === 'operation' && result.kind === 'operation') {
    if (!sequence(result.record.terminalSequence)) throw invalid();
    requireHistorySelection(source, result.record, query.selector, request.environmentId);
    return capture;
  }
  if (query.kind === 'task' && result.kind === 'task') {
    const summary = result.summary;
    if (summary.taskId !== query.taskId) throw invalid();
    requireSummary(summary, sequence);
    // Read access does not require the historical owner to be this reader.
    return capture;
  }
  if (query.kind === 'taskDecision' && result.kind === 'taskDecision') {
    const receipt = result.receipt;
    if (!sameKey(receipt.decision.submissionKey, query.submissionKey)) throw invalid();
    requireReceipt(receipt, sequence);
    return capture;
  }
  if (query.kind === 'taskStopDecision' && result.kind === 'taskDecision') {
    const receipt = result.receipt;
    if (receipt.taskId !== query.taskId || receipt.decisionKind !== 'stop' ||
      query.expectedControlVersion >= Number.MAX_SAFE_INTEGER || receipt.controlVersion !== query.expectedControlVersion + 1) throw invalid();
    requireReceipt(receipt, sequence);
    return capture;
  }
  if (query.kind === 'tasks' && result.kind === 'tasks') {
    let previous: string | undefined;
    const owners = new Map<number, string>();
    for (const summary of result.tasks) {
      requireSummary(summary, sequence);
      if (previous !== undefined && previous >= summary.taskId) throw invalid();
      for (const number of [summary.createdSequence, summary.lastDecisionSequence]) {
        if (owners.has(number) && owners.get(number) !== summary.taskId) throw invalid();
        owners.set(number, summary.taskId);
      }
      previous = summary.taskId;
    }
    return capture;
  }
  if (query.kind === 'taskGoal' && result.kind === 'taskGoal') {
    const page = result.page;
    if (page.taskId !== query.taskId || page.goalRevision !== query.goalRevision || page.section !== query.section ||
      page.offset !== query.offset || !sequence(page.sequence)) throw invalid();
    const text = query.section === 'title' || query.section === 'requestText';
    if (text !== (page.kind === 'text')) throw invalid();
    const count = page.kind === 'text' ? [...page.text].length : page.items.length;
    const maximum = text ? query.section === 'title' ? 128 : 2048 : 16;
    if (count > query.limit || page.offset + count > maximum || page.nextOffset !== null &&
      (count !== query.limit || page.nextOffset !== page.offset + count || page.nextOffset >= maximum)) throw invalid();
    return capture;
  }
  throw invalid();
}
