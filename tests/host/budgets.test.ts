import { test } from 'node:test';
import assert from 'node:assert/strict';
import { PassThrough, Writable } from 'node:stream';
import { setTimeout as delay } from 'node:timers/promises';
import { startStdioHost, JsonLineBudget } from '../../src/host/src/mcp/transports/stdio.ts';
import { probeLimits } from '../../src/host/src/shared/limits.ts';
import { checkRawJson, measureJson } from '../../src/host/src/shared/json-budget.ts';
import { BoundedTransport } from '../../src/host/src/mcp/transports/bounded-transport.ts';
import { MemoryGame, fixtureCapabilities } from './fixture.ts';
import { syntheticContext } from './fixture.ts';
import { startHttpHost } from '../../src/host/src/mcp/transports/http.ts';
import { Client, StreamableHTTPClientTransport } from '@modelcontextprotocol/client';
import { randomBytes } from 'node:crypto';
import { createConnection } from 'node:net';
import { InMemoryTransport } from '@modelcontextprotocol/server';
import { createMcpServer, WorkBudget } from '../../src/host/src/mcp/server.ts';
import { NamedPipeGameClient } from '../../src/host/src/game-client/named-pipe-client.ts';
import { GameClientError } from '../../src/host/src/game-client/game-client.ts';
import { parseHostOptions, isDeploymentIdentifier } from '../../src/host/src/composition/options.ts';
import type { Transport, JSONRPCMessage, TransportSendOptions, MessageExtraInfo } from '@modelcontextprotocol/server';

const tiny = { ...probeLimits, receiveDeadlineMs: 80, sendDeadlineMs: 80 };

test('一个close分支失败仍结束其他自有传输分支，错误显式返回', async () => {
  let innerClosed = 0;
  const inner: Transport = { start: async () => {}, close: async () => { innerClosed++; }, send: async () => {} };
  const transport = new BoundedTransport(inner, tiny, { settled: async () => {}, close: async () => { throw new Error('fixture close failure'); } });
  await transport.start();
  await assert.rejects(transport.close());
  assert.equal(innerClosed, 1);
  assert.equal(transport.usage.closed, true);
});

test('原始输入先限制bytes/depth/tokens；共享schema非循环对象可用', () => {
  assert.throws(() => checkRawJson(Buffer.alloc(65537, 32), probeLimits));
  assert.throws(() => checkRawJson(Buffer.from('['.repeat(33) + '0' + ']'.repeat(33)), probeLimits));
  assert.throws(() => checkRawJson(Buffer.from('[0,0,0]'), { ...probeLimits, jsonTokens: 2 }));
  assert.throws(() => checkRawJson(Buffer.from([0xff]), probeLimits));
  const shared = { x: 1 }; assert.ok(measureJson({ a: shared, b: shared }, probeLimits) > 0);
  const cycle: { x?: unknown } = {}; cycle.x = cycle; assert.throws(() => measureJson(cycle, probeLimits));
  assert.throws(() => measureJson(new Array(1000000), probeLimits));
  assert.ok(measureJson(new Array(8), probeLimits) >= JSON.stringify(new Array(8)).length);
  assert.throws(() => measureJson(new Date(), probeLimits));
  let accessed = false; const hooked: unknown[] = [];
  Object.defineProperty(hooked, 'toJSON', { get() { accessed = true; return () => 'unmeasured'; } });
  assert.throws(() => measureJson(hooked, probeLimits)); assert.equal(accessed, false);
  const getter = Object.defineProperty({}, 'field', { enumerable: true, get() { accessed = true; return 'value'; } });
  assert.throws(() => measureJson(getter, probeLimits)); assert.equal(accessed, false);
  let descriptors = 0;
  const many = new Proxy(Object.fromEntries(Array.from({ length: 4096 }, (_, i) => [`k${i}`, i])), {
    getOwnPropertyDescriptor(target, key) { descriptors++; return Reflect.getOwnPropertyDescriptor(target, key); },
  });
  assert.throws(() => measureJson(many, { ...probeLimits, jsonTokens: 128 }));
  assert.ok(descriptors <= 256, 'reject oversized plain-value metadata before copying every property descriptor');
  let arrayDescriptors = 0;
  let nested: unknown = 0;
  for (let depth = 0; depth < 10; depth++) {
    const values = new Array<unknown>(4000).fill(0);
    values[3999] = nested;
    nested = new Proxy(values, {
      getOwnPropertyDescriptor(target, key) { arrayDescriptors++; return Reflect.getOwnPropertyDescriptor(target, key); },
    });
  }
  assert.throws(() => measureJson(nested, probeLimits));
  assert.ok(arrayDescriptors <= probeLimits.jsonTokens + probeLimits.jsonDepth,
    'reserve pending sibling values before inspecting another nested array');
  assert.ok(measureJson([0], { ...probeLimits, jsonTokens: 2 }) >= 3);
  assert.throws(() => measureJson([0], { ...probeLimits, jsonTokens: 1 }));
  assert.ok(measureJson({ x: 0 }, { ...probeLimits, jsonTokens: 3 }) >= 7);
  assert.throws(() => measureJson({ x: 0 }, { ...probeLimits, jsonTokens: 2 }));
});

test('半条stdio消息按绝对期限关闭，后续滴入不能延长', { timeout: 2000 }, async () => {
  const lines = new JsonLineBudget(tiny); lines.on('error', () => {}); lines.resume();
  lines.write('{'); await delay(50); lines.write(' '); await delay(70);
  assert.equal(lines.destroyed, true);
});

test('公开Transport装饰器透传选项，输出租约等实际输出完成', { timeout: 2000 }, async () => {
  let release!: () => void; const settled = new Promise<void>(resolve => { release = resolve; });
  let sentOptions: TransportSendOptions | undefined; let protocol = ''; let supported: string[] = [];
  const inner: Transport = { sessionId: 'fixture', hasPerRequestStream: true, start: async () => {}, close: async () => {}, send: async (_message, options) => { sentOptions = options; }, setProtocolVersion: version => { protocol = version; }, setSupportedProtocolVersions: versions => { supported = versions; } };
  const transport = new BoundedTransport(inner, tiny, { settled: () => settled, close: async () => { release(); } });
  await transport.start(); transport.setProtocolVersion('fixture-version'); assert.equal(protocol, 'fixture-version'); assert.equal(transport.sessionId, 'fixture');
  const versions = ['2025-11-25', '2026-07-28']; transport.setSupportedProtocolVersions(versions); assert.equal(supported, versions);
  assert.equal(transport.hasPerRequestStream, true); inner.sessionId = 'next-session'; assert.equal(transport.sessionId, 'next-session');
  let received: JSONRPCMessage | undefined; let receivedExtra: MessageExtraInfo | undefined;
  transport.onmessage = (message, extra) => { received = message; receivedExtra = extra; };
  const extra: MessageExtraInfo = { request: new Request('http://localhost/mcp'), authInfo: { token: 'synthetic', clientId: 'fixture', scopes: ['read'] }, closeSSEStream: () => {}, closeStandaloneSSEStream: () => {} };
  const request: JSONRPCMessage = { jsonrpc: '2.0', id: 1, method: 'ping' }; inner.onmessage?.(request, extra); assert.equal(received, request); assert.equal(receivedExtra, extra);
  const options: TransportSendOptions = { relatedRequestId: 1, resumptionToken: 'synthetic-resume', onresumptiontoken: () => {}, requestSignal: new AbortController().signal, onRequestStreamEnd: () => {}, headers: { 'x-fixture': 'value' } };
  const sending = transport.send({ jsonrpc: '2.0', id: 1, result: {} }, options);
  await delay(0); assert.equal(sentOptions, options); assert.equal(transport.usage.sendCount, 1); assert.ok(transport.usage.sendBytes > 0);
  release(); await sending; assert.equal(transport.usage.sendBytes, 0); assert.equal(transport.usage.activeRequests, 0); await transport.close();
});

test('stdio慢读者：实际关闭输出后释放发送租约；EOF关闭GameClient', { timeout: 3000 }, async () => {
  const input = new PassThrough(); let closed = 0;
  const game = new MemoryGame(); game.close = async () => { closed++; };
  const output = new Writable({ write(_chunk, _encoding, _callback) { /* fixture deliberately never drains */ } });
  const host = startStdioHost({ game, limits: tiny }, input, output);
  input.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'slow-fixture', version: '1' } } }) + '\n');
  await delay(200); assert.equal(output.destroyed, true);
  await host.close(); assert.equal(host.transport.usage.sendBytes, 0); assert.ok(closed >= 1);
  const input2 = new PassThrough(); const output2 = new PassThrough(); output2.resume();
  const host2 = startStdioHost({ game, limits: tiny }, input2, output2); const before = closed;
  input2.end(); await delay(30); assert.ok(closed > before, 'EOF must release the game connection without a signal'); await host2.close();
});

for (const legacy of [false, true]) test(`HTTP ${legacy ? '2025' : 'modern'}取消不提前归还实际handler额度`, { timeout: 5000 }, async () => {
  let started!: () => void; const entered = new Promise<void>(resolve => { started = resolve; });
  let finish!: () => void; let observedSignal: AbortSignal | undefined;
  const token = randomBytes(32).toString('hex');
  const game = { call: async (_id: string, _revision: string, _args: unknown, signal: AbortSignal) => {
    observedSignal = signal; started(); await new Promise<void>(resolve => { finish = resolve; }); return { ...syntheticContext };
  }, close: async () => { finish?.(); } };
  const host = await startHttpHost({ game, port: 0, limits: { ...probeLimits, activeRequests: 1 }, authorization: { token, profileId: 'cancel-fixture', scopes: ['read'] } });
  const client = new Client({ name: 'cancel-fixture', version: '1' }, { capabilities: {}, versionNegotiation: { mode: { pin: '2026-07-28' } } });
  const abort = new AbortController();
  try {
    let pending: Promise<unknown>;
    if (legacy) pending = fetch(host.url, { method: 'POST', signal: abort.signal,
      headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json', accept: 'application/json, text/event-stream', 'mcp-protocol-version': '2025-11-25' },
      body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name: 'skylines_context', arguments: {} } }) });
    else {
      await client.connect(new StreamableHTTPClientTransport(host.url, { requestInit: { headers: { authorization: `Bearer ${token}` } } }));
      assert.match(client.getNegotiatedProtocolVersion() ?? '', /^2026-/);
      pending = client.callTool({ name: 'skylines_context', arguments: {} }, { signal: abort.signal });
    }
    const rejected = assert.rejects(pending);
    await entered; assert.equal(host.usage().handlers, 1); abort.abort(); await rejected;
    for (let i = 0; i < 30 && !observedSignal?.aborted; i++) await delay(10);
    assert.equal(observedSignal?.aborted, true); assert.equal(host.usage().handlers, 1, 'cancel does not mean the real work finished');
    finish(); for (let i = 0; i < 30 && host.usage().handlers !== 0; i++) await delay(10);
    assert.equal(host.usage().handlers, 0);
  } finally { await client.close(); await host.close(); }
  assert.deepEqual(host.usage(), { handlers: 0, requests: 0, pipelines: 0, connections: 0 });
});

test('接入层拒绝同块请求洪水与通知洪水，不创建无界错误队列', { timeout: 2000 }, async () => {
  for (const notifications of [false, true]) {
    let accepted = 0;
    const inner: Transport = { start: async () => {}, close: async () => {}, send: async () => {} };
    const transport = new BoundedTransport(inner, { ...tiny, activeRequests: 2, messagesPerWindow: 3 }, { close: async () => {}, settled: async () => {} });
    await transport.start(); transport.onmessage = () => { accepted++; };
    for (let id = 0; id < 100; id++) inner.onmessage?.({ jsonrpc: '2.0', ...(notifications ? {} : { id }), method: notifications ? 'notifications/initialized' : 'ping' });
    await transport.close(); assert.equal(accepted, notifications ? 3 : 2); assert.equal(transport.usage.activeRequests, 0);
  }
});

test('SDK解析前，同一原始输入块的小消息洪水受帧预算限制', { timeout: 2000 }, async () => {
  let admitted = 0; const lines = new JsonLineBudget({ ...tiny, messagesPerWindow: 4 });
  lines.on('error', () => {}); lines.on('data', () => { admitted++; });
  lines.end(Buffer.from('{}\n'.repeat(20000))); await delay(0);
  assert.equal(lines.destroyed, true); assert.equal(admitted, 4);
});

test('真实SDK入口的握手与非法消息洪泛有界关闭且不触及游戏', { timeout: 2000 }, async () => {
  for (const invalid of [false, true]) {
    const input = new PassThrough(); const output = new PassThrough(); let outputBytes = 0;
    output.on('data', (chunk: Buffer) => { outputBytes += chunk.length; });
    const game = new MemoryGame(); let closed = 0; game.close = async () => { closed++; };
    const host = startStdioHost({ game, limits: { ...tiny, activeRequests: 2, messagesPerWindow: 4 } }, input, output);
    const payload = Array.from({ length: 12 }, (_, id) => JSON.stringify(invalid ? { invalid: true } : { jsonrpc: '2.0', id, method: 'initialize', params: { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'flood', version: '1' } } })).join('\n') + '\n';
    input.write(payload); await delay(0); await host.close();
    assert.equal(closed, 1); assert.deepEqual(game.calls, []);
    assert.ok(outputBytes <= tiny.messageBytes * 2);
    assert.deepEqual(host.transport.usage, { activeRequests: 0, sendBytes: 0, sendCount: 0, closed: true });
  }
});

test('HTTP半个请求头尚未触发handler也必须在有限期限断开', { timeout: 2000 }, async () => {
  const host = await startHttpHost({ game: new MemoryGame(), port: 0, limits: tiny, authorization: { token: randomBytes(32).toString('hex'), profileId: 'header-fixture', scopes: ['read'] } });
  const socket = createConnection({ host: host.url.hostname, port: Number(host.url.port) }); socket.on('error', () => {});
  try {
    await new Promise<void>(resolve => socket.once('connect', resolve)); socket.write('POST /mcp HTTP/1.1\r\nX-Slow: ');
    await delay(500); assert.equal(socket.destroyed, true); assert.equal(host.usage().handlers, 0);
  } finally { socket.destroy(); await host.close(); }
});

test('HTTP零字节连接也有首请求期限，且端口冲突不改变原监听', { timeout: 2000 }, async () => {
  const options = { game: new MemoryGame(), port: 0, limits: tiny, authorization: { token: randomBytes(32).toString('hex'), profileId: 'idle-fixture', scopes: ['read'] } };
  const host = await startHttpHost(options);
  const socket = createConnection({ host: host.url.hostname, port: Number(host.url.port) }); socket.on('error', () => {});
  try {
    await new Promise<void>(resolve => socket.once('connect', resolve));
    await assert.rejects(startHttpHost({ ...options, port: Number(host.url.port) }), (error: unknown) => error instanceof Error && 'code' in error && error.code === 'EADDRINUSE');
    assert.equal(host.server.listening, true);
    await delay(250);
    assert.equal(socket.destroyed, true, 'a silent peer cannot retain its connection slot indefinitely');
    assert.equal(host.usage().connections, 0);
  } finally { socket.destroy(); await host.close(); }
});

test('HTTP关闭等待真实工作与SDK适配收敛，另一清理分支失败仍释放监听', { timeout: 3000 }, async () => {
  let entered!: () => void; const started = new Promise<void>(resolve => { entered = resolve; });
  let release!: () => void; let closeEntered!: () => void;
  const workDone = new Promise<void>(resolve => { release = resolve; });
  const closingGame = new Promise<void>(resolve => { closeEntered = resolve; });
  const game = { call: async () => { entered(); await workDone; return { ...syntheticContext }; }, close: async () => { closeEntered(); await workDone; throw new Error('synthetic close failure'); } };
  const token = randomBytes(32).toString('hex');
  const host = await startHttpHost({ game, port: 0, authorization: { token, profileId: 'shutdown', scopes: ['read'] } });
  const response = fetch(host.url, { method: 'POST', headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json', accept: 'application/json, text/event-stream', 'mcp-protocol-version': '2025-11-25' }, body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name: 'skylines_context', arguments: {} } }) });
  const abandoned = response.then(async reply => { await reply.text(); }).catch(() => {});
  try {
    await started; assert.equal(host.usage().pipelines, 1); assert.equal(host.usage().handlers, 1);
    let settled = false; const close = host.close(); const rejected = assert.rejects(close, /清理未全部成功/).then(() => { settled = true; });
    assert.equal(host.close(), close); await closingGame; await delay(10);
    assert.equal(settled, false, 'close cannot finish while owned work still uses its result');
    assert.equal(host.usage().handlers, 1);
    release(); await rejected; await abandoned;
    assert.equal(host.server.listening, false);
    assert.deepEqual(host.usage(), { handlers: 0, requests: 0, pipelines: 0, connections: 0 });
  } finally { release(); await host.close().catch(() => {}); await abandoned; }
});

test('legacy stateless取消通知不伪称停止执行，绝对期限最终关闭原请求', { timeout: 3000 }, async () => {
  let started!: () => void; const entered = new Promise<void>(resolve => { started = resolve; });
  let finish!: () => void; let observedSignal: AbortSignal | undefined;
  const token = randomBytes(32).toString('hex');
  const game = { call: async (_id: string, _rev: string, _args: unknown, signal: AbortSignal) => {
    observedSignal = signal; started(); await new Promise<void>(resolve => { finish = resolve; }); return { ...syntheticContext };
  }, close: async () => { finish?.(); } };
  const host = await startHttpHost({ game, port: 0, limits: { ...probeLimits, receiveDeadlineMs: 250, sendDeadlineMs: 250 }, authorization: { token, profileId: 'legacy-cancel', scopes: ['read'] } });
  const client = new Client({ name: 'legacy-cancel', version: '1' }, { capabilities: {}, versionNegotiation: { mode: 'legacy' } });
  try {
    await client.connect(new StreamableHTTPClientTransport(host.url, { requestInit: { headers: { authorization: `Bearer ${token}` } } }));
    const abort = new AbortController(); const pending = assert.rejects(client.callTool({ name: 'skylines_context', arguments: {} }, { signal: abort.signal }));
    await entered; abort.abort(); await pending; await delay(30);
    assert.equal(observedSignal?.aborted, false); assert.equal(host.usage().handlers, 1);
    await delay(650); assert.equal(observedSignal?.aborted, true); assert.equal(host.usage().handlers, 1);
    finish();
  } finally { await client.close(); await host.close(); }
  assert.deepEqual(host.usage(), { handlers: 0, requests: 0, pipelines: 0, connections: 0 });
});

test('写结果输出预算失败仍保留已知operationId，不引导重提', { timeout: 3000 }, async () => {
  const operationId = 'p'.repeat(128);
  const game = { call: async () => ({ operationId, state: 'succeeded', mayHaveApplied: true }), close: async () => {} };
  const server = createMcpServer({ game, capabilities: fixtureCapabilities, limits: { ...probeLimits, messageBytes: 400 } });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: 'result-budget', version: '1' }, { capabilities: {} });
  try {
    await server.connect(serverTransport); await client.connect(clientTransport);
    const result = await client.callTool({ name: 'probe_submit', arguments: {} });
    assert.equal(result.isError, true);
    assert.equal((result.structuredContent as { error: { operationId: string; mayHaveApplied: boolean } }).error.operationId, operationId);
    assert.equal((result.structuredContent as { error: { mayHaveApplied: boolean } }).error.mayHaveApplied, true);
  } finally { await client.close(); await server.close(); }
});

test('只读查询失败保留原selector，合法嵌套操作ID不因输出预算丢失', { timeout: 5000 }, async () => {
  const key = { kind: 'submissionKey', submissionKey: { writeContextId: 'original-context', requestId: 'original-request' } };
  const operationId = 'p'.repeat(128);
  const scenarios = [
    { selector: { kind: 'operationId', operationId }, result: undefined, fail: true, expectedId: operationId, code: 'DISCONNECTED' },
    { selector: key, result: undefined, fail: true, expectedId: undefined, code: 'DISCONNECTED' },
    { selector: key, result: { lookup: { kind: 'found', operationId, originInstanceId: 'offline-fixture', state: 'succeeded', mayHaveApplied: true, durability: 'volatile' } }, fail: false, expectedId: operationId, code: 'HOST_FAILURE' },
    { selector: key, result: { lookup: { kind: 'pending', operationId, originInstanceId: 'offline-fixture', durability: 'pending' } }, fail: false, expectedId: operationId, code: 'HOST_FAILURE' },
    { selector: key, result: { lookup: { kind: 'found', operationId: 'untrusted-id' } }, fail: false, expectedId: undefined, code: 'INVALID_GAME_RESULT' },
    { selector: { kind: 'operationId', operationId: 'original-id' }, result: { lookup: { kind: 'pending', operationId: 'other-id', originInstanceId: 'offline-fixture', durability: 'pending' } }, fail: false, expectedId: 'original-id', code: 'INVALID_GAME_RESULT' },
    { selector: { kind: 'operationId', operationId: 'original-id' }, result: { lookup: { kind: 'expired' } }, fail: false, expectedId: 'original-id', code: 'INVALID_GAME_RESULT' },
    { selector: { kind: 'operationId', operationId: 'original-id' }, result: { lookup: { kind: 'unavailable', reason: 'CONTEXT_UNKNOWN' } }, fail: false, expectedId: 'original-id', code: 'INVALID_GAME_RESULT' },
  ];
  for (const scenario of scenarios) {
    let calls = 0;
    const game = { call: async () => { calls++; if (scenario.fail) throw new GameClientError('DISCONNECTED', '查询连接中断。', true);
      return { selector: scenario.selector, result: { kind: 'live', instanceId: 'offline-fixture', ...scenario.result } }; }, close: async () => {} };
    const server = createMcpServer({ game, limits: { ...probeLimits, messageBytes: 400 } });
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
    const client = new Client({ name: 'read-correlation', version: '1' }, { capabilities: {} });
    try {
      await server.connect(serverTransport); await client.connect(clientTransport);
      const result = await client.callTool({ name: 'skylines_operation_get', arguments: { selector: scenario.selector } });
      assert.equal(result.isError, true);
      const error = (result.structuredContent as { error: Record<string, unknown> }).error;
      assert.equal(error.code, scenario.code);
      assert.equal(Object.hasOwn(error, 'mayHaveApplied'), false, 'a read failure does not classify the original operation');
      assert.deepEqual(error.selector, scenario.selector);
      assert.equal(error.source, 'live');
      assert.equal(error.operationId, scenario.expectedId);
      assert.deepEqual(JSON.parse((result.content[0] as { text: string }).text), result.structuredContent);
      assert.equal(calls, 1, 'a query cannot replay a submission or retry implicitly');
    } finally { await client.close(); await server.close(); }
  }
});

test('真实stdio接入满额后取消无响应，只有实际work结束才恢复额度', { timeout: 3000 }, async () => {
  const input = new PassThrough(); const output = new PassThrough(); let text = '';
  let entered!: () => void; const started = new Promise<void>(resolve => { entered = resolve; });
  let finish!: () => void; let signal: AbortSignal | undefined; let first = true;
  const game = { call: async (_id: string, _rev: string, _args: unknown, observed: AbortSignal) => {
    if (first) { first = false; signal = observed; entered(); await new Promise<void>(resolve => { finish = resolve; }); }
    return { ...syntheticContext };
  }, close: async () => { finish?.(); } };
  const host = startStdioHost({ game, limits: { ...probeLimits, activeRequests: 2 }, workBudget: new WorkBudget(1) }, input, output);
  // Test-only byte bridge; official Client owns all negotiation and cancellation semantics.
  const transport: Transport = { start: async () => {}, send: async message => { input.write(JSON.stringify(message) + '\n'); }, close: async () => { input.end(); } };
  output.once('close', () => transport.onclose?.());
  output.on('data', (chunk: Buffer) => {
    text += chunk.toString('utf8'); assert.ok(text.length <= probeLimits.messageBytes * 2);
    for (let newline = text.indexOf('\n'); newline >= 0; newline = text.indexOf('\n')) {
      const line = text.slice(0, newline); text = text.slice(newline + 1); transport.onmessage?.(JSON.parse(line) as JSONRPCMessage);
    }
  });
  const client = new Client({ name: 'stdio-cancel', version: '1' }, { capabilities: {}, versionNegotiation: { mode: { pin: '2026-07-28' } } });
  try {
    await client.connect(transport); const abort = new AbortController();
    const rejected = assert.rejects(client.callTool({ name: 'skylines_context', arguments: {} }, { signal: abort.signal }));
    await started; abort.abort(); await rejected; await delay(0); assert.equal(signal?.aborted, true);
    const busy = await client.callTool({ name: 'skylines_context', arguments: {} }); assert.equal(busy.isError, true);
    assert.equal((busy.structuredContent as { error: { code: string } }).error.code, 'BUSY');
    finish(); await delay(0);
    const recovered = await client.callTool({ name: 'skylines_context', arguments: {} }); assert.deepEqual(recovered.structuredContent, syntheticContext);
  } finally { await client.close(); await host.close(); }
  assert.deepEqual(host.transport.usage, { activeRequests: 0, sendBytes: 0, sendCount: 0, closed: true });
});

test('启动参数对齐部署标识边界；非法预算在创建连接/SDK前拒绝', async () => {
  for (const value of ['default', 'a_b', 'dev.1', '-a', 'a'.repeat(64)]) assert.equal(isDeploymentIdentifier(value), true);
  for (const value of ['', '.', '..', 'a.', 'a b', 'a/b', 'a\\b', '中', 'a'.repeat(65)]) assert.equal(isDeploymentIdentifier(value), false);
  const identity = ['--profile', 'a_b', '--environment', 'DEV.1', '--core-manifest-sha256', 'a'.repeat(64)];
  const relay = ['--stdio-relay', 'b'.repeat(32)];
  assert.deepEqual(parseHostOptions([...identity, ...relay]), { transport: 'stdio', profile: 'a_b', environment: 'dev.1', coreManifestSha256: 'a'.repeat(64), stdioRelay: 'b'.repeat(32) });
  assert.throws(() => parseHostOptions(identity));
  for (const nonce of ['', 'b'.repeat(31), 'B'.repeat(32), '../pipe']) assert.throws(() => parseHostOptions([...identity, '--stdio-relay', nonce]));
  assert.throws(() => parseHostOptions([...identity, ...relay, ...relay]));
  assert.throws(() => parseHostOptions(['--profile', 'a_b', '--environment', 'dev.1']));
  assert.throws(() => parseHostOptions(['--profile', 'a_b', '--environment', 'CON.txt', '--core-manifest-sha256', 'a'.repeat(64), ...relay]));
  assert.throws(() => parseHostOptions(['--profile', 'a', '--profile', 'b', '--environment', 'default']));
  for (const maximum of [0, -1, NaN, Infinity, 1.5]) assert.throws(() => new WorkBudget(maximum));
  assert.throws(() => createMcpServer({ game: new MemoryGame(), limits: { ...probeLimits, activeRequests: Infinity } }));
  await assert.rejects(NamedPipeGameClient.connect({ pipeName: 'skylineslink-invalid-budget', profileId: 'probe', profileInstanceId: 'a'.repeat(32), token: 'unused', instanceId: 'unused', environmentId: 'probe', authorityEpoch: 1, grantEpoch: 1, coreManifestSha256: 'a'.repeat(64), productVersion: '0.1.0-dev.0', contractVersion: '2' }, { ...probeLimits, messageBytes: Infinity }));
});
