import { requireContract, schemaIds, type CityOverviewV2 } from '@skylineslink/contracts';
import { GameClientError } from './game-client.ts';
import { measureJson } from '../shared/json-budget.ts';
import { probeLimits } from '../shared/limits.ts';

const invalid = () => new GameClientError('INVALID_GAME_RESULT', '城市概览的作用域、采集窗口或观测字段不一致，请重新读取。');

/** Keep unavailable observations distinct from actual zero values at both Host boundaries. */
export function requireCityOverview(value: unknown, expectedInstanceId?: string): CityOverviewV2 {
  try {
    measureJson(value, probeLimits);
    requireContract(schemaIds.CityOverviewV2, value);
  } catch { throw invalid(); }
  const result = value as CityOverviewV2;
  if (result.kind === 'unavailable') return result;
  const generation = result.scope.generation;
  if (expectedInstanceId !== undefined && result.scope.instanceId !== expectedInstanceId
    || !/^[1-9][0-9]{0,18}$/.test(generation)
    || generation.length === 19 && generation > '9223372036854775807'
    || result.capture.startedAtUnixMs > result.capture.endedAtUnixMs) throw invalid();
  for (const observation of [result.population, result.treasury]) {
    if (observation.status === 'observed'
      ? observation.value === null || observation.reason !== null
      : observation.value !== null || observation.reason === null) throw invalid();
  }
  if ((result.treasury.status === 'unavailable') !== (result.treasury.unlimited === null)) throw invalid();
  const simulation = result.simulation;
  if (simulation.status === 'observed') {
    if (!nativeSpeed(simulation.selectedSpeed) || !nativeSpeed(simulation.smoothSpeed)
      || simulation.selectedPaused !== (simulation.selectedSpeed === 0) || simulation.reason !== null) throw invalid();
  } else if (simulation.selectedSpeed !== null || simulation.selectedPaused !== null
    || simulation.smoothSpeed !== null || simulation.reason === null) throw invalid();
  const clock = result.clock;
  const clockData = [clock.year, clock.dayFraction, clock.yearFraction, clock.daysPerYear,
    clock.firstFrame, clock.epoch, clock.atSimulationFrame];
  if (clock.status === 'observed') {
    if (clock.year === null || clock.epoch === null || clockData.some(field => field === null) || clock.reason !== null
      || !fraction(clock.dayFraction) || !fraction(clock.yearFraction)
      || clock.year < clock.epoch.startingYear) throw invalid();
    const start = result.capture.simulationFrameStart, end = result.capture.simulationFrameEnd;
    const frame = clock.atSimulationFrame!;
    if (start <= end ? frame < start || frame > end : frame < start && frame > end) throw invalid();
  } else if (clockData.some(field => field !== null) || clock.reason === null) throw invalid();
  return result;
}

function nativeSpeed(value: number | null): value is number {
  return value !== null && Number.isFinite(value) && value >= 0 && value <= 3.4028234663852886e38;
}

function fraction(value: number | null): value is number {
  return value !== null && Number.isFinite(value) && value >= 0 && value < 1;
}
