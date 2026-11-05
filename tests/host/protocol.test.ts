import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { randomBytes } from 'node:crypto';
import { request } from 'node:http';
import { Client, StreamableHTTPClientTransport } from '@modelcontextprotocol/client';
import { StdioClientTransport } from '@modelcontextprotocol/client/stdio';
import { startHttpHost } from '../../src/host/src/mcp/transports/http.ts';
import { fixtureCapabilities, MemoryGame, syntheticContext, syntheticLoadQueries, syntheticSubmissionKey, liveLookup } from './fixture.ts';
import type { CallToolResult } from '@modelcontextprotocol/server';
import { requireContract, schemaIds, type OperationLookupV3, type OperationStatus } from '../../contracts/ts/index.ts';

async function exercise(client: Client) {
  const tools = await client.listTools();
  assert.deepEqual(tools.tools.map(tool => tool.name).sort(), fixtureCapabilities.map(tool => tool.toolName).sort());
  const context = await client.callTool({ name: 'skylines_context', arguments: {} }) as CallToolResult;
  assert.deepEqual(context.structuredContent, syntheticContext);
  assert.equal(context.content[0]?.type, 'text');
  assert.deepEqual(JSON.parse((context.content[0] as { text: string }).text), context.structuredContent);
  const invalid = await client.callTool({ name: 'skylines_context', arguments: { extra: true } }) as CallToolResult;
  assert.equal(invalid.isError, true);
  const submitted = await client.callTool({ name: 'probe_submit', arguments: {} }) as CallToolResult;
  const queried = await client.callTool({ name: 'skylines_operation_get', arguments: { selector: { kind: 'operationId', operationId: (submitted.structuredContent as { operationId: string }).operationId } } }) as CallToolResult;
  const submittedId = (submitted.structuredContent as OperationStatus).operationId;
  assert.deepEqual(queried.structuredContent, liveLookup({ kind: 'operationId', operationId: submittedId }, { lookup: { kind: 'found', ...(submitted.structuredContent as OperationStatus), originInstanceId: syntheticContext.instanceId, durability: 'volatile' } }));
  requireContract(schemaIds.OperationLookupV3, queried.structuredContent);
  assert.deepEqual(JSON.parse((queried.content[0] as { text: string }).text), queried.structuredContent);
  const byKey = await client.callTool({ name: 'skylines_operation_get', arguments: { selector: { kind: 'submissionKey', submissionKey: syntheticSubmissionKey } } }) as CallToolResult;
  assert.deepEqual(byKey.structuredContent, { selector: { kind: 'submissionKey', submissionKey: syntheticSubmissionKey }, result: (queried.structuredContent as OperationLookupV3).result });
  const missing = await client.callTool({ name: 'skylines_operation_get', arguments: { selector: { kind: 'operationId', operationId: 'missing' } } }) as CallToolResult;
  assert.notEqual(missing.isError, true);
  assert.deepEqual(missing.structuredContent, liveLookup({ kind: 'operationId', operationId: 'missing' }, { lookup: { kind: 'absent' } }));
  const oldQuery = await client.callTool({ name: 'skylines_operation_get', arguments: { operationId: 'missing' } }) as CallToolResult;
  assert.equal(oldQuery.isError, true);
  const oldEnvelope = await client.callTool({ name: 'skylines_operation_get', arguments: { selector: { kind: 'operationId', operationId: 'offline-old-envelope' } } }) as CallToolResult;
  assert.equal(oldEnvelope.isError, true);
  assert.equal((oldEnvelope.structuredContent as { error: { code: string } }).error.code, 'INVALID_GAME_RESULT');
  for (const [operationId, expected] of Object.entries(syntheticLoadQueries)) {
    const observed = await client.callTool({ name: 'skylines_operation_get', arguments: { selector: { kind: 'operationId', operationId } } }) as CallToolResult;
    assert.notEqual(observed.isError, true);
    assert.deepEqual(observed.structuredContent, expected);
    assert.deepEqual(JSON.parse((observed.content[0] as { text: string }).text), expected);
  }
  const oldSelector = { kind: 'operationId', operationId: 'offline-old-load-shape' };
  const oldResult = await client.callTool({ name: 'skylines_operation_get', arguments: { selector: oldSelector } }) as CallToolResult;
  assert.equal(oldResult.isError, true);
  const error = (oldResult.structuredContent as { error: Record<string, unknown> }).error;
  assert.equal(error.code, 'INVALID_GAME_RESULT');
  assert.deepEqual(error.selector, oldSelector);
  assert.equal(error.operationId, oldSelector.operationId);
  assert.equal(Object.hasOwn(error, 'mayHaveApplied'), false);
  assert.equal(error.source, 'live');
  assert.deepEqual(JSON.parse((oldResult.content[0] as { text: string }).text), oldResult.structuredContent);
  const incompatible = await client.callTool({ name: 'skylines_operation_get', arguments: { selector: { kind: 'operationId', operationId: 'offline-old-journal' } } }) as CallToolResult;
  assert.notEqual(incompatible.isError, true);
  assert.deepEqual(incompatible.structuredContent, liveLookup({ kind: 'operationId', operationId: 'offline-old-journal' }, { lookup: { kind: 'unavailable', reason: 'JOURNAL_INCOMPATIBLE' } }));
  return context.structuredContent;
}

for (const modern of [false, true]) test(`官方 SDK stdio ${modern ? '2026' : '2025'}：普通工具完成合成提交/查询，stdout只有协议`, { timeout: 15000 }, async () => {
  const transport = new StdioClientTransport({ command: process.execPath, args: [fileURLToPath(new URL('./stdio-fixture.mjs', import.meta.url))], stderr: 'pipe' });
  const client = new Client({ name: 'skylineslink-offline-test', version: '0.1.0' }, { capabilities: {}, versionNegotiation: { mode: modern ? { pin: '2026-07-28' } : 'legacy' } });
  try { await client.connect(transport); assert.equal(client.getNegotiatedProtocolVersion(), modern ? '2026-07-28' : '2025-11-25'); await exercise(client); }
  finally { await client.close(); }
});

for (const modern of [false, true]) test(`官方 SDK Streamable HTTP ${modern ? '2026' : '2025'}：同一工具契约，认证/Origin/Host与拒绝参数`, { timeout: 15000 }, async () => {
  const token = randomBytes(32).toString('hex');
  const game = new MemoryGame();
  const host = await startHttpHost({ game, capabilities: fixtureCapabilities, authorization: { token, profileId: 'offline', scopes: ['read', 'write'] }, port: 0 });
  const client = new Client({ name: 'skylineslink-offline-test', version: '0.1.0' }, { capabilities: {}, versionNegotiation: { mode: modern ? { pin: '2026-07-28' } : 'legacy' } });
  try {
    await client.connect(new StreamableHTTPClientTransport(host.url, { requestInit: { headers: { authorization: `Bearer ${token}` } } }));
    assert.equal(client.getNegotiatedProtocolVersion(), modern ? '2026-07-28' : '2025-11-25');
    assert.deepEqual(await exercise(client), syntheticContext);
    for (const headers of [{}, { authorization: `Bearer ${token}`, origin: 'https://example.invalid' }]) {
      const response = await fetch(host.url, { method: 'POST', headers: { 'content-type': 'application/json', ...headers }, body: '{}' });
      assert.ok(response.status === 401 || response.status === 403);
    }
    const badHost = await new Promise<number>((resolve, reject) => {
      const req = request(host.url, { method: 'POST', headers: { host: 'example.invalid', authorization: `Bearer ${token}`, 'content-type': 'application/json' } }, response => { response.resume(); resolve(response.statusCode!); });
      req.once('error', reject); req.end('{}');
    });
    assert.equal(badHost, 403);
    assert.equal(game.calls.filter(id => id === 'system.context').length, 1);
  } finally { await client.close(); const closing = host.close(); assert.equal(host.close(), closing); await closing; }
  assert.deepEqual(host.usage(), { requests: 0, pipelines: 0, connections: 0, handlers: 0 });
});

test('HTTP 2025初始化路径仍使用同一schema与结果', { timeout: 10000 }, async () => {
  const token = randomBytes(32).toString('hex');
  const host = await startHttpHost({ game: new MemoryGame(), authorization: { token, profileId: 'legacy-test', scopes: ['read'] }, port: 0 });
  try {
    const response = await fetch(host.url, { method: 'POST', headers: { 'content-type': 'application/json', accept: 'application/json, text/event-stream', authorization: `Bearer ${token}` },
      body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'legacy-test', version: '1' } } }) });
    assert.equal(response.status, 200);
    const body = await response.text();
    assert.match(body, /2025-11-25/);
    assert.match(body, /SkylinesLink/);
  } finally { await host.close(); }
});
