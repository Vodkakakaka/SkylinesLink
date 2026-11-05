import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { randomBytes } from 'node:crypto';
import { Client, StreamableHTTPClientTransport } from '@modelcontextprotocol/client';
import { StdioClientTransport } from '@modelcontextprotocol/client/stdio';
import { InMemoryTransport, type CallToolResult } from '@modelcontextprotocol/server';
import { capabilities, schemaIds, requireContract } from '../../contracts/ts/index.ts';
import { startHttpHost } from '../../src/host/src/mcp/transports/http.ts';
import { createMcpServer, WorkBudget } from '../../src/host/src/mcp/server.ts';
import { GameClientError } from '../../src/host/src/game-client/game-client.ts';
import { LocalReads } from '../../src/host/src/guidance/local-reads.ts';
import { measureJson } from '../../src/host/src/shared/json-budget.ts';
import { probeLimits } from '../../src/host/src/shared/limits.ts';

const localNames = ['skylines_capability_describe', 'skylines_guidance_get', 'skylines_guidance_search'];

async function call(client: Client, name: string, args: Record<string, unknown>) {
  const result = await client.callTool({ name, arguments: args }) as CallToolResult;
  assert.equal(result.content[0]?.type, 'text');
  assert.deepEqual(JSON.parse((result.content[0] as { text: string }).text), result.structuredContent);
  measureJson({ jsonrpc: '2.0', id: 42, result }, probeLimits);
  return result;
}

function errorCode(result: CallToolResult): string | undefined {
  return (result.structuredContent as { error?: { code?: string } }).error?.code;
}

function listedSchema(source: Readonly<Record<string, unknown>>) {
  if (source.$id !== schemaIds.ModuleRuntimeQuery && source.$id !== schemaIds.ModuleRuntimePage
    && source.$id !== schemaIds.CityOverviewV2) return source;
  // SDK 2.4 adds the MCP object root. It is equivalent only because every
  // existing branch requires an object; retain and compare every source field.
  assert.equal(Object.hasOwn(source, 'type'), false);
  const branches = source.oneOf;
  assert.ok(Array.isArray(branches) && branches.length > 0);
  for (const branch of branches) assert.equal(branch.type, 'object');
  return { type: 'object', ...source };
}

async function exercise(client: Client) {
  const listed = await client.listTools();
  const bindings = [
    ['system.context', 'skylines_context', 'read'], ['operations.get', 'skylines_operation_get', 'read'],
    ['guidance.search', 'skylines_guidance_search', 'read'], ['guidance.get', 'skylines_guidance_get', 'read'],
    ['capabilities.describe', 'skylines_capability_describe', 'read'],
    ['tasks.stop', 'skylines_task_stop', 'write'], ['tasks.stop.get', 'skylines_task_stop_decision_get', 'read'],
    ['system.modules.get', 'skylines_module_runtime_get', 'read'],
    ['city.overview', 'skylines_city_overview', 'read'],
  ] as const;
  assert.deepEqual(listed.tools.map(tool => tool.name).sort(), bindings.map(binding => binding[1]).sort());
  for (const [id, name, effect] of bindings) {
    const capability = capabilities.find(item => item.id === id)!;
    assert.equal(capability.toolName, name); assert.equal(capability.effect, effect);
    const tool = listed.tools.find(item => item.name === name)!;
    assert.equal(tool.annotations?.readOnlyHint, effect === 'read');
    assert.equal(tool.annotations?.destructiveHint, effect === 'write');
    assert.equal(tool.annotations?.idempotentHint, effect === 'read');
    assert.deepEqual(tool.inputSchema, listedSchema(capability.inputSchema));
    assert.deepEqual(tool.outputSchema, listedSchema(capability.outputSchema));
  }
  measureJson({ jsonrpc: '2.0', id: 1, result: listed }, probeLimits);
  const browse = await call(client, 'skylines_guidance_search', {});
  assert.notEqual(browse.isError, true);
  requireContract(schemaIds.GuidanceSearchResult, browse.structuredContent);
  const search = browse.structuredContent as { sourceKind: string; language: string; query: string; truncated: boolean; results: { guideId: string; revision: string }[] };
  assert.equal(search.sourceKind, 'productGuide'); assert.equal(search.language, 'en');
  assert.equal(search.query, ''); assert.equal(search.truncated, false); assert.equal(search.results.length, 4);
  const limited = await call(client, 'skylines_guidance_search', { limit: 1 });
  assert.equal((limited.structuredContent as { truncated: boolean }).truncated, true);
  assert.equal((limited.structuredContent as { results: unknown[] }).results.length, 1);
  for (const item of search.results) for (const language of ['en', 'zh-CN']) {
    const result = await call(client, 'skylines_guidance_get', { guideId: item.guideId, language });
    assert.notEqual(result.isError, true); requireContract(schemaIds.GuidanceDocument, result.structuredContent);
    const guide = result.structuredContent as { sourceKind: string; productVersion: string; language: string; revision: string };
    assert.equal(guide.sourceKind, 'productGuide'); assert.equal(guide.productVersion, '0.1.0-dev.0');
    assert.equal(guide.language, language); assert.equal(guide.revision, item.revision);
  }
  const chinese = await call(client, 'skylines_guidance_search', { query: '规划', language: 'zh-CN' });
  assert.ok((chinese.structuredContent as { results: unknown[] }).results.length > 0);
  const empty = await call(client, 'skylines_guidance_search', { query: 'no-such-public-guide-fixture' });
  assert.deepEqual((empty.structuredContent as { results: unknown[] }).results, []);
  for (const capability of capabilities) {
    const result = await call(client, 'skylines_capability_describe', { capabilityId: capability.id, schemaRevision: capability.schemaRevision });
    assert.notEqual(result.isError, true); requireContract(schemaIds.CapabilityDescription, result.structuredContent);
    assert.deepEqual(result.structuredContent, { capabilityId: capability.id, schemaRevision: capability.schemaRevision,
      inputSchema: capability.inputSchema, outputSchema: capability.outputSchema });
  }
  for (const [name, args, code] of [
    ['skylines_guidance_get', { guideId: '../../private-file' }, 'GUIDANCE_NOT_FOUND'],
    ['skylines_capability_describe', { capabilityId: 'not.registered' }, 'CAPABILITY_NOT_FOUND'],
    ['skylines_capability_describe', { capabilityId: 'operations.get', schemaRevision: '4' }, 'CAPABILITY_REVISION_MISMATCH'],
  ] as const) assert.equal(errorCode(await call(client, name, args)), code);
  for (const [name, args] of [
    ['skylines_guidance_get', { guideId: 'getting-started', language: 'fr' }],
    ['skylines_guidance_search', { limit: 9 }],
    ['skylines_guidance_search', { query: 'x'.repeat(129) }],
    ['skylines_guidance_search', { query: 'x', execute: true }],
    ['skylines_capability_describe', { capabilityId: 'system.context', arguments: {} }],
  ] as const) assert.equal((await client.callTool({ name, arguments: args })).isError, true);
  const offline = await call(client, 'skylines_context', {});
  assert.equal(errorCode(offline), 'OFFLINE_FIXTURE');
  // A failed game attempt must not poison or silently rebind the public-data path.
  assert.notEqual((await call(client, 'skylines_guidance_get', { guideId: 'getting-started' })).isError, true);
}

for (const modern of [false, true]) test(`公共tools-only指引/完整声明：进程stdio ${modern ? '2026' : '2025'}离线可读`, { timeout: 15000 }, async () => {
  const client = new Client({ name: 'public-guide-test', version: '1' }, { capabilities: {}, versionNegotiation: { mode: modern ? { pin: '2026-07-28' } : 'legacy' } });
  try {
    await client.connect(new StdioClientTransport({ command: process.execPath,
      args: [fileURLToPath(new URL('./guidance-stdio-fixture.mjs', import.meta.url))], stderr: 'pipe' }));
    assert.equal(client.getNegotiatedProtocolVersion(), modern ? '2026-07-28' : '2025-11-25');
    await exercise(client);
  } finally { await client.close(); }
});

for (const modern of [false, true]) test(`公共tools-only指引/完整声明：认证HTTP ${modern ? '2026' : '2025'}不调用游戏`, { timeout: 15000 }, async () => {
  let calls = 0;
  const token = randomBytes(32).toString('hex');
  const host = await startHttpHost({ game: { call: async () => { calls++; throw new GameClientError('OFFLINE_FIXTURE', 'The isolated game fixture is unavailable.'); }, close: async () => {} },
    authorization: { token, profileId: 'offline-public-guide', scopes: ['read'] }, port: 0 });
  const client = new Client({ name: 'public-guide-test', version: '1' }, { capabilities: {}, versionNegotiation: { mode: modern ? { pin: '2026-07-28' } : 'legacy' } });
  try {
    await client.connect(new StreamableHTTPClientTransport(host.url, { requestInit: { headers: { authorization: `Bearer ${token}` } } }));
    assert.equal(client.getNegotiatedProtocolVersion(), modern ? '2026-07-28' : '2025-11-25');
    await exercise(client); assert.equal(calls, 1, 'only the explicit context read reaches the game client');
  } finally { try { await client.close(); } finally { await host.close(); } }
  assert.deepEqual(host.usage(), { requests: 0, pipelines: 0, connections: 0, handlers: 0 });
});

test('本地处理器精确绑定；共享工作预算及本地结果错误不回退到游戏', async () => {
  const local = capabilities.find(capability => capability.id === 'guidance.search')!;
  for (const change of [{ schemaRevision: '2' }, { effect: 'write' as const }, { inputId: schemaIds.Empty }, { outputId: schemaIds.Context }, { toolName: 'other' }]) {
    assert.throws(() => createMcpServer({ game: { call: async () => { throw new Error('not reachable'); }, close: async () => {} }, capabilities: [{ ...local, ...change }] }));
  }
  const subset = new LocalReads([local], '0.1.0-dev.0');
  assert.equal(subset.resolve(capabilities[0]!), undefined, 'game capability has no implicit local implementation');
  assert.throws(() => new LocalReads([local, local], '0.1.0-dev.0'));
  const describe = capabilities.find(capability => capability.id === 'capabilities.describe')!;
  const describeSubset = new LocalReads([describe], '0.1.0-dev.0').resolve(describe)!;
  assert.throws(() => describeSubset({ capabilityId: 'system.context' }),
    error => error instanceof GameClientError && error.code === 'CAPABILITY_NOT_FOUND',
    'a declared product capability hidden on this connection is not exposed by describe');
  let calls = 0;
  const work = new WorkBudget(1);
  const server = createMcpServer({ capabilities: [local], workBudget: work,
    game: { call: async () => { calls++; return {}; }, close: async () => {} } });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: 'guide-budget', version: '1' }, { capabilities: {} });
  try {
    await server.connect(serverTransport); await client.connect(clientTransport);
    const release = work.acquire();
    try { assert.equal(errorCode(await call(client, local.toolName, {})), 'BUSY'); } finally { release(); }
    assert.notEqual((await call(client, local.toolName, {})).isError, true);
    assert.equal(work.active, 0); assert.equal(calls, 0);
  } finally { await client.close(); await server.close(); }
  const invalid = createMcpServer({ capabilities: [{ ...local, validateOutput: () => ({ valid: false, issues: [] }) }],
    game: { call: async () => { calls++; return {}; }, close: async () => {} } });
  const [input, output] = InMemoryTransport.createLinkedPair();
  const reader = new Client({ name: 'guide-invalid', version: '1' }, { capabilities: {} });
  try {
    await invalid.connect(output); await reader.connect(input);
    assert.equal(errorCode(await call(reader, local.toolName, {})), 'INVALID_LOCAL_RESULT'); assert.equal(calls, 0);
  } finally { await reader.close(); await invalid.close(); }
});

test('本地读取拒绝超出响应预算；发送前取消不进入游戏且实际工作归零', async () => {
  let calls = 0;
  const work = new WorkBudget(1);
  const server = createMcpServer({ workBudget: work, limits: { ...probeLimits, messageBytes: 1024 },
    game: { call: async () => { calls++; return {}; }, close: async () => {} } });
  const [input, output] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: 'guide-limit', version: '1' }, { capabilities: {} });
  try {
    await server.connect(output); await client.connect(input);
    const abort = new AbortController(); abort.abort();
    await assert.rejects(client.callTool({ name: 'skylines_guidance_get', arguments: { guideId: 'getting-started' } }, { signal: abort.signal }));
    assert.equal(work.active, 0);
    const result = await call(client, 'skylines_guidance_get', { guideId: 'getting-started' });
    assert.equal(result.isError, true); assert.equal(errorCode(result), 'LOCAL_READ_FAILURE');
    assert.equal(Object.hasOwn((result.structuredContent as { error: object }).error, 'mayHaveApplied'), false);
    assert.equal(calls, 0); assert.equal(work.active, 0);
  } finally { await client.close(); await server.close(); }
});
