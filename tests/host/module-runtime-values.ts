import type { ModuleRuntimePage, ModuleRuntimeRow } from '../../contracts/ts/index.ts';

export const moduleSnapshotId = '0123456789abcdef0123456789abcdef';
export const moduleInstanceId = 'module-fixture-instance';
export type ModulePage = Extract<ModuleRuntimePage, { kind: 'page' }>;

export function moduleRow(ordinal = 0, patch: Partial<ModuleRuntimeRow> = {}): ModuleRuntimeRow {
  return { ordinal, assetGuid: 'a'.repeat(32), isMod: true, loaderStateCode: 1, loaderState: 'loaded',
    assemblyPresent: true, assemblyName: 'Fixture.Module', assemblyVersion: '1.2.3.4',
    instanceCount: 1, hasLoadError: false, databaseKind: 'user', fieldIssues: [], ...patch };
}
export function modulePage(rows: readonly ModuleRuntimeRow[] = [moduleRow()], observedCount = rows.length,
  offset = 0): ModulePage {
  return { kind: 'page', snapshot: { snapshotId: moduleSnapshotId, instanceId: moduleInstanceId,
    captureStartedAtUnixMs: 1000, captureEndedAtUnixMs: 1005, expiresAtUnixMs: 301005,
    gameVersion: '1.6.2f1', gameVersionIssue: null, firstInitializationComplete: true, restartRequired: false,
    coverage: { complete: true, reason: null, visited: observedCount, observedCount, totalCount: observedCount } },
    offset, entries: rows, returnedCount: rows.length, nextOffset: offset + rows.length < observedCount ? offset + rows.length : null };
}

/** Schema-valid simultaneous field maxima; not a claim about a particular installed module. */
export function worstModulePage(character: string): ModulePage {
  const base = modulePage(Array.from({ length: 16 }, (_, ordinal) => moduleRow(ordinal, {
    assemblyName: character.repeat(128), assemblyVersion: '2147483647.2147483647.2147483647',
    loaderStateCode: 8, loaderState: 'loadAssemblyReferenceError', instanceCount: 2147483647,
    hasLoadError: true, databaseKind: 'paradoxMods',
  })), 128);
  return { ...base, snapshot: { ...base.snapshot, instanceId: character.repeat(128), gameVersion: character.repeat(64),
    firstInitializationComplete: false, restartRequired: true,
    coverage: { complete: false, reason: 'ENTRY_LIMIT', visited: 129, observedCount: 128, totalCount: null } } };
}
