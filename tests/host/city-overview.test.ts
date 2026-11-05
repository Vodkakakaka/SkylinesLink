import test from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { Client, StreamableHTTPClientTransport } from '@modelcontextprotocol/client';
import type { CallToolResult } from '@modelcontextprotocol/server';
import { type CityOverviewV2, capabilities, schemaIds } from '../../contracts/ts/index.ts';
import { GameClientError } from '../../src/host/src/game-client/game-client.ts';
import { requireCityOverview } from '../../src/host/src/game-client/city-overview-result.ts';
import { DiscoveredGameClient } from '../../src/host/src/game-client/discovered-game-client.ts';
import type { PipeConnection } from '../../src/host/src/game-client/named-pipe-client.ts';
import { startHttpHost } from '../../src/host/src/mcp/transports/http.ts';

const instanceId = 'city-overview-fixture';
type Overview = Extract<CityOverviewV2, { kind: 'overview' }>;
function overview(): Overview {
  return { kind: 'overview', scope: { instanceId, generation: '2', cityId: null }, gameVersion: '1.6.2f1',
    capture: { startedAtUnixMs: 1000, endedAtUnixMs: 1001, simulationFrameStart: 40, simulationFrameEnd: 41 },
    coverage: 'city', population: { status: 'observed', value: 0, unit: 'residents',
      source: 'city-population-component', sourceUpdatedFrame: null, reason: null },
    treasury: { status: 'observed', value: -125, unlimited: false, unit: 'game-currency',
      source: 'player-money-component', sourceUpdatedFrame: null, reason: null },
    simulation: { status: 'observed', selectedSpeed: 2.5, selectedPaused: false, smoothSpeed: 1.75,
      pauseReason: null, advancingNow: null, source: 'simulation-system', sourceUpdatedFrame: null, reason: null },
    clock: { status: 'observed', year: 2023, dayFraction: 0.25, yearFraction: 0.125, daysPerYear: 12,
      ticksPerDay: 262144, firstFrame: 7, epoch: { startingYear: 2023, startingMonth: 1, startingHour: 8, startingMinute: 0 },
      atSimulationFrame: 41, source: 'native-time-functions', sourceUpdatedFrame: null, reason: null } };
}
function missing(): Overview {
  const value = overview();
  return { ...value,
    population: { ...value.population, status: 'unavailable', value: null, reason: 'MISSING_COMPONENT' },
    treasury: { ...value.treasury, status: 'unavailable', value: null, unlimited: null, reason: 'INVALID_VALUE' } };
}
function missingClock(): Overview {
  const value = overview();
  return { ...value, clock: { ...value.clock, status: 'unavailable', year: null, dayFraction: null,
    yearFraction: null, daysPerYear: null, firstFrame: null, epoch: null, atSimulationFrame: null,
    reason: 'COMPONENT_UNAVAILABLE' } };
}
const invalid = (value: unknown, expectedInstanceId = instanceId) => assert.throws(
  () => requireCityOverview(value, expectedInstanceId), error => error instanceof GameClientError && error.code === 'INVALID_GAME_RESULT');
const errorCode = (value: CallToolResult) => (value.structuredContent as { error?: { code?: string } }).error?.code;

test('城市概览保留已观测零、负余额及未知刷新；缺失不能伪成零或互相矛盾的来源', () => {
  const value = overview();
  assert.equal(requireCityOverview(value, instanceId), value);
  const unavailable = missing(); assert.equal(requireCityOverview(unavailable), unavailable);
  const unlimited = { ...value, treasury: { ...value.treasury, value: 2_000_000_000, unlimited: true } };
  assert.equal(requireCityOverview(unlimited), unlimited);
  const wrap = { ...value, capture: { ...value.capture, simulationFrameStart: 4294967295, simulationFrameEnd: 0 },
    clock: { ...value.clock, atSimulationFrame: 0 } };
  assert.equal(requireCityOverview(wrap), wrap, 'capture frames do not assert source update times or exclude uint wrap');
  invalid({ ...wrap, clock: { ...wrap.clock, atSimulationFrame: 1 } });
  invalid({ ...value, clock: { ...value.clock, atSimulationFrame: 42 } });
  const maximumScope = { ...value, scope: { ...value.scope, generation: '9223372036854775807', cityId: 'known-city' } };
  assert.equal(requireCityOverview(maximumScope), maximumScope);
  for (const reason of ['MENU', 'LOADING', 'SCOPE_CHANGED'] as const) {
    const result = { kind: 'unavailable' as const, reason };
    assert.equal(requireCityOverview(result), result);
  }
  for (const generation of ['0', '01', '-1', '9223372036854775808']) invalid({ ...value, scope: { ...value.scope, generation } });
  invalid(value, 'different-pipe-instance');
  invalid({ ...value, capture: { ...value.capture, endedAtUnixMs: 999 } });
  for (const population of [
    { ...value.population, value: null }, { ...value.population, reason: 'MISSING_COMPONENT' },
    { ...unavailable.population, value: 0 }, { ...unavailable.population, reason: null },
    { ...value.population, source: 'player-money-component' }, { ...value.population, sourceUpdatedFrame: 40 },
  ]) invalid({ ...value, population });
  for (const treasury of [
    { ...value.treasury, unlimited: null }, { ...value.treasury, value: null },
    { ...value.treasury, reason: 'INVALID_VALUE' }, { ...unavailable.treasury, unlimited: false },
    { ...unavailable.treasury, value: 0 }, { ...unavailable.treasury, reason: null },
    { ...value.treasury, source: 'city-population-component' },
  ]) invalid({ ...value, treasury });
});

test('模拟设置与原生时钟分组独立，不从正速度或同帧推断推进，也不替未知原因补值', () => {
  const value = overview();
  const sameFrame = { ...value, capture: { ...value.capture, simulationFrameEnd: value.capture.simulationFrameStart },
    clock: { ...value.clock, atSimulationFrame: value.capture.simulationFrameStart },
    simulation: { ...value.simulation, smoothSpeed: 0 } };
  assert.equal(requireCityOverview(sameFrame), sameFrame);
  assert.equal(sameFrame.simulation.advancingNow, null);
  const paused = { ...value, simulation: { ...value.simulation, selectedSpeed: 0, selectedPaused: true } };
  assert.equal(requireCityOverview(paused), paused);
  assert.equal(paused.simulation.pauseReason, null);
  const maximum = { ...value, simulation: { ...value.simulation, selectedSpeed: 3.4028234663852886e38,
    smoothSpeed: 3.4028234663852886e38 } };
  assert.equal(requireCityOverview(maximum), maximum, 'native finite speeds are not restricted to UI speed buttons');
  for (const year of [0, -20]) {
    const clock = { ...value.clock, year, epoch: { startingYear: year - 1, startingMonth: 1, startingHour: 8, startingMinute: 0 } };
    const result = { ...value, clock }; assert.equal(requireCityOverview(result), result);
  }
  const clockUnavailable = missingClock();
  assert.equal(requireCityOverview(clockUnavailable), clockUnavailable);
  assert.equal(clockUnavailable.population.value, 0); assert.equal(clockUnavailable.simulation.selectedSpeed, 2.5);
  const simulationUnavailable = { ...value, simulation: { ...value.simulation, status: 'unavailable' as const,
    selectedSpeed: null, selectedPaused: null, smoothSpeed: null, reason: 'SOURCE_UNAVAILABLE' as const } };
  assert.equal(requireCityOverview(simulationUnavailable), simulationUnavailable);
  assert.equal(simulationUnavailable.clock.year, 2023);

  for (const simulation of [
    { ...value.simulation, selectedSpeed: null }, { ...value.simulation, smoothSpeed: null },
    { ...value.simulation, selectedPaused: true }, { ...value.simulation, selectedSpeed: 0, selectedPaused: false },
    { ...value.simulation, reason: 'SOURCE_UNAVAILABLE' }, { ...value.simulation, advancingNow: true },
    { ...value.simulation, pauseReason: 'player' }, { ...simulationUnavailable.simulation, selectedSpeed: 0 },
    { ...simulationUnavailable.simulation, reason: null },
    ...[-1, Number.NaN, Number.POSITIVE_INFINITY, 3.5e38].map(selectedSpeed => ({ ...value.simulation, selectedSpeed })),
  ]) invalid({ ...value, simulation });
  for (const clock of [
    { ...value.clock, year: null }, { ...value.clock, epoch: null },
    { ...value.clock, atSimulationFrame: null }, { ...value.clock, reason: 'COMPONENT_UNAVAILABLE' },
    { ...value.clock, year: 2022 }, { ...value.clock, dayFraction: 1 }, { ...value.clock, yearFraction: 1 },
    { ...value.clock, dayFraction: -0.01 }, { ...value.clock, yearFraction: Number.NaN },
    { ...clockUnavailable.clock, year: 0 }, { ...clockUnavailable.clock, epoch: value.clock.epoch },
    { ...clockUnavailable.clock, reason: null }, { ...value.clock, ticksPerDay: 24 },
  ]) invalid({ ...value, clock });
});

test('城市概览单次转发绑定发现所得实例，无前置context、缓存或撤权后重试', async () => {
  const connection: PipeConnection = { pipeName: 'skylineslink-city-overview-fixture', profileId: 'reader',
    profileInstanceId: 'a'.repeat(32), token: 'f'.repeat(64), instanceId, environmentId: 'fixture', authorityEpoch: 2,
    grantEpoch: 3, coreManifestSha256: 'a'.repeat(64), productVersion: '0.1.0-dev.0', contractVersion: '2' };
  let current: unknown = overview(), discovered = 0, connected = 0, closed = 0;
  const calls: { id: string; revision: string; args: unknown }[] = [];
  const game = new DiscoveredGameClient({ environment: 'fixture', profile: 'reader',
    coreManifestSha256: connection.coreManifestSha256, userProfile: process.cwd() }, {
    discover: async () => { discovered++; return connection; },
    connect: async () => { connected++; return { connection, usage: { closed: false },
      call: async (id, revision, args) => { calls.push({ id, revision, args }); if (current instanceof Error) throw current; return current; },
      close: async () => { closed++; } }; },
  });
  const call = (args: unknown = {}) => game.call('city.overview', '2', args, new AbortController().signal);
  try {
    await assert.rejects(call({ extra: true }));
    assert.equal(discovered, 0); assert.equal(connected, 0);
    assert.deepEqual(await call(), overview());
    assert.deepEqual(calls, [{ id: 'city.overview', revision: '2', args: {} }]);
    current = missing(); assert.deepEqual(await call(), missing());
    current = { kind: 'unavailable', reason: 'MENU' }; assert.deepEqual(await call(), current);
    current = { ...overview(), scope: { ...overview().scope, instanceId: 'foreign-instance' } };
    await assert.rejects(call(), error => error instanceof GameClientError && error.code === 'INVALID_GAME_RESULT');
    current = new GameClientError('UNAUTHORIZED', 'Read access was revoked.');
    await assert.rejects(call(), error => error instanceof GameClientError && error.code === 'UNAUTHORIZED');
    assert.equal(calls.length, 5); assert.equal(discovered, 1); assert.equal(connected, 1);
  } finally { await game.close(); }
  assert.equal(closed, 1);
});

for (const modern of [false, true]) test(`官方SDK城市概览 ${modern ? '2026' : '2025'}：read渠道、菜单和缺失如实传递，拒绝无授权与非法观测`, async () => {
  let current: unknown = { kind: 'unavailable', reason: 'MENU' };
  const calls: string[] = [], token = randomBytes(32).toString('hex');
  const host = await startHttpHost({ port: 0, authorization: { token, profileId: 'city-reader', scopes: ['read'] },
    game: { call: async (id, revision, args) => {
      assert.equal(id, 'city.overview'); assert.equal(revision, '2'); assert.deepEqual(args, {});
      calls.push(id); if (current instanceof Error) throw current; return current;
    }, close: async () => {} } });
  const client = new Client({ name: 'city-overview-fixture', version: '1' },
    { capabilities: {}, versionNegotiation: { mode: modern ? { pin: '2026-07-28' } : 'legacy' } });
  try {
    const denied = await fetch(host.url, { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' });
    assert.equal(denied.status, 401); await denied.arrayBuffer(); assert.equal(calls.length, 0);
    await client.connect(new StreamableHTTPClientTransport(host.url, { requestInit: { headers: { authorization: `Bearer ${token}` } } }));
    assert.equal(client.getNegotiatedProtocolVersion(), modern ? '2026-07-28' : '2025-11-25');
    const capability = capabilities.find(item => item.id === 'city.overview')!;
    assert.equal(capability.schemaRevision, '2');
    assert.equal(capability.outputSchema.$id, schemaIds.CityOverviewV2);
    assert.equal(capabilities.filter(item => item.id === 'city.overview').length, 1);
    const tool = (await client.listTools()).tools.find(item => item.name === 'skylines_city_overview')!;
    assert.equal(tool.annotations?.readOnlyHint, true); assert.equal(tool.annotations?.destructiveHint, false);
    assert.deepEqual(tool.inputSchema, capability.inputSchema);
    assert.deepEqual(tool.outputSchema, { type: 'object', ...capability.outputSchema });
    const call = () => client.callTool({ name: 'skylines_city_overview', arguments: {} }) as Promise<CallToolResult>;
    for (const value of [{ kind: 'unavailable', reason: 'MENU' }, overview(), missing(), missingClock()]) {
      current = value; const result = await call(); assert.notEqual(result.isError, true);
      assert.deepEqual(result.structuredContent, value);
      assert.equal(result.content[0]?.type, 'text');
      assert.deepEqual(JSON.parse((result.content[0] as { text: string }).text), value);
    }
    const value = overview();
    for (const invalidValue of [
      { ...value, population: { ...value.population, value: null } },
      { ...value, simulation: { ...value.simulation, selectedPaused: true } },
      { ...value, clock: { ...value.clock, dayFraction: 1 } },
      { ...value, clock: { ...value.clock, atSimulationFrame: 42 } },
    ]) {
      current = invalidValue;
      const invalidResult = await call(); assert.equal(invalidResult.isError, true); assert.equal(errorCode(invalidResult), 'INVALID_GAME_RESULT');
    }
    current = new GameClientError('UNAUTHORIZED', 'Read access was revoked.');
    const revoked = await call(); assert.equal(revoked.isError, true); assert.equal(errorCode(revoked), 'UNAUTHORIZED');
    assert.equal(calls.length, 9, 'each tool call reaches only the requested native read once');
  } finally { await client.close(); await host.close(); }
});
