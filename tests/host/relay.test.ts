import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createServer, type Socket } from 'node:net';
import { randomBytes } from 'node:crypto';
import { spawn } from 'node:child_process';
import { resolve } from 'node:path';
import { PassThrough } from 'node:stream';
import { connectStdioRelay, relayPipeNames } from '../../src/host/src/composition/stdio-relay.ts';
import { createRelayServers, within } from './relay-fixture.ts';
import { startStdioHost } from '../../src/host/src/mcp/transports/stdio.ts';
import { MemoryGame } from './fixture.ts';

test('私有stdio管道：不合法身份、缺端点、单端失败均关闭且不重试', { timeout: 3000 }, async () => {
  for (const nonce of ['', 'A'.repeat(32), '../other', 'a'.repeat(33)]) assert.throws(() => relayPipeNames(nonce));
  const nonce = randomBytes(16).toString('hex');
  await assert.rejects(connectStdioRelay(nonce, 100));
  await assert.rejects(connectStdioRelay(nonce, 100, AbortSignal.abort()));
  let connection: Socket | undefined, accepted = 0;
  let released!: () => void;
  const closed = new Promise<void>(resolve => { released = resolve; });
  const server = createServer(socket => { accepted++; connection = socket; socket.on('error', () => {}); socket.once('close', released); socket.resume(); });
  await new Promise<void>(resolve => server.listen(relayPipeNames(nonce).input, resolve));
  try {
    await assert.rejects(connectStdioRelay(nonce, 100));
    await within(closed, 500);
    assert.equal(accepted, 1, 'the partial-startup case must actually accept then release the first endpoint');
  } finally { connection?.destroy(); await new Promise<void>(resolve => server.close(() => resolve())); }
});

test('私有stdio管道：输入EOF或输出反向字节结束整个对，无静默重连', { timeout: 3000 }, async () => {
  for (const mode of ['eof', 'reverse']) {
    const servers = await createRelayServers();
    try {
      const relay = await connectStdioRelay(servers.nonce, 500);
      const [input, output] = await Promise.all([servers.input, servers.output]);
      const closed = Promise.all([relay.input, relay.output].map(socket => new Promise<void>(resolve => socket.once('close', resolve))));
      relay.input.resume();
      if (mode === 'eof') input.end(); else output.write('unexpected');
      await within(closed, 500); await relay.close(); await relay.close();
      assert.equal(relay.input.destroyed, true); assert.equal(relay.output.destroyed, true);
    } finally { await servers.close(); }
  }
});

test('自然EOF清理失败通过同一closed结果报告，其他自有流仍真实关闭', async () => {
  const input = new PassThrough(), output = new PassThrough(); output.resume();
  const game = new MemoryGame(); game.close = async () => { throw new Error('synthetic close error'); };
  const host = startStdioHost({ game }, input, output);
  const completion = assert.rejects(host.closed);
  input.end(); await completion; await assert.rejects(host.close());
  assert.equal(input.destroyed, true); assert.equal(output.destroyed, true); assert.equal(host.transport.usage.sendCount, 0);
});

test('产品CLI无relay拒绝；缺端点启动失败，不向任何标准输出写诊断', { timeout: 5000 }, async () => {
  const base = ['--profile', 'probe', '--environment', 'probe', '--core-manifest-sha256', 'a'.repeat(64)];
  for (const [args, expected] of [[base, 2], [[...base, '--stdio-relay', randomBytes(16).toString('hex')], 3]] as const) {
    const child = spawn(process.execPath, [resolve('../../artifacts/host/host.mjs'), ...args], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    let bytes = 0;
    child.stdout.on('data', (chunk: Buffer) => { bytes += chunk.length; });
    child.stderr.on('data', (chunk: Buffer) => { bytes += chunk.length; });
    const exit = new Promise<number | null>((resolve, reject) => { child.once('error', reject); child.once('exit', resolve); });
    try { assert.equal(await within(exit, 1500), expected); assert.equal(bytes, 0); }
    finally {
      if (child.exitCode === null && child.signalCode === null) child.kill();
      await exit.catch(() => {}); child.stdout.destroy(); child.stderr.destroy();
    }
  }
});
