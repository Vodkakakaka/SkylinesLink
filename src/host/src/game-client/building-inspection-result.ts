import { requireContract, schemaIds, type BuildingInspection, type BuildingInspectionQuery } from '@skylineslink/contracts';
import { GameClientError } from './game-client.ts';
import { measureJson } from '../shared/json-budget.ts';
import { probeLimits } from '../shared/limits.ts';

const invalid = () => new GameClientError('INVALID_GAME_RESULT', '建筑观察的对象、作用域或直接引用覆盖不一致，请重新读取。');
const validGeneration = (value: string) => /^[1-9][0-9]{0,18}$/.test(value) &&
  (value.length < 19 || value <= '9223372036854775807');

/** Freeze declared values before an asynchronous game call can mutate its input. */
export function copyBuildingInspectionQuery(value: unknown): BuildingInspectionQuery {
  try { requireContract(schemaIds.BuildingInspectionQuery, value); }
  catch { throw new GameClientError('INVALID_ARGUMENT', '建筑读取参数不符合契约。'); }
  const query = value as BuildingInspectionQuery;
  if (query.target.kind === 'entityRef' && !validGeneration(query.target.ref.generation))
    throw new GameClientError('INVALID_ARGUMENT', '加载代次必须是有效的正整数文本。');
  return { target: query.target.kind === 'selectedInfo' ? { kind: 'selectedInfo' } :
    { kind: 'entityRef', ref: { ...query.target.ref } }, ...(query.relationLimit === undefined ? {} : { relationLimit: query.relationLimit }) };
}

export function requireBuildingInspection(query: BuildingInspectionQuery, value: unknown, expectedInstanceId?: string): BuildingInspection {
  try {
    measureJson(value, probeLimits);
    requireContract(schemaIds.BuildingInspection, value);
    if (Buffer.byteLength(JSON.stringify(value), 'utf8') > 16 * 1024) throw invalid();
  } catch { throw invalid(); }
  const result = value as BuildingInspection;
  if (result.kind === 'unavailable') return result;
  if (!validGeneration(result.scope.generation) || expectedInstanceId !== undefined && result.scope.instanceId !== expectedInstanceId ||
    result.object.index === 0 && result.object.version === 0) throw invalid();
  if (query.target.kind === 'entityRef') {
    const reference = query.target.ref;
    if (reference.instanceId !== result.scope.instanceId || reference.generation !== result.scope.generation ||
      reference.index !== result.object.index || reference.version !== result.object.version) throw invalid();
  }
  if (result.kind === 'unsupported') return result;
  if (result.selectionSource !== query.target.kind || result.relationLimit !== (query.relationLimit ?? 8) ||
    result.capture.endedAtUnixMs < result.capture.startedAtUnixMs || result.prefab.ref.index === 0 && result.prefab.ref.version === 0) throw invalid();
  for (const field of [result.prefab.name, result.prefab.nativeId, result.prefab.baseConstructionCost, result.prefab.locked])
    if ((field.value === null) !== (field.reason !== null)) throw invalid();
  const lot = result.prefab.lotCells;
  if ((lot.width === null) !== (lot.depth === null) || (lot.width === null) !== (lot.reason !== null) ||
    result.building.roadEdge === null && result.building.roadEdgeExists ||
    result.building.roadEdge?.index === 0 && result.building.roadEdge.version === 0) throw invalid();
  const bounds = result.prefab.prefabLocalBounds;
  if ((bounds.min === null) !== (bounds.max === null) || (bounds.min === null) !== (bounds.reason !== null) ||
    bounds.min !== null && bounds.max !== null && bounds.min.some((value, index) => value > bounds.max![index]!)) throw invalid();
  for (const [kind, group] of Object.entries(result.relations)) {
    if (group.returned !== group.entries.length || group.returned > result.relationLimit) throw invalid();
    if (group.present === true) {
      if (group.length === null || group.returned !== Math.min(group.length, result.relationLimit) ||
        group.complete !== (group.returned === group.length) || group.reason !== (group.complete ? null : 'LIMIT')) throw invalid();
    } else if (group.length !== null || group.returned !== 0 || group.complete ||
      group.reason !== (group.present === false ? 'MISSING_BUFFER' : 'READ_FAILED')) throw invalid();
    for (const entry of group.entries) {
      if (entry.ref === null && entry.exists || entry.ref?.index === 0 && entry.ref.version === 0 ||
        (entry.optionMask === null) !== (kind !== 'installedUpgrades')) throw invalid();
    }
  }
  return result;
}
