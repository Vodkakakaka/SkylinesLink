import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { PassThrough } from 'node:stream';
import { randomBytes } from 'node:crypto';
import { Client, StreamableHTTPClientTransport } from '@modelcontextprotocol/client';
import type { CallToolResult, JSONRPCMessage, Transport } from '@modelcontextprotocol/server';
import { capabilities, type BuildingInspection, type BuildingInspectionQuery } from '../../contracts/ts/index.ts';
import { copyBuildingInspectionQuery, requireBuildingInspection } from '../../src/host/src/game-client/building-inspection-result.ts';
import { GameClientError, type GameClient } from '../../src/host/src/game-client/game-client.ts';
import { DiscoveredGameClient } from '../../src/host/src/game-client/discovered-game-client.ts';
import type { PipeConnection } from '../../src/host/src/game-client/named-pipe-client.ts';
import { startHttpHost } from '../../src/host/src/mcp/transports/http.ts';
import { startStdioHost } from '../../src/host/src/mcp/transports/stdio.ts';
import { measureJson } from '../../src/host/src/shared/json-budget.ts';
import { probeLimits } from '../../src/host/src/shared/limits.ts';

type Observed = Extract<BuildingInspection, { kind: 'observed' }>;
const vectors = JSON.parse(readFileSync(new URL('../../contracts/fixtures/building-inspection-cases.json', import.meta.url), 'utf8')) as { name: string; json: string }[];
const sample = (name: string) => JSON.parse(vectors.find(row => row.name === `building/${name}`)!.json);
const query = (): BuildingInspectionQuery => sample('exact-query');
const facts = (): Observed => sample('observed-zero-base-cost');
const invalid = (input: unknown, requested = query()) => assert.throws(() => requireBuildingInspection(requested, input, 'building-fixture'),
  error => error instanceof GameClientError && error.code === 'INVALID_GAME_RESULT');

test('建筑观察绑定完整对象和scope，保持基础元数据、unknown及直接前缀口径', () => {
  const value = facts(); assert.equal(requireBuildingInspection(query(), value, 'building-fixture'), value);
  const unknown = sample('unknown-fields'); assert.equal(requireBuildingInspection(query(), unknown), unknown);
  for (const reason of ['DELETED', 'PREVIEW', 'NOT_BUILDING'] as const) {
    const unsupported = { kind: 'unsupported' as const, scope: value.scope, object: value.object, reason };
    assert.equal(requireBuildingInspection(query(), unsupported), unsupported);
  }
  for (const reason of ['CONTEXT_MISMATCH', 'ENTITY_UNAVAILABLE', 'SCOPE_CHANGED', 'NO_SELECTION', 'SELECTION_SOURCE_NOT_READY'] as const) {
    const unavailable = { kind: 'unavailable' as const, reason }; assert.equal(requireBuildingInspection(query(), unavailable), unavailable);
  }
  invalid({ ...value, object: { ...value.object, version: 8 } });
  invalid({ ...value, object: { ...value.object, index: 43 } });
  invalid({ ...value, scope: { ...value.scope, instanceId: 'another-world' } });
  invalid({ ...value, scope: { ...value.scope, generation: '3' } });
  invalid({ ...value, selectionSource: 'selectedInfo' });
  invalid({ ...value, relationLimit: 16 });
  invalid({ ...value, selectionSourceUpdatedFrame: 40 });
  invalid({ ...value, prefab: { ...value.prefab, baseConstructionCost: { value: 0, reason: 'MISSING_COMPONENT' } } });
  invalid({ ...value, prefab: { ...value.prefab, lotCells: { width: 3, depth: null, reason: null } } });
  invalid({ ...value, prefab: { ...value.prefab, prefabLocalBounds: { ...value.prefab.prefabLocalBounds, min: [20, 0, 0] } } });
  invalid({ ...value, building: { ...value.building, roadEdgeExists: true } });
  invalid({ ...value, building: { ...value.building, roadEdge: { index: 0, version: 0 } } });
  invalid({ ...value, relations: { ...value.relations, subObjects: { ...value.relations.subObjects, complete: false } } });
  invalid({ ...value, relations: { ...value.relations, subNets: { present: true, length: 20, returned: 0, entries: [], complete: false, reason: 'LIMIT' } } });
  const maximum = sample('maximum-escaped-result') as Observed;
  const requested: BuildingInspectionQuery = { target: { kind: 'entityRef', ref: { ...maximum.scope, ...maximum.object } }, relationLimit: 16 };
  assert.equal(requireBuildingInspection(requested, maximum), maximum);
  assert(Buffer.byteLength(JSON.stringify(maximum)) <= 16 * 1024);
  measureJson({ jsonrpc: '2.0', id: '\0'.repeat(128), result: { structuredContent: maximum,
    content: [{ type: 'text', text: JSON.stringify(maximum) }] } }, probeLimits);
});

test('请求冻结保留四项身份、版本和limit，拒绝不规范加载代次', () => {
  const original = query(); const copy = copyBuildingInspectionQuery(original);
  assert.notEqual(copy, original); assert.notEqual(copy.target, original.target);
  assert.deepEqual(copy, original);
  for (const generation of ['0', '01', '-1', '9223372036854775808']) {
    const target = original.target; assert.equal(target.kind, 'entityRef'); if (target.kind !== 'entityRef') throw new Error('fixture');
    assert.throws(() => copyBuildingInspectionQuery({ target: { kind: 'entityRef', ref: { ...target.ref, generation } } }),
      error => error instanceof GameClientError && error.code === 'INVALID_ARGUMENT');
  }
});

test('发现连接保留原selector，原生返回不能借参数别名更换对象或换城', async () => {
  const connection: PipeConnection = { pipeName: 'fixture', profileId: 'reader', profileInstanceId: '1'.repeat(32), token: 't'.repeat(64),
    instanceId: 'building-fixture', environmentId: 'test', authorityEpoch: 1, grantEpoch: 1,
    coreManifestSha256: 'c'.repeat(64), productVersion: '0.1.0-dev.0', contractVersion: '2' };
  let returned: unknown = facts(); let discoveries = 0;
  const game = new DiscoveredGameClient({ environment: 'test', profile: 'reader', userProfile: 'fixture-profile', coreManifestSha256: connection.coreManifestSha256 }, {
    discover: async () => { discoveries++; return connection; }, connect: async () => ({ connection, usage: { closed: false },
      call: async (_id, _revision, args) => {
        const mutable = args as { target: { ref: { version: number } } }; mutable.target.ref.version = 99;
        return returned;
      }, close: async () => {} }),
  });
  try {
    assert.deepEqual(await game.call('buildings.inspect', '1', query(), new AbortController().signal), facts());
    returned = { ...facts(), object: { index: 42, version: 99 } };
    await assert.rejects(game.call('buildings.inspect', '1', query(), new AbortController().signal),
      error => error instanceof GameClientError && error.code === 'INVALID_GAME_RESULT');
    assert.equal(discoveries, 1);
  } finally { await game.close(); }
});

async function connect(http: boolean, modern: boolean, game: GameClient) {
  const client = new Client({ name: 'building-inspection-fixture', version: '1' },
    { capabilities: {}, versionNegotiation: { mode: modern ? { pin: '2026-07-28' } : 'legacy' } });
  let close: () => Promise<void>; let transport: Transport;
  if (http) {
    const token = randomBytes(32).toString('hex');
    const host = await startHttpHost({ game, port: 0, authorization: { token, profileId: 'reader', scopes: ['read'] } });
    transport = new StreamableHTTPClientTransport(host.url, { requestInit: { headers: { authorization: `Bearer ${token}` } } });
    close = () => host.close();
  } else {
    const input = new PassThrough(), output = new PassThrough(); let buffer = '';
    transport = { start: async () => {}, send: async message => { input.write(JSON.stringify(message) + '\n'); }, close: async () => { input.end(); } };
    output.once('close', () => transport.onclose?.());
    output.on('data', (chunk: Buffer) => {
      buffer += chunk.toString('utf8'); assert(buffer.length <= probeLimits.messageBytes * 2);
      for (let end = buffer.indexOf('\n'); end >= 0; end = buffer.indexOf('\n')) {
        const line = buffer.slice(0, end); buffer = buffer.slice(end + 1); transport.onmessage?.(JSON.parse(line) as JSONRPCMessage);
      }
    });
    const host = startStdioHost({ game }, input, output); close = () => host.close();
  }
  try { await client.connect(transport); return { client, close }; }
  catch (error) { await client.close(); await close(); throw error; }
}

for (const http of [false, true]) for (const modern of [false, true]) test(`建筑工具 ${http ? 'HTTP' : 'stdio'} ${modern ? '2026' : '2025'}：普通tools返回同义事实并拒绝伪造`, { timeout: 8000 }, async () => {
  let returned: unknown = facts(); let calls = 0;
  const { client, close } = await connect(http, modern, { call: async (id, revision, args) => {
    assert.equal(id, 'buildings.inspect'); assert.equal(revision, '1'); assert.deepEqual(args, query()); calls++;
    if (returned instanceof Error) throw returned; return returned;
  }, close: async () => {} });
  try {
    const capability = capabilities.find(row => row.id === 'buildings.inspect')!;
    const listed = await client.listTools(); const tool = listed.tools.find(row => row.name === capability.toolName)!;
    assert.equal(tool.annotations?.readOnlyHint, true); assert.equal(tool.annotations?.destructiveHint, false);
    assert.equal(client.getNegotiatedProtocolVersion(), modern ? '2026-07-28' : '2025-11-25');
    const call = () => client.callTool({ name: capability.toolName, arguments: query() as Record<string, unknown> }) as Promise<CallToolResult>;
    for (const value of [facts(), sample('unknown-fields'), { kind: 'unavailable', reason: 'ENTITY_UNAVAILABLE' },
      { kind: 'unavailable', reason: 'SELECTION_SOURCE_NOT_READY' },
      { kind: 'unsupported', reason: 'DELETED', scope: facts().scope, object: facts().object }]) {
      returned = value; const result = await call(); assert.notEqual(result.isError, true);
      assert.deepEqual(result.structuredContent, value); assert.deepEqual(JSON.parse((result.content[0] as { text: string }).text), value);
    }
    returned = { ...facts(), object: { index: 42, version: 8 } };
    const invalid = await call(); assert.equal(invalid.isError, true);
    assert.equal((invalid.structuredContent as { error: { code: string } }).error.code, 'INVALID_GAME_RESULT');
    returned = new GameClientError('UNAUTHORIZED', 'Read was revoked.');
    const denied = await call(); assert.equal(denied.isError, true);
    assert.equal((denied.structuredContent as { error: { code: string } }).error.code, 'UNAUTHORIZED');
    const before = calls;
    const malformed = await client.callTool({ name: capability.toolName, arguments: { target: { kind: 'entityRef', ref: { index: 42, version: 7 } } } });
    assert.equal(malformed.isError, true); assert.equal(calls, before, 'malformed refs never reach the game');
  } finally { await client.close(); await close(); }
});
