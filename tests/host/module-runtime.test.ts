import test from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { Client, StreamableHTTPClientTransport } from '@modelcontextprotocol/client';
import { type CallToolResult, type JSONRPCMessage, type Transport } from '@modelcontextprotocol/server';
import { requireContract, schemaIds, type ModuleRuntimeRow, type ModuleRuntimeQuery } from '../../contracts/ts/index.ts';
import { GameClientError } from '../../src/host/src/game-client/game-client.ts';
import { DiscoveredGameClient } from '../../src/host/src/game-client/discovered-game-client.ts';
import type { PipeConnection } from '../../src/host/src/game-client/named-pipe-client.ts';
import { copyModuleRuntimeQuery, requireModuleRuntimePage } from '../../src/host/src/game-client/module-runtime-result.ts';
import { startHttpHost } from '../../src/host/src/mcp/transports/http.ts';
import { BoundedTransport } from '../../src/host/src/mcp/transports/bounded-transport.ts';
import { checkRawJson, measureJson } from '../../src/host/src/shared/json-budget.ts';
import { probeLimits, TransportBudgetError } from '../../src/host/src/shared/limits.ts';
import { moduleInstanceId, modulePage, moduleRow, moduleSnapshotId, worstModulePage, type ModulePage } from './module-runtime-values.ts';

const capture: ModuleRuntimeQuery = { kind: 'capture' };
const pageQuery: ModuleRuntimeQuery = { kind: 'page', snapshotId: moduleSnapshotId, offset: 0 };
const invalid = (value: unknown, query: ModuleRuntimeQuery = capture, instance = moduleInstanceId) => assert.throws(
  () => requireModuleRuntimePage(query, value, instance), error => error instanceof GameClientError && error.code === 'INVALID_GAME_RESULT');
const withRow = (patch: Partial<ModuleRuntimeRow>) => modulePage([moduleRow(0, patch)]);
const withCoverage = (patch: Partial<ModulePage['snapshot']['coverage']>) => {
  const value = modulePage(); return { ...value, snapshot: { ...value.snapshot, coverage: { ...value.snapshot.coverage, ...patch } } };
};

test('模块观察按原snapshot/offset/instance关联，complete与partial覆盖不能伪造总数或后续页', () => {
  const value = modulePage();
  assert.equal(requireModuleRuntimePage(capture, value, moduleInstanceId), value);
  assert.equal(requireModuleRuntimePage(pageQuery, value, moduleInstanceId), value);
  const empty = modulePage([]); assert.equal(requireModuleRuntimePage(capture, empty), empty);
  const partial = withCoverage({ complete: false, reason: 'TIME_LIMIT', visited: 2, totalCount: null });
  assert.equal(requireModuleRuntimePage(capture, partial), partial);
  const prefix = worstModulePage('x'); assert.equal(requireModuleRuntimePage(capture, prefix), prefix);
  invalid(value, { ...pageQuery, snapshotId: 'b'.repeat(32) }); invalid(value, { ...pageQuery, offset: 1 });
  invalid(value, capture, 'other-instance'); invalid({ ...value, offset: 1 });
  invalid({ ...value, returnedCount: 0 }); invalid({ ...value, nextOffset: 1 });
  for (const patch of [{ totalCount: 2 }, { visited: 2 }, { reason: 'TIME_LIMIT' as const },
    { complete: false, reason: 'TIME_LIMIT' as const }, { complete: false, reason: 'TIME_LIMIT' as const, totalCount: null, visited: 3 },
    { complete: false, reason: 'ENTRY_LIMIT' as const, totalCount: null }]) invalid(withCoverage(patch));
  invalid({ ...value, snapshot: { ...value.snapshot, captureStartedAtUnixMs: 1006 } });
  invalid({ ...value, snapshot: { ...value.snapshot, expiresAtUnixMs: 301006 } });
  invalid({ ...value, snapshot: { ...value.snapshot, gameVersionIssue: 'missing' } });
  invalid({ ...value, snapshot: { ...value.snapshot, gameVersion: null } });
  invalid({ kind: 'unavailable', reason: 'PAGE_OUT_OF_RANGE' });
  invalid({ kind: 'unavailable', reason: 'SNAPSHOT_UNAVAILABLE' });
  assert.equal(requireModuleRuntimePage(pageQuery, { kind: 'unavailable', reason: 'SNAPSHOT_UNAVAILABLE' }).kind, 'unavailable');
});

test('模块行保raw状态/false/零；父缺失级联唯一，字段问题与null严格一致，全零GUID无效', () => {
  const unavailableEntry: ModuleRuntimeRow = { ordinal: 0, assetGuid: null, isMod: null, loaderStateCode: null,
    loaderState: 'unavailable', assemblyPresent: null, assemblyName: null, assemblyVersion: null,
    instanceCount: null, hasLoadError: null, databaseKind: 'unknown', fieldIssues: [{ field: 'entry', reason: 'missing' }] };
  const missingAsset = { ...unavailableEntry, loaderStateCode: 2, loaderState: 'disposed' as const,
    instanceCount: 0, hasLoadError: true, fieldIssues: [{ field: 'asset', reason: 'missing' }] as const };
  for (const row of [unavailableEntry, missingAsset, moduleRow(0, { assemblyPresent: false, assemblyName: null, assemblyVersion: null,
    isMod: false, instanceCount: 0, hasLoadError: false }), moduleRow(0, { loaderStateCode: 2147483647, loaderState: 'unrecognized' }),
    moduleRow(0, { assetGuid: null, databaseKind: 'unknown', assemblyName: null, assemblyVersion: null, instanceCount: null,
      fieldIssues: [{ field: 'assetGuid', reason: 'invalid' }, { field: 'databaseKind', reason: 'missing' },
        { field: 'assemblyName', reason: 'tooLong' }, { field: 'assemblyVersion', reason: 'unavailable' }, { field: 'instanceCount', reason: 'missing' }] })])
    assert.equal(requireModuleRuntimePage(capture, modulePage([row])).kind, 'page');
  for (const patch of [{ assetGuid: '0'.repeat(32) }, { assetGuid: null }, { loaderStateCode: null }, { loaderState: 'disposed' },
    { databaseKind: 'unknown' }, { assemblyName: null }, { assemblyVersion: null }, { instanceCount: null }, { isMod: null },
    { hasLoadError: null }, { assemblyPresent: false }, { fieldIssues: [{ field: 'assemblyName', reason: 'missing' }] },
    { fieldIssues: [{ field: 'databaseKind', reason: 'missing' }, { field: 'databaseKind', reason: 'invalid' }] }])
    invalid(withRow(patch as Partial<ModuleRuntimeRow>));
  invalid(modulePage([{ ...unavailableEntry, fieldIssues: [...unavailableEntry.fieldIssues, { field: 'databaseKind', reason: 'missing' }] }]));
  invalid(modulePage([{ ...missingAsset, fieldIssues: [...missingAsset.fieldIssues, { field: 'assetGuid', reason: 'missing' }] }]));
  invalid(withRow({ assemblyPresent: false, assemblyName: null, assemblyVersion: null, fieldIssues: [{ field: 'assemblyName', reason: 'missing' }] }));
  invalid(withRow({ assemblyVersion: '1\n' }));
});

test('模块查询在异步发现前固定原值，活连接校实例且失败不读离线历史', async () => {
  const connection: PipeConnection = { pipeName: 'skylineslink-module-fixture', profileId: 'reader', profileInstanceId: 'a'.repeat(32),
    token: 'f'.repeat(64), instanceId: moduleInstanceId, environmentId: 'fixture', authorityEpoch: 2, grantEpoch: 3,
    coreManifestSha256: 'a'.repeat(64), productVersion: '0.1.0-dev.0', contractVersion: '2' };
  let discover!: (connection: PipeConnection) => void, calls = 0, closed = 0;
  const discovered = new Promise<PipeConnection>(resolve => { discover = resolve; });
  const game = new DiscoveredGameClient({ environment: 'fixture', profile: 'reader', coreManifestSha256: connection.coreManifestSha256, userProfile: process.cwd() },
    { discover: async () => discovered, connect: async () => ({ connection, usage: { closed: false },
      call: async (id, revision, args) => { calls++; assert.equal(id, 'system.modules.get'); assert.equal(revision, '1');
        assert.deepEqual(args, pageQuery); return modulePage(); }, close: async () => { closed++; } }) });
  const query = { kind: 'page' as const, snapshotId: moduleSnapshotId, offset: 0 };
  const original = copyModuleRuntimeQuery(query);
  const waiting = game.call('system.modules.get', '1', query, new AbortController().signal);
  query.snapshotId = 'b'.repeat(32); query.offset = 128; discover(connection);
  try { assert.deepEqual(await waiting, modulePage()); assert.deepEqual(original, pageQuery); assert.equal(calls, 1); }
  finally { await game.close(); }
  assert.equal(closed, 1);
  const wrong = new DiscoveredGameClient({ environment: 'fixture', profile: 'reader', coreManifestSha256: connection.coreManifestSha256, userProfile: process.cwd() },
    { discover: async () => connection, connect: async () => ({ connection, usage: { closed: false },
      call: async () => ({ ...modulePage(), snapshot: { ...modulePage().snapshot, instanceId: 'other' } }), close: async () => {} }) });
  try { await assert.rejects(wrong.call('system.modules.get', '1', capture, new AbortController().signal),
    error => error instanceof GameClientError && error.code === 'INVALID_GAME_RESULT'); }
  finally { await wrong.close(); }
});

for (const modern of [false, true]) test(`官方SDK HTTP模块读取 ${modern ? '2026' : '2025'}：真实双投影及合法最大字段在声明包络内`, async t => {
  let current: unknown = modulePage();
  let maximumRawBytes = 0, maximumEnvelopeBytes = 0;
  const token = randomBytes(32).toString('hex');
  const server = await startHttpHost({ port: 0, authorization: { token, profileId: 'module-fixture', scopes: ['read'] },
    game: { call: async (id, revision) => {
    assert.equal(id, 'system.modules.get'); assert.equal(revision, '1'); return current;
  }, close: async () => {} } });
  const client = new Client({ name: 'module-runtime-fixture', version: '1' },
    { capabilities: {}, versionNegotiation: { mode: modern ? { pin: '2026-07-28' } : 'legacy' } });
  try {
    await client.connect(new StreamableHTTPClientTransport(server.url, { requestInit: { headers: { authorization: `Bearer ${token}` } } }));
    assert.equal(client.getNegotiatedProtocolVersion(), modern ? '2026-07-28' : '2025-11-25');
    const tool = (await client.listTools()).tools.find(tool => tool.name === 'skylines_module_runtime_get')!;
    assert.equal(tool.annotations?.readOnlyHint, true); assert.equal(tool.annotations?.destructiveHint, false);
    for (const character of ['\0', '\\', '"', '\n', '\u{1f680}']) {
      const value = worstModulePage(character); current = value;
      requireContract(schemaIds.ModuleRuntimePage, value); requireModuleRuntimePage(capture, value);
      const result = await client.callTool({ name: 'skylines_module_runtime_get', arguments: capture }) as CallToolResult;
      assert.notEqual(result.isError, true); assert.deepEqual(result.structuredContent, value);
      assert.equal(result.content[0]?.type, 'text');
      assert.deepEqual(JSON.parse((result.content[0] as { text: string }).text), value);
      const id = '\0'.repeat(128), metadata = { note: 'm'.repeat(1013) };
      assert.equal([...id].length, 128); assert.ok(Buffer.byteLength(JSON.stringify(id)) <= 1024);
      assert.equal(Buffer.byteLength(JSON.stringify(metadata)), 1024);
      const envelope = { jsonrpc: '2.0' as const, id, result: { ...result, _meta: metadata } };
      const rawBytes = Buffer.byteLength(JSON.stringify(value)), envelopeBytes = Buffer.byteLength(JSON.stringify(envelope));
      maximumRawBytes = Math.max(maximumRawBytes, rawBytes); maximumEnvelopeBytes = Math.max(maximumEnvelopeBytes, envelopeBytes);
      assert.ok(rawBytes <= 24 * 1024); assert.ok(envelopeBytes <= 65536);
      assert.ok(measureJson(envelope, probeLimits) <= 65536);
      checkRawJson(Buffer.from(JSON.stringify(envelope)), probeLimits);
    }
    current = withRow({ assetGuid: '0'.repeat(32) });
    const rejected = await client.callTool({ name: 'skylines_module_runtime_get', arguments: capture });
    assert.equal(rejected.isError, true);
    assert.equal((rejected.structuredContent as { error: { code: string } }).error.code, 'INVALID_GAME_RESULT');
    t.diagnostic(JSON.stringify({ maximumRawBytes, maximumEnvelopeBytes, testedCharacters: 5,
      envelopeIdScalars: 128, envelopeIdJsonBytes: 770, metadataJsonBytes: 1024,
      scope: 'simultaneous row field maxima in the stated envelope; not arbitrary near-limit RPC ids' }));
  } finally { await client.close(); await server.close(); }
});

test('合法近64KiB请求id可能令模块双投影响应超额；真实bounded输出关闭且不改普通id规则', async () => {
  let writes = 0, closes = 0;
  const inner: Transport = { start: async () => {}, close: async () => {}, send: async () => { writes++; } };
  const transport = new BoundedTransport(inner, probeLimits, { close: async () => { closes++; }, settled: async () => {} });
  const id = 'i'.repeat(64000);
  const request: JSONRPCMessage = { jsonrpc: '2.0', id, method: 'tools/call',
    params: { name: 'skylines_module_runtime_get', arguments: capture } };
  measureJson(request, probeLimits);
  const value = worstModulePage('\0');
  const message: JSONRPCMessage = { jsonrpc: '2.0', id, result: {
    structuredContent: value, content: [{ type: 'text', text: JSON.stringify(value) }],
  } };
  await transport.start();
  try {
    inner.onmessage?.(request);
    await assert.rejects(transport.send(message), error => error instanceof TransportBudgetError);
    await transport.close(); assert.equal(writes, 0); assert.equal(closes, 1);
    assert.deepEqual(transport.usage, { activeRequests: 0, sendBytes: 0, sendCount: 0, closed: true });
  } finally { await transport.close(); }
});
