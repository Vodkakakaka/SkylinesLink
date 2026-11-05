import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createServer, request } from 'node:http';
import { PassThrough } from 'node:stream';
import { randomBytes } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';
import { Client, StreamableHTTPClientTransport } from '@modelcontextprotocol/client';
import { createMcpHandler, type JSONRPCMessage, type Transport } from '@modelcontextprotocol/server';
import { serveStdio, StdioServerTransport } from '@modelcontextprotocol/server/stdio';
import { toNodeHandler } from '@modelcontextprotocol/node';
import { createMcpServer } from '../../src/host/src/mcp/server.ts';
import { startStdioHost } from '../../src/host/src/mcp/transports/stdio.ts';
import { startHttpHost } from '../../src/host/src/mcp/transports/http.ts';
import { probeLimits } from '../../src/host/src/shared/limits.ts';
import { MemoryGame, liveLookup } from './fixture.ts';
import { capabilities, type OperationQueryV3 } from '../../contracts/ts/index.ts';

async function inverseReplies(modern: boolean, http: boolean, bounded: boolean, signal: AbortSignal, fixtureError?: Error) {
  const releases = new Map<string, () => void>();
  let entered!: () => void;
  let entryFailed!: (error: unknown) => void;
  const twoEntered = new Promise<void>((resolve, reject) => { entered = resolve; entryFailed = reject; });
  // A failure can arrive before the helper reaches its readiness await.
  void twoEntered.catch(() => {});
  const aborted = () => entryFailed(signal.reason);
  signal.addEventListener('abort', aborted, { once: true });
  if (signal.aborted) aborted();
  const activeQuery = capabilities.find(capability => capability.id === 'operations.get')!;
  const requests: Promise<unknown>[] = [];
  let closed = 0;
  const game = {
    call: async (_id: string, revision: string, input: unknown) => {
      try {
        if (fixtureError) throw fixtureError;
        assert.equal(revision, activeQuery.schemaRevision);
        const selector = (input as OperationQueryV3).selector;
        assert.equal(selector.kind, 'operationId');
        if (selector.kind !== 'operationId') throw new Error('Wrong fixture selector');
        await new Promise<void>(resolve => { releases.set(selector.operationId, resolve); if (releases.size === 2) entered(); });
        return liveLookup(selector, { lookup: { kind: 'found', operationId: selector.operationId, originInstanceId: 'offline-history', state: 'queued', mayHaveApplied: false, durability: 'volatile' } });
      } catch (error) { entryFailed(error); throw error; }
    },
    close: async () => { closed++; for (const release of releases.values()) release(); },
  };
  const client = new Client({ name: 'inverse-offline', version: '1' }, { capabilities: {}, versionNegotiation: { mode: modern ? { pin: '2026-07-28' } : 'legacy' } });
  let transport: Transport;
  let cleanup!: () => Promise<void>;
  let usage: (() => unknown) | undefined;
  if (http) {
    const token = randomBytes(32).toString('hex');
    const authorization = { token, profileId: 'inverse-offline', scopes: ['read'] };
    let url: URL;
    if (bounded) {
      const host = await startHttpHost({ game, port: 0, authorization });
      url = host.url; cleanup = () => host.close(); usage = () => host.usage();
    } else {
      // Bare public SDK baseline, with the same per-request identity and stateless legacy policy.
      const handler = createMcpHandler(() => createMcpServer({ game }), { legacy: 'stateless', maxSubscriptions: 1, keepAliveMs: 0 });
      const adapter = toNodeHandler(handler, { onerror: () => {} });
      const server = createServer((req, res) => {
        void adapter({ method: req.method!, url: req.url!, headers: req.headers,
          auth: { token, clientId: authorization.profileId, scopes: authorization.scopes },
          [Symbol.asyncIterator]: () => req[Symbol.asyncIterator]() }, res).catch(() => res.destroy());
      });
      await new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve));
      const address = server.address(); assert.ok(address && typeof address !== 'string');
      url = new URL(`http://127.0.0.1:${address.port}/mcp`);
      cleanup = async () => { server.closeAllConnections(); await Promise.all([handler.close(), game.close(), new Promise<void>(resolve => server.close(() => resolve()))]); };
    }
    transport = new StreamableHTTPClientTransport(url, { requestInit: { headers: { authorization: `Bearer ${token}` } } });
  } else {
    const input = new PassThrough(), output = new PassThrough();
    let buffer = '';
    transport = { start: async () => {}, send: async message => { input.write(JSON.stringify(message) + '\n'); }, close: async () => { input.end(); } };
    output.once('close', () => transport.onclose?.());
    output.on('data', (chunk: Buffer) => {
      buffer += chunk.toString('utf8'); assert.ok(buffer.length <= probeLimits.messageBytes * 2);
      for (let end = buffer.indexOf('\n'); end >= 0; end = buffer.indexOf('\n')) {
        const line = buffer.slice(0, end); buffer = buffer.slice(end + 1); transport.onmessage?.(JSON.parse(line) as JSONRPCMessage);
      }
    });
    if (bounded) {
      const host = startStdioHost({ game }, input, output);
      cleanup = () => host.close(); usage = () => host.transport.usage;
    } else {
      const handle = serveStdio(() => createMcpServer({ game }), { transport: new StdioServerTransport(input, output), maxSubscriptions: 1, onerror: () => {} });
      cleanup = async () => { await handle.close(); input.destroy(); output.destroy(); await game.close(); };
    }
  }
  try {
    await client.connect(transport);
    const protocol = client.getNegotiatedProtocolVersion();
    assert.equal(protocol, modern ? '2026-07-28' : '2025-11-25');
    const completionOrder: string[] = [];
    const query = (id: string) => {
      const pending = client.callTool({ name: activeQuery.toolName, arguments: { selector: { kind: 'operationId', operationId: id } } }).then(value => {
        if (value.isError) throw new Error('Inverse fixture query failed before both calls became ready.');
        completionOrder.push(id); return value;
      }).catch(error => { entryFailed(error); throw error; });
      requests.push(pending); void pending.catch(() => {});
      return pending;
    };
    const first = query('first'), second = query('第二');
    await twoEntered; releases.get('第二')!(); const resultB = await second;
    releases.get('first')!(); const resultA = await first;
    assert.deepEqual(completionOrder, ['第二', 'first']);
    for (const [id, result] of [['first', resultA], ['第二', resultB]] as const) {
      assert.notEqual(result.isError, true);
      assert.equal((result.structuredContent as { result: { lookup: { operationId: string } } }).result.lookup.operationId, id);
      assert.deepEqual((result.structuredContent as { selector: unknown }).selector, { kind: 'operationId', operationId: id });
      assert.deepEqual(JSON.parse((result.content[0] as { text: string }).text), result.structuredContent);
    }
    const invalid = await client.callTool({ name: 'skylines_operation_get', arguments: { operationId: 'old-shape' } });
    assert.equal(invalid.isError, true);
    return { protocol, completionOrder, resultA, resultB, invalid };
  } finally {
    signal.removeEventListener('abort', aborted);
    for (const release of releases.values()) release();
    const settled = Promise.allSettled(requests);
    try { await client.close(); }
    finally { try { await cleanup(); } finally { await settled; } }
    assert.equal(closed, 1);
    if (usage) assert.deepEqual(usage(), http
      ? { handlers: 0, requests: 0, pipelines: 0, connections: 0 }
      : { activeRequests: 0, sendBytes: 0, sendCount: 0, closed: true });
  }
}

for (const http of [false, true]) for (const modern of [false, true]) test(`bare SDK 对照 ${http ? 'HTTP' : 'stdio'} ${modern ? '2026' : '2025'}：并发逆序读回复保持各自身份`, { timeout: 8000 }, async context => {
  const baseline = await inverseReplies(modern, http, false, context.signal);
  assert.deepEqual(await inverseReplies(modern, http, true, context.signal), baseline);
});

test('逆序夹具在readiness前失败仍关闭真实HTTP及两个请求', { timeout: 8000 }, async context => {
  const failure = new Error('Synthetic inverse fixture failure before readiness.');
  await assert.rejects(inverseReplies(false, true, true, context.signal, failure), error => error === failure);
});

test('真实 HTTP 请求体在分派前受字节/结构/UTF8/绝对期限限制，关闭后额度回零', { timeout: 5000 }, async () => {
  const game = new MemoryGame(); const token = randomBytes(32).toString('hex');
  const limits = { ...probeLimits, messageBytes: 1024, receiveDeadlineMs: 120, sendDeadlineMs: 120 };
  const host = await startHttpHost({ game, port: 0, limits, authorization: { token, profileId: 'body-offline', scopes: ['read'] } });
  try {
    for (const sample of [
      { name: 'declared bytes', body: Buffer.from('{}'), declared: '1025', slow: false, status: 413 },
      { name: 'chunked bytes', body: Buffer.alloc(1025, 32), declared: undefined, slow: false, status: 413 },
      { name: 'JSON depth', body: Buffer.from('['.repeat(33) + '0' + ']'.repeat(33)), declared: undefined, slow: false, status: 413 },
      { name: 'invalid UTF8', body: Buffer.from([0xff]), declared: undefined, slow: false, status: 400 },
      { name: 'incomplete body', body: Buffer.from('{'), declared: undefined, slow: true, status: undefined },
    ]) {
      const outcome = await new Promise<number | string>((resolve, reject) => {
        const req = request(host.url, { method: 'POST', agent: false, headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json',
          ...(sample.declared ? { 'content-length': sample.declared } : { 'transfer-encoding': 'chunked' }) } }, res => { res.resume(); res.once('end', () => resolve(res.statusCode!)); });
        req.once('error', error => { if ('code' in error && error.code === 'ECONNRESET') resolve('ECONNRESET'); else reject(error); });
        req.setTimeout(1000, () => { req.destroy(); reject(new Error('Body fixture failed its finite deadline')); });
        req.write(sample.body); if (!sample.slow) req.end();
      });
      assert.ok(outcome === sample.status || outcome === 'ECONNRESET', `${sample.name}: unexpected outcome ${outcome}`);
      for (let i = 0; i < 30 && (host.usage().requests !== 0 || host.usage().pipelines !== 0); i++) await delay(5);
      assert.equal(host.usage().requests, 0); assert.equal(host.usage().pipelines, 0); assert.equal(host.usage().handlers, 0);
    }
    assert.deepEqual(game.calls, []);
  } finally { await host.close(); }
  assert.deepEqual(host.usage(), { handlers: 0, requests: 0, pipelines: 0, connections: 0 });
});
