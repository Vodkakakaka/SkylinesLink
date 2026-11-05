import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn, ChildProcess } from 'node:child_process';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { PassThrough } from 'node:stream';
import { setImmediate as immediate } from 'node:timers/promises';
import { ReadBuffer, serializeMessage, type Transport } from '@modelcontextprotocol/server';
import { Client } from '@modelcontextprotocol/client';
import { capabilities, requireContract, schemaById, schemaIds, validateContract, type NativeHistoryCapture, type NativeHistoryRequest,
  type OperationLookupV4, type OperationQueryV3 } from '../../contracts/ts/index.ts';
import { NativeHistoryReader, type HistoryReader } from '../../src/host/src/game-client/history-reader.ts';
import { DiscoveredGameClient, type LiveGameConnection } from '../../src/host/src/game-client/discovered-game-client.ts';
import { GameClientError } from '../../src/host/src/game-client/game-client.ts';
import type { PipeConnection } from '../../src/host/src/game-client/named-pipe-client.ts';
import { historyAge, requireCapture, requireOperationLookup, requireOperationLookupV4, compatibleSubject } from '../../src/host/src/game-client/history-result.ts';
import { WorkBudget } from '../../src/host/src/mcp/server.ts';
import { startStdioHost } from '../../src/host/src/mcp/transports/stdio.ts';
import { parseHostOptions } from '../../src/host/src/composition/options.ts';
import { historyCapture, historyCaptureV2, historyRequestV2, historyCaptureV3, historyRequestV3, historyQuery, activeLookup } from './history-values.ts';

const signal = () => new AbortController().signal;
const code = (expected: string) => (error: unknown) => error instanceof GameClientError && error.code === expected;
const selection = { environment: 'fixture', profile: 'reader', coreManifestSha256: 'a'.repeat(64), userProfile: resolve('.') };
const connection: PipeConnection = { environmentId: 'fixture', profileId: 'reader', profileInstanceId: 'a'.repeat(32), authorityEpoch: 2, grantEpoch: 3,
  instanceId: 'live-instance', coreManifestSha256: 'a'.repeat(64), productVersion: '0.1.0-dev.0', contractVersion: '2',
  pipeName: 'skylineslink-fixture', token: 'f'.repeat(64) };
class FakeLive implements LiveGameConnection {
  readonly usage = { closed: false };
  calls: unknown[] = [];
  revisions: string[] = [];
  result: ((query: OperationQueryV3) => unknown) | undefined;
  failure: GameClientError | undefined;
  constructor(readonly connection: PipeConnection) {}
  async call(_id: string, _revision: string, args: unknown, waiting: AbortSignal) {
    waiting.throwIfAborted(); this.calls.push(args); this.revisions.push(_revision);
    if (this.failure) throw this.failure;
    if (this.usage.closed) throw new GameClientError('GAME_DISCONNECTED', 'fixture');
    return this.result ? this.result(args as OperationQueryV3) : activeLookup(args as OperationQueryV3);
  }
  async close() { this.usage.closed = true; }
}
class FakeReader implements HistoryReader {
  requests: NativeHistoryRequest[] = [];
  deadlines: number[] = [];
  next: (request: NativeHistoryRequest, waiting: AbortSignal) => Promise<NativeHistoryCapture> = async () => structuredClone(historyCapture);
  async read(request: NativeHistoryRequest, waiting: AbortSignal, deadline: number) {
    this.requests.push(structuredClone(request)); this.deadlines.push(deadline); return this.next(request, waiting);
  }
  async close() {}
}
function setup(reader = new FakeReader(), value = connection) {
  const live = new FakeLive(value); let discoveries = 0, connections = 0;
  const game = new DiscoveredGameClient(selection, { historyReader: reader, discover: async () => { discoveries++; return value; },
    connect: async () => { connections++; return live; }, now: () => 200 });
  return { game, reader, live, counts: () => ({ discoveries, connections }) };
}

test('history显式路由保旧生产者来源，首次主体固定且不绑定无关recordRevision', async () => {
  const { game, reader, counts } = setup();
  try {
    const result = await game.call('operations.get', '4', historyQuery, signal()) as Record<string, unknown>;
    requireContract(schemaIds.OperationLookupV3, result);
    assert.equal((result.result as { kind: string }).kind, 'history');
    assert.deepEqual(counts(), { discoveries: 0, connections: 0 });
    assert.equal(reader.requests[0]?.expectedAuthorityEpoch, null);
    assert.deepEqual(game.subject, { environmentId: 'fixture', profileId: 'reader', authorityEpoch: 2, grantEpoch: 3 });
    reader.next = async () => ({ ...historyCapture, authorizationRecordRevision: 99, record: null });
    const missing = await game.call('operations.get', '4', historyQuery, signal()) as { result: { lookup: unknown; source: unknown } };
    assert.deepEqual(missing.result.lookup, { kind: 'unavailable', reason: 'HISTORY_NOT_COVERED' });
    assert.equal(reader.requests[1]?.expectedAuthorityEpoch, 2); assert.equal(reader.requests[1]?.expectedGrantEpoch, 3);
    reader.next = async () => ({ ...historyCapture, authorityEpoch: 3 });
    await assert.rejects(game.call('operations.get', '4', historyQuery, signal()), code('SUBJECT_CHANGED'));
  } finally { await game.close(); }
});

test('缺省source仅live；实时失败当次不回退，真实关闭后的下一次显式history可只读', async () => {
  const { game, reader, live, counts } = setup();
  try {
    await game.call('operations.get', '4', { selector: historyQuery.selector }, signal());
    assert.equal(reader.requests.length, 0);
    live.failure = new GameClientError('UNAUTHORIZED', 'fixture');
    await assert.rejects(game.call('operations.get', '4', historyQuery, signal()), code('UNAUTHORIZED'));
    assert.equal(reader.requests.length, 0);
    live.failure = undefined; await live.close();
    await game.call('operations.get', '4', historyQuery, signal());
    assert.equal(reader.requests.length, 1);
    reader.next = async () => { throw new GameClientError('HISTORY_AUTHORITY_BUSY', 'fixture'); };
    await assert.rejects(game.call('operations.get', '4', historyQuery, signal()), code('GAME_DISCONNECTED'));
    assert.deepEqual(counts(), { discoveries: 1, connections: 1 });
  } finally { await game.close(); }
});

test('可信live仅补Native1未知的incarnation，随后旧历史不清除已固定PID', async () => {
  const { game, reader, live } = setup();
  try {
    await game.call('operations.get', '5', historyQuery, signal());
    const legacy = game.subject!;
    assert.equal(legacy.profileInstanceId, undefined);
    await game.call('operations.get', '5', { selector: historyQuery.selector }, signal());
    const complete = game.subject!;
    assert.equal(complete.profileInstanceId, connection.profileInstanceId);
    assert.equal(Object.isFrozen(complete), true);
    assert.notEqual(complete, legacy); assert.equal(legacy.profileInstanceId, undefined);
    await live.close();
    await game.call('operations.get', '5', historyQuery, signal());
    assert.equal(game.subject, complete, 'a legacy capture cannot weaken the fixed current subject');
    assert.equal(reader.requests.at(-1)?.expectedGrantEpoch, complete.grantEpoch);
    assert.equal(compatibleSubject(complete, { ...complete, profileInstanceId: 'b'.repeat(32) }), false);
    assert.equal(compatibleSubject(legacy, { ...complete, grantEpoch: complete.grantEpoch + 1 }), false);
  } finally { await game.close(); }
});

test('并发首次Native1与同epochs的认证live只补未知身份，不改四值或丢查询', async () => {
  const reader = new FakeReader(); let capture!: (value: NativeHistoryCapture) => void, authenticate!: (game: LiveGameConnection) => void;
  reader.next = () => new Promise(resolve => { capture = resolve; });
  const live = new FakeLive(connection);
  const game = new DiscoveredGameClient(selection, { historyReader: reader, discover: async () => live.connection,
    connect: () => new Promise(resolve => { authenticate = resolve; }) });
  try {
    const historical = game.call('operations.get', '5', historyQuery, signal());
    const realtime = game.call('operations.get', '5', { selector: historyQuery.selector }, signal());
    await immediate(); capture(structuredClone(historyCapture)); await historical;
    assert.equal(game.subject?.profileInstanceId, undefined);
    authenticate(live); await realtime;
    assert.equal(game.subject?.profileInstanceId, connection.profileInstanceId);
    assert.equal(game.subject?.authorityEpoch, historyCapture.authorityEpoch);
    assert.equal(game.subject?.grantEpoch, historyCapture.grantEpoch);
    assert.deepEqual(live.revisions, ['5']);
  } finally { await game.close(); }
});

test('只有明确authority busy可一次live；失败发现不因历史请求重置', async () => {
  const reader = new FakeReader(); reader.next = async () => { throw new GameClientError('HISTORY_UNAVAILABLE', 'fixture'); };
  const first = setup(reader);
  try {
    await assert.rejects(first.game.call('operations.get', '4', historyQuery, signal()), code('HISTORY_UNAVAILABLE'));
    assert.equal(first.counts().discoveries, 0);
    reader.next = async () => { throw new GameClientError('HISTORY_AUTHORITY_BUSY', 'fixture'); };
    await first.game.call('operations.get', '4', historyQuery, signal());
    assert.deepEqual(first.counts(), { discoveries: 1, connections: 1 });
  } finally { await first.game.close(); }
  let discoveries = 0;
  const failed = new DiscoveredGameClient(selection, { historyReader: reader, discover: async () => { discoveries++; throw new GameClientError('GAME_UNAVAILABLE', 'fixture'); } });
  try {
    await assert.rejects(failed.call('system.context', '1', {}, signal()), code('GAME_UNAVAILABLE'));
    await assert.rejects(failed.call('operations.get', '4', historyQuery, signal()), code('GAME_UNAVAILABLE'));
    assert.equal(discoveries, 1);
  } finally { await failed.close(); }
});

test('并发首次Native/live捕获CAS拒另一主体，不把同名profile升级', async () => {
  const reader = new FakeReader(); let complete!: (value: NativeHistoryCapture) => void;
  reader.next = () => new Promise(resolve => { complete = resolve; });
  const { game } = setup(reader, { ...connection, authorityEpoch: 3 });
  try {
    const history = game.call('operations.get', '4', historyQuery, signal());
    const rejected = assert.rejects(history, code('SUBJECT_CHANGED'));
    await game.call('operations.get', '4', { selector: historyQuery.selector }, signal());
    complete(structuredClone(historyCapture)); await rejected;
    assert.equal(game.subject?.authorityEpoch, 3);
  } finally { await game.close(); }
});

test('Native先完成的并发CAS同样拒迟到live主体，并关闭未采用连接', async () => {
  const reader = new FakeReader(); let capture!: (value: NativeHistoryCapture) => void, authenticate!: (game: LiveGameConnection) => void;
  reader.next = () => new Promise(resolve => { capture = resolve; });
  const live = new FakeLive({ ...connection, authorityEpoch: 3 });
  const game = new DiscoveredGameClient(selection, { historyReader: reader, discover: async () => live.connection,
    connect: () => new Promise(resolve => { authenticate = resolve; }) });
  try {
    const historical = game.call('operations.get', '4', historyQuery, signal());
    const realtime = assert.rejects(game.call('operations.get', '4', { selector: historyQuery.selector }, signal()), code('SUBJECT_CHANGED'));
    await immediate(); capture(structuredClone(historyCapture)); await historical;
    authenticate(live); await realtime;
    assert.equal(game.subject?.authorityEpoch, 2); assert.equal(live.usage.closed, true);
    assert.equal(live.calls.length, 0, 'losing subject must never execute the pending operation query');
  } finally { await game.close(); }
});

test('definite busy转live沿用同一个绝对期限signal，不创建第二等待窗口', async () => {
  const reader = new FakeReader(); let nativeSignal: AbortSignal | undefined, liveSignal: AbortSignal | undefined;
  reader.next = async (_request, waiting) => { nativeSignal = waiting; await immediate(); throw new GameClientError('HISTORY_AUTHORITY_BUSY', 'fixture'); };
  const live = new FakeLive(connection);
  const game = new DiscoveredGameClient(selection, { historyReader: reader, discover: async () => connection,
    connect: async (_connection, waiting) => { liveSignal = waiting; return live; } });
  try {
    await game.call('operations.get', '4', historyQuery, signal());
    assert.equal(nativeSignal, liveSignal); assert.equal(reader.deadlines.length, 1);
  } finally { await game.close(); }
});

test('没有固定reader依赖的standalone组合不从PATH或discovery猜历史入口', async () => {
  let discoveries = 0;
  const game = new DiscoveredGameClient(selection, { discover: async () => { discoveries++; return connection; } });
  try { await assert.rejects(game.call('operations.get', '4', historyQuery, signal()), code('HISTORY_READER_UNAVAILABLE')); assert.equal(discoveries, 0); }
  finally { await game.close(); }
  const args = ['--profile', 'reader', '--environment', 'fixture', '--core-manifest-sha256', 'a'.repeat(64), '--stdio-relay', 'a'.repeat(32)];
  assert.equal(parseHostOptions(args).historyReader, undefined);
  assert.throws(() => parseHostOptions([...args, '--history-reader', 'PATH-program']));
});

test('有限capture保current=false、canonical generation及selector关联；age仅墙钟差', () => {
  const record = historyCapture.record!;
  const unknownCity: NativeHistoryCapture = { ...historyCapture, record: { ...record,
    sourceContext: { ...record.sourceContext, gameState: 'ready', cityId: null },
    load: { ...record.load, observationContext: { ...record.load.observationContext!, cityId: null } } } };
  assert.equal(requireCapture(unknownCity, selection, historyQuery.selector), unknownCity);
  for (const mutate of [
    (v: any) => { v.record.operationId = 'wrong'; },
    (v: any) => { v.record.load.isObservationCurrent = true; },
    (v: any) => { v.record.sourceContext.generation = '01'; },
    (v: any) => { v.record.sourceContext.cityId = 'unexpected-menu-city'; },
    (v: any) => { v.record.load.observationContext.gameState = 'loading'; },
    (v: any) => { v.record.load.observationContext.generation = '9223372036854775808'; },
    (v: any) => { v.record.terminalSequence = v.source.throughSequence + 1; },
    (v: any) => { v.record.originInstanceId = 'wrong'; },
    (v: any) => { v.source.environmentId = 'other'; },
  ]) {
    const value = structuredClone(historyCapture); mutate(value);
    assert.throws(() => requireCapture(value, selection, historyQuery.selector), code('INVALID_HISTORY_RESULT'));
  }
  assert.equal(historyAge(100, 99), null); assert.equal(historyAge(100, 200), 100); assert.equal(historyAge(1, Number.MAX_VALUE), null);
});

const request: NativeHistoryRequest = { formatVersion: 1, environmentId: 'fixture', profileId: 'reader', expectedAuthorityEpoch: null,
  expectedGrantEpoch: null, selector: historyQuery.selector };
function native(mode: string, version: 1 | 2 | 3 = 1) {
  let child: ChildProcess | undefined, closed = false;
  const reader = new NativeHistoryReader(resolve('fixture-reader.exe'), (_path, args, options) => {
    assert.deepEqual(args, [version === 1 ? '--read-history' : version === 2 ? '--read-history-v2' : '--read-history-v3']); assert.deepEqual(options.stdio, ['overlapped', 'overlapped', 'ignore']);
    assert.equal(options.shell, false); assert.equal(options.windowsHide, true);
    child = spawn(process.execPath, [fileURLToPath(new URL('./history-reader-fixture.mjs', import.meta.url)), mode], options);
    child.once('close', () => { closed = true; }); return child;
  });
  return { reader, isClosed: () => closed, getChild: () => child };
}

test('真实短命协议进程仅完整JSON+exit0+close成功，未知/超额/坏UTF8拒绝', { timeout: 10000 }, async () => {
  for (const [mode, expected] of [['normal', undefined], ['busy', 'HISTORY_AUTHORITY_BUSY'], ['unknown', 'HISTORY_READER_UNAVAILABLE'],
    ['invalid', 'INVALID_HISTORY_RESPONSE'], ['utf8', 'INVALID_HISTORY_RESPONSE'], ['large', 'BUDGET_EXCEEDED'], ['nonzero', 'HISTORY_IO_UNAVAILABLE']] as const) {
    const { reader, isClosed } = native(mode);
    try {
      const result = reader.read(request, signal(), performance.now() + 2000);
      if (expected) await assert.rejects(result, code(expected)); else assert.deepEqual(await result, historyCapture);
      assert.equal(isClosed(), true); assert.deepEqual(reader.usage, { readers: 0, reservedPayloadBytes: 0 });
    } finally { await reader.close(); }
  }
});

test('Native2使用显式命令和typed契约，关联本次发送快照而非等待期间改过的原对象', { timeout: 6000 }, async () => {
  const { reader, isClosed } = native('normal', 2);
  const mutable = structuredClone(historyRequestV2);
  try {
    const pending = reader.readV2(mutable, signal(), performance.now() + 2000);
    Object.assign(mutable, { query: { kind: 'task', taskId: 'changed-after-send' } });
    assert.deepEqual(await pending, historyCaptureV2);
    assert.equal(isClosed(), true); assert.deepEqual(reader.usage, { readers: 0, reservedPayloadBytes: 0 });
  } finally { await reader.close(); }
  for (const [mode, expected] of [['wrong-version', 'INVALID_HISTORY_RESPONSE'], ['large', 'BUDGET_EXCEEDED'], ['busy', 'HISTORY_AUTHORITY_BUSY']] as const) {
    const fixture = native(mode, 2);
    try {
      await assert.rejects(fixture.reader.readV2(historyRequestV2, signal(), performance.now() + 2000), code(expected));
      assert.equal(fixture.isClosed(), true); assert.equal(fixture.reader.usage.readers, 0);
    } finally { await fixture.reader.close(); }
  }
});

test('完整响应后进程仍活不能成功；单reader到真实关闭后才退额度', { timeout: 5000 }, async () => {
  const { reader, getChild, isClosed } = native('hold'); let settled = false;
  const pending = reader.read(request, signal(), performance.now() + 2000);
  const rejected = assert.rejects(pending, code('CANCELLED')).finally(() => { settled = true; });
  try {
    await new Promise<void>(resolve => getChild()!.stdout!.once('data', () => resolve()));
    await immediate(); assert.equal(settled, false); assert.equal(reader.usage.readers, 1);
    await assert.rejects(reader.read(request, signal(), performance.now() + 1000), code('BUSY'));
    await assert.rejects(reader.readV2(historyRequestV2, signal(), performance.now() + 1000), code('BUSY'));
    const closing = reader.close(); assert.equal(reader.usage.readers, 1);
    await closing; await rejected;
    assert.equal(isClosed(), true); assert.deepEqual(reader.usage, { readers: 0, reservedPayloadBytes: 0 });
  } finally { await reader.close(); }
});

test('Native2与旧read共用一个owner，外部取消和原绝对期限都等实际进程关闭', { timeout: 5000 }, async () => {
  const { reader, getChild, isClosed } = native('hold', 2);
  const controller = new AbortController(); let settled = false;
  const pending = reader.readV2(historyRequestV2, controller.signal, performance.now() + 2000);
  const rejected = assert.rejects(pending, code('CANCELLED')).finally(() => { settled = true; });
  try {
    await new Promise<void>(resolve => getChild()!.stdout!.once('data', () => resolve()));
    await immediate(); assert.equal(settled, false);
    assert.deepEqual(reader.usage, { readers: 1, reservedPayloadBytes: 4096 + 32768 });
    await assert.rejects(reader.read(request, signal(), performance.now() + 1000), code('BUSY'));
    controller.abort(); await rejected;
    assert.equal(isClosed(), true); assert.deepEqual(reader.usage, { readers: 0, reservedPayloadBytes: 0 });
    await reader.close();
    await assert.rejects(reader.read(request, signal(), performance.now() + 1000), code('CANCELLED'));
  } finally { controller.abort(); await Promise.allSettled([pending, reader.close()]); }
  const deadline = native('hold', 2);
  try {
    await assert.rejects(deadline.reader.readV2(historyRequestV2, signal(), performance.now() + 300), code('HISTORY_TIMEOUT'));
    assert.equal(deadline.isClosed(), true); assert.deepEqual(deadline.reader.usage, { readers: 0, reservedPayloadBytes: 0 });
  } finally { await deadline.reader.close(); }
});

test('Native3显式48KiB档保原发送快照，旧协议仍32KiB，完整8项可超过32KiB', { timeout: 10000 }, async () => {
  const normal = native('normal', 3), mutable = structuredClone(historyRequestV3);
  try {
    const pending = normal.reader.readV3(mutable, signal(), performance.now() + 2000);
    Object.assign(mutable, { query: { kind: 'task', taskId: 'changed-after-send' } });
    assert.deepEqual(await pending, historyCaptureV3);
    assert.equal(normal.isClosed(), true); assert.deepEqual(normal.reader.usage, { readers: 0, reservedPayloadBytes: 0 });
  } finally { await normal.reader.close(); }
  const expanded = native('eight-large', 3);
  try {
    const value = await expanded.reader.readV3(historyRequestV3, signal(), performance.now() + 2000);
    assert.equal(value.result.kind === 'tasks' && value.result.tasks.length, 8);
    assert.equal(value.source.producerInstanceId, '\u{1F680}'.repeat(128));
    assert.equal(expanded.isClosed(), true);
  } finally { await expanded.reader.close(); }
  for (const [mode, expected] of [['wrong-version', 'INVALID_HISTORY_RESPONSE'], ['large', 'BUDGET_EXCEEDED'], ['busy', 'HISTORY_AUTHORITY_BUSY']] as const) {
    const fixture = native(mode, 3);
    try { await assert.rejects(fixture.reader.readV3(historyRequestV3, signal(), performance.now() + 2000), code(expected));
      assert.equal(fixture.isClosed(), true); assert.deepEqual(fixture.reader.usage, { readers: 0, reservedPayloadBytes: 0 });
    } finally { await fixture.reader.close(); }
  }
});

test('Native3与两旧版本共用实际owner，取消/截止仍等child和两个stdio关闭', { timeout: 6000 }, async () => {
  const fixture = native('hold', 3), controller = new AbortController(); let settled = false;
  const pending = fixture.reader.readV3(historyRequestV3, controller.signal, performance.now() + 2000);
  const rejected = assert.rejects(pending, code('CANCELLED')).finally(() => { settled = true; });
  try {
    await new Promise<void>(resolveData => fixture.getChild()!.stdout!.once('data', () => resolveData()));
    await immediate(); assert.equal(settled, false);
    assert.deepEqual(fixture.reader.usage, { readers: 1, reservedPayloadBytes: 4096 + 49152 });
    await assert.rejects(fixture.reader.read(request, signal(), performance.now() + 1000), code('BUSY'));
    await assert.rejects(fixture.reader.readV2(historyRequestV2, signal(), performance.now() + 1000), code('BUSY'));
    controller.abort(); await rejected; assert.equal(fixture.isClosed(), true);
    assert.deepEqual(fixture.reader.usage, { readers: 0, reservedPayloadBytes: 0 });
  } finally { controller.abort(); await Promise.allSettled([pending, fixture.reader.close()]); }
  const timeout = native('hold', 3);
  try {
    await assert.rejects(timeout.reader.readV3(historyRequestV3, signal(), performance.now() + 300), code('HISTORY_TIMEOUT'));
    assert.equal(timeout.isClosed(), true); assert.equal(timeout.reader.usage.readers, 0);
  } finally { await timeout.reader.close(); }
});

for (const version of [1, 2, 3] as const) test(`Native${version} child close先于stdio close仍持reader，两个流独立关闭才完成`, { timeout: 3000 }, async () => {
  const input = new PassThrough({ autoDestroy: false }), output = new PassThrough({ autoDestroy: false });
  const child = new ChildProcess();
  Object.defineProperties(child, { stdin: { value: input }, stdout: { value: output } });
  const reader = new NativeHistoryReader(resolve('fixture-reader.exe'), () => child);
  let settled = false;
  const pending = (version === 1 ? reader.read(request, signal(), performance.now() + 2000)
    : version === 2 ? reader.readV2(historyRequestV2, signal(), performance.now() + 2000)
    : reader.readV3(historyRequestV3, signal(), performance.now() + 2000)).finally(() => { settled = true; });
  const expected = version === 1 ? historyCapture : version === 2 ? historyCaptureV2 : historyCaptureV3;
  try {
    input.resume(); output.end(JSON.stringify(expected));
    await immediate(); child.emit('close', 0, null); await immediate();
    assert.equal(settled, false, 'process close and completed stdin write do not prove stdio close');
    assert.equal(reader.usage.readers, 1);
    input.destroy(); await immediate(); assert.equal(settled, false, 'stdout must independently close');
    output.destroy(); assert.deepEqual(await pending, expected);
    assert.deepEqual(reader.usage, { readers: 0, reservedPayloadBytes: 0 });
  } finally { input.destroy(); output.destroy(); await Promise.allSettled([pending, reader.close()]); }
});

test('绝对deadline和spawn失败均等待实际owner完成，不留新的reader槽', { timeout: 5000 }, async () => {
  const { reader, isClosed } = native('hold');
  try { await assert.rejects(reader.read(request, signal(), performance.now() + 300), code('HISTORY_TIMEOUT')); assert.equal(isClosed(), true); }
  finally { await reader.close(); }
  const missing = new NativeHistoryReader(resolve('definitely-absent-history-reader.exe'));
  try { await assert.rejects(missing.read(request, signal(), performance.now() + 1000), code('HISTORY_READER_UNAVAILABLE')); assert.equal(missing.usage.readers, 0); }
  finally { await missing.close(); }
});

const fixedHistoryCapability = {
  ...capabilities.find(capability => capability.id === 'operations.get')!, schemaRevision: '4',
  inputId: schemaIds.OperationQueryV3, inputSchema: schemaById[schemaIds.OperationQueryV3]!,
  outputId: schemaIds.OperationLookupV3, outputSchema: schemaById[schemaIds.OperationLookupV3]!,
  validateInput: (value: unknown) => validateContract(schemaIds.OperationQueryV3, value),
  validateOutput: (value: unknown) => validateContract(schemaIds.OperationLookupV3, value),
};

for (const modern of [false, true]) test(`官方SDK ${modern ? '2026' : '2025'}修订4固定历史契约保source/selector和只读错误事实`, async () => {
  const { game, reader } = setup();
  const budget = new WorkBudget(8);
  const input = new PassThrough(), output = new PassThrough();
  const parser = new ReadBuffer({ maxBufferSize: 65537 });
  const clientTransport: Transport = { start: async () => {}, send: async value => { input.write(serializeMessage(value)); }, close: async () => { input.end(); } };
  output.once('close', () => clientTransport.onclose?.());
  output.on('data', (chunk: Buffer) => { parser.append(chunk); for (let value = parser.readMessage(); value !== null; value = parser.readMessage()) clientTransport.onmessage?.(value); });
  const host = startStdioHost({ game, workBudget: budget, capabilities: [fixedHistoryCapability] }, input, output);
  const client = new Client({ name: 'history-fixture', version: '1' }, { capabilities: {}, versionNegotiation: { mode: modern ? { pin: '2026-07-28' } : 'legacy' } });
  try {
    await client.connect(clientTransport);
    const success = await client.callTool({ name: 'skylines_operation_get', arguments: historyQuery });
    assert.notEqual(success.isError, true); requireContract(schemaIds.OperationLookupV3, success.structuredContent);
    reader.next = async () => { throw new GameClientError('HISTORY_UNAVAILABLE', '历史记录不可用。'); };
    const rejected = await client.callTool({ name: 'skylines_operation_get', arguments: historyQuery });
    const error = (rejected.structuredContent as { error: Record<string, unknown> }).error;
    assert.equal(rejected.isError, true); assert.equal(error.source, 'history'); assert.deepEqual(error.selector, historyQuery.selector);
    assert.equal(error.operationId, 'operation-1'); assert.equal(Object.hasOwn(error, 'mayHaveApplied'), false);
    assert.deepEqual((await client.listTools()).tools.find(tool => tool.name === fixedHistoryCapability.toolName)?.outputSchema,
      schemaById[schemaIds.OperationLookupV3]);
  } finally { await client.close(); await host.close(); await game.close(); assert.equal(budget.active, 0); }
});

function retainedMissing(query: OperationQueryV3): OperationLookupV4 {
  return { selector: query.selector, result: { kind: 'live', instanceId: connection.instanceId,
    lookup: { kind: 'unavailable', reason: 'RETENTION_NOT_COVERED' } } };
}

test('查询5直接读取新未覆盖原因，保原revision与选择器；旧4仍按固定schema拒绝', async () => {
  const { game, live, reader, counts } = setup();
  live.result = retainedMissing;
  try {
    for (const selector of [historyQuery.selector, { kind: 'submissionKey' as const,
      submissionKey: { writeContextId: 'ctx-retired', requestId: 'req-retired' } }]) {
      const query = { selector };
      assert.deepEqual(await game.call('operations.get', '5', query, signal()), retainedMissing(query));
      assert.throws(() => requireOperationLookup(query, retainedMissing(query)), code('INVALID_GAME_RESULT'));
      requireOperationLookupV4(query, retainedMissing(query));
    }
    await assert.rejects(game.call('operations.get', '4', { selector: historyQuery.selector }, signal()), code('INVALID_GAME_RESULT'));
    assert.deepEqual(live.revisions, ['5', '5', '4']);
    assert.equal(reader.requests.length, 0); assert.deepEqual(counts(), { discoveries: 1, connections: 1 });
    const invalid = retainedMissing({ selector: historyQuery.selector });
    assert.throws(() => requireOperationLookupV4({ selector: historyQuery.selector }, { ...invalid,
      result: { ...invalid.result, instanceId: 'other-instance' } }, { instanceId: connection.instanceId }), code('INVALID_GAME_RESULT'));
  } finally { await game.close(); }
});

test('查询5保原Native历史协议、固定主体和唯一definite-busy活来源，不因读取失败改源', async () => {
  const { game, reader, live, counts } = setup();
  try {
    const historical = await game.call('operations.get', '5', historyQuery, signal());
    requireContract(schemaIds.OperationLookupV4, historical);
    assert.deepEqual(counts(), { discoveries: 0, connections: 0 });
    assert.equal(reader.requests[0]?.formatVersion, 1);
    assert.deepEqual(reader.requests[0]?.selector, historyQuery.selector);
    reader.next = async () => ({ ...historyCapture, grantEpoch: 4, authorizationRecordRevision: 4 });
    await assert.rejects(game.call('operations.get', '5', historyQuery, signal()), code('SUBJECT_CHANGED'));
    reader.next = async () => { throw new GameClientError('HISTORY_AUTHORITY_BUSY', 'fixture'); };
    requireContract(schemaIds.OperationLookupV4, await game.call('operations.get', '5', historyQuery, signal()));
    assert.deepEqual(live.revisions, ['5']); assert.deepEqual(counts(), { discoveries: 1, connections: 1 });
    const nativeRequests = reader.requests.length;
    live.failure = new GameClientError('UNAUTHORIZED', 'fixture');
    await assert.rejects(game.call('operations.get', '5', historyQuery, signal()), code('UNAUTHORIZED'));
    assert.equal(reader.requests.length, nativeRequests);
    assert.deepEqual(live.revisions, ['5', '5']);
  } finally { await game.close(); }
});

test('查询5首次Native/live并发仍以同一次主体CAS拒迟到来源', async () => {
  const reader = new FakeReader(); let complete!: (value: NativeHistoryCapture) => void;
  reader.next = () => new Promise(resolve => { complete = resolve; });
  const { game, live } = setup(reader, { ...connection, authorityEpoch: 3 });
  try {
    const historical = game.call('operations.get', '5', historyQuery, signal());
    const rejected = assert.rejects(historical, code('SUBJECT_CHANGED'));
    await game.call('operations.get', '5', { selector: historyQuery.selector }, signal());
    complete(structuredClone(historyCapture)); await rejected;
    assert.equal(game.subject?.authorityEpoch, 3); assert.deepEqual(live.revisions, ['5']);
  } finally { await game.close(); }
});

const retainedCapability = capabilities.find(capability => capability.id === 'operations.get')!;

test('活动查询5使用生成绑定，停用4不静默映射到5', () => {
  assert.equal(retainedCapability.schemaRevision, '5');
  assert.equal(retainedCapability.inputId, schemaIds.OperationQueryV3);
  assert.equal(retainedCapability.outputId, schemaIds.OperationLookupV4);
  assert.equal(validateContract(schemaIds.WireRequest, { kind: 'request', requestId: 'unregistered-five',
    capabilityId: 'operations.get', schemaRevision: '5', arguments: historyQuery }).valid, true);
  assert.equal(validateContract(schemaIds.WireRequest, { kind: 'request', requestId: 'inactive-four',
    capabilityId: 'operations.get', schemaRevision: '4', arguments: historyQuery }).valid, false);
});

for (const modern of [false, true]) test(`官方SDK ${modern ? '2026' : '2025'}活动查询5保未覆盖结果及source/selector错误关联`, async () => {
  let next: (query: OperationQueryV3) => unknown = retainedMissing;
  const requests: string[] = [];
  const game = { call: async (id: string, revision: string, args: unknown) => {
    assert.equal(id, 'operations.get'); requests.push(revision); return next(args as OperationQueryV3);
  }, close: async () => {} };
  const budget = new WorkBudget(8), input = new PassThrough(), output = new PassThrough();
  const parser = new ReadBuffer({ maxBufferSize: 65537 });
  const transport: Transport = { start: async () => {}, send: async value => { input.write(serializeMessage(value)); }, close: async () => { input.end(); } };
  output.once('close', () => transport.onclose?.());
  output.on('data', (chunk: Buffer) => { parser.append(chunk); for (let value = parser.readMessage(); value !== null; value = parser.readMessage()) transport.onmessage?.(value); });
  const host = startStdioHost({ game, workBudget: budget, capabilities: [retainedCapability] }, input, output);
  const client = new Client({ name: 'retention-consumer-fixture', version: '1' }, { capabilities: {}, versionNegotiation: { mode: modern ? { pin: '2026-07-28' } : 'legacy' } });
  try {
    await client.connect(transport);
    for (const selector of [historyQuery.selector, { kind: 'submissionKey' as const,
      submissionKey: { writeContextId: 'retired-context', requestId: 'retired-request' } }]) {
      const query = { selector };
      const result = await client.callTool({ name: retainedCapability.toolName, arguments: query });
      assert.notEqual(result.isError, true); assert.deepEqual(result.structuredContent, retainedMissing(query));
      requireContract(schemaIds.OperationLookupV4, result.structuredContent);
      assert.deepEqual(JSON.parse((result.content[0] as { text: string }).text), result.structuredContent);
    }
    for (const [query, make] of [
      [{ selector: historyQuery.selector }, (query: OperationQueryV3) => ({ ...retainedMissing(query), selector: { kind: 'operationId', operationId: 'other-id' } })],
      [{ selector: historyQuery.selector }, (query: OperationQueryV3) => activeLookup({ ...query, source: 'history' })],
      [{ selector: historyQuery.selector }, (query: OperationQueryV3) => ({ ...retainedMissing(query), result: { kind: 'live', instanceId: connection.instanceId, lookup: { kind: 'expired' } } })],
      [historyQuery, (query: OperationQueryV3) => {
        const value = activeLookup(query); if (value.result.kind !== 'history' || value.result.lookup.kind !== 'found') throw new Error('History fixture');
        return { ...value, result: { ...value.result, lookup: { kind: 'found', record: { ...value.result.lookup.record, operationId: 'other-id' } } } };
      }],
    ] as const) {
      next = make;
      const result = await client.callTool({ name: retainedCapability.toolName, arguments: query });
      const error = (result.structuredContent as { error: Record<string, unknown> }).error;
      assert.equal(result.isError, true); assert.match(String(error.code), /^INVALID_(GAME|HISTORY)_RESULT$/);
      assert.deepEqual(error.selector, query.selector); assert.equal(error.source, 'source' in query ? query.source : 'live');
      assert.equal(error.operationId, historyQuery.selector.kind === 'operationId' ? historyQuery.selector.operationId : undefined);
      assert.equal(Object.hasOwn(error, 'mayHaveApplied'), false);
    }
    assert(requests.every(revision => revision === '5'));
  } finally { await client.close(); await host.close(); assert.equal(budget.active, 0); }
});
