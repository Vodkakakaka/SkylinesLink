import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { PassThrough } from 'node:stream';
import { setTimeout as delay } from 'node:timers/promises';
import { randomBytes } from 'node:crypto';
import { Client, StreamableHTTPClientTransport } from '@modelcontextprotocol/client';
import { type Transport, type JSONRPCMessage } from '@modelcontextprotocol/server';
import { capabilities, schemaIds, requireContract, type TaskStopSelector, type TaskStopResult } from '../../contracts/ts/index.ts';
import { startStdioHost, JsonLineBudget } from '../../src/host/src/mcp/transports/stdio.ts';
import { startHttpHost } from '../../src/host/src/mcp/transports/http.ts';
import { BoundedTransport } from '../../src/host/src/mcp/transports/bounded-transport.ts';
import { WorkBudget } from '../../src/host/src/mcp/server.ts';
import { probeLimits, type TransportLimits } from '../../src/host/src/shared/limits.ts';
import { ControlClassifier, MessageRate, controlTransportProfile } from '../../src/host/src/shared/control-admission.ts';
import { requireStopResult, requireStopDecisionLookup } from '../../src/host/src/game-client/task-stop-result.ts';
import { syntheticContext } from './fixture.ts';
import { measureJson } from '../../src/host/src/shared/json-budget.ts';

const selector = (taskId = 'task-one'): TaskStopSelector => ({ taskId, expectedControlVersion: 1 });
const ack = (input: TaskStopSelector): TaskStopResult => ({ selector: { ...input }, lookup: { kind: 'pending', receipt: {
  submissionKey: { writeContextId: 'original-control-key', requestId: 'original-stop' }, controlVersion: 2,
  stopRequested: true, persistenceAttempted: true, journal: null,
} } });
async function until(check: () => boolean, detail?: () => unknown) {
  const end = performance.now() + 1500;
  while (!check()) { if (performance.now() >= end) throw new Error('Bounded Stop fixture deadline. ' + JSON.stringify(detail?.())); await delay(1); }
}

function stdio(game: { call(id: string, rev: string, args: unknown, signal: AbortSignal): Promise<unknown>; close(): Promise<void> }, workBudget?: WorkBudget, limits?: TransportLimits) {
  const input = new PassThrough(), output = new PassThrough();
  const host = startStdioHost({ game, ...(workBudget ? { workBudget } : {}), ...(limits ? { limits } : {}) }, input, output);
  const inputFrames: Array<{ method: string; id: unknown }> = [];
  let text = '';
  const transport: Transport = { start: async () => {}, close: async () => { input.end(); }, send: async value => {
    if ('method' in value && inputFrames.length < 24) inputFrames.push({ method: value.method, id: 'id' in value ? value.id : null });
    input.write(JSON.stringify(value) + '\n');
  } };
  output.on('data', (chunk: Buffer) => {
    text += chunk.toString('utf8'); assert.ok(text.length <= probeLimits.messageBytes * 2);
    for (let n = text.indexOf('\n'); n >= 0; n = text.indexOf('\n')) {
      const line = text.slice(0, n); text = text.slice(n + 1); transport.onmessage?.(JSON.parse(line) as JSONRPCMessage);
    }
  });
  output.once('close', () => transport.onclose?.());
  return { host, transport, output, inputFrames, chunk: (messages: JSONRPCMessage[]) => { input.write(messages.map(value => JSON.stringify(value)).join('\n') + '\n'); } };
}

for (const http of [false, true]) test(`正式SDK ${http ? 'HTTP' : 'stdio'}：普通8在途不占8个不同Stop工作/回执额度`, { timeout: 10000 }, async () => {
  let ordinary = 0, controls = 0;
  let releaseNormal!: () => void, releaseControl!: () => void;
  const normal = new Promise<void>(r => { releaseNormal = r; }), control = new Promise<void>(r => { releaseControl = r; });
  const game = { call: async (id: string, _rev: string, args: unknown) => {
    if (id === 'system.context') { ordinary++; await normal; return syntheticContext; }
    assert.equal(id, 'tasks.stop'); controls++; await control; return ack(args as TaskStopSelector);
  }, close: async () => { releaseNormal(); releaseControl(); } };
  const local = http ? undefined : stdio(game);
  const token = randomBytes(32).toString('hex');
  const remote = http ? await startHttpHost({ game, port: 0, authorization: { token, profileId: 'fixture', scopes: ['read', 'control'] } }) : undefined;
  const client = new Client({ name: 'bounded-stop', version: '1' }, { capabilities: {}, versionNegotiation: { mode: { pin: '2026-07-28' } } });
  const pending: Promise<unknown>[] = [];
  try {
    await client.connect(remote ? new StreamableHTTPClientTransport(remote.url, { requestInit: { headers: { authorization: `Bearer ${token}` } } }) : local!.transport);
    for (let i = 0; i < 8; i++) { const call = client.callTool({ name: 'skylines_context', arguments: {} }); pending.push(call); void call.catch(() => {}); }
    await until(() => ordinary === 8, () => ({ ordinary, usage: local?.host.transport.usage, frames: local?.inputFrames }));
    const stops = Array.from({ length: 8 }, (_, i) => client.callTool({ name: 'skylines_task_stop', arguments: selector(`task-${i}`) }));
    pending.push(...stops); for (const p of stops) void p.catch(() => {});
    await until(() => controls === 8);
    assert.equal(ordinary, 8); releaseControl();
    const replies = await Promise.all(stops);
    for (const [i, reply] of replies.entries()) {
      assert.notEqual(reply.isError, true); assert.deepEqual(reply.structuredContent, ack(selector(`task-${i}`)));
    }
    releaseNormal(); await Promise.all(pending);
  } finally { releaseNormal(); releaseControl(); await client.close(); await (remote?.close() ?? local?.host.close()); await Promise.allSettled(pending); }
});

test('SDK取消Stop抑制输出：真实工作未结束不退款，结束后释放no-output handoff', { timeout: 5000 }, async () => {
  let entered = false, finish!: () => void;
  const work = new WorkBudget(8);
  const game = { call: async (_id: string, _rev: string, args: unknown) => { entered = true; await new Promise<void>(r => { finish = r; }); return ack(args as TaskStopSelector); }, close: async () => { finish?.(); } };
  const { host, transport } = stdio(game, work);
  const client = new Client({ name: 'cancel-stop', version: '1' }, { capabilities: {}, versionNegotiation: { mode: { pin: '2026-07-28' } } });
  try {
    await client.connect(transport);
    const abort = new AbortController();
    const call = assert.rejects(client.callTool({ name: 'skylines_task_stop', arguments: selector() }, { signal: abort.signal }));
    await until(() => entered); abort.abort(); await call; await delay(0);
    assert.equal(work.active, 1); assert.equal(host.transport.usage.activeRequests, 1);
    finish(); await until(() => work.active === 0 && host.transport.usage.activeRequests === 0);
    assert.equal(host.transport.usage.sendCount, 0);
  } finally { finish?.(); await client.close(); await host.close(); }
});

for (const sameChunk of [false, true]) test(`真实SDK取消普通RPC后复用同id为Stop：${sameChunk ? '旧handler开始前同chunk' : '旧handler实际开始后'}不能串票`, { timeout: 5000 }, async () => {
  let ordinary = 0, stops = 0, finish!: () => void;
  const held = new Promise<void>(r => { finish = r; });
  const work = new WorkBudget(8);
  const game = { call: async (id: string, _rev: string, args: unknown) => {
    if (id === 'system.context') { ordinary++; await held; return syntheticContext; }
    assert.equal(id, 'tasks.stop'); stops++; await held; return ack(args as TaskStopSelector);
  }, close: async () => { finish(); } };
  const { host, transport, chunk } = stdio(game, work);
  const client = new Client({ name: 'id-handoff', version: '1' }, { capabilities: {}, versionNegotiation: { mode: 'legacy' } });
  const request: JSONRPCMessage = { jsonrpc: '2.0', id: 'reused-id', method: 'tools/call', params: { name: 'skylines_context', arguments: {} } };
  const cancel: JSONRPCMessage = { jsonrpc: '2.0', method: 'notifications/cancelled', params: { requestId: 'reused-id' } };
  const stop: JSONRPCMessage = { jsonrpc: '2.0', id: 'reused-id', method: 'tools/call', params: { name: 'skylines_task_stop', arguments: selector() } };
  try {
    await client.connect(transport);
    if (sameChunk) chunk([request, cancel, stop]);
    else { chunk([request]); await until(() => ordinary === 1); chunk([cancel, stop]); }
    await until(() => host.transport.usage.closed || stops > 0);
    assert.equal(stops, 0, 'an unsettled cancelled handler retains its id; a new Stop cannot inherit its callback');
    await host.close(); assert.equal(work.active, 0); assert.equal(host.transport.usage.activeRequests, 0);
  } finally { finish(); await client.close(); await host.close(); }
});

test('真实SDK同id只在取消的旧handler实际完成/no-output之后可再次用于Stop', { timeout: 5000 }, async () => {
  let ordinary = 0, stops = 0, finish!: () => void;
  const held = new Promise<void>(r => { finish = r; }); const work = new WorkBudget(8);
  const { host, transport, chunk } = stdio({ call: async (id, _rev, args) => {
    if (id === 'system.context') { ordinary++; await held; return syntheticContext; }
    stops++; return ack(args as TaskStopSelector);
  }, close: async () => { finish(); } }, work);
  const client = new Client({ name: 'settled-id', version: '1' }, { capabilities: {}, versionNegotiation: { mode: 'legacy' } });
  try {
    await client.connect(transport);
    chunk([{ jsonrpc: '2.0', id: 'settled-id', method: 'tools/call', params: { name: 'skylines_context', arguments: {} } }]);
    await until(() => ordinary === 1);
    chunk([{ jsonrpc: '2.0', method: 'notifications/cancelled', params: { requestId: 'settled-id' } }]);
    assert.equal(work.active, 1); assert.equal(host.transport.usage.activeRequests, 1);
    finish(); await until(() => work.active === 0 && host.transport.usage.activeRequests === 0);
    chunk([{ jsonrpc: '2.0', id: 'settled-id', method: 'tools/call', params: { name: 'skylines_task_stop', arguments: selector() } }]);
    await until(() => stops === 1 && host.transport.usage.activeRequests === 0);
    assert.equal(host.transport.usage.closed, false);
  } finally { finish(); await client.close(); await host.close(); }
});

test('SDK builtin取消无公开handlerDone时不猜完成，保id到原绝对deadline实际关闭', { timeout: 3000 }, async () => {
  let calls = 0;
  const { host, transport, chunk } = stdio({ call: async () => { calls++; return {}; }, close: async () => {} }, undefined,
    { ...probeLimits, receiveDeadlineMs: 150, sendDeadlineMs: 150 });
  const client = new Client({ name: 'builtin-cancel', version: '1' }, { capabilities: {}, versionNegotiation: { mode: 'legacy' } });
  try {
    await client.connect(transport); await until(() => host.transport.usage.activeRequests === 0);
    chunk([{ jsonrpc: '2.0', id: 'builtin', method: 'ping' },
      { jsonrpc: '2.0', method: 'notifications/cancelled', params: { requestId: 'builtin' } }]);
    await delay(0);
    assert.equal(host.transport.usage.activeRequests, 1); assert.equal(host.transport.usage.sendCount, 0);
    await until(() => host.transport.usage.closed);
    assert.equal(host.transport.usage.activeRequests, 0); assert.equal(calls, 0);
  } finally { await client.close(); await host.close(); }
});

test('raw取消通知不等于SDK采纳：非法reason不abort公开signal，旧响应实际写完前仍持原票', { timeout: 3000 }, async () => {
  let entered: AbortSignal | undefined, finish!: () => void, atResponse: number | undefined;
  const held = new Promise<void>(r => { finish = r; });
  const { host, transport, output, chunk } = stdio({ call: async (_id, _rev, _args, signal) => {
    entered = signal; await held; return syntheticContext;
  }, close: async () => { finish(); } });
  const client = new Client({ name: 'invalid-cancel', version: '1' }, { capabilities: {}, versionNegotiation: { mode: 'legacy' } });
  output.on('data', (bytes: Buffer) => {
    const message = JSON.parse(bytes.toString('utf8')) as JSONRPCMessage;
    if ('id' in message && message.id === 'invalid-cancel') atResponse = host.transport.usage.activeRequests;
  });
  try {
    await client.connect(transport);
    chunk([{ jsonrpc: '2.0', id: 'invalid-cancel', method: 'tools/call', params: { name: 'skylines_context', arguments: {} } }]);
    await until(() => entered !== undefined);
    chunk([{ jsonrpc: '2.0', method: 'notifications/cancelled', params: { requestId: 'invalid-cancel', reason: 123 } }]);
    await delay(0); assert.equal(entered!.aborted, false); assert.equal(host.transport.usage.activeRequests, 1);
    finish(); await until(() => atResponse !== undefined);
    assert.equal(atResponse, 1, 'the SDK still sends a result, so handler settlement alone cannot release its response ticket');
    await until(() => host.transport.usage.activeRequests === 0); assert.equal(host.transport.usage.closed, false);
  } finally { finish(); await client.close(); await host.close(); }
});

test('唯一owned输出pump：normal当前帧后优先Stop，已认领但未物理开始的回复不因迟到cancel退款', async () => {
  const writes: JSONRPCMessage[] = [], finishers: Array<() => void> = [];
  const inner: Transport = { start: async () => {}, close: async () => { for (const finish of finishers.splice(0)) finish(); },
    send: message => { writes.push(message); return new Promise<void>(resolve => { finishers.push(resolve); }); } };
  const transport = new BoundedTransport(inner, probeLimits, { settled: async () => {}, close: async () => { for (const finish of finishers.splice(0)) finish(); } });
  await transport.start();
  const pending: Promise<void>[] = [];
  try {
    for (let id = 0; id < 8; id++) {
      inner.onmessage?.({ jsonrpc: '2.0', id, method: 'ping' });
      const p = transport.send({ jsonrpc: '2.0', id, result: {} }); pending.push(p); void p.catch(() => {});
    }
    inner.onmessage?.({ jsonrpc: '2.0', id: 'stop', method: 'tools/call', params: { name: 'skylines_task_stop', arguments: selector() } });
    const signal = new AbortController(); transport.lifecycle.handlerStarted('stop', signal.signal);
    transport.lifecycle.handlerDone('stop', signal.signal);
    const stop = transport.send({ jsonrpc: '2.0', id: 'stop', result: { structuredContent: ack(selector()) } }); pending.push(stop); void stop.catch(() => {});
    signal.abort(); assert.equal(transport.usage.activeRequests, 9); assert.equal(writes.length, 1);
    finishers.shift()!(); await until(() => writes.length === 2);
    const next = writes[1]!; assert.equal('id' in next ? next.id : undefined, 'stop'); finishers.shift()!(); await stop;
    while (transport.usage.sendCount > 0) { finishers.shift()?.(); await delay(0); }
    await Promise.all(pending); assert.equal(transport.usage.activeRequests, 0);
  } finally { await transport.close(); await Promise.allSettled(pending); }
});

test('Stop完整raw4KiB包括空白；工具名/参数/当前连接catalog决定分类，普通64之外仅8条控制', async () => {
  const frame = JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name: 'skylines_task_stop', arguments: selector() } });
  const lines = new JsonLineBudget(probeLimits); let emitted = 0;
  const failed = new Promise<void>(resolve => { lines.once('error', () => resolve()); });
  lines.on('data', () => { emitted++; }); lines.end(' '.repeat(4096) + frame + '\n'); await failed;
  assert.equal(emitted, 0);
  const classifier = new ControlClassifier(); const message = JSON.parse(frame) as JSONRPCMessage;
  assert.ok(classifier.raw(message, Buffer.byteLength(frame)));
  assert.equal(new ControlClassifier(capabilities.filter(c => c.id !== 'tasks.stop')).message(message), undefined);
  const rate = new MessageRate(probeLimits, true);
  for (let n = 0; n < 64; n++) rate.accept(false);
  for (let n = 0; n < 8; n++) rate.accept(true);
  assert.throws(() => rate.accept(true));
});

test('共享Stop vectors：结构合法不代替latch/原控制版本/持久/历史关联事实', async () => {
  const vectors = JSON.parse(await readFile('../../contracts/fixtures/task-stop-cases.json', 'utf8')) as Array<{ name: string; schemaId: string; json: string; valid: boolean; semanticValid?: boolean }>;
  const failures = vectors.filter(v => v.semanticValid === false);
  assert.equal(failures.length, 7);
  for (const vector of failures) {
    const value = JSON.parse(vector.json) as TaskStopResult & { result?: { kind: string } };
    requireContract(vector.schemaId, value);
    assert.throws(() => vector.schemaId === schemaIds.TaskStopResult ? requireStopResult(value.selector, value)
      : requireStopDecisionLookup({ ...value.selector, source: 'history' }, value), vector.name);
  }
});

test('stdio setup只多1个真实owner：8普通+8Stop+1setup共17输出仍限同2MiB，迟后callback不提前释放', async () => {
  const writes: JSONRPCMessage[] = [], finishers: Array<() => void> = [];
  const inner: Transport = { start: async () => {}, close: async () => { for (const done of finishers.splice(0)) done(); },
    send: async value => { writes.push(value); await new Promise<void>(r => { finishers.push(r); }); } };
  const transport = new BoundedTransport(inner, probeLimits, { close: async () => { for (const done of finishers.splice(0)) done(); }, settled: async () => {} });
  const pending: Promise<void>[] = [];
  const output = (id: string | number, maximum: number) => {
    const value = { jsonrpc: '2.0' as const, id, result: { padding: '' } };
    value.result.padding = 'a'.repeat(maximum - measureJson(value, { ...probeLimits, messageBytes: maximum })); return value;
  };
  const send = (request: JSONRPCMessage, maximum: number) => {
    inner.onmessage?.(request); const p = transport.send(output('id' in request ? request.id! : 0, maximum)); pending.push(p); void p.catch(() => {});
  };
  await transport.start();
  try {
    send({ jsonrpc: '2.0', id: 'bootstrap', method: 'server/discover' }, controlTransportProfile.maximumSetupResponseBytes);
    for (let id = 0; id < 8; id++) send({ jsonrpc: '2.0', id, method: 'ping' }, probeLimits.messageBytes);
    for (let i = 0; i < 8; i++) send({ jsonrpc: '2.0', id: `stop-${i}`, method: 'tools/call',
      params: { name: 'skylines_task_stop', arguments: selector(`task-${i}`) } }, controlTransportProfile.maximumResponseBytes);
    assert.equal(transport.usage.sendCount, 17); assert.equal(transport.usage.activeRequests, 17);
    assert.equal(transport.usage.sendBytes, 2_015_232); assert.ok(transport.usage.sendBytes <= probeLimits.sendBytes);
    assert.equal(writes.length, 1, 'all other frames remain in the single owned pump');
    while (transport.usage.sendCount > 0) { finishers.shift()?.(); await delay(0); }
    await Promise.all(pending); assert.equal(transport.usage.activeRequests, 0); assert.equal(transport.usage.sendBytes, 0);
  } finally { await transport.close(); await Promise.allSettled(pending); }
});

test('stdio另一种首次setup可占普通槽，重复或首次非setup RPC之后的setup不再旁路', async () => {
  for (const afterRpc of [false, true]) {
    const finishers: Array<() => void> = [];
    const inner: Transport = { start: async () => {}, close: async () => { for (const done of finishers.splice(0)) done(); },
      send: () => new Promise<void>(r => { finishers.push(r); }) };
    const transport = new BoundedTransport(inner, probeLimits, { close: async () => { for (const done of finishers.splice(0)) done(); }, settled: async () => {} });
    await transport.start();
    try {
      inner.onmessage?.({ jsonrpc: '2.0', id: 'modern', method: 'server/discover' });
      // Initialized is only a notification, so a legacy fallback remains valid.
      inner.onmessage?.({ jsonrpc: '2.0', method: 'notifications/initialized' });
      if (!afterRpc) inner.onmessage?.({ jsonrpc: '2.0', id: 'legacy', method: 'initialize' });
      for (let id = 0; id < (afterRpc ? 8 : 7); id++) inner.onmessage?.({ jsonrpc: '2.0', id, method: 'ping' });
      assert.equal(transport.usage.activeRequests, 9, 'one dedicated setup plus eight ordinary slots, including an overlapping fallback');
      // Both the repeated-method and sealed-phase paths close, with no hidden
      // ninth ordinary ticket or second setup ticket.
      inner.onmessage?.({ jsonrpc: '2.0', id: 'invalid', method: afterRpc ? 'initialize' : 'server/discover' });
      await until(() => transport.usage.closed); assert.equal(transport.usage.activeRequests, 0);
    } finally { await transport.close(); }
  }
});

test('stdio和stateless HTTP先核setup完整raw4KiB；HTTP多次独立connect不伪造socket会话', async () => {
  for (const method of ['initialize', 'server/discover']) {
    const line = JSON.stringify({ jsonrpc: '2.0', id: 1, method });
    const lines = new JsonLineBudget(probeLimits); let emitted = 0;
    lines.on('data', () => { emitted++; });
    const failed = new Promise<void>(r => { lines.once('error', () => r()); });
    lines.end(' '.repeat(controlTransportProfile.maximumSetupRequestBytes) + line + '\n'); await failed;
    assert.equal(emitted, 0);
  }
  const token = randomBytes(32).toString('hex');
  const host = await startHttpHost({ game: { call: async () => ({}), close: async () => {} }, port: 0,
    authorization: { token, profileId: 'fixture', scopes: ['read'] } });
  try {
    for (const method of ['initialize', 'server/discover']) {
      const response = await fetch(host.url, { method: 'POST', headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
        body: ' '.repeat(controlTransportProfile.maximumSetupRequestBytes) + JSON.stringify({ jsonrpc: '2.0', id: 1, method }) });
      assert.equal(response.status, 413); await response.arrayBuffer();
    }
    for (let i = 0; i < 3; i++) {
      const client = new Client({ name: 'repeat-stateless', version: '1' }, { capabilities: {}, versionNegotiation: { mode: { pin: '2026-07-28' } } });
      try { await client.connect(new StreamableHTTPClientTransport(host.url, { requestInit: { headers: { authorization: `Bearer ${token}` } } })); }
      finally { await client.close(); }
    }
  } finally { await host.close(); }
});
