import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { randomBytes, createHash } from 'node:crypto';
import { readFile, lstat } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { createInterface } from 'node:readline';

const root = resolve(import.meta.dirname, '../../..');
const launcher = process.env.SKYLINESLINK_TEST_LAUNCHER ?? join(root, 'artifacts/dotnet/bin/SkylinesLink.Launcher/debug/SkylinesLink.Launcher.exe');
const processProbe = join(root, 'artifacts/dotnet/bin/SkylinesLink.ProcessProbe/debug/SkylinesLink.ProcessProbe.exe');
const runtimeProbe = join(root, 'artifacts/dotnet/bin/SkylinesLink.RuntimeProbe/debug/SkylinesLink.RuntimeProbe.exe');
const sha = value => createHash('sha256').update(value).digest();

// Only directly owned children. A timeout/kill is cleanup, never a successful close.
function owned(executable, args, { input = 'pipe', output = 'pipe', maximum = 65536, secret = '', deadline = 15000 } = {}) {
  const child = spawn(executable, args, { windowsHide: true, stdio: [input, output, 'pipe'] });
  const chunks = []; let count = 0, errorCount = 0, failure, exited = false;
  const fail = error => { failure ??= error; if (!exited) child.kill(); };
  const streams = [child.stdin, child.stdout, child.stderr];
  const streamClose = streams.map(stream => new Promise(resolveClose => stream.once('close', resolveClose)));
  for (const stream of streams) stream.on('error', () => fail(new Error('Owned fixture pipe failed.')));
  child.on('error', () => fail(new Error('Owned fixture process failed.')));
  child.stdout.on('data', chunk => {
    count += chunk.length;
    if (count > maximum) fail(new Error('Owned fixture output exceeded its bound.'));
    else chunks.push(chunk);
  });
  child.stderr.on('data', chunk => { errorCount += chunk.length; if (errorCount > 4096) fail(new Error('Owned fixture diagnostics exceeded its bound.')); });
  const exit = new Promise(resolveExit => child.once('close', (code, signal) => resolveExit({ code, signal })));
  child.once('exit', () => { exited = true; if (!child.stdin.writableEnded) child.stdin.end(); });
  const timer = setTimeout(() => { fail(new Error('Owned fixture deadline exceeded.')); for (const stream of streams) stream.destroy(); }, deadline);
  const closed = (async () => {
    try {
      const [result] = await Promise.all([exit, ...streamClose]);
      if (failure) throw failure;
      assert.equal(result.signal, null); assert.equal(errorCount, 0);
      const bytes = Buffer.concat(chunks);
      if (secret && bytes.includes(secret)) throw new Error('Owned fixture exposed a synthetic credential.');
      return { ...result, bytes };
    } finally { clearTimeout(timer); }
  })();
  // Cleanup may observe a failure before the main flow awaits this same promise.
  void closed.catch(() => {});
  return { child, closed, abort() { fail(new Error('Owned fixture aborted after a test failure.')); } };
}

async function nativeRead(request) {
  const process = owned(launcher, ['--read-history'], { input: 'overlapped', output: 'overlapped', maximum: 32768, deadline: 8000 });
  process.child.stdin.end(JSON.stringify(request));
  const result = await process.closed;
  return { code: result.code, value: result.code === 0 ? JSON.parse(result.bytes.toString('utf8')) : null, bytes: result.bytes };
}

test('真实Runtime journal→publisher→SLHC→Native：正常关闭持久取消，同key/ID完整历史一致', { timeout: 20000 }, async () => {
  const environment = `probe-${randomBytes(12).toString('hex')}`;
  const fixture = JSON.parse(execFileSync(processProbe, ['history-root', environment], { windowsHide: true, encoding: 'utf8', timeout: 5000 }));
  const connection = join(fixture.directory, 'connection');
  const token = randomBytes(32).toString('hex');
  const runtime = owned(runtimeProbe, ['--serve'], { secret: token });
  const lines = createInterface({ input: runtime.child.stdout, crlfDelay: Infinity });
  const queue = []; let waiting, lineFailure, normalStop = false;
  lines.on('line', line => {
    if (line.includes(token) || queue.length >= 16) { lineFailure ??= new Error('Runtime control output failed its bound.'); runtime.abort(); return; }
    if (waiting) { const consume = waiting; waiting = undefined; consume(line); } else queue.push(line);
  });
  const next = async () => {
    let timer;
    try {
      const line = queue.length ? queue.shift() : await new Promise((resolveLine, reject) => {
        waiting = resolveLine;
        timer = setTimeout(() => { waiting = undefined; reject(new Error('Runtime control response deadline exceeded.')); }, 4000);
      });
      if (lineFailure) throw lineFailure;
      return JSON.parse(line);
    } finally { clearTimeout(timer); }
  };
  const command = async value => {
    const text = typeof value === 'string' ? value : JSON.stringify(value);
    await new Promise((resolveWrite, reject) => runtime.child.stdin.write(`${text}\n`, error => error ? reject(error) : resolveWrite()));
    return next();
  };
  try {
    await assert.rejects(lstat(connection), { code: 'ENOENT' }, 'root fixture must not pre-create authorization or connection');
    const ready = await command({ privateDirectory: connection, environmentId: environment, profileId: 'History.Reader',
      internalToken: token, externalHttpToken: null, coreManifestSha256: 'a'.repeat(64), initialize: true, operationProbe: true, deadlineMilliseconds: 3000 });
    assert.equal(ready.kind, 'ready');
    const context = await command({ command: 'context' }); assert.equal(context.kind, 'write-context');
    const key = { writeContextId: context.writeContextId, requestId: '历史真实出版/😀' };
    const accepted = await command({ command: 'submit', ...key }); assert.equal(accepted.kind, 'accepted');
    const duplicate = await command({ command: 'submit', ...key }); assert.equal(duplicate.operationId, accepted.operationId);
    const selector = { kind: 'submissionKey', submissionKey: key };
    const request = { formatVersion: 1, environmentId: environment, profileId: 'History.Reader',
      expectedAuthorityEpoch: ready.authorityEpoch, expectedGrantEpoch: ready.grantEpoch, selector };
    const busy = await nativeRead(request); assert.equal(busy.code, 82); assert.equal(busy.bytes.length, 0);
    runtime.child.stdin.end('stop\n');
    const stopped = await next(); assert.equal(stopped.kind, 'stopped');
    assert.equal(stopped.connected, 0); assert.equal(stopped.authenticated, 0);
    assert.equal(stopped.ownedPayloadCount, 0); assert.equal(stopped.ownedPayloadCapacity, 0);
    const ended = await runtime.closed; assert.equal(ended.code, 0); normalStop = true;
    assert.equal(queue.length, 0);

    const historyPath = join(connection, 'operations.history');
    const fileInfo = await lstat(historyPath); assert.ok(fileInfo.isFile() && fileInfo.size >= 45 && fileInfo.size <= 262144);
    const raw = await readFile(historyPath);
    assert.equal(raw.subarray(0, 4).toString('ascii'), 'SLHC'); assert.equal(raw.readUInt16LE(4), 1);
    assert.equal(raw.readUInt16LE(6), 0); assert.equal(raw.readUInt32LE(8) + 44, raw.length);
    assert.deepEqual(sha(raw.subarray(0, -32)), raw.subarray(-32));
    const checkpoint = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(raw.subarray(12, -32)));
    assert.equal(checkpoint.source.environmentId, environment); assert.equal(checkpoint.source.producerInstanceId, ready.instanceId);
    assert.equal(checkpoint.source.producerCoreManifestSha256, ready.coreManifestSha256);
    assert.equal(checkpoint.records.length, 1);
    const record = checkpoint.records[0];
    assert.equal(record.operationId, accepted.operationId); assert.deepEqual(record.submissionKey, key);
    assert.equal(record.originInstanceId, ready.instanceId); assert.equal(record.sourceContext.instanceId, ready.instanceId);
    assert.equal(record.state, 'cancelled'); assert.equal(record.mayHaveApplied, false); assert.equal(record.durability, 'durable');
    assert.equal(record.load.physicalTaskState, 'notStarted'); assert.equal(record.load.loadReturnValue, null);
    assert.equal(record.load.isObservationCurrent, false); assert.equal(record.load.historicalCompletionProven, false);
    assert.ok(record.terminalSequence <= checkpoint.source.throughSequence);
    const byKey = await nativeRead(request); assert.equal(byKey.code, 0);
    assert.deepEqual(byKey.value.source, checkpoint.source); assert.deepEqual(byKey.value.record, record);
    assert.equal(byKey.value.authorityEpoch, ready.authorityEpoch); assert.equal(byKey.value.grantEpoch, ready.grantEpoch);
    const byId = await nativeRead({ ...request, selector: { kind: 'operationId', operationId: accepted.operationId } });
    assert.equal(byId.code, 0); assert.deepEqual(byId.value, byKey.value);
    assert.equal(byKey.bytes.includes(token), false);
    assert.deepEqual(await readFile(historyPath), raw, 'Native reads must leave the actual publisher bytes unchanged');
  } finally {
    if (!normalStop) runtime.abort();
    try { await runtime.closed; } catch (error) { if (normalStop) throw error; }
    lines.close();
    execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-File', join(import.meta.dirname, 'remove-owned-probe.ps1'), '-ProbeRoot', fixture.directory], { windowsHide: true, stdio: 'pipe' });
  }
});
