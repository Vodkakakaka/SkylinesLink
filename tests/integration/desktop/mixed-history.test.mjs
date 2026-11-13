import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import { readFile, writeFile, readdir } from 'node:fs/promises';
import { homedir } from 'node:os';
import { dirname, join, resolve } from 'node:path';

const root = resolve(import.meta.dirname, '../../..');
const launcher = process.env.SKYLINESLINK_TEST_LAUNCHER ?? join(root, 'artifacts/dotnet/bin/SkylinesLink.Launcher/debug/SkylinesLink.Launcher.exe');
const runtime = join(root, 'artifacts/dotnet/bin/SkylinesLink.RuntimeProbe/debug/SkylinesLink.RuntimeProbe.exe');
const processProbe = join(root, 'artifacts/dotnet/bin/SkylinesLink.ProcessProbe/debug/SkylinesLink.ProcessProbe.exe');
const digest = bytes => createHash('sha256').update(bytes).digest();

// Directly owned bounded children only. A forced timeout is a failure, never a normal close.
async function owned(executable, args, input, { maximum = 32768, overlapped = false, secret = '' } = {}) {
  const pipe = overlapped ? 'overlapped' : 'pipe';
  const child = spawn(executable, args, { windowsHide: true, stdio: [pipe, pipe, 'pipe'] });
  let failure, exited = false, inputBytes = 0, outputBytes = 0, errorBytes = 0;
  const chunks = [], streams = [child.stdin, child.stdout, child.stderr];
  const role = executable === runtime ? 'runtime' : executable === launcher ? 'launcher' : executable === processProbe ? 'processProbe' : 'unknown';
  const mode = ['--mixed-history-probe', '--read-history', '--read-history-v2', '--read-history-v3', 'task-rules'].includes(args[0]) ? args[0] : 'unknown';
  const diagnostic = () => JSON.stringify({ role, mode, exitObserved: exited,
    stdinClosed: child.stdin.closed, stdinEnded: child.stdin.writableEnded, stdinFinished: child.stdin.writableFinished,
    stdoutClosed: child.stdout.closed, stdoutEnded: child.stdout.readableEnded,
    stderrClosed: child.stderr.closed, stderrEnded: child.stderr.readableEnded,
    inputBytes, stdoutBytes: outputBytes, stderrBytes: errorBytes });
  const fail = message => {
    // Freeze only bounded state before our kill/destroy changes the evidence.
    failure ??= new Error(`${message} ${diagnostic()}`);
    if (!exited) child.kill();
    for (const stream of streams) stream.destroy();
  };
  const closed = streams.map(stream => new Promise(done => stream.once('close', done)));
  for (const stream of streams) stream.on('error', () => fail('Mixed history owned pipe failed.'));
  child.on('error', () => fail('Mixed history owned process failed.'));
  child.stdout.on('data', chunk => {
    outputBytes += chunk.length;
    if (outputBytes > maximum) fail('Mixed history output exceeded its bound.'); else chunks.push(chunk);
  });
  child.stderr.on('data', chunk => { errorBytes += chunk.length; if (errorBytes > 256) fail('Mixed history diagnostics exceeded their bound.'); });
  const exit = new Promise(done => child.once('close', (code, signal) => done({ code, signal })));
  child.once('exit', () => { exited = true; if (!child.stdin.writableEnded) child.stdin.end(); });
  const timer = setTimeout(() => fail('Mixed history owned deadline exceeded.'), 5000);
  const payload = typeof input === 'string' ? input : JSON.stringify(input);
  inputBytes = typeof payload === 'string' ? Buffer.byteLength(payload) : 0;
  child.stdin.end(payload);
  try {
    const [result] = await Promise.all([exit, ...closed]);
    if (failure) throw failure;
    assert.equal(result.signal, null); assert.equal(errorBytes, 0, 'reader must not expose diagnostics');
    const bytes = Buffer.concat(chunks);
    assert.ok(!secret || !bytes.includes(secret), 'owned output must not expose the synthetic credential');
    return { code: result.code, bytes, value: result.code === 0 ? JSON.parse(bytes.toString('utf8')) : null };
  } finally { clearTimeout(timer); }
}
function frame(document) {
  const body = Buffer.from(JSON.stringify(document));
  const head = Buffer.alloc(12); head.write('SLHC'); head.writeUInt16LE(2, 4); head.writeUInt32LE(body.length, 8);
  const prefix = Buffer.concat([head, body]); return Buffer.concat([prefix, digest(prefix)]);
}
async function fingerprint(directory) {
  const connection = join(directory, 'connection');
  const paths = ['activation.json', 'connection/authorization.json', 'connection/operations.history', 'connection/operations.journal'];
  const values = {};
  for (const path of paths) {
    const bytes = await readFile(join(directory, path));
    values[path] = { bytes: bytes.length, sha256: digest(bytes).toString('hex') };
  }
  values.connectionFiles = (await readdir(connection)).sort();
  return values;
}
function cleanup(directory) {
  execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-File', join(import.meta.dirname, 'remove-owned-probe.ps1'), '-ProbeRoot', directory],
    { windowsHide: true, stdio: 'pipe', timeout: 5000 });
}

test('CP2真实私有writer→publisher→Native2：完整Task/false、有限页、授权和坏尾全验证', { timeout: 60000 }, async () => {
  const environment = `probe-${randomBytes(12).toString('hex')}`;
  const directory = join(homedir(), '.skylineslink/environments', environment);
  const token = randomBytes(32).toString('hex');
  try {
    const fixture = await owned(runtime, ['--mixed-history-probe'], { environmentId: environment, profileId: 'Mixed.History.Reader', internalToken: token },
      { maximum: 4096, secret: token });
    assert.equal(fixture.code, 0);
    const ready = fixture.value;
    assert.equal(ready.kind, 'mixed-history-ready'); assert.equal(ready.environmentId, environment);
    assert.equal(ready.fixture, 'synthetic-known-facts-no-game');
    assert.equal(await readFile(join(directory, '.owned-probe'), 'utf8'), environment);
    const connection = join(directory, 'connection'), historyPath = join(connection, 'operations.history'), authPath = join(connection, 'authorization.json');
    const historyBytes = await readFile(historyPath), authBytes = await readFile(authPath);
    assert.equal(historyBytes.subarray(0, 4).toString('ascii'), 'SLHC'); assert.equal(historyBytes.readUInt16LE(4), 2);
    assert.deepEqual(digest(historyBytes.subarray(0, -32)), historyBytes.subarray(-32));
    const checkpoint = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(historyBytes.subarray(12, -32)));
    assert.deepEqual(checkpoint.source, ready.source);
    assert.deepEqual(checkpoint.taskRecords.map(row => row.sequence), [2, 7, 8]);
    assert.equal(checkpoint.records[0].terminalSequence, 6);
    const baseline = await fingerprint(directory);
    const request = query => ({ formatVersion: 2, environmentId: environment, profileId: ready.profileId,
      expectedAuthorityEpoch: ready.authorityEpoch, expectedGrantEpoch: ready.grantEpoch,
      expectedProfileInstanceId: ready.profileInstanceId, query });
    const read = async (query, extra = {}) => {
      const result = await owned(launcher, ['--read-history-v2'], { ...request(query), ...extra }, { overlapped: true, secret: token });
      if (result.code === 0) {
        assert.equal(result.value.formatVersion, 2); assert.deepEqual(result.value.source, ready.source);
        assert.deepEqual(result.value.query, query); assert.equal(result.value.profileInstanceId, ready.profileInstanceId);
      } else assert.equal(result.bytes.length, 0, 'rejected selection must emit no partial DTO');
      return result;
    };
    const found = async query => { const result = await read(query); assert.equal(result.code, 0); return result.value.result; };
    const goalQuery = (revision, section, offset = 0, limit = 512) => ({ kind: 'taskGoal', taskId: ready.taskId, goalRevision: revision, section, offset, limit });
    const byId = await found({ kind: 'operation', selector: { kind: 'operationId', operationId: ready.operationId } });
    const byKey = await found({ kind: 'operation', selector: { kind: 'submissionKey', submissionKey: ready.operationKey } });
    assert.deepEqual(byKey, byId); assert.equal(byId.kind, 'operation');
    assert.equal(byId.record.state, 'failed'); assert.equal(byId.record.load.loadReturnValue, false);
    assert.equal(byId.record.mayHaveApplied, true); assert.equal(byId.record.load.isObservationCurrent, false);
    assert.equal(byId.record.sourceContext.gameState, 'ready'); assert.equal(byId.record.sourceContext.cityId, null);
    assert.equal(byId.record.load.observationContext.gameState, 'ready'); assert.equal(byId.record.load.observationContext.cityId, null);
    assert.notEqual(byId.record.originInstanceId, ready.source.producerInstanceId);
    const summary = await found({ kind: 'task', taskId: ready.taskId });
    assert.equal(summary.kind, 'task'); assert.equal(summary.summary.title, 'Fixture goal 2');
    assert.equal(summary.summary.goalRevision, 2); assert.equal(summary.summary.controlVersion, 2); assert.equal(summary.summary.isStopped, true);
    assert.equal(summary.summary.owner.profileInstanceId, ready.profileInstanceId);
    assert.equal(summary.summary.createdSequence, 2); assert.equal(summary.summary.lastDecisionSequence, 8);
    const created = await found({ kind: 'taskDecision', submissionKey: ready.createKey });
    const stopped = await found({ kind: 'taskDecision', submissionKey: ready.stopKey });
    assert.equal(created.receipt.decisionKind, 'create'); assert.equal(created.receipt.goalRevision, 1); assert.equal(created.receipt.controlVersion, 1);
    assert.equal(created.receipt.sequence, 2); assert.equal(stopped.receipt.decisionKind, 'stop');
    assert.equal(stopped.receipt.goalRevision, null); assert.equal(stopped.receipt.controlVersion, 2); assert.equal(stopped.receipt.sequence, 8);
    assert.equal(stopped.receipt.decision.source.gameState, 'ready'); assert.equal(stopped.receipt.decision.source.cityId, null);
    let text = '';
    for (const offset of [0, 512, 1024]) {
      const result = await found(goalQuery(1, 'requestText', offset));
      assert.equal(result.kind, 'taskGoal'); assert.equal(result.page.kind, 'text');
      assert.equal(result.page.text.isWellFormed(), true);
      assert.equal([...result.page.text].length, offset === 1024 ? 76 : 512);
      assert.equal(result.page.nextOffset, offset === 1024 ? null : offset + 512);
      text += result.page.text;
    }
    assert.equal(text, '😀'.repeat(1100), 'original revision remains available after later goal and Stop');
    const revised = await found(goalQuery(2, 'requestText'));
    assert.equal(revised.page.text, [...`第二版:${'🚆'.repeat(1096)}`].slice(0, 512).join(''));
    assert.equal(revised.page.text.isWellFormed(), true);
    const empty = await found(goalQuery(1, 'requestText', 1100));
    assert.equal(empty.page.text, ''); assert.equal(empty.page.nextOffset, null);
    assert.equal((await read(goalQuery(1, 'requestText', 1101))).code, 80);
    const list = await found(goalQuery(1, 'constraintsText', 0, 4));
    assert.deepEqual(list.page.items, Array.from({ length: 4 }, (_, i) => `${i}:${'😀'.repeat(254)}`));
    assert.equal(list.page.nextOffset, 4); assert.ok(list.page.items.every(item => [...item].length === 256 && item.isWellFormed()));
    const listEnd = await found(goalQuery(1, 'constraintsText', 4, 4));
    assert.equal(listEnd.page.items.length, 2); assert.equal(listEnd.page.nextOffset, null);
    assert.equal((await read(goalQuery(1, 'constraintsText', 0, 5))).code, 80);
    for (const [query, expected] of [
      [{ kind: 'operation', selector: { kind: 'operationId', operationId: ready.operationId } }, byId],
      [{ kind: 'operation', selector: { kind: 'submissionKey', submissionKey: ready.operationKey } }, byKey],
      [{ kind: 'task', taskId: ready.taskId }, summary],
      [{ kind: 'taskDecision', submissionKey: ready.stopKey }, stopped],
      [goalQuery(1, 'constraintsText', 4, 4), listEnd],
    ]) {
      const result = await owned(launcher, ['--read-history-v3'], { ...request(query), formatVersion: 3 },
        { maximum: 49152, overlapped: true, secret: token });
      assert.equal(result.code, 0); assert.equal(result.value.formatVersion, 3);
      assert.deepEqual(result.value.source, ready.source); assert.deepEqual(result.value.query, query);
      assert.deepEqual(result.value.result, expected, 'Native3 old-query adapters preserve the complete Native2 value');
    }
    for (const query of [{ kind: 'task', taskId: 'not-covered' }, goalQuery(3, 'title'),
      { kind: 'taskDecision', submissionKey: { writeContextId: 'not-covered', requestId: 'one' } },
      { kind: 'operation', selector: { kind: 'operationId', operationId: 'not-covered' } }]) {
      assert.deepEqual(await found(query), { kind: 'notCovered' });
    }
    const old = await owned(launcher, ['--read-history'], { formatVersion: 1, environmentId: environment, profileId: ready.profileId,
      expectedAuthorityEpoch: ready.authorityEpoch, expectedGrantEpoch: ready.grantEpoch,
      selector: { kind: 'operationId', operationId: ready.operationId } }, { overlapped: true, secret: token });
    assert.equal(old.code, 86); assert.equal(old.bytes.length, 0, 'old format cannot silently downgrade complete CP2');
    const summaryQuery = { kind: 'task', taskId: ready.taskId };
    const wrongIdentity = (ready.profileInstanceId[0] === 'a' ? 'b' : 'a') + ready.profileInstanceId.slice(1);
    assert.equal((await read(summaryQuery, { expectedProfileInstanceId: wrongIdentity })).code, 84);
    assert.equal((await read(summaryQuery, { expectedGrantEpoch: ready.grantEpoch + 1 })).code, 84);
    assert.deepEqual(await fingerprint(directory), baseline, 'all successful and rejected reads preserve authorization, activation, journal and checkpoint');
    for (const mutation of ['late-hash', 'late-cas']) {
      const bad = structuredClone(checkpoint), last = bad.taskRecords.at(-1).entry;
      if (mutation === 'late-hash') last.decision.commandHash = '0'.repeat(64);
      else last.controlVersion = 3; // Original hash/expectedControl remain valid; only the persisted CAS result is wrong.
      const corrupted = frame(bad); await writeFile(historyPath, corrupted);
      const before = await fingerprint(directory);
      assert.equal((await read(goalQuery(1, 'requestText', 0, 1))).code, 87, mutation);
      assert.deepEqual(await fingerprint(directory), before, 'a matching early one-scalar page never bypasses complete-file validation or repairs it');
    }
    await writeFile(historyPath, historyBytes);
    // A newly authorized reader may inspect old facts without becoming their
    // original owner. Reusing the profile name does not rewrite provenance.
    const replacement = JSON.parse(authBytes.toString('utf8'));
    replacement.recordRevision++;
    replacement.profiles[0].profileInstanceId = wrongIdentity;
    replacement.profiles[0].grantEpoch++;
    replacement.profiles[0].scopeMask = 1;
    await writeFile(authPath, JSON.stringify(replacement));
    const replacementBefore = await fingerprint(directory);
    assert.equal((await read(summaryQuery)).code, 84, 'the old incarnation remains invalid');
    const replacementRead = await owned(launcher, ['--read-history-v2'], {
      ...request(summaryQuery), expectedProfileInstanceId: wrongIdentity,
      expectedGrantEpoch: replacement.profiles[0].grantEpoch,
    }, { overlapped: true, secret: token });
    assert.equal(replacementRead.code, 0);
    assert.equal(replacementRead.value.profileInstanceId, wrongIdentity);
    assert.equal(replacementRead.value.grantEpoch, replacement.profiles[0].grantEpoch);
    assert.deepEqual(replacementRead.value.result.summary.owner, summary.summary.owner,
      'Read scope preserves the old owner and original decision epochs');
    assert.deepEqual(replacementRead.value.source, ready.source);
    assert.deepEqual(await fingerprint(directory), replacementBefore);
    await writeFile(authPath, authBytes);
    const revoked = JSON.parse(authBytes.toString('utf8'));
    revoked.recordRevision++; revoked.profiles[0].scopeMask = 0;
    await writeFile(authPath, JSON.stringify(revoked));
    const deniedBefore = await fingerprint(directory);
    assert.equal((await read(summaryQuery)).code, 83);
    assert.deepEqual(await fingerprint(directory), deniedBefore, 'unauthorized read does not repair or replace its private source');
    await writeFile(authPath, authBytes);
    assert.deepEqual(await fingerprint(directory), baseline, 'fixture-only mutations are restored before exact owned cleanup');
  } finally { cleanup(directory); }
});

test('Native2实际读取真实publisher出版的超1MiB完整CP2，仍只返回有限历史页', { timeout: 30000 }, async () => {
  const environment = `probe-${randomBytes(12).toString('hex')}`;
  const directory = join(homedir(), '.skylineslink/environments', environment);
  const token = randomBytes(32).toString('hex');
  try {
    const fixture = await owned(runtime, ['--mixed-history-probe'], { environmentId: environment, profileId: 'Large.History.Reader', internalToken: token, large: true },
      { maximum: 4096, secret: token });
    assert.equal(fixture.code, 0);
    const ready = fixture.value;
    assert.equal(ready.kind, 'mixed-history-ready'); assert.equal(ready.large, true);
    const checkpoint = await readFile(join(directory, 'connection/operations.history'));
    assert.ok(checkpoint.length > 1048576 && checkpoint.length <= 3 * ready.source.throughBytes + 262144 + 4096);
    assert.equal(checkpoint.readUInt16LE(4), 2); assert.deepEqual(digest(checkpoint.subarray(0, -32)), checkpoint.subarray(-32));
    assert.equal(ready.source.throughSequence, 23); assert.ok(ready.source.throughBytes > 1048576 && ready.source.throughBytes < 2097152);
    // This fixture is a fresh gen0 bank0 with no rotation. Count actual framed
    // bytes directly; do not re-encode its JSON or charge the fixed zero tail.
    const journal = await readFile(join(directory, 'connection/operations.journal'));
    const bodyStart = 4096 + 3 * 4096; let position = bodyStart;
    for (let sequence = 1; sequence <= ready.source.throughSequence; sequence++) {
      assert.equal(journal.subarray(position, position + 4).toString('ascii'), 'SLJO');
      const payloadLength = journal.readUInt32LE(position + 8), end = position + payloadLength + 44;
      assert.deepEqual(digest(journal.subarray(position, end - 32)), journal.subarray(end - 32, end));
      const row = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(journal.subarray(position + 12, end - 32)));
      assert.equal(row.sequence, sequence); position = end;
    }
    assert.equal(position - bodyStart, ready.source.throughBytes, 'source B is exactly the original confirmed frame bytes, below fixed container EOF');
    assert.ok(position < journal.length);
    const before = await fingerprint(directory);
    const read = async query => {
      const result = await owned(launcher, ['--read-history-v2'], { formatVersion: 2, environmentId: environment, profileId: ready.profileId,
        expectedAuthorityEpoch: ready.authorityEpoch, expectedGrantEpoch: ready.grantEpoch,
        expectedProfileInstanceId: ready.profileInstanceId, query }, { overlapped: true, secret: token });
      assert.equal(result.code, 0); assert.deepEqual(result.value.source, ready.source); assert.deepEqual(result.value.query, query);
      assert.ok(result.bytes.length <= 32768); return result.value.result;
    };
    const summary = await read({ kind: 'task', taskId: ready.taskId });
    assert.equal(summary.summary.goalRevision, 17); assert.equal(summary.summary.controlVersion, 2); assert.equal(summary.summary.isStopped, true);
    assert.equal(summary.summary.title, '\u0085'.repeat(128));
    const goal = await read({ kind: 'taskGoal', taskId: ready.taskId, goalRevision: 1, section: 'requestText', offset: 0, limit: 512 });
    assert.equal(goal.page.text, '\u0085'.repeat(512)); assert.equal(goal.page.nextOffset, 512); assert.equal(goal.page.sequence, 2);
    const items = await read({ kind: 'taskGoal', taskId: ready.taskId, goalRevision: 1, section: 'constraintsText', offset: 0, limit: 4 });
    assert.deepEqual(items.page.items, Array(4).fill('\u0085'.repeat(256))); assert.equal(items.page.nextOffset, 4);
    const operation = await read({ kind: 'operation', selector: { kind: 'operationId', operationId: ready.operationId } });
    assert.equal(operation.record.load.loadReturnValue, false); assert.equal(operation.record.state, 'failed');
    const legacy = await owned(launcher, ['--read-history'], { formatVersion: 1, environmentId: environment, profileId: ready.profileId,
      expectedAuthorityEpoch: ready.authorityEpoch, expectedGrantEpoch: ready.grantEpoch,
      selector: { kind: 'operationId', operationId: ready.operationId } }, { overlapped: true, secret: token });
    assert.equal(legacy.code, 86); assert.equal(legacy.bytes.length, 0, 'legacy CLI refuses the CP2 header instead of silently interpreting a large CP1');
    assert.deepEqual(await fingerprint(directory), before, 'large complete-file reads leave every original private source and activation unchanged');
  } finally { cleanup(directory); }
});

test('CP2消费者沿用实际Deployment BCL依赖闭包，不Compile旁路规则副本', { timeout: 15000 }, async () => {
  const result = await owned(processProbe, ['task-rules', join(root, 'contracts/fixtures/task-rules-vectors.json')], '', { maximum: 8192 });
  assert.equal(result.code, 0); assert.equal(result.value.ruleAssembly, 'SkylinesLink.Deployment'); assert.equal(result.value.managed, true);
  const forbidden = name => /^(?:SkylinesLink\.(?:Application|Contracts|Infrastructure)|Newtonsoft\.Json)$/.test(name);
  assert.ok(!result.value.references.some(forbidden)); assert.ok(!result.value.consumerReferences.some(forbidden));
  const deps = JSON.parse(await readFile(join(dirname(launcher), 'SkylinesLink.Launcher.deps.json'), 'utf8'));
  assert.ok(!Object.keys(deps.libraries).some(name => forbidden(name.split('/')[0])), 'actual managed Launcher dependency manifest stays BCL/Deployment only');
});

test('Native3真实publisher完整0/8任务发现、原Stop身份与独立48KiB有限输出', { timeout: 60000 }, async () => {
  for (const taskCount of [0, 8]) {
    const environment = `probe-${randomBytes(12).toString('hex')}`;
    const directory = join(homedir(), '.skylineslink/environments', environment);
    const token = randomBytes(32).toString('hex');
    try {
      const fixture = await owned(runtime, ['--mixed-history-probe'], { environmentId: environment,
        profileId: 'Discovery.Reader.'.padEnd(64, 'a'), internalToken: token, taskCount }, { maximum: 4096, secret: token });
      assert.equal(fixture.code, 0);
      const ready = fixture.value;
      assert.equal(ready.taskCount, taskCount); assert.equal(ready.fixture, 'synthetic-known-facts-no-game');
      const connection = join(directory, 'connection');
      const historyPath = join(connection, 'operations.history'), authPath = join(connection, 'authorization.json');
      const historyBytes = await readFile(historyPath), authBytes = await readFile(authPath);
      const checkpoint = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(historyBytes.subarray(12, -32)));
      const baseline = await fingerprint(directory);
      const request = query => ({ formatVersion: 3, environmentId: environment, profileId: ready.profileId,
        expectedAuthorityEpoch: ready.authorityEpoch, expectedGrantEpoch: ready.grantEpoch,
        expectedProfileInstanceId: ready.profileInstanceId, query });
      const read = async (query, extra = {}, version = 3) => {
        const result = await owned(launcher, [version === 2 ? '--read-history-v2' : '--read-history-v3'],
          { ...request(query), formatVersion: version, ...extra }, { maximum: version === 3 ? 49152 : 32768, overlapped: true, secret: token });
        if (result.code === 0) {
          assert.equal(result.value.formatVersion, version); assert.deepEqual(result.value.query, query);
          assert.deepEqual(result.value.source, ready.source);
          assert.equal(result.value.environmentId, environment); assert.equal(result.value.profileId, ready.profileId);
          assert.equal(result.value.profileInstanceId, ready.profileInstanceId);
          assert.equal(result.value.authorityEpoch, ready.authorityEpoch); assert.equal(result.value.grantEpoch, ready.grantEpoch);
        } else assert.equal(result.bytes.length, 0, 'rejected complete-file selection emits no partial response');
        return result;
      };
      const found = async (query, version = 3) => {
        const result = await read(query, {}, version); assert.equal(result.code, 0); return result.value.result;
      };
      const listed = await read({ kind: 'tasks' }); assert.equal(listed.code, 0);
      assert.equal(listed.value.result.kind, 'tasks');
      const summaries = listed.value.result.tasks;
      const created = checkpoint.taskRecords.filter(row => row.entry.kind === 'taskCreated');
      const ids = created.map(row => row.entry.taskId).sort();
      assert.equal(summaries.length, taskCount); assert.deepEqual(summaries.map(value => value.taskId), ids);
      assert.deepEqual(Object.keys(listed.value.result).sort(), ['kind', 'tasks']);
      assert.equal((await read({ kind: 'tasks' }, {}, 2)).code, 80, 'immutable Native2 never accepts task discovery');
      assert.equal((await read({ kind: 'tasks', hidden: true })).code, 80);
      assert.equal((await read({ kind: 'tasks' }, { expectedGrantEpoch: null })).code, 80);
      assert.equal((await read({ kind: 'tasks' }, { expectedGrantEpoch: ready.grantEpoch + 1 })).code, 84);
      assert.equal((await read({ kind: 'tasks' }, { expectedProfileInstanceId: '0'.repeat(32) })).code, 84);
      assert.equal((await read({ kind: 'tasks' }, { environmentId: environment.toUpperCase() })).code, 80);
      assert.equal((await read({ kind: 'tasks' }, { profileId: 'Other.Reader' })).code, 83);
      assert.deepEqual(await found({ kind: 'taskStopDecision', taskId: 'not-retained', expectedControlVersion: 1 }), { kind: 'notCovered' });
      assert.equal((await read({ kind: 'taskStopDecision', taskId: 'not-retained', expectedControlVersion: 0 })).code, 80);
      if (taskCount === 0) assert.deepEqual(summaries, []);
      else {
        assert.ok(listed.bytes.length > 32768, 'eight complete maximum-scalar summaries exceed the old native output ceiling');
        assert.ok(listed.bytes.length <= 49152, 'the actual generated writer fits the independent Native3 ceiling');
        assert.equal(ready.profileId.length, 64);
        assert.equal([...ready.source.producerVersion].length, 64); assert.equal([...ready.source.producerInstanceId].length, 128);
        assert.ok(summaries.every(value => [...value.taskId].length === 128 && [...value.cityId].length === 128 && [...value.title].length === 128));
        assert.equal(summaries.filter(value => value.isStopped).length, 4);
        assert.equal(summaries.filter(value => value.goalRevision === 2).length, 4);
        for (const summary of summaries) {
          const query = { kind: 'task', taskId: summary.taskId };
          assert.deepEqual(await found(query), { kind: 'task', summary });
          assert.deepEqual(await found(query, 2), { kind: 'task', summary }, 'known-ID Native2 and Native3 preserve the same original summary');
          const selected = await found({ kind: 'taskStopDecision', taskId: summary.taskId, expectedControlVersion: 1 });
          const row = checkpoint.taskRecords.find(row => row.entry.kind === 'taskStopped' && row.entry.taskId === summary.taskId);
          if (!row) assert.deepEqual(selected, { kind: 'notCovered' }, 'confirmed unstopped history cannot prove current Stop absence');
          else {
            assert.equal(selected.kind, 'taskDecision');
            assert.deepEqual(selected.receipt, { taskId: row.entry.taskId, decisionKind: 'stop', goalRevision: null,
              controlVersion: row.entry.controlVersion, sequence: row.sequence, decision: row.entry.decision });
            assert.match(selected.receipt.decision.submissionKey.requestId, /^historical-stop-/);
            assert.equal(selected.receipt.decision.source.gameState, 'menu');
            assert.deepEqual(await found({ kind: 'taskDecision', submissionKey: row.entry.decision.submissionKey }, 2), selected);
          }
          assert.deepEqual(await found({ kind: 'taskStopDecision', taskId: summary.taskId, expectedControlVersion: 2 }), { kind: 'notCovered' });
        }
        // A create hash covers source/goal, not the assigned task identity. This
        // legal fixture-only identity change distinguishes Ordinal from scalar
        // or culture sorting without changing the writer's maximum-byte sample.
        const ordinal = structuredClone(checkpoint);
        const unstopped = ordinal.taskRecords.find(row => row.entry.kind === 'taskCreated' &&
          !ordinal.taskRecords.some(other => other.entry.taskId === row.entry.taskId && other.entry.kind !== 'taskCreated'));
        const oldId = unstopped.entry.taskId; unstopped.entry.taskId = '\ue000'.repeat(128);
        await writeFile(historyPath, frame(ordinal));
        const ordered = await found({ kind: 'tasks' });
        assert.deepEqual(ordered.tasks.map(value => value.taskId), ids.filter(id => id !== oldId).concat(unstopped.entry.taskId).sort());
        assert.equal(ordered.tasks.at(-1).taskId, unstopped.entry.taskId, 'Ordinal compares UTF-16 code units');
        await writeFile(historyPath, historyBytes);
        const earlyStop = checkpoint.taskRecords.find(row => row.entry.kind === 'taskStopped').entry;
        for (const mutation of ['late-hash', 'late-cas']) {
          const bad = structuredClone(checkpoint), last = bad.taskRecords.at(-1).entry;
          if (mutation === 'late-hash') last.decision.commandHash = '0'.repeat(64); else last.controlVersion++;
          await writeFile(historyPath, frame(bad));
          const damaged = await fingerprint(directory);
          assert.equal((await read({ kind: 'tasks' })).code, 87);
          assert.equal((await read({ kind: 'taskStopDecision', taskId: earlyStop.taskId, expectedControlVersion: 1 })).code, 87);
          assert.deepEqual(await fingerprint(directory), damaged, 'neither selection repairs a bad tail or emits an early match');
        }
        await writeFile(historyPath, historyBytes);
      }
      assert.deepEqual(await fingerprint(directory), baseline, 'all successful and rejected reads preserve original private files');
      const revoked = JSON.parse(authBytes.toString('utf8'));
      revoked.recordRevision++; revoked.profiles[0].scopeMask = 0;
      await writeFile(authPath, JSON.stringify(revoked));
      const denied = await fingerprint(directory);
      assert.equal((await read({ kind: 'tasks' })).code, 83);
      assert.equal((await read({ kind: 'taskStopDecision', taskId: ids[0] ?? 'not-retained', expectedControlVersion: 1 })).code, 83);
      assert.deepEqual(await fingerprint(directory), denied);
      await writeFile(authPath, authBytes);
      const replacement = JSON.parse(authBytes.toString('utf8'));
      replacement.recordRevision++; replacement.profiles[0].grantEpoch++;
      const newInstance = (ready.profileInstanceId[0] === 'a' ? 'b' : 'a') + ready.profileInstanceId.slice(1);
      replacement.profiles[0].profileInstanceId = newInstance;
      await writeFile(authPath, JSON.stringify(replacement));
      assert.equal((await read({ kind: 'tasks' })).code, 84, 'same profile name cannot erase the fixed reader incarnation');
      const newRead = await owned(launcher, ['--read-history-v3'], { ...request({ kind: 'tasks' }),
        expectedGrantEpoch: replacement.profiles[0].grantEpoch, expectedProfileInstanceId: newInstance },
        { maximum: 49152, overlapped: true, secret: token });
      assert.equal(newRead.code, 0); assert.equal(newRead.value.profileInstanceId, newInstance);
      assert.equal(newRead.value.grantEpoch, replacement.profiles[0].grantEpoch);
      assert.deepEqual(newRead.value.result, listed.value.result, 'a new Read subject preserves historical owners and does not acquire their Control');
      await writeFile(authPath, authBytes);
      assert.deepEqual(await fingerprint(directory), baseline);
    } finally { cleanup(directory); }
  }
});
