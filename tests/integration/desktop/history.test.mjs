import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { once } from 'node:events';
import { randomBytes, createHash } from 'node:crypto';
import { readFile, writeFile, rename, symlink, lstat } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const root = resolve(import.meta.dirname, '../../..');
const launcher = process.env.SKYLINESLINK_TEST_LAUNCHER ?? join(root, 'artifacts/dotnet/bin/SkylinesLink.Launcher/debug/SkylinesLink.Launcher.exe');
const probe = join(root, 'artifacts/dotnet/bin/SkylinesLink.ProcessProbe/debug/SkylinesLink.ProcessProbe.exe');
const hash = bytes => createHash('sha256').update(bytes).digest();
const selector = { kind: 'submissionKey', submissionKey: { writeContextId: '窗口/😀', requestId: '原请求/中文' } };
const clone = value => structuredClone(value);

async function fixture() {
  const environment = `probe-${randomBytes(12).toString('hex')}`;
  const f = JSON.parse(execFileSync(probe, ['history-fixture', environment], { windowsHide: true, encoding: 'utf8', timeout: 5000 }));
  f.connection = join(f.directory, 'connection');
  f.authorization = JSON.parse(await readFile(join(f.connection, 'authorization.json'), 'utf8'));
  const shared = JSON.parse(await readFile(join(root, 'contracts/fixtures/history-cases.json'), 'utf8'));
  f.checkpoint = JSON.parse(shared.find(value => value.name === '历史源记录上下文完整').json);
  Object.assign(f.checkpoint.source, { environmentId: environment, producerVersion: '0.0.9-old', producerInstanceId: 'different-producer', producerCoreManifestSha256: 'e'.repeat(64) });
  f.checkpoint.records[0].submissionKey = clone(selector.submissionKey);
  await publish(f);
  return f;
}
function frame(document) {
  const body = Buffer.isBuffer(document) ? document : Buffer.from(JSON.stringify(document));
  const head = Buffer.alloc(12); head.write('SLHC'); head.writeUInt16LE(1, 4); head.writeUInt32LE(body.length, 8);
  const payload = Buffer.concat([head, body]);
  return Buffer.concat([payload, hash(payload)]);
}
async function publish(f, document = f.checkpoint) { await writeFile(join(f.connection, 'operations.history'), frame(document)); }
function request(f, extra = {}) { return { formatVersion: 1, environmentId: f.environment, profileId: 'Reader.With_Id', expectedAuthorityEpoch: null, expectedGrantEpoch: null, selector: clone(selector), ...extra }; }
function cleanup(f) { execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-File', join(import.meta.dirname, 'remove-owned-probe.ps1'), '-ProbeRoot', f.directory], { windowsHide: true, stdio: 'pipe' }); }
function acl(f, object, mode) { execFileSync(probe, ['history-acl', f.environment, object, mode], { windowsHide: true, stdio: 'pipe', timeout: 5000 }); }

function startNative(value, { pipeMode = 'overlapped', endInput = true, env } = {}) {
  const child = spawn(launcher, ['--read-history'], { windowsHide: true, stdio: [pipeMode, pipeMode, 'pipe'], ...(env ? { env } : {}) });
  const outputs = []; let bytes = 0, errors = 0, forced = false;
  const closed = ['stdin', 'stdout', 'stderr'].map(name => new Promise(resolveClose => child[name].once('close', resolveClose)));
  for (const stream of [child.stdin, child.stdout, child.stderr]) stream.on('error', () => {});
  child.stdout.on('data', chunk => {
    bytes += chunk.length;
    if (bytes > 32768) { forced = true; child.kill(); } else outputs.push(chunk);
  });
  child.stderr.on('data', chunk => { errors += chunk.length; if (errors > 256) { forced = true; child.kill(); } });
  const exit = once(child, 'close');
  child.once('exit', () => { if (!child.stdin.writableEnded) child.stdin.end(); });
  const timer = setTimeout(() => { forced = true; child.kill(); child.stdin.destroy(); child.stdout.destroy(); child.stderr.destroy(); }, 8000);
  const input = Buffer.isBuffer(value) ? value : Buffer.from(JSON.stringify(value));
  if (endInput) child.stdin.end(input); else child.stdin.write(input);
  const result = (async () => {
    try {
      const [[code, signal]] = await Promise.all([exit, ...closed]);
      assert.equal(forced, false, 'timeout/forced cleanup cannot pass');
      assert.equal(signal, null); assert.equal(errors, 0, 'Native reader must not disclose stderr');
      const output = Buffer.concat(outputs);
      assert.ok(output.length <= 32768);
      return { code, output, value: code === 0 ? JSON.parse(output.toString('utf8')) : null };
    } finally { clearTimeout(timer); }
  })();
  return { child, result };
}
async function read(value, options) { return startNative(value, options).result; }
async function rejected(f, expected, value = request(f)) {
  const result = await read(value); assert.equal(result.code, expected); assert.equal(result.output.length, 0);
}

test('Native完整SLHC只选一条：Unicode key/ID、旧生产者、未覆盖及授权主体', { timeout: 20000 }, async () => {
  const f = await fixture();
  try {
    const raw = await readFile(join(f.connection, 'operations.history'));
    const found = await read(request(f));
    assert.equal(found.code, 0);
    assert.deepEqual(found.value.record, f.checkpoint.records[0]);
    assert.deepEqual(found.value.source, f.checkpoint.source);
    assert.equal(found.value.authorityEpoch, 3); assert.equal(found.value.grantEpoch, 7);
    assert.equal(found.value.authorizationRecordRevision, 8);
    assert.equal(found.output.includes(f.authorization.profiles[0].internalToken), false);
    assert.equal(Object.hasOwn(found.value, 'records'), false);
    const byId = await read(request(f, { selector: { kind: 'operationId', operationId: f.checkpoint.records[0].operationId }, expectedAuthorityEpoch: 3, expectedGrantEpoch: 7 }));
    assert.deepEqual(byId.value.record, found.value.record);
    const missing = await read(request(f, { selector: { kind: 'operationId', operationId: 'missing' } }));
    assert.equal(missing.code, 0); assert.equal(missing.value.record, null); assert.deepEqual(missing.value.source, found.value.source);
    await rejected(f, 84, request(f, { expectedAuthorityEpoch: 2, expectedGrantEpoch: 7 }));
    await rejected(f, 83, request(f, { profileId: 'reader.with_id' }));
    await rejected(f, 80, request(f, { expectedAuthorityEpoch: 3 }));
    await rejected(f, 80, request(f, { environmentId: '../outside' }));
    const changedRevision = clone(f.authorization); changedRevision.recordRevision++;
    await writeFile(join(f.connection, 'authorization.json'), JSON.stringify(changedRevision));
    assert.equal((await read(request(f, { expectedAuthorityEpoch: 3, expectedGrantEpoch: 7 }))).code, 0, 'unrelated record revision is not a new subject');
    assert.deepEqual(await readFile(join(f.connection, 'operations.history')), raw, 'read never rewrites history');
  } finally { cleanup(f); }
});

test('Native全快照语义：命中首项也拒绝坏尾项，保留矛盾观察与真实false', { timeout: 20000 }, async () => {
  const f = await fixture();
  try {
    const different = clone(f.checkpoint); different.records[0].load.targetMatch = 'different';
    await publish(f, different); assert.equal((await read(request(f))).value.record.load.historicalCompletionProven, true);
    const negative = clone(f.checkpoint); negative.records[0].state = 'failed'; negative.records[0].load.loadReturnValue = false; negative.records[0].load.historicalCompletionProven = false;
    await publish(f, negative); assert.deepEqual((await read(request(f))).value.record, negative.records[0]);
    for (const mutation of ['duplicate-id', 'duplicate-key', 'duplicate-sequence', 'late-invalid', 'past-watermark', 'generation-leading-zero', 'current-history']) {
      const cp = clone(f.checkpoint); cp.source.throughSequence = 8;
      const other = clone(cp.records[0]); other.operationId = 'op-second'; other.terminalSequence = 8; other.submissionKey.requestId = 'second';
      cp.records.push(other);
      if (mutation === 'duplicate-id') other.operationId = cp.records[0].operationId;
      if (mutation === 'duplicate-key') other.submissionKey = clone(cp.records[0].submissionKey);
      if (mutation === 'duplicate-sequence') other.terminalSequence = cp.records[0].terminalSequence;
      if (mutation === 'late-invalid') other.load.loadReturnValue = false;
      if (mutation === 'past-watermark') other.terminalSequence = 9;
      if (mutation === 'generation-leading-zero') other.sourceContext.generation = '01';
      if (mutation === 'current-history') other.load.isObservationCurrent = true;
      await publish(f, cp); await rejected(f, 87);
    }
  } finally { cleanup(f); }
});

test('Native完整性/权限：损坏、旧格式、未激活授权、同token与真实私有ACL', { timeout: 20000 }, async () => {
  const f = await fixture();
  try {
    const good = frame(f.checkpoint);
    const badDigest = Buffer.from(good); badDigest[badDigest.indexOf('0.0.9-old')] = 0x31;
    const reserved = Buffer.from(good); reserved[6] = 1;
    const incompatible = Buffer.from(good); incompatible.writeUInt16LE(2, 4);
    const hugeLength = Buffer.from(good); hugeLength.writeUInt32LE(0xffffffff, 8);
    const badUtf8 = frame(Buffer.from([0xc3, 0x28]));
    for (const [bytes, code] of [[badDigest, 87], [reserved, 87], [incompatible, 86], [hugeLength, 87], [badUtf8, 87], [Buffer.concat([good, Buffer.from([0])]), 87], [Buffer.alloc(262145), 87]]) {
      await writeFile(join(f.connection, 'operations.history'), bytes); await rejected(f, code);
      assert.deepEqual(await readFile(join(f.connection, 'operations.history')), bytes);
    }
    const oldFormat = clone(f.checkpoint); oldFormat.source.formatVersion = 2;
    await publish(f, oldFormat); await rejected(f, 86); await publish(f);
    for (const mutation of ['not-activated', 'equal-tokens', 'revoked', 'invalid-instance', 'duplicate-instance']) {
      const auth = clone(f.authorization);
      if (mutation === 'not-activated') auth.authorityEpoch = 0;
      if (mutation === 'equal-tokens') auth.profiles[0].externalHttpToken = auth.profiles[0].internalToken;
      if (mutation === 'revoked') auth.profiles[0].scopeMask = 0;
      if (mutation === 'invalid-instance') auth.profiles[0].profileInstanceId = 'G'.repeat(32);
      if (mutation === 'duplicate-instance') auth.profiles.push({ ...auth.profiles[0], profileId: 'Other' });
      await writeFile(join(f.connection, 'authorization.json'), JSON.stringify(auth));
      await rejected(f, mutation === 'revoked' ? 83 : 87);
    }
    const legacy = clone(f.authorization); delete legacy.formatVersion;
    for (const profile of legacy.profiles) delete profile.profileInstanceId;
    const legacyBytes = Buffer.from(JSON.stringify(legacy));
    await writeFile(join(f.connection, 'authorization.json'), legacyBytes);
    await rejected(f, 86);
    assert.deepEqual(await readFile(join(f.connection, 'authorization.json')), legacyBytes, 'read-only history cannot fabricate or persist an old profile identity');
    await writeFile(join(f.connection, 'authorization.json'), JSON.stringify(f.authorization));
    for (const object of ['connection', 'authorization.json']) {
      acl(f, object, 'broad'); await rejected(f, 87); acl(f, object, 'private');
    }
    assert.equal((await read(request(f))).code, 0);
  } finally { cleanup(f); }
});

test('Native只读existing边界：缺失不创建、真实lock busy、输入未EOF不占lease与异步截止', { timeout: 25000 }, async () => {
  const f = await fixture(); let holder;
  try {
    const missingEnvironment = `probe-${randomBytes(12).toString('hex')}`;
    await rejected(f, 81, request(f, { environmentId: missingEnvironment }));
    await assert.rejects(lstat(join(f.directory, '..', missingEnvironment)), { code: 'ENOENT' });
    const savedHistory = join(f.connection, 'history.saved');
    await rename(join(f.connection, 'operations.history'), savedHistory);
    await rejected(f, 85); await assert.rejects(readFile(join(f.connection, 'operations.history')), { code: 'ENOENT' });
    await rename(savedHistory, join(f.connection, 'operations.history'));
    holder = spawn(probe, ['hold-file', join(f.connection, 'authority.lock')], { windowsHide: true, stdio: 'pipe' });
    holder.stderr.resume(); const heldExit = once(holder, 'close');
    await once(holder.stdout, 'data'); await rejected(f, 82); holder.stdin.end(); await heldExit; holder = null;
    const pending = startNative(request(f), { endInput: false });
    holder = spawn(probe, ['hold-file', join(f.connection, 'authority.lock')], { windowsHide: true, stdio: 'pipe' });
    holder.stderr.resume(); const secondExit = once(holder, 'close');
    await once(holder.stdout, 'data');
    assert.equal((await pending.result).code, 88, 'no EOF must end by real async IO cancellation, not parse/lease admission');
    holder.stdin.end(); await secondExit; holder = null;
    assert.equal((await read(request(f), { pipeMode: 'pipe' })).code, 80, 'synchronous stdio is refused');
    assert.equal((await read(Buffer.concat([Buffer.from(JSON.stringify(request(f))), Buffer.from('{}')]))).code, 80);
    assert.equal((await read(Buffer.alloc(4097, 0x20))).code, 80);
    assert.equal((await read(request(f), { env: { ...process.env, USERPROFILE: join(f.directory, 'ignored-userprofile') } })).code, 0, 'real Windows UserProfile determines the store');
  } finally {
    if (holder) { holder.stdin.end(); await once(holder, 'close'); }
    cleanup(f);
  }
});

test('Native拒绝重解析connection，不跟随测试junction', { timeout: 10000 }, async () => {
  const f = await fixture(); let linked = false;
  try {
    await rename(f.connection, join(f.directory, 'connection-original'));
    await symlink(join(f.directory, 'connection-original'), f.connection, 'junction'); linked = true;
    await rejected(f, 87);
  } finally {
    if (linked) execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-File', join(import.meta.dirname, 'remove-history-link.ps1'), '-ProbeRoot', f.directory], { windowsHide: true, stdio: 'pipe' });
    cleanup(f);
  }
});
