import { test } from 'node:test';
import assert from 'node:assert/strict';
import { resolve } from 'node:path';
import { randomBytes } from 'node:crypto';
import { Client, StreamableHTTPClientTransport } from '@modelcontextprotocol/client';
import { startRelayChild, relayClientTransport } from './relay-fixture.ts';
import { startRuntimeFixture } from './runtime-fixture.ts';
import { capabilities, requireContract, schemaIds, type OperationLookupV3 } from '../../contracts/ts/index.ts';
import { startHttpHost, type HttpHost } from '../../src/host/src/mcp/transports/http.ts';
import { DiscoveredGameClient } from '../../src/host/src/game-client/discovered-game-client.ts';

test('真实SDK→产品Host→net48：双Host按原键查持久受理、发行拒绝、即时撤权', { timeout: 15000 }, async () => {
  const fixture = await startRuntimeFixture({ operationProbe: true });
  const clients: Client[] = []; const relays: Awaited<ReturnType<typeof startRelayChild>>[] = [];
  const connect = async (modern: boolean, coreHash = fixture.coreManifestSha256) => {
    const args = ['--transport', 'stdio', '--profile', 'probe', '--environment', 'PROBE', '--core-manifest-sha256', coreHash];
    assert.equal(args.join(' ').includes(fixture.token), false);
    const relay = await startRelayChild(resolve('../../artifacts/host/host.mjs'), args, { env: { ...process.env, USERPROFILE: fixture.base } });
    relays.push(relay);
    const transport = relayClientTransport(relay);
    const client = new Client({ name: 'isolated-product-test', version: '1' }, { capabilities: {}, versionNegotiation: { mode: modern ? { pin: '2026-07-28' } : 'legacy' } });
    clients.push(client); await client.connect(transport);
    assert.equal(client.getNegotiatedProtocolVersion(), modern ? '2026-07-28' : '2025-11-25');
    return client;
  };
  try {
    const legacy = await connect(false); const modern = await connect(true);
    for (const client of [legacy, modern]) assert.deepEqual((await client.listTools()).tools.map(tool => tool.name).sort(), capabilities.map(capability => capability.toolName).sort());
    assert.equal((await fixture.command('metrics')).authenticated, 0, 'initialize/list must not connect to the game');
    for (const client of [legacy, modern]) {
      const result = await client.callTool({ name: 'skylines_context', arguments: {} });
      assert.equal(result.isError, undefined); assert.equal((result.structuredContent as { instanceId: string }).instanceId, fixture.ready.instanceId);
      assert.equal(JSON.stringify(result).includes(fixture.token), false);
      const lookup = await client.callTool({ name: 'skylines_operation_get', arguments: { selector: { kind: 'operationId', operationId: 'missing' } } });
      assert.notEqual(lookup.isError, true);
      assert.deepEqual(lookup.structuredContent, { selector: { kind: 'operationId', operationId: 'missing' },
        result: { kind: 'live', instanceId: fixture.ready.instanceId, lookup: { kind: 'absent' } } });
    }
    assert.equal((await fixture.command('metrics')).authenticated, 2);
    // 仅私有探针 stdin 受理固定合成 Load 意图并抑制回执，不调用原生，也不增加产品提交工具。
    const writeContext = await fixture.command({ command: 'context' });
    assert.equal(writeContext.kind, 'write-context');
    assert.equal(typeof writeContext.writeContextId, 'string');
    const key = { writeContextId: writeContext.writeContextId as string, requestId: 'ack-suppressed-request' };
    assert.deepEqual(await fixture.command({ command: 'submit-lost-ack', ...key }), { kind: 'ack-suppressed' });
    let operationId: string | undefined;
    for (const client of [legacy, modern]) {
      const result = await client.callTool({ name: 'skylines_operation_get', arguments: { selector: { kind: 'submissionKey', submissionKey: key } } });
      assert.notEqual(result.isError, true);
      requireContract(schemaIds.OperationLookupV3, result.structuredContent);
      const envelope = result.structuredContent as OperationLookupV3;
      assert.deepEqual(envelope.selector, { kind: 'submissionKey', submissionKey: key });
      assert.equal(envelope.result.kind, 'live');
      if (envelope.result.kind !== 'live') throw new Error('Expected current authority result.');
      assert.equal(envelope.result.instanceId, fixture.ready.instanceId);
      const lookup = envelope.result.lookup;
      assert.equal(lookup.kind, 'found');
      if (lookup.kind !== 'found') throw new Error('Expected confirmed operation in the real journal.');
      assert.equal(lookup.state, 'queued'); assert.equal(lookup.mayHaveApplied, false); assert.equal(lookup.durability, 'durable');
      assert.equal(lookup.originInstanceId, fixture.ready.instanceId);
      if (operationId !== undefined) assert.equal(lookup.operationId, operationId); else operationId = lookup.operationId;
      assert.deepEqual(JSON.parse((result.content[0] as { text: string }).text), result.structuredContent);
      const byId = await client.callTool({ name: 'skylines_operation_get', arguments: { selector: { kind: 'operationId', operationId: lookup.operationId } } });
      assert.deepEqual(byId.structuredContent, { selector: { kind: 'operationId', operationId: lookup.operationId }, result: envelope.result });
    }
    const duplicate = await fixture.command({ command: 'submit', ...key });
    assert.equal(duplicate.kind, 'accepted'); assert.equal(duplicate.operationId, operationId);
    const mismatch = await connect(false, 'b'.repeat(64));
    const rejected = await mismatch.callTool({ name: 'skylines_context', arguments: {} });
    assert.equal((rejected.structuredContent as { error: { code: string } }).error.code, 'INCOMPATIBLE_COMPONENT');
    assert.equal((await fixture.command('metrics')).authenticated, 2, 'mismatched release must not connect');
    assert.equal((await fixture.command('revoke')).kind, 'revoked');
    for (const client of [legacy, modern]) {
      const result = await client.callTool({ name: 'skylines_context', arguments: {} });
      assert.equal(result.isError, true); assert.equal((result.structuredContent as { error: { code: string } }).error.code, 'UNAUTHORIZED');
      const guide = await client.callTool({ name: 'skylines_guidance_get', arguments: { guideId: 'getting-started' } });
      assert.notEqual(guide.isError, true); assert.equal((guide.structuredContent as { sourceKind: string }).sourceKind, 'productGuide');
      const stillDenied = await client.callTool({ name: 'skylines_context', arguments: {} });
      assert.equal(stillDenied.isError, true);
      // The rejection can close the existing pipe before this later read; neither
      // classification permits public guide access to reopen the game session.
      assert.ok(['UNAUTHORIZED', 'GAME_UNAVAILABLE'].includes((stillDenied.structuredContent as { error: { code: string } }).error.code));
    }
  } finally {
    const results = await Promise.allSettled(clients.map(client => client.close()));
    await Promise.all(relays.map(relay => relay.cleanup())); await fixture.stop();
    for (const result of results) if (result.status === 'rejected') throw result.reason;
  }
});

test('真实HTTP两跳：两代SDK经发现与net48查回同一持久受理，发行/撤权拒绝且实际清理', { timeout: 15000 }, async () => {
  const fixture = await startRuntimeFixture({ operationProbe: true });
  const clients: Client[] = [];
  const hosts: HttpHost[] = [];
  // 外部凭据只属于隔离夹具；runtime的持久externalHttpToken仍为null。
  // 随机端口不证明生产HTTP profile配置、固定端口或业务drain已实现。
  const bearer = randomBytes(32).toString('hex');
  assert.equal(bearer === fixture.token, false, 'HTTP and internal credentials must be independent');
  const connect = async (modern: boolean, coreHash = fixture.coreManifestSha256) => {
    const game = new DiscoveredGameClient({ userProfile: fixture.base, environment: 'probe', profile: 'probe', coreManifestSha256: coreHash });
    const host = await startHttpHost({ game, port: 0, authorization: { token: bearer, profileId: 'isolated-http-reader', scopes: ['read'] } });
    hosts.push(host);
    // 即使是有效的内部token，也不能直接越过HTTP这一跳。
    const denied = await fetch(host.url, { method: 'POST', headers: { authorization: `Bearer ${fixture.token}`, 'content-type': 'application/json' }, body: '{}' });
    assert.equal(denied.status, 401); await denied.arrayBuffer();
    const client = new Client({ name: 'isolated-http-discovery-test', version: '1' }, { capabilities: {}, versionNegotiation: { mode: modern ? { pin: '2026-07-28' } : 'legacy' } });
    clients.push(client);
    await client.connect(new StreamableHTTPClientTransport(host.url, { requestInit: { headers: { authorization: `Bearer ${bearer}` } } }));
    assert.equal(client.getNegotiatedProtocolVersion(), modern ? '2026-07-28' : '2025-11-25');
    assert.deepEqual((await client.listTools()).tools.map(tool => tool.name).sort(), capabilities.map(capability => capability.toolName).sort());
    return client;
  };
  const noSecrets = (value: unknown) => {
    const json = JSON.stringify(value);
    assert.equal(json.includes(fixture.token) || json.includes(bearer), false, 'MCP output must not disclose either credential');
  };
  try {
    const legacy = await connect(false), modern = await connect(true);
    const lazy = await fixture.command('metrics');
    assert.equal(lazy.authenticated, 0, 'HTTP initialize/list must not authenticate an internal pipe');
    assert.equal(lazy.connected, 0, 'HTTP initialize/list must not open an internal pipe');

    const context = await fixture.command({ command: 'context' });
    assert.equal(context.kind, 'write-context'); assert.equal(typeof context.writeContextId, 'string');
    const key = { writeContextId: context.writeContextId as string, requestId: 'http-ack-suppressed-request' };
    assert.deepEqual(await fixture.command({ command: 'submit-lost-ack', ...key }), { kind: 'ack-suppressed' });
    let operationId: string | undefined;
    for (const client of [legacy, modern]) {
      const result = await client.callTool({ name: 'skylines_operation_get', arguments: { selector: { kind: 'submissionKey', submissionKey: key } } });
      assert.notEqual(result.isError, true); noSecrets(result);
      requireContract(schemaIds.OperationLookupV3, result.structuredContent);
      const envelope = result.structuredContent as OperationLookupV3;
      assert.deepEqual(envelope.selector, { kind: 'submissionKey', submissionKey: key });
      assert.equal(envelope.result.kind, 'live');
      if (envelope.result.kind !== 'live') throw new Error('Expected current authority result.');
      assert.equal(envelope.result.instanceId, fixture.ready.instanceId);
      const lookup = envelope.result.lookup;
      assert.equal(lookup.kind, 'found');
      if (lookup.kind !== 'found') throw new Error('Expected the accepted record from the real journal.');
      assert.equal(lookup.state, 'queued'); assert.equal(lookup.mayHaveApplied, false); assert.equal(lookup.durability, 'durable');
      assert.equal(lookup.originInstanceId, fixture.ready.instanceId);
      if (operationId === undefined) operationId = lookup.operationId; else assert.equal(lookup.operationId, operationId);
      assert.deepEqual(JSON.parse((result.content[0] as { text: string }).text), result.structuredContent);
      const byId = await client.callTool({ name: 'skylines_operation_get', arguments: { selector: { kind: 'operationId', operationId: lookup.operationId } } });
      assert.deepEqual(byId.structuredContent, { selector: { kind: 'operationId', operationId: lookup.operationId }, result: envelope.result }); noSecrets(byId);
      const observed = await client.callTool({ name: 'skylines_context', arguments: {} });
      assert.notEqual(observed.isError, true);
      assert.equal((observed.structuredContent as { instanceId: string }).instanceId, fixture.ready.instanceId); noSecrets(observed);
    }
    assert.equal((await fixture.command('metrics')).authenticated, 2);
    const repeated = await fixture.command({ command: 'submit', ...key });
    assert.equal(repeated.kind, 'accepted'); assert.equal(repeated.operationId, operationId);

    const mismatch = await connect(false, 'b'.repeat(64));
    const rejected = await mismatch.callTool({ name: 'skylines_context', arguments: {} });
    assert.equal(rejected.isError, true);
    assert.equal((rejected.structuredContent as { error: { code: string } }).error.code, 'INCOMPATIBLE_COMPONENT'); noSecrets(rejected);
    assert.equal((await fixture.command('metrics')).authenticated, 2, 'release mismatch must fail before internal authentication');
    assert.equal((await fixture.command('revoke')).kind, 'revoked');
    for (const client of [legacy, modern]) {
      // 外Bearer仍有效；此拒绝来自真实runtime的实时授权，不是HTTP模拟拒绝。
      const result = await client.callTool({ name: 'skylines_operation_get', arguments: { selector: { kind: 'submissionKey', submissionKey: key } } });
      assert.equal(result.isError, true);
      assert.equal((result.structuredContent as { error: { code: string } }).error.code, 'UNAUTHORIZED'); noSecrets(result);
    }
  } finally {
    const clientsClosed = await Promise.allSettled(clients.map(client => client.close()));
    try {
      const hostsClosed = await Promise.allSettled(hosts.map(host => host.close()));
      for (const host of hosts) {
        assert.equal(host.server.listening, false);
        assert.deepEqual(host.usage(), { requests: 0, pipelines: 0, connections: 0, handlers: 0 });
      }
      for (const result of [...clientsClosed, ...hostsClosed]) if (result.status === 'rejected') throw result.reason;
    } finally {
      // stop等待真实net48退出，并检查连接和owned payload的数量/容量都归零。
      await fixture.stop();
    }
  }
});
