import { spawn } from 'node:child_process';
import { randomBytes, randomUUID } from 'node:crypto';
import { mkdir, rmdir, unlink } from 'node:fs/promises';
import { resolve, dirname } from 'node:path';
import { createInterface } from 'node:readline';
import assert from 'node:assert/strict';

/** Directional peaks are independent measurements, never a simultaneous sum. */
function checkPayloadMetrics(value: Record<string, unknown>): void {
  assert.equal(value.payloadUsageSnapshotKind, 'directionalSamples');
  assert.equal(value.maximumOwnedPayloadCount, 8);
  assert.equal(value.maximumOwnedPayloadCapacity, 512 * 1024);
  for (const name of ['ownedPayloadCount', 'ownedPayloadCapacity', 'peakIngressPayloadCount', 'peakIngressPayloadCapacity',
    'peakEgressPayloadCount', 'peakEgressPayloadCapacity']) {
    assert.equal(Number.isSafeInteger(value[name]), true, name);
    assert.ok(Number(value[name]) >= 0, name);
  }
  assert.ok(Number(value.ownedPayloadCount) <= Number(value.maximumOwnedPayloadCount));
  assert.ok(Number(value.ownedPayloadCapacity) <= Number(value.maximumOwnedPayloadCapacity));
  for (const direction of ['Ingress', 'Egress']) {
    assert.ok(Number(value[`peak${direction}PayloadCount`]) <= Number(value.maximumOwnedPayloadCount) / 2);
    assert.ok(Number(value[`peak${direction}PayloadCapacity`]) <= Number(value.maximumOwnedPayloadCapacity) / 2);
  }
  assert.equal('peakOwnedPayloadCount' in value || 'peakOwnedPayloadCapacity' in value, false);
}

/** Isolated direct-spawn .NET fixture only; never resolves or launches a game/Steam process. */
export async function startRuntimeFixture(options: { operationProbe?: boolean } = {}) {
  const base = resolve('../../artifacts/host-fixtures', randomUUID());
  const directory = resolve(base, '.skylineslink/environments/probe/connection');
  await mkdir(dirname(directory), { recursive: true });
  const token = randomBytes(32).toString('hex'); const coreManifestSha256 = 'a'.repeat(64);
  const child = spawn(resolve('../../artifacts/dotnet/bin/SkylinesLink.RuntimeProbe/debug/SkylinesLink.RuntimeProbe.exe'), ['--serve'], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  child.stdin.on('error', () => { child.kill(); });
  let stderr = ''; child.stderr.on('data', (chunk: Buffer) => { stderr += chunk.toString('utf8'); if (stderr.length > 8192) child.kill(); });
  const lines = createInterface({ input: child.stdout }); const iterator = lines[Symbol.asyncIterator]();
  const exit = new Promise<number | null>((done) => { child.once('exit', done); child.once('error', () => done(null)); });
  const next = async (): Promise<Record<string, unknown>> => {
    let timer: NodeJS.Timeout | undefined;
    try {
      const line = await Promise.race([iterator.next(), new Promise<never>((_, reject) => { timer = setTimeout(() => { child.kill(); reject(new Error('Runtime fixture response timeout.')); }, 5000); })]);
      assert.equal(line.done, false, 'Runtime fixture exited before expected response');
      assert.equal(line.value!.includes(token), false, 'fixture stdout must not disclose credentials');
      const value = JSON.parse(line.value!) as Record<string, unknown>;
      if (value.kind === 'metrics') checkPayloadMetrics(value);
      return value;
    } finally { clearTimeout(timer); }
  };
  let stopped = false;
  const stop = async () => {
    if (stopped) return; stopped = true;
    try {
      if (child.exitCode === null && !child.killed) {
        child.stdin.write('stop\n'); const reply = await next();
        assert.equal(reply.kind, 'stopped'); assert.equal(reply.connected, 0);
        assert.equal(reply.ownedPayloadCount, 0); assert.equal(reply.ownedPayloadCapacity, 0);
      }
      const code = await exit;
      assert.equal(stderr.includes(token), false);
      const stages = stderr.split(/\r?\n/).filter(line => /^Runtime probe failed: [A-Za-z0-9_.-]+:[A-Za-z0-9_.-]+$|^Stage method: [A-Za-z0-9_.-]+$/.test(line));
      assert.equal(code, 0, stages.join('; ') || 'Runtime fixture failed without a public diagnostic.');
    } finally {
      child.stdin.end(); if (child.exitCode === null && !child.killed) child.kill(); await exit; lines.close();
      for (const file of ['authorization.json', 'discovery.json', 'authority.lock', 'operations.journal', 'operations.history']) { try { await unlink(resolve(directory, file)); } catch (error) { if (!(error instanceof Error && 'code' in error && error.code === 'ENOENT')) throw error; } }
      for (let current = directory; ; current = dirname(current)) { try { await rmdir(current); } catch (error) { if (!(error instanceof Error && 'code' in error && error.code === 'ENOENT')) throw error; } if (current === base) break; }
    }
  };
  try {
    child.stdin.write(JSON.stringify({ initialize: true, operationProbe: options.operationProbe === true, internalToken: token, externalHttpToken: null, profileId: 'probe', environmentId: 'probe', coreManifestSha256, privateDirectory: directory, deadlineMilliseconds: 3000 }) + '\n');
    const ready = await next(); assert.equal(ready.kind, 'ready');
    return { base, directory, token, coreManifestSha256, ready, stop, async command(command: 'metrics' | 'revoke' | 'load-generation' | { command: 'context' } | { command: 'submit' | 'submit-lost-ack'; writeContextId: string; requestId: string }) { child.stdin.write((typeof command === 'string' ? command : JSON.stringify(command)) + '\n'); return next(); } };
  } catch (error) { try { await stop(); } catch { /* preserve the initial setup failure */ } throw error; }
}
