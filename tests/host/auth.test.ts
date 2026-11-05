import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { randomBytes, randomUUID } from 'node:crypto';
import { createServer, Socket } from 'node:net';
import { setTimeout as delay, setImmediate as immediate } from 'node:timers/promises';
import { createHandshakeProof } from '../../src/host/src/game-client/auth-proof.ts';
import { NamedPipeGameClient, encodePipeFrame, decodePipeFrameLength, type PipeConnection } from '../../src/host/src/game-client/named-pipe-client.ts';
import { GameClientError } from '../../src/host/src/game-client/game-client.ts';
import { probeLimits } from '../../src/host/src/shared/limits.ts';
import { checkRawJson } from '../../src/host/src/shared/json-budget.ts';
import { parseContractJson, requireContract, schemaIds, ContractValidationError, type WireAuthenticateV2, type WireChallengeV2, type WireRequest } from '../../contracts/ts/index.ts';
import { syntheticContext } from './fixture.ts';
import { moduleInstanceId, modulePage, moduleRow } from './module-runtime-values.ts';

interface FrameVectors {
  format: string;
  maximumBytes: number;
  encodeVectors: Array<{ name: string; json: string; payloadBytes: number; payloadHex: string; frameHex: string }>;
  headers: Array<{ name: string; hex: string; length: number | null; error: string | null }>;
  invalidUtf8: Array<{ name: string; frameHex: string; error: string }>;
}

/** One controlled authenticated OS pipe; it holds bounded RPCs, never invokes game code. */
async function heldRpcFixture(receiveDeadlineMs = 1000, maximum = 2) {
  const connection: PipeConnection = { pipeName: `skylineslink-rpc-${randomUUID()}`, profileId: 'Codex', profileInstanceId: 'a'.repeat(32),
    environmentId: 'fixture', token: randomBytes(32).toString('hex'), instanceId: 'fixture-instance', authorityEpoch: 2, grantEpoch: 3,
    coreManifestSha256: 'a'.repeat(64), productVersion: '0.1.0-dev.0', contractVersion: '2' };
  const requests: WireRequest[] = [], sockets = new Set<Socket>();
  const limits = { ...probeLimits, activeRequests: maximum, receiveDeadlineMs, sendDeadlineMs: 200 };
  let peer: Socket | undefined;
  const server = createServer(socket => {
    peer = socket; sockets.add(socket); socket.on('close', () => sockets.delete(socket)); socket.on('error', () => {});
    let input = Buffer.alloc(0), auth: WireAuthenticateV2 | undefined, nonce = '', authenticated = false;
    socket.on('data', (chunk: Buffer) => {
      input = Buffer.concat([input, chunk]); assert.ok(input.length <= probeLimits.messageBytes + 12);
      while (input.length >= 12) {
        const length = decodePipeFrameLength(input.subarray(0, 12), probeLimits.messageBytes);
        if (input.length < length + 12) break;
        const value = parseContractJson(checkRawJson(input.subarray(12, length + 12), probeLimits)) as Record<string, unknown>;
        input = input.subarray(length + 12);
        assert.equal(JSON.stringify(value).includes(connection.token), false);
        if (value.kind === 'authenticate') {
          assert.equal(auth, undefined); requireContract(schemaIds.WireAuthenticateV2, value); auth = value as unknown as WireAuthenticateV2;
          nonce = randomBytes(32).toString('hex'); const key = Buffer.from(connection.token, 'hex');
          try { socket.write(encodePipeFrame({ kind: 'challenge', ...binding(auth), clientNonce: auth.clientNonce, serverNonce: nonce,
            serverProof: createHandshakeProof(key, 'server', auth, nonce) })); } finally { key.fill(0); }
        } else if (value.kind === 'proof') {
          assert.ok(auth); requireContract(schemaIds.WireProofV2, value); const key = Buffer.from(connection.token, 'hex');
          try { assert.equal(value.clientProof, createHandshakeProof(key, 'client', auth, nonce)); } finally { key.fill(0); }
          authenticated = true; socket.write(encodePipeFrame({ kind: 'authenticated', ...binding(auth) }));
        } else {
          assert.equal(authenticated, true); requireContract(schemaIds.WireRequest, value);
          assert.ok(requests.length < 16, 'finite endpoint fixture cannot collect an unbounded request log');
          requests.push(value as unknown as WireRequest);
        }
      }
    });
  });
  await new Promise<void>((resolve, reject) => { server.once('error', reject); server.listen(`\\\\.\\pipe\\${connection.pipeName}`, resolve); });
  let client: NamedPipeGameClient;
  try { client = await NamedPipeGameClient.connect(connection, limits); }
  catch (error) { for (const socket of sockets) socket.destroy(); await new Promise<void>(resolve => server.close(() => resolve())); throw error; }
  const context = { productVersion: connection.productVersion, contractVersion: '2', instanceId: connection.instanceId,
    world: null, gameState: 'menu', catalogRevision: 'fixture' };
  return { client, requests, context,
    async waitFor(count: number) {
      const until = performance.now() + 800;
      while (requests.length < count) { if (performance.now() >= until) throw new Error('Controlled pipe request deadline.'); await delay(1); }
    },
    send(value: unknown) { assert.ok(peer); peer.write(encodePipeFrame(value)); },
    reply(index: number) { assert.ok(peer); assert.ok(requests[index]); peer.write(encodePipeFrame({ kind: 'result', requestId: requests[index]!.requestId, result: context })); },
    async close() { await client.close(); for (const socket of sockets) socket.destroy(); await new Promise<void>(resolve => server.close(() => resolve())); },
  };
}

test('真实认证pipe模块读取绑定本次instance，语义坏GUID拒绝并实际关闭释放', { timeout: 5000 }, async () => {
  const f = await heldRpcFixture();
  const valid = { ...modulePage(), snapshot: { ...modulePage().snapshot, instanceId: f.client.connection.instanceId } };
  assert.notEqual(valid.snapshot.instanceId, moduleInstanceId);
  try {
    const first = f.client.call('system.modules.get', '1', { kind: 'capture' }, new AbortController().signal);
    await f.waitFor(1); f.send({ kind: 'result', requestId: f.requests[0]!.requestId, result: valid });
    assert.deepEqual(await first, valid); assert.equal(f.client.usage.pending, 0);
    const next = f.client.call('system.modules.get', '1', { kind: 'capture' }, new AbortController().signal);
    // The existing pipe boundary rejects all still-pending reads as disconnected after protocol close.
    const rejected = assert.rejects(next, error => error instanceof GameClientError && error.code === 'GAME_DISCONNECTED');
    await f.waitFor(2); f.send({ kind: 'result', requestId: f.requests[1]!.requestId,
      result: { ...valid, entries: [moduleRow(0, { assetGuid: '0'.repeat(32) })] } });
    await rejected; await f.client.close();
    assert.deepEqual(f.client.usage, { pending: 0, pendingBytes: 0, frameBytes: 0, closed: true });
  } finally { await f.close(); }
});

test('认证后单RPC取消只保留draining：逆序其他reply成功、满槽不早退、合法原reply后才CANCELLED', { timeout: 5000 }, async () => {
  const f = await heldRpcFixture();
  const abort = new AbortController(); let settled = false;
  const first = f.client.call('system.context', '2', {}, abort.signal).finally(() => { settled = true; });
  const cancelled = assert.rejects(first, (error: unknown) => error instanceof GameClientError && error.code === 'CANCELLED' && !error.mayHaveApplied);
  const second = f.client.call('system.context', '2', {}, new AbortController().signal); void second.catch(() => {});
  try {
    await f.waitFor(2); abort.abort(); await immediate();
    assert.equal(settled, false); assert.equal(f.client.usage.pending, 2); assert.equal(f.client.usage.closed, false);
    await assert.rejects(f.client.call('system.context', '2', {}, new AbortController().signal),
      (error: unknown) => error instanceof GameClientError && error.code === 'BUSY');
    assert.equal(f.requests.length, 2, 'a draining entry retains admission and sends no cancellation/retry frame');
    f.reply(1); assert.deepEqual(await second, f.context); assert.equal(f.client.usage.pending, 1); assert.equal(settled, false);
    const third = f.client.call('system.context', '2', {}, new AbortController().signal); void third.catch(() => {});
    await f.waitFor(3); f.reply(2); assert.deepEqual(await third, f.context); assert.equal(f.client.usage.pending, 1);
    f.reply(0); await cancelled; assert.equal(f.client.usage.pending, 0); assert.equal(f.client.usage.closed, false);
    const before = new AbortController(); before.abort();
    await assert.rejects(f.client.call('system.context', '2', {}, before.signal),
      (error: unknown) => error instanceof GameClientError && error.code === 'CANCELLED');
    await immediate(); assert.equal(f.requests.length, 3, 'pre-aborted calls consume no request or send slot');
    const errorAbort = new AbortController();
    const lateError = assert.rejects(f.client.call('system.context', '2', {}, errorAbort.signal),
      (error: unknown) => error instanceof GameClientError && error.code === 'CANCELLED');
    await f.waitFor(4); errorAbort.abort();
    f.send({ kind: 'error', requestId: f.requests[3]!.requestId, code: 'UNAUTHORIZED', message: 'fixture rejection' });
    await lateError; assert.equal(f.client.usage.pending, 0); assert.equal(f.client.usage.closed, false);
  } finally { abort.abort(); await f.close(); await Promise.allSettled([first, second]); }
  assert.deepEqual(f.client.usage, { pending: 0, pendingBytes: 0, frameBytes: 0, closed: true });
});

for (const mode of ['malformed', 'unknown-id', 'close'] as const) test(`取消后${mode}仍真实关闭共享连接并清算所有owner`, { timeout: 4000 }, async () => {
  const f = await heldRpcFixture(); const abort = new AbortController();
  const cancelled = assert.rejects(f.client.call('system.context', '2', {}, abort.signal),
    (error: unknown) => error instanceof GameClientError && error.code === 'CANCELLED' && !error.mayHaveApplied);
  const other = assert.rejects(f.client.call('system.context', '2', {}, new AbortController().signal),
    (error: unknown) => error instanceof GameClientError && error.code === 'GAME_DISCONNECTED');
  try {
    await f.waitFor(2); abort.abort(); await immediate(); assert.equal(f.client.usage.pending, 2);
    if (mode === 'close') await f.client.close();
    else f.send({ kind: 'result', requestId: mode === 'unknown-id' ? 'unrelated-request' : f.requests[0]!.requestId,
      result: mode === 'malformed' ? { invalid: true } : f.context });
    await Promise.all([cancelled, other]); await f.client.close();
    assert.deepEqual(f.client.usage, { pending: 0, pendingBytes: 0, frameBytes: 0, closed: true });
  } finally { abort.abort(); await f.close(); await Promise.allSettled([cancelled, other]); }
});

test('取消不重置已受理RPC的原绝对接收期限，迟迟无reply仍按原期限真实关闭', { timeout: 4000 }, async () => {
  const f = await heldRpcFixture(800); const abort = new AbortController(); const started = performance.now();
  let settled = false;
  const pending = f.client.call('system.context', '2', {}, abort.signal).finally(() => { settled = true; });
  const cancelled = assert.rejects(pending, (error: unknown) => error instanceof GameClientError && error.code === 'CANCELLED');
  try {
    await f.waitFor(1); await delay(600); abort.abort(); await immediate();
    assert.equal(settled, false); assert.equal(f.client.usage.pending, 1); assert.equal(f.client.usage.closed, false);
    await cancelled; const elapsed = performance.now() - started;
    assert.ok(elapsed >= 850 && elapsed < 1500, 'original 800+200ms deadline remains; abort neither closes early nor starts another full window');
    await f.client.close(); assert.deepEqual(f.client.usage, { pending: 0, pendingBytes: 0, frameBytes: 0, closed: true });
  } finally { abort.abort(); await f.close(); await Promise.allSettled([pending]); }
});

test('真实OS pipe：8普通pending满后8个不同Stop仍发出，重复pair不叠waiter，冻结原输入并保取消owner', { timeout: 6000 }, async () => {
  const f = await heldRpcFixture(2500, 8);
  const pending: Promise<unknown>[] = [];
  const cancelled = new AbortController();
  try {
    for (let i = 0; i < 8; i++) pending.push(f.client.call('system.context', '2', {}, new AbortController().signal));
    await f.waitFor(8);
    for (let i = 0; i < 8; i++) {
      const args = { taskId: `stop-task-${i}`, expectedControlVersion: 1 };
      const call = f.client.call('tasks.stop', '1', args, i === 0 ? cancelled.signal : new AbortController().signal);
      pending.push(i === 0 ? assert.rejects(call, (error: unknown) => error instanceof GameClientError && error.code === 'CANCELLED' && error.mayHaveApplied) : call);
      args.taskId = 'caller-mutated'; args.expectedControlVersion = 2;
    }
    for (const p of pending) void p.catch(() => {});
    await f.waitFor(16); assert.equal(f.client.usage.pending, 16);
    await assert.rejects(f.client.call('tasks.stop', '1', { taskId: 'stop-task-0', expectedControlVersion: 1 }, new AbortController().signal),
      (error: unknown) => error instanceof GameClientError && error.code === 'BUSY' && error.mayHaveApplied);
    cancelled.abort(); await immediate(); assert.equal(f.client.usage.pending, 16);
    for (let i = 15; i >= 8; i--) {
      const request = f.requests[i]!;
      const input = request.arguments as { taskId: string; expectedControlVersion: number };
      assert.equal(input.taskId, `stop-task-${i - 8}`); assert.equal(input.expectedControlVersion, 1);
      f.send({ kind: 'result', requestId: request.requestId, result: { selector: input, lookup: { kind: 'pending', receipt: {
        submissionKey: { writeContextId: 'retained-original-key', requestId: 'stop' }, controlVersion: 2,
        stopRequested: true, persistenceAttempted: true, journal: null,
      } } } });
    }
    await Promise.all(pending.slice(8)); assert.equal(f.client.usage.pending, 8);
    for (let i = 0; i < 8; i++) f.reply(i);
    await Promise.all(pending);
  } finally { await f.close(); await Promise.allSettled(pending); }
  assert.deepEqual(f.client.usage, { pending: 0, pendingBytes: 0, frameBytes: 0, closed: true });
});

async function frameVectors(): Promise<FrameVectors> {
  return JSON.parse(await readFile('../../contracts/fixtures/pipe-frame-v1.json', 'utf8')) as FrameVectors;
}

test('共享 SLNK/1 固定字节：编码与头边界独立于 Wire 契约', async () => {
  const vectors = await frameVectors();
  assert.equal(vectors.format, 'SLNK/1');
  assert.equal(vectors.maximumBytes, probeLimits.messageBytes);
  for (const vector of vectors.encodeVectors) {
    const frame = encodePipeFrame(JSON.parse(vector.json));
    assert.equal(frame.toString('hex'), vector.frameHex, vector.name);
    assert.equal(frame.subarray(12).toString('hex'), vector.payloadHex, vector.name);
    assert.equal(frame.length - 12, vector.payloadBytes, vector.name);
  }
  for (const vector of vectors.headers) {
    const decode = () => decodePipeFrameLength(Buffer.from(vector.hex, 'hex'), vectors.maximumBytes);
    if (vector.length === null) assert.throws(decode, vector.name);
    else assert.equal(decode(), vector.length, vector.name);
  }
});

test('共享载荷分层：非法 UTF8 在解码失败；1/2 字节 JSON 在 Wire 契约失败', async () => {
  const vectors = await frameVectors();
  for (const vector of vectors.invalidUtf8) {
    const frame = Buffer.from(vector.frameHex, 'hex');
    assert.equal(decodePipeFrameLength(frame.subarray(0, 12), vectors.maximumBytes), frame.length - 12, vector.name);
    assert.throws(() => checkRawJson(frame.subarray(12), probeLimits), TypeError, vector.name);
  }
  for (const vector of vectors.encodeVectors.filter(v => v.payloadBytes <= 2)) {
    const frame = Buffer.from(vector.frameHex, 'hex');
    assert.equal(decodePipeFrameLength(frame.subarray(0, 12), vectors.maximumBytes), vector.payloadBytes, vector.name);
    const json = checkRawJson(frame.subarray(12), probeLimits);
    assert.equal(json, vector.json, vector.name);
    assert.throws(() => requireContract(schemaIds.WireChallengeV2, parseContractJson(json)), ContractValidationError, vector.name);
  }
});

test('实际接收拒绝共享非法 UTF8、非 Wire 小载荷及仅超长头，不等巨大 body', { timeout: 5000 }, async () => {
  const vectors = await frameVectors();
  const cases = [
    ...vectors.invalidUtf8,
    ...vectors.encodeVectors.filter(v => v.payloadBytes <= 2),
    { name: 'uint32-maximum-header-only', frameHex: vectors.headers.find(v => v.name === 'uint32-maximum')!.hex }
  ];
  for (const vector of cases) {
    const connection: PipeConnection = { pipeName: `skylineslink-frame-${randomUUID()}`, profileId: 'Codex', profileInstanceId: 'a'.repeat(32), environmentId: 'fixture', token: randomBytes(32).toString('hex'),
      instanceId: 'fixture-instance', authorityEpoch: 2, grantEpoch: 3, coreManifestSha256: 'a'.repeat(64), productVersion: '0.1.0-dev.0', contractVersion: '2' };
    const sockets = new Set<Socket>(); let received = 0;
    const server = createServer(socket => {
      sockets.add(socket); socket.on('close', () => sockets.delete(socket)); socket.on('error', () => {});
      let input = Buffer.alloc(0);
      socket.on('data', (chunk: Buffer) => {
        input = Buffer.concat([input, chunk]); assert.ok(input.length <= 65548);
        if (input.length < 12) return;
        const length = decodePipeFrameLength(input.subarray(0, 12), vectors.maximumBytes);
        if (input.length < 12 + length) return;
        assert.equal(input.length, 12 + length, vector.name);
        requireContract(schemaIds.WireAuthenticateV2, parseContractJson(checkRawJson(input.subarray(12), probeLimits)));
        input = Buffer.alloc(0);
        assert.equal(++received, 1, 'a rejected frame must not elicit another authentication frame');
        socket.write(Buffer.from(vector.frameHex, 'hex'));
      });
    });
    await new Promise<void>((resolve, reject) => { server.once('error', reject); server.listen(`\\\\.\\pipe\\${connection.pipeName}`, resolve); });
    try {
      await assert.rejects(NamedPipeGameClient.connect(connection, { ...probeLimits, receiveDeadlineMs: 1000 }),
        (error: unknown) => error instanceof GameClientError && error.code === 'INVALID_GAME_RESPONSE', vector.name);
      assert.equal(received, 1, vector.name);
    } finally {
      for (const socket of sockets) socket.destroy();
      await new Promise<void>(resolve => server.close(() => resolve()));
    }
  }
});

test('三组独立HMAC向量：角色、UTF8字节长度及安全整数上界一致', async () => {
  const data = JSON.parse(await readFile('../../contracts/fixtures/handshake-v2.json', 'utf8')) as { vectors: Array<{ internalToken: string; fields: Omit<WireChallengeV2, 'kind' | 'serverProof'>; serverProof: string; clientProof: string }> };
  for (const vector of data.vectors) {
    const f = vector.fields;
    const auth: WireAuthenticateV2 = { kind: 'authenticate', requestId: f.requestId, profileId: f.profileId, environmentId: f.environmentId,
      expectedInstanceId: f.instanceId, expectedAuthorityEpoch: f.authorityEpoch, expectedGrantEpoch: f.grantEpoch,
      productVersion: f.productVersion, contractVersion: '2', coreManifestSha256: f.coreManifestSha256, clientNonce: f.clientNonce };
    const key = Buffer.from(vector.internalToken, 'hex');
    try { assert.equal(createHandshakeProof(key, 'server', auth, f.serverNonce), vector.serverProof); assert.equal(createHandshakeProof(key, 'client', auth, f.serverNonce), vector.clientProof); }
    finally { key.fill(0); }
  }
});

function binding(auth: WireAuthenticateV2) {
  return { requestId: auth.requestId, environmentId: auth.environmentId, profileId: auth.profileId, instanceId: auth.expectedInstanceId,
    productVersion: auth.productVersion, contractVersion: '2' as const, coreManifestSha256: auth.coreManifestSha256,
    authorityEpoch: auth.expectedAuthorityEpoch, grantEpoch: auth.expectedGrantEpoch };
}

test('真实OS proof已送达且合法ACK已消费，connect仍等待proof实际callback；完成后立即8普通不借握手槽', { timeout: 5000 }, async t => {
  const connection: PipeConnection = { pipeName: `skylineslink-handoff-${randomUUID()}`, profileId: 'Codex', profileInstanceId: 'a'.repeat(32),
    environmentId: 'fixture', token: randomBytes(32).toString('hex'), instanceId: 'fixture-instance', authorityEpoch: 2, grantEpoch: 3,
    coreManifestSha256: 'a'.repeat(64), productVersion: '0.1.0-dev.0', contractVersion: '2' };
  let heldWrite!: () => void, actualWrite!: () => void, observedAck!: () => void;
  const proofWritten = new Promise<void>(r => { actualWrite = r; });
  const ackDelivered = new Promise<void>(r => { observedAck = r; });
  const originalWrite = Socket.prototype.write, originalEmit = Socket.prototype.emit;
  const sockets = new Set<Socket>(); const requests: WireRequest[] = [];
  const server = createServer(socket => {
    sockets.add(socket); socket.on('close', () => sockets.delete(socket)); socket.on('error', () => {});
    let buffered = Buffer.alloc(0), auth: WireAuthenticateV2;
    socket.on('data', (chunk: Buffer) => {
      buffered = Buffer.concat([buffered, chunk]); assert.ok(buffered.length <= probeLimits.messageBytes * 2);
      while (buffered.length >= 12 && buffered.length >= 12 + buffered.readUInt32LE(8)) {
        const size = buffered.readUInt32LE(8), value = JSON.parse(buffered.subarray(12, 12 + size).toString('utf8'));
        buffered = buffered.subarray(12 + size);
        if (value.kind === 'authenticate') {
          auth = value; const nonce = randomBytes(32).toString('hex'), key = Buffer.from(connection.token, 'hex');
          try { socket.write(encodePipeFrame({ kind: 'challenge', ...binding(auth), clientNonce: auth.clientNonce,
            serverNonce: nonce, serverProof: createHandshakeProof(key, 'server', auth, nonce) })); }
          finally { key.fill(0); }
        } else if (value.kind === 'proof') socket.write(encodePipeFrame({ kind: 'authenticated', ...binding(auth!) }));
        else { assert.equal(value.kind, 'request'); requests.push(value); }
      }
    });
  });
  // Only withhold the completion callback of the real proof write, not bytes,
  // socket events, authentication phase, or a timer-based imitation of IO.
  t.mock.method(Socket.prototype, 'write', function(this: Socket, ...args: unknown[]) {
    const frame = args[0];
    if (Buffer.isBuffer(frame) && frame.length >= 12 && frame.subarray(0, 4).toString('ascii') === 'SLNK'
      && JSON.parse(frame.subarray(12).toString('utf8')).kind === 'proof') {
      const callback = args.at(-1) as (error?: Error | null) => void;
      assert.equal(typeof callback, 'function');
      args[args.length - 1] = (error?: Error | null) => {
        let held = true; heldWrite = () => { if (held) { held = false; callback(error); } }; actualWrite();
      };
    }
    return Reflect.apply(originalWrite, this, args) as boolean;
  });
  t.mock.method(Socket.prototype, 'emit', function(this: Socket, ...args: unknown[]) {
    const result = Reflect.apply(originalEmit, this, args) as boolean;
    const chunk = args[1];
    if (args[0] === 'data' && !sockets.has(this) && Buffer.isBuffer(chunk) && chunk.length > 12
      && chunk.subarray(0, 4).toString('ascii') === 'SLNK'
      && chunk.length >= 12 + chunk.readUInt32LE(8)
      && JSON.parse(chunk.subarray(12, 12 + chunk.readUInt32LE(8)).toString('utf8')).kind === 'authenticated') observedAck();
    return result;
  });
  await new Promise<void>((r, reject) => { server.once('error', reject); server.listen(`\\\\.\\pipe\\${connection.pipeName}`, r); });
  let client: NamedPipeGameClient | undefined; const pending: Promise<unknown>[] = [];
  let settled = false;
  const connecting = NamedPipeGameClient.connect(connection).then(value => { settled = true; return value; });
  try {
    await Promise.all([proofWritten, ackDelivered]); await immediate();
    assert.equal(settled, false, 'a received valid ACK does not refund the still-owned proof write');
    heldWrite(); client = await connecting;
    assert.equal(client.usage.pendingBytes, 0);
    for (let i = 0; i < 8; i++) { const call = client.call('system.context', '2', {}, new AbortController().signal); pending.push(call); void call.catch(() => {}); }
    const end = performance.now() + 1000;
    while (requests.length !== 8) { assert.ok(performance.now() < end); await immediate(); }
    const peer = [...sockets][0]!;
    for (const request of requests) peer.write(encodePipeFrame({ kind: 'result', requestId: request.requestId, result: syntheticContext }));
    await Promise.all(pending);
  } finally {
    heldWrite?.(); await client?.close(); for (const socket of sockets) socket.destroy();
    await Promise.allSettled(pending); await Promise.allSettled([connecting]);
    await new Promise<void>(r => server.close(() => r()));
  }
});

test('缺失或非法本地profile incarnation在打开任何socket之前拒绝', { timeout: 3000 }, async () => {
  let accepted = 0;
  const connection: PipeConnection = { pipeName: `skylineslink-pid-${randomUUID()}`, profileId: 'Codex', profileInstanceId: 'a'.repeat(32),
    environmentId: 'fixture', token: randomBytes(32).toString('hex'), instanceId: 'fixture-instance', authorityEpoch: 2, grantEpoch: 3,
    coreManifestSha256: 'a'.repeat(64), productVersion: '0.1.0-dev.0', contractVersion: '2' };
  const server = createServer(socket => { accepted++; socket.destroy(); });
  await new Promise<void>((resolve, reject) => { server.once('error', reject); server.listen(`\\\\.\\pipe\\${connection.pipeName}`, resolve); });
  try {
    for (const profileInstanceId of [undefined, null, '', 'a'.repeat(31), 'a'.repeat(33), 'A'.repeat(32), 'g'.repeat(32), { toString: () => 'a'.repeat(32) }]) {
      await assert.rejects(NamedPipeGameClient.connect({ ...connection, profileInstanceId } as unknown as PipeConnection),
        (error: unknown) => error instanceof GameClientError && error.code === 'INCOMPATIBLE_COMPONENT');
    }
    await new Promise<void>(resolve => setImmediate(resolve));
    assert.equal(accepted, 0, 'invalid incarnation admission never creates a pipe connection');
  } finally { await new Promise<void>(resolve => server.close(() => resolve())); }
});

for (const scenario of ['bad-proof', 'reflection', 'replay', 'non-ascii-magic', 'early-ack', 'coalesced-ack', 'duplicate-challenge', 'wrong-final-epoch', 'absolute-timeout', 'valid'] as const) {
  test(`独立假端点：${scenario}，长期token不进入任何帧`, { timeout: 3000 }, async () => {
    const connection: PipeConnection = { pipeName: `skylineslink-auth-${randomUUID()}`, profileId: 'Codex', profileInstanceId: 'a'.repeat(32), environmentId: 'fixture', token: randomBytes(32).toString('hex'),
      instanceId: 'fixture-instance', authorityEpoch: 2, grantEpoch: 3, coreManifestSha256: 'a'.repeat(64), productVersion: '0.1.0-dev.0', contractVersion: '2' };
    const frames: Array<Record<string, unknown>> = []; const sockets = new Set<Socket>(); const timers: NodeJS.Timeout[] = [];
    const server = createServer(socket => {
      sockets.add(socket); socket.on('close', () => sockets.delete(socket)); socket.on('error', () => {});
      let buffer = Buffer.alloc(0); let auth: WireAuthenticateV2; let nonce = '';
      socket.on('data', (chunk: Buffer) => {
        buffer = Buffer.concat([buffer, chunk]); assert.ok(buffer.length <= 65536);
        while (buffer.length >= 12 && buffer.length >= 12 + buffer.readUInt32LE(8)) {
          const n = buffer.readUInt32LE(8); const frame = JSON.parse(buffer.subarray(12, 12 + n).toString('utf8')) as Record<string, unknown>; buffer = buffer.subarray(12 + n);
          frames.push(frame); assert.equal(JSON.stringify(frame).includes(connection.token), false);
          assert.equal('profileInstanceId' in frame, false, 'local incarnation metadata is not added to the frozen Wire2 grammar or proof');
          if (frame.kind === 'authenticate') {
            auth = frame as unknown as WireAuthenticateV2; nonce = randomBytes(32).toString('hex');
            const key = Buffer.from(connection.token, 'hex');
            const challenge = { kind: 'challenge', ...binding(auth), clientNonce: auth.clientNonce, serverNonce: nonce, serverProof: createHandshakeProof(key, 'server', auth, nonce) };
            const accepted = { kind: 'authenticated', ...binding(auth) };
            if (scenario === 'bad-proof') challenge.serverProof = '0'.repeat(64);
            if (scenario === 'reflection') challenge.serverProof = createHandshakeProof(key, 'client', auth, nonce);
            if (scenario === 'replay') challenge.serverProof = createHandshakeProof(key, 'server', { ...auth, clientNonce: '1'.repeat(64), requestId: 'previous-connection' }, nonce);
            key.fill(0);
            if (scenario === 'non-ascii-magic') {
              const frame = encodePipeFrame(challenge);
              // ASCII decoding masks bit 7; the wire magic is four exact bytes.
              for (let i = 0; i < 4; i++) frame[i] = frame[i]! | 0x80;
              socket.write(frame);
            }
            else if (scenario === 'early-ack') socket.write(encodePipeFrame(accepted));
            else if (scenario === 'coalesced-ack') socket.write(Buffer.concat([encodePipeFrame(challenge), encodePipeFrame(accepted)]));
            else if (scenario === 'duplicate-challenge') socket.write(Buffer.concat([encodePipeFrame(challenge), encodePipeFrame(challenge)]));
            else if (scenario === 'absolute-timeout') timers.push(setTimeout(() => socket.write(encodePipeFrame(challenge)), 80));
            else socket.write(encodePipeFrame(challenge));
          } else if (frame.kind === 'proof') {
            const key = Buffer.from(connection.token, 'hex');
            try { assert.equal(frame.clientProof, createHandshakeProof(key, 'client', auth!, nonce)); } finally { key.fill(0); }
            if (scenario !== 'absolute-timeout') socket.write(encodePipeFrame({ kind: 'authenticated', ...binding(auth!), ...(scenario === 'wrong-final-epoch' ? { grantEpoch: auth!.expectedGrantEpoch + 1 } : {}) }));
          }
        }
      });
    });
    await new Promise<void>((resolve, reject) => { server.once('error', reject); server.listen(`\\\\.\\pipe\\${connection.pipeName}`, resolve); });
    try {
      if (scenario === 'valid') {
        const client = await NamedPipeGameClient.connect(connection);
        assert.equal(client.connection.profileInstanceId, connection.profileInstanceId); assert.equal(Object.isFrozen(client.connection), true);
        assert.equal('token' in client.connection, false);
        await client.close(); assert.equal(client.usage.pendingBytes, 0);
      }
      else await assert.rejects(NamedPipeGameClient.connect(connection, { ...probeLimits, receiveDeadlineMs: scenario === 'absolute-timeout' ? 150 : 1000 }), (error: unknown) => error instanceof GameClientError && (scenario !== 'absolute-timeout' || error.code === 'AUTHENTICATION_TIMEOUT'));
      if (['bad-proof', 'reflection', 'replay', 'non-ascii-magic', 'early-ack', 'coalesced-ack', 'duplicate-challenge'].includes(scenario)) assert.equal(frames.length, 1, 'must not emit client proof to an unauthenticated/wrong-phase endpoint');
      else assert.equal(frames.length, 2);
    } finally { for (const timer of timers) clearTimeout(timer); for (const socket of sockets) socket.destroy(); await new Promise<void>(resolve => server.close(() => resolve())); }
  });
}
