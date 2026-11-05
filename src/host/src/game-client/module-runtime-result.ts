import { requireContract, schemaIds, type ModuleRuntimeQuery, type ModuleRuntimePage, type ModuleRuntimeRow } from '@skylineslink/contracts';
import { GameClientError } from './game-client.ts';
import { measureJson } from '../shared/json-budget.ts';
import { probeLimits } from '../shared/limits.ts';

const maximumRawResultBytes = 24 * 1024;
const retentionMilliseconds = 300_000;
const stateLabels = ['unknown', 'loaded', 'disposed', 'notModWarning', 'notUniqueWarning', 'generalError',
  'missingDependenciesError', 'loadAssemblyError', 'loadAssemblyReferenceError'] as const;
const invalid = () => new GameClientError('INVALID_GAME_RESULT', '模块观察的身份、覆盖或字段说明不一致，请重新读取。');

/** Keep the original selector independent of caller-owned objects and async IO. */
export function copyModuleRuntimeQuery(query: ModuleRuntimeQuery): ModuleRuntimeQuery {
  return query.kind === 'capture' ? { kind: 'capture' }
    : { kind: 'page', snapshotId: query.snapshotId, offset: query.offset };
}

/** Schema checks shape; this boundary checks the meaning of the same captured page. */
export function requireModuleRuntimePage(query: ModuleRuntimeQuery, value: unknown, expectedInstanceId?: string): ModuleRuntimePage {
  try {
    // Bound plain JSON before validation/stringification; never invoke an output getter.
    measureJson(value, probeLimits);
    requireContract(schemaIds.ModuleRuntimePage, value);
    if (Buffer.byteLength(JSON.stringify(value), 'utf8') > maximumRawResultBytes) throw invalid();
  } catch { throw invalid(); }
  const result = value as ModuleRuntimePage;
  if (result.kind === 'unavailable') {
    if (query.kind === 'capture' && (result.reason === 'SNAPSHOT_UNAVAILABLE' || result.reason === 'PAGE_OUT_OF_RANGE')) throw invalid();
    return result;
  }
  const snapshot = result.snapshot, coverage = snapshot.coverage;
  if (expectedInstanceId !== undefined && snapshot.instanceId !== expectedInstanceId
    || snapshot.captureStartedAtUnixMs > snapshot.captureEndedAtUnixMs
    || snapshot.expiresAtUnixMs !== snapshot.captureEndedAtUnixMs + retentionMilliseconds
    || (snapshot.gameVersion === null) !== (snapshot.gameVersionIssue !== null)) throw invalid();
  if (coverage.complete) {
    if (coverage.reason !== null || coverage.totalCount !== coverage.observedCount || coverage.visited !== coverage.observedCount) throw invalid();
  } else if (coverage.totalCount !== null || coverage.reason === null
    || coverage.visited < coverage.observedCount || coverage.visited > coverage.observedCount + 1
    || coverage.reason === 'ENTRY_LIMIT' && (coverage.observedCount !== 128 || coverage.visited !== 129)) throw invalid();
  if (query.kind === 'capture' ? result.offset !== 0 : result.snapshot.snapshotId !== query.snapshotId || result.offset !== query.offset) throw invalid();
  const count = coverage.observedCount;
  const expectedReturned = Math.min(16, count - result.offset);
  if (result.offset > count || result.returnedCount !== result.entries.length || result.returnedCount !== expectedReturned
    || result.nextOffset !== (result.offset + result.returnedCount < count ? result.offset + result.returnedCount : null)) throw invalid();
  for (const [index, row] of result.entries.entries()) {
    if (row.ordinal !== result.offset + index) throw invalid();
    validateRow(row);
  }
  return result;
}

function validateRow(row: ModuleRuntimeRow): void {
  const issues = new Map(row.fieldIssues.map(issue => [issue.field, issue.reason]));
  if (issues.size !== row.fieldIssues.length) throw invalid();
  if (row.assetGuid === '0'.repeat(32)) throw invalid();
  const state = row.loaderStateCode;
  if (row.loaderState !== (state === null ? 'unavailable' : stateLabels[state] ?? 'unrecognized')) throw invalid();
  if (issues.has('entry')) {
    if (issues.size !== 1 || issues.get('entry') !== 'missing' || row.assetGuid !== null || row.isMod !== null
      || state !== null || row.assemblyPresent !== null || row.assemblyName !== null || row.assemblyVersion !== null
      || row.instanceCount !== null || row.hasLoadError !== null || row.databaseKind !== 'unknown') throw invalid();
    return;
  }
  // The registered ModInfo exists even when its asset is missing.
  if (state === null || row.hasLoadError === null || (row.instanceCount === null) !== issues.has('instanceCount')) throw invalid();
  if (issues.has('asset')) {
    if (issues.get('asset') !== 'missing' || row.assetGuid !== null || row.isMod !== null || row.assemblyPresent !== null
      || row.assemblyName !== null || row.assemblyVersion !== null || row.databaseKind !== 'unknown'
      || [...issues.keys()].some(field => field !== 'asset' && field !== 'instanceCount')) throw invalid();
    return;
  }
  if (row.isMod === null || row.assemblyPresent === null || (row.assetGuid === null) !== issues.has('assetGuid')
    || (row.databaseKind === 'unknown') !== issues.has('databaseKind')) throw invalid();
  if (row.assemblyPresent) {
    if ((row.assemblyName === null) !== issues.has('assemblyName') || (row.assemblyVersion === null) !== issues.has('assemblyVersion')) throw invalid();
  } else if (row.assemblyName !== null || row.assemblyVersion !== null || issues.has('assemblyName') || issues.has('assemblyVersion')) throw invalid();
}
