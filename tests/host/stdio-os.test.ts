import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { startRelayChild } from './relay-fixture.ts';
import { measureJson } from '../../src/host/src/shared/json-budget.ts';
import { probeLimits } from '../../src/host/src/shared/limits.ts';
import { controlTransportProfile } from '../../src/host/src/shared/control-admission.ts';

for (const mode of ['eof', 'backpressure']) test(`真实 OS 私有stdio管道 ${mode}：只清理直属夹具，正常退出与超时分列`, { timeout: 7000 }, async () => {
  const relay = await startRelayChild(fileURLToPath(new URL('./stdio-os-fixture.mjs', import.meta.url)), [mode], { ipc: true });
  const { child, input, output } = relay;
  let forced = false, outputBytes = 0, buffer = '';
  let initialized!: () => void;
  let calibrated!: (value: Record<string, unknown>) => void;
  let readFailed!: (error: unknown) => void;
  const ready = new Promise<void>(resolve => { initialized = resolve; });
  const calibration = new Promise<Record<string, unknown>>(resolve => { calibrated = resolve; });
  const readFailure = new Promise<never>((_resolve, reject) => { readFailed = reject; });
  void readFailure.catch(() => {});
  const messages: Record<string, unknown>[] = [];
  child.on('message', value => messages.push(value as Record<string, unknown>));
  const read = (chunk: Buffer) => {
    try {
      outputBytes += chunk.length; assert.ok(outputBytes <= 1024 * 1024);
      buffer += chunk.toString('utf8');
      for (let end = buffer.indexOf('\n'); end >= 0; end = buffer.indexOf('\n')) {
        const value = JSON.parse(buffer.slice(0, end)); buffer = buffer.slice(end + 1);
        assert.equal(value.error, undefined);
        if (value.id === 1) {
          assert.equal(value.result?.protocolVersion, '2025-11-25');
          if (mode === 'backpressure') { initialized(); continue; }
        } else {
          assert.equal(mode, 'backpressure'); assert.equal(value.id, 2);
          assert.ok(Array.isArray(value.result?.tools) && value.result.tools.length > 0);
        }
        // Requests are sent sequentially through calibration. No later response
        // has been requested yet, so do not discard an unexpected tail.
        assert.equal(buffer, ''); output.off('data', read); output.pause();
        if (value.id === 1) initialized(); else calibrated(value);
        return;
      }
    } catch (error) {
      output.off('data', read); readFailed(error);
    }
  };
  output.on('data', read);
  let deadline!: ReturnType<typeof setTimeout>;
  const actualExit = relay.exit.finally(() => clearTimeout(deadline));
  const exited = Promise.race([actualExit, new Promise<never>((_resolve, reject) => {
    deadline = setTimeout(() => { forced = true; child.kill(); reject(new Error(`Owned stdio child exceeded validation deadline: ${JSON.stringify(messages)}`)); }, 4000);
  })]);
  // Attach failure observation before waiting for handshake, so failed startup cannot leak an unhandled rejection.
  void exited.catch(() => {});
  try {
    input.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'os-offline', version: '1' } } }) + '\n');
    await Promise.race([ready, readFailure, exited.then(() => { throw new Error('Owned child exited before initialization'); })]);
    input.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
    if (mode === 'eof') input.end();
    else {
      input.write(JSON.stringify({ jsonrpc: '2.0', id: 2, method: 'tools/list', params: {} }) + '\n');
      const listed = await Promise.race([calibration, readFailure,
        exited.then(() => { throw new Error('Owned child exited before catalogue calibration'); })]);
      assert.equal(output.isPaused(), true);
      // Real catalogue size changes with capabilities. Stay inside ordinary
      // output credits so an aggregate-budget rejection cannot masquerade as
      // OS backpressure. Leave one slot/reservation for calibration's tail.
      const reservation = measureJson({ ...listed, id: Number.MAX_SAFE_INTEGER }, probeLimits) * 3;
      const reserved = (controlTransportProfile.maximumControlRequests * controlTransportProfile.maximumResponseBytes
        + controlTransportProfile.maximumSetupRequests * controlTransportProfile.maximumSetupResponseBytes) * 3;
      const batch = Math.min(probeLimits.activeRequests - 1, probeLimits.sendCount - 1,
        Math.floor((probeLimits.sendBytes - reserved) / reservation) - 1);
      assert.ok(batch >= 2, 'The actual catalogue must leave enough bounded output credits for a real blocked-writer test');
      input.write(Array.from({ length: batch }, (_, i) => JSON.stringify({ jsonrpc: '2.0', id: i + 3, method: 'tools/list', params: {} })).join('\n') + '\n');
    }
    assert.deepEqual(await exited, { code: 0, signal: null }); assert.equal(forced, false);
    const report = messages.find(value => value.kind === 'closed'); assert.ok(report);
    assert.equal(report.safetyExpired, false); assert.equal(report.gameClosed, 1); assert.equal(report.gameCalls, 0);
    assert.equal(report.inputDestroyed, true); assert.equal(report.outputDestroyed, true); assert.equal(report.closed, true);
    assert.equal(report.activeRequests, 0); assert.equal(report.sendBytes, 0); assert.equal(report.sendCount, 0);
    if (mode === 'backpressure') {
      const pending = messages.find(value => value.kind === 'backpressure');
      assert.ok(pending, 'The bounded fixture did not actually trigger OS pipe backpressure; it is not a pass');
      assert.ok(Number(pending.pendingBytes) > 0 && Number(pending.sendCount) > 0 && Number(pending.sendBytes) > 0);
      assert.equal(pending.needDrain, true); assert.equal(pending.highWaterMark, 1);
      assert.ok(Number(pending.stablePendingMilliseconds) >= 60 && Number(pending.pendingSamples) >= 2);
      assert.equal(report.backpressureObserved, true);
    }
  } finally {
    clearTimeout(deadline);
    await relay.cleanup();
  }
});
