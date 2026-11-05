import test from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes, createHash } from 'node:crypto';
import { mkdir, copyFile, writeFile, readFile, realpath, link, readdir, rename, unlink, rmdir } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { spawn, execFileSync } from 'node:child_process';
import { once } from 'node:events';
import { createRequire } from 'node:module';
import { createInterface } from 'node:readline';
import { verifyNodeRuntime } from '../../../tools/release/runtime-inputs.mjs';

const root = resolve(import.meta.dirname, '../../..');
const productToolNames = JSON.parse(await readFile(join(root, 'contracts/capabilities.json'), 'utf8')).capabilities.map(capability => capability.toolName).sort();
const launcher = process.env.SKYLINESLINK_TEST_LAUNCHER ?? join(root, 'artifacts/dotnet/bin/SkylinesLink.Launcher/debug/SkylinesLink.Launcher.exe');
const setup = process.env.SKYLINESLINK_TEST_SETUP ?? join(root, 'artifacts/dotnet/bin/SkylinesLink.Setup/debug/SkylinesLink.Setup.exe');
const launcherKind = process.env.SKYLINESLINK_TEST_LAUNCHER_KIND ?? 'managed';
assert.ok(['managed', 'nativeaot'].includes(launcherKind));
const processProbe = join(root, 'artifacts/dotnet/bin/SkylinesLink.ProcessProbe/debug/SkylinesLink.ProcessProbe.exe');
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const sdk = createRequire(join(root, 'src/host/package.json'));
const { Client } = sdk('@modelcontextprotocol/client');
const { StdioClientTransport } = sdk('@modelcontextprotocol/client/stdio');
const { ReadBuffer, serializeMessage } = sdk('@modelcontextprotocol/server');

async function fixture(productionHost = false, environment = `probe-${randomBytes(12).toString('hex')}`) {
  const directory = join(process.env.USERPROFILE, '.skylineslink', 'environments', environment);
  const core = join(directory, 'releases', '0.1.0-dev.0');
  await mkdir(join(core, 'runtime'), { recursive: true });
  await mkdir(join(core, 'host'), { recursive: true });
  await writeFile(join(directory, '.owned-probe'), environment);
  await copyFile(process.execPath, join(core, 'runtime/node.exe'));
  const runtime = await verifyNodeRuntime(join(core, 'runtime/node.exe'));
  await copyFile(runtime.license.sourcePath, join(core, runtime.license.relativePath));
  const paths = ['runtime/node.exe', runtime.license.relativePath, 'host/host.mjs'];
  await mkdir(join(core, 'desktop/launcher'), { recursive: true });
  const launcherFiles = launcherKind === 'nativeaot'
    ? ['SkylinesLink.Launcher.exe', 'DOTNET.LICENSE.txt', 'DOTNET.THIRD-PARTY-NOTICES.txt', 'DOTNET.SOURCES.txt']
    : ['SkylinesLink.Launcher.exe', 'SkylinesLink.Launcher.dll', 'SkylinesLink.Deployment.dll', 'SkylinesLink.Launcher.deps.json', 'SkylinesLink.Launcher.runtimeconfig.json'];
  for (const file of launcherFiles) {
    const path = `desktop/launcher/${file}`;
    await copyFile(join(dirname(launcher), file), join(core, path));
    paths.push(path);
  }
  if (productionHost) {
    await copyFile(join(root, 'artifacts/host/host.mjs'), join(core, 'host/host.mjs'));
    await copyFile(join(root, 'artifacts/host/host.mjs.LEGAL.txt'), join(core, 'host/host.mjs.LEGAL.txt'));
    await copyFile(join(root, 'artifacts/host/host.THIRD_PARTY_NOTICES.txt'), join(core, 'host/host.THIRD_PARTY_NOTICES.txt'));
    paths.push('host/host.mjs.LEGAL.txt', 'host/host.THIRD_PARTY_NOTICES.txt');
  } else await copyFile(join(import.meta.dirname, 'fixture-host.mjs'), join(core, 'host/host.mjs'));
  const files = [];
  for (const path of paths) {
    const bytes = await readFile(join(core, path));
    files.push({ path, size: bytes.length, sha256: hash(bytes) });
  }
  const manifest = Buffer.from(JSON.stringify({ productId: 'skylineslink', version: '0.1.0-dev.0', sourceCommit: 'synthetic-probe', contractVersion: '2', files }));
  await writeFile(join(core, 'core-manifest.json'), manifest);
  await writeFile(join(directory, 'activation.json'), JSON.stringify({ productId: 'skylineslink', version: '0.1.0-dev.0', coreManifestSha256: hash(manifest) }));
  return { environment, directory, core, files, manifestSha256: hash(manifest) };
}

function cleanup(fixture) {
  execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-File', join(import.meta.dirname, 'remove-owned-probe.ps1'), '-ProbeRoot', fixture.directory], { windowsHide: true, stdio: 'pipe' });
}

async function within(promise, milliseconds = 8000) {
  let timer;
  try { return await Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('离线端到端探针超过截止时间')), milliseconds); })]); }
  finally { clearTimeout(timer); }
}

function start(fixture, { profile = 'Probe.With_Id', drainOutput = true, pipeMode = 'pipe', relayReport = false, entryPath = launcher } = {}) {
  const reportPath = join(fixture.directory, 'relay-report.json');
  const command = relayReport ? processProbe : entryPath;
  const args = relayReport ? ['relay-run', join(fixture.core, 'runtime/node.exe'), join(fixture.core, 'host/host.mjs'), fixture.core,
    profile, fixture.environment, fixture.manifestSha256, reportPath]
    : ['--profile', profile, '--environment', fixture.environment.toUpperCase()];
  const child = spawn(command, args, { windowsHide: true, stdio: [pipeMode, pipeMode, 'pipe'] });
  const output = [];
  if (drainOutput) child.stdout.on('data', chunk => output.push(chunk));
  else child.stdout.pause();
  const diagnostics = [];
  child.stderr.on('data', chunk => diagnostics.push(chunk));
  const exit = once(child, drainOutput ? 'close' : 'exit');
  const ready = (async () => {
    const deadline = performance.now() + 8000;
    while (performance.now() < deadline) {
      try { return JSON.parse(await readFile(join(fixture.directory, 'fixture-ready.json'), 'utf8')); }
      catch (error) { if (error.code !== 'ENOENT') throw error; }
      if (child.exitCode !== null || child.signalCode !== null) throw new Error('启动器在子进程就绪前退出');
      await sleep(20);
    }
    throw new Error('启动器未产生子进程就绪证据');
  })();
  void ready.catch(() => {});
  return { child, output, diagnostics, exit, ready, reportPath };
}

async function requireGone(pid) {
  for (let i = 0; i < 50; i++) {
    try { process.kill(pid, 0); } catch (error) { if (error.code === 'ESRCH') return; throw error; }
    await sleep(20);
  }
  assert.fail('本次启动器仍留下自有子进程');
}

test('进程创建时已有Job归属，且不继承额外可继承句柄', () => {
  const probe = join(root, 'artifacts/dotnet/bin/SkylinesLink.ProcessProbe/debug/SkylinesLink.ProcessProbe.exe');
  const observed = JSON.parse(execFileSync(probe, [], { windowsHide: true, encoding: 'utf8' }));
  assert.deepEqual(observed, { inJob: true, inheritedUnrelatedHandle: false });
});

test('Managed Deployment实际运行同源Task固定向量且不引入Application或Newtonsoft', { timeout: 8000 }, async () => {
  const filename = join(root, 'contracts/fixtures/task-rules-vectors.json');
  const vectors = JSON.parse(await readFile(filename, 'utf8'));
  const observed = JSON.parse(execFileSync(processProbe, ['task-rules', filename], {
    windowsHide: true, encoding: 'utf8', timeout: 5000, maxBuffer: 16384,
  }));
  assert.equal(observed.kind, 'task-rules'); assert.equal(observed.runtimeMajor, 10); assert.equal(observed.managed, true);
  assert.equal(observed.ruleAssembly, 'SkylinesLink.Deployment');
  assert.equal(observed.hashes, vectors.hashes.length); assert.equal(observed.transitions, vectors.transitions.length);
  const forbidden = /(?:Newtonsoft\.Json|SkylinesLink\.(?:Application|Contracts|Infrastructure))/;
  assert.equal(observed.references.some(name => forbidden.test(name)), false);
  assert.equal(observed.consumerReferences.some(name => forbidden.test(name)), false);
  const dependencies = await readFile(join(dirname(processProbe), 'SkylinesLink.ProcessProbe.deps.json'), 'utf8');
  assert.equal(forbidden.test(dependencies), false, 'the actual Managed consumer dependency manifest stays on the Desktop boundary');
});

for (const modern of [false, true]) test(`官方SDK ${modern ? '2026' : '2025'} → 启动器 → 随包Node → 产品Host`, { timeout: 20000 }, async () => {
  const f = await fixture(true);
  const transport = new StdioClientTransport({ command: launcher, args: ['--profile', 'probe.with_id', '--environment', f.environment], stderr: 'pipe' });
  const client = new Client({ name: 'packaged-foundation-probe', version: '0.1.0' }, { capabilities: {}, versionNegotiation: { mode: modern ? { pin: '2026-07-28' } : 'legacy' } });
  try {
    await client.connect(transport);
    assert.equal(client.getNegotiatedProtocolVersion(), modern ? '2026-07-28' : '2025-11-25');
    assert.deepEqual((await client.listTools()).tools.map(tool => tool.name).sort(), productToolNames);
    const result = await client.callTool({ name: 'skylines_context', arguments: {} });
    assert.equal(result.isError, true);
    assert.equal(result.structuredContent.error.code, 'GAME_UNAVAILABLE');
    assert.equal(Object.hasOwn(result.structuredContent.error, 'mayHaveApplied'), false);
    assert.deepEqual(JSON.parse(result.content[0].text), result.structuredContent);
    const selector = { kind: 'submissionKey', submissionKey: { writeContextId: 'known-before-submit', requestId: 'original-request' } };
    const query = await client.callTool({ name: 'skylines_operation_get', arguments: { selector } });
    assert.equal(query.isError, true);
    assert.equal(query.structuredContent.error.code, 'GAME_UNAVAILABLE');
    assert.deepEqual(query.structuredContent.error.selector, selector);
    assert.equal(Object.hasOwn(query.structuredContent.error, 'mayHaveApplied'), false);
    // Actual bundled Host data remains readable after the one failed game discovery.
    // There is no separate guide file, service or client-specific prompt in this fixture.
    for (const language of ['en', 'zh-CN']) {
      const search = await client.callTool({ name: 'skylines_guidance_search', arguments: { language } });
      assert.notEqual(search.isError, true); assert.equal(search.structuredContent.results.length, 4);
      assert(search.structuredContent.results.some(guide => guide.guideId === 'module-runtime'));
      const guide = await client.callTool({ name: 'skylines_guidance_get', arguments: { guideId: search.structuredContent.results[0].guideId, language } });
      assert.notEqual(guide.isError, true); assert.equal(guide.structuredContent.sourceKind, 'productGuide');
      assert.equal(guide.structuredContent.language, language);
      assert.deepEqual(JSON.parse(guide.content[0].text), guide.structuredContent);
    }
    const described = await client.callTool({ name: 'skylines_capability_describe', arguments: { capabilityId: 'operations.get', schemaRevision: '5' } });
    assert.notEqual(described.isError, true);
    assert.equal(described.structuredContent.outputSchema.$id, 'urn:skylineslink:contract:operation-lookup:4');
  } finally { await client.close(); cleanup(f); }
});

test('完整离线链路：SDK → 启动器 → 产品Host → 私有发现 → net48，撤权立即拒绝', { timeout: 30000 }, async () => {
  const f = await fixture(true);
  const probePath = join(root, 'artifacts/dotnet/bin/SkylinesLink.RuntimeProbe/debug/SkylinesLink.RuntimeProbe.exe');
  const probe = spawn(probePath, ['--serve'], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  const exit = once(probe, 'exit');
  const lines = createInterface({ input: probe.stdout });
  const iterator = lines[Symbol.asyncIterator]();
  const read = async () => {
    const line = await within(iterator.next());
    if (line.done) throw new Error('离线端点在预期回复前退出');
    return JSON.parse(line.value);
  };
  let diagnostics = '';
  probe.stderr.on('data', chunk => { diagnostics = (diagnostics + chunk.toString()).slice(-4096); });
  const internalToken = randomBytes(32).toString('hex');
  const externalHttpToken = randomBytes(32).toString('hex');
  const transport = new StdioClientTransport({ command: launcher, args: ['--profile', 'Probe.With_Id', '--environment', f.environment.toUpperCase()], stderr: 'pipe' });
  const client = new Client({ name: 'offline-full-chain', version: '0.1.0' }, { capabilities: {}, versionNegotiation: { mode: { pin: '2026-07-28' } } });
  try {
    probe.stdin.write(JSON.stringify({ privateDirectory: join(f.directory, 'connection'), environmentId: f.environment,
      coreManifestSha256: f.manifestSha256, profileId: 'Probe.With_Id', internalToken, externalHttpToken, initialize: true, deadlineMilliseconds: 5000 }) + '\n');
    const ready = await read();
    assert.equal(ready.kind, 'ready');
    if (JSON.stringify(ready).includes(internalToken) || JSON.stringify(ready).includes(externalHttpToken)) throw new Error('探针公开状态泄露测试凭据');
    const discoveryPath = join(f.directory, 'connection', 'discovery.json');
    assert.equal(resolve(await realpath(discoveryPath)).toLowerCase(), resolve(discoveryPath).toLowerCase(), '统一连接文件不得进入客户端私有重定向目录');
    await client.connect(transport);
    assert.deepEqual((await client.listTools()).tools.map(tool => tool.name).sort(), productToolNames);
    const result = await client.callTool({ name: 'skylines_context', arguments: {} });
    if (result.isError) {
      const code = result.structuredContent?.error?.code;
      throw new Error(typeof code === 'string' && /^[A-Z_]+$/.test(code) ? `完整离线连接失败：${code}` : '完整离线连接失败');
    }
    assert.equal(result.structuredContent.instanceId, ready.instanceId);
    assert.equal(result.structuredContent.contractVersion, '2');
    assert.equal(result.structuredContent.gameState, 'menu');
    assert.equal(result.structuredContent.world, null);
    assert.deepEqual(JSON.parse(result.content[0].text), result.structuredContent);
    const lookup = await client.callTool({ name: 'skylines_operation_get', arguments: { selector: { kind: 'operationId', operationId: 'missing' } } });
    assert.notEqual(lookup.isError, true);
    assert.deepEqual(lookup.structuredContent, { selector: { kind: 'operationId', operationId: 'missing' },
      result: { kind: 'live', instanceId: ready.instanceId, lookup: { kind: 'absent' } } });
    probe.stdin.write('revoke\n');
    assert.equal((await read()).kind, 'revoked');
    const denied = await client.callTool({ name: 'skylines_context', arguments: {} });
    assert.equal(denied.isError, true);
    assert.equal(denied.structuredContent.error.code, 'UNAUTHORIZED');
    if (diagnostics.includes(internalToken) || diagnostics.includes(externalHttpToken)) throw new Error('探针诊断泄露测试凭据');
  } finally {
    try { await client.close(); }
    finally {
      try {
        if (probe.exitCode === null && probe.signalCode === null) {
          probe.stdin.end('stop\n');
          try { await within(exit, 5000); }
          catch { probe.kill(); await within(exit, 5000); }
        }
      } finally { lines.close(); cleanup(f); }
    }
  }
});

// Public SDK transport contract, with directly owned Launcher stdio so graceful
// EOF evidence cannot be confused with the SDK's eventual child termination.
function historyLauncherTransport(fixture) {
  const child = spawn(launcher, ['--profile', 'History.FullChain', '--environment', fixture.environment], { windowsHide: true, stdio: 'pipe' });
  const reader = new ReadBuffer({ maxBufferSize: 131072 });
  let failure, started = false, closing, inputBytes = 0, outputBytes = 0, diagnosticBytes = 0;
  const fail = error => { failure ??= error; if (child.exitCode === null && child.signalCode === null) child.kill(); };
  const streams = [child.stdin, child.stdout, child.stderr];
  const streamClose = streams.map(stream => new Promise(resolveClose => stream.once('close', resolveClose)));
  for (const stream of streams) stream.on('error', () => fail(new Error('History Launcher fixture stream failed.')));
  child.on('error', () => fail(new Error('History Launcher fixture failed to start.')));
  const childClose = new Promise(resolveClose => child.once('close', (code, signal) => resolveClose({ code, signal })));
  const closed = Promise.all([childClose, ...streamClose]);
  child.stderr.on('data', chunk => { diagnosticBytes += chunk.length; if (diagnosticBytes > 4096) fail(new Error('History Launcher diagnostic bound exceeded.')); });
  child.stdout.on('data', chunk => {
    try {
      outputBytes += chunk.length;
      if (outputBytes > 1048576) throw new Error('History Launcher response bound exceeded.');
      reader.append(chunk);
      for (let message = reader.readMessage(); message !== null; message = reader.readMessage()) transport.onmessage?.(message);
    } catch (error) { fail(error); transport.onerror?.(error); }
  });
  child.stdout.once('close', () => { reader.clear(); transport.onclose?.(); });
  const transport = {
    start: async () => { if (started) throw new Error('History Launcher transport cannot restart.'); started = true; },
    send: async message => {
      if (failure) throw failure;
      const bytes = serializeMessage(message); inputBytes += Buffer.byteLength(bytes);
      if (inputBytes > 131072) throw new Error('History Launcher request bound exceeded.');
      await new Promise((resolveWrite, reject) => child.stdin.write(bytes, error => error ? reject(error) : resolveWrite()));
    },
    close: () => closing ??= (async () => {
      try {
        child.stdin.end();
        const [result] = await within(closed, 6000);
        if (failure) throw failure;
        assert.equal(result.code, 0); assert.equal(result.signal, null); assert.equal(diagnosticBytes, 0);
      } catch (error) {
        fail(error); await within(closed, 5000); throw error;
      }
    })(),
  };
  return transport;
}

test('完整MCP停止决定：真实Mixed writer→CP2→Launcher/Native3→普通tool保原身份且不自动改源', { timeout: 30000 }, async () => {
  const environment = `probe-${randomBytes(12).toString('hex')}`;
  const directory = join(process.env.USERPROFILE, '.skylineslink', 'environments', environment);
  const token = randomBytes(32).toString('hex');
  let client;
  try {
    const ready = JSON.parse(execFileSync(join(root, 'artifacts/dotnet/bin/SkylinesLink.RuntimeProbe/debug/SkylinesLink.RuntimeProbe.exe'),
      ['--mixed-history-probe'], { input: JSON.stringify({ environmentId: environment, profileId: 'History.FullChain', internalToken: token }),
        windowsHide: true, encoding: 'utf8', timeout: 10000, maxBuffer: 16384 }));
    assert.equal(ready.kind, 'mixed-history-ready'); assert.equal(ready.fixture, 'synthetic-known-facts-no-game');
    assert.equal(await readFile(join(directory, '.owned-probe'), 'utf8'), environment);
    const f = await fixture(true, environment);
    const paths = ['authorization.json', 'operations.history', 'operations.journal'];
    const fingerprint = async () => Object.fromEntries(await Promise.all(paths.map(async path =>
      [path, hash(await readFile(join(directory, 'connection', path)))])));
    const baseline = await fingerprint();
    const bytes = await readFile(join(directory, 'connection/operations.history'));
    const checkpoint = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes.subarray(12, -32)));
    const row = checkpoint.taskRecords.find(value => value.entry.kind === 'taskStopped');
    const selector = { taskId: row.entry.taskId, expectedControlVersion: row.entry.expectedControlVersion };
    client = new Client({ name: 'mixed-public-stop-chain', version: '1' },
      { capabilities: {}, versionNegotiation: { mode: { pin: '2026-07-28' } } });
    await client.connect(historyLauncherTransport(f));
    const read = args => within(client.callTool({ name: 'skylines_task_stop_decision_get', arguments: args }));
    const found = await read({ ...selector, source: 'history' });
    assert.notEqual(found.isError, true);
    assert.deepEqual(found.structuredContent.selector, selector);
    const body = found.structuredContent.result;
    assert.equal(body.kind, 'history'); assert.equal(body.access, 'offlineLease'); assert.equal(body.integrity, 'sha256Verified');
    assert.deepEqual(body.source, ready.source); assert.equal(body.lookup.kind, 'found');
    assert.deepEqual(body.lookup.receipt.decision, row.entry.decision);
    assert.equal(body.lookup.receipt.sequence, row.sequence);
    assert.equal(body.lookup.receipt.controlVersion, row.entry.controlVersion);
    assert.deepEqual(JSON.parse(found.content.find(value => value.type === 'text').text), found.structuredContent);
    const missing = await read({ taskId: selector.taskId, expectedControlVersion: 2, source: 'history' });
    assert.equal(missing.structuredContent.result.lookup.reason, 'HISTORY_NOT_COVERED');
    const live = await read(selector);
    assert.equal(live.isError, true); assert.equal(live.structuredContent.error.source, 'live');
    assert.deepEqual(live.structuredContent.error.taskSelector, selector);
    const again = await read({ ...selector, source: 'history' });
    assert.deepEqual(again.structuredContent.result.lookup.receipt.decision, row.entry.decision);
    assert.deepEqual(await fingerprint(), baseline, 'public reads and failed live discovery do not rewrite the actual Mixed writer files');
    const authPath = join(directory, 'connection/authorization.json');
    const authorization = JSON.parse(await readFile(authPath, 'utf8'));
    authorization.recordRevision++; authorization.profiles[0].scopeMask = 0;
    await writeFile(authPath, JSON.stringify(authorization));
    const deniedBaseline = await fingerprint();
    const denied = await read({ ...selector, source: 'history' });
    assert.equal(denied.isError, true); assert.equal(denied.structuredContent.error.source, 'history');
    assert.deepEqual(denied.structuredContent.error.taskSelector, selector);
    assert.deepEqual(await fingerprint(), deniedBaseline, 'Native3 denial neither repairs nor replays the original journal/history');
  } finally {
    try { if (client) await client.close(); }
    finally {
      // The writer creates this exact marker before any data, and cleanup verifies
      // ownership/containment even when setup or the public client fails.
      cleanup({ directory });
    }
  }
});

test('完整MCP历史：同会话活动权威→正常退出→Native历史，live不静默改源', { timeout: 30000 }, async () => {
  const f = await fixture(true);
  const token = randomBytes(32).toString('hex');
  const probe = spawn(join(root, 'artifacts/dotnet/bin/SkylinesLink.RuntimeProbe/debug/SkylinesLink.RuntimeProbe.exe'), ['--serve'], { windowsHide: true, stdio: 'pipe' });
  const streams = [probe.stdin, probe.stdout, probe.stderr];
  const streamClose = streams.map(stream => new Promise(resolveClose => stream.once('close', resolveClose)));
  const probeClose = new Promise(resolveClose => probe.once('close', (code, signal) => resolveClose({ code, signal })));
  const allClosed = Promise.all([probeClose, ...streamClose]);
  const lines = createInterface({ input: probe.stdout, crlfDelay: Infinity });
  const iterator = lines[Symbol.asyncIterator]();
  let runtimeFailure, stdoutBytes = 0, stderrBytes = 0, stopped = false;
  const fail = error => { runtimeFailure ??= error; if (probe.exitCode === null && probe.signalCode === null) probe.kill(); };
  for (const stream of streams) stream.on('error', () => fail(new Error('Runtime history fixture stream failed.')));
  probe.on('error', () => fail(new Error('Runtime history fixture failed to start.')));
  probe.stdout.on('data', chunk => { stdoutBytes += chunk.length; if (stdoutBytes > 65536) fail(new Error('Runtime history output bound exceeded.')); });
  probe.stderr.on('data', chunk => { stderrBytes += chunk.length; if (stderrBytes > 4096) fail(new Error('Runtime history diagnostic bound exceeded.')); });
  const read = async () => {
    const line = await within(iterator.next(), 5000);
    if (runtimeFailure) throw runtimeFailure;
    if (line.done || line.value.includes(token)) throw new Error('Runtime history response unavailable or exposed a synthetic credential.');
    return JSON.parse(line.value);
  };
  const command = async value => {
    await new Promise((resolveWrite, reject) => probe.stdin.write(`${JSON.stringify(value)}\n`, error => error ? reject(error) : resolveWrite()));
    return read();
  };
  const clients = [], transports = [];
  const connect = async () => {
    const transport = historyLauncherTransport(f); transports.push(transport);
    const client = new Client({ name: 'full-history-chain', version: '1' }, { capabilities: {}, versionNegotiation: { mode: { pin: '2026-07-28' } } });
    clients.push(client); await within(client.connect(transport)); return client;
  };
  const call = async (client, name, args) => {
    const result = await within(client.callTool({ name, arguments: args }));
    if (JSON.stringify(result).includes(token)) throw new Error('MCP result exposed a synthetic credential.');
    assert.deepEqual(JSON.parse(result.content[0].text), result.structuredContent); return result;
  };
  try {
    const ready = await command({ privateDirectory: join(f.directory, 'connection'), environmentId: f.environment,
      coreManifestSha256: f.manifestSha256, profileId: 'History.FullChain', internalToken: token, externalHttpToken: null,
      initialize: true, operationProbe: true, deadlineMilliseconds: 5000 });
    assert.equal(ready.kind, 'ready');
    const client = await connect();
    assert.deepEqual((await client.listTools()).tools.map(tool => tool.name).sort(), productToolNames);
    const context = await call(client, 'skylines_context', {}); assert.notEqual(context.isError, true);
    assert.equal(context.structuredContent.instanceId, ready.instanceId);
    const writeContext = await command({ command: 'context' }); assert.equal(writeContext.kind, 'write-context');
    const key = { writeContextId: writeContext.writeContextId, requestId: '完整MCP历史/😀' };
    const accepted = await command({ command: 'submit', ...key }); assert.equal(accepted.kind, 'accepted');
    const selector = { kind: 'submissionKey', submissionKey: key };
    const live = await call(client, 'skylines_operation_get', { selector, source: 'live' }); assert.notEqual(live.isError, true);
    assert.equal(live.structuredContent.result.kind, 'live'); assert.equal(live.structuredContent.result.lookup.kind, 'found');
    assert.equal(live.structuredContent.result.lookup.operationId, accepted.operationId);
    assert.equal(live.structuredContent.result.lookup.state, 'queued'); assert.equal(live.structuredContent.result.lookup.mayHaveApplied, false);
    const active = await call(client, 'skylines_operation_get', { selector, source: 'history' }); assert.notEqual(active.isError, true);
    assert.equal(active.structuredContent.result.kind, 'history'); assert.equal(active.structuredContent.result.access, 'activeAuthority');
    assert.deepEqual(active.structuredContent.result.lookup, { kind: 'unavailable', reason: 'HISTORY_NOT_COVERED' });
    assert.equal(active.structuredContent.result.source.producerInstanceId, ready.instanceId);

    probe.stdin.end('stop\n');
    const reply = await read(); assert.equal(reply.kind, 'stopped');
    assert.equal(reply.connected, 0); assert.equal(reply.authenticated, 0);
    assert.equal(reply.ownedPayloadCount, 0); assert.equal(reply.ownedPayloadCapacity, 0);
    const [exit] = await within(allClosed, 5000);
    if (runtimeFailure) throw runtimeFailure;
    assert.equal(exit.code, 0); assert.equal(exit.signal, null); assert.equal(stderrBytes, 0); stopped = true;
    const history = await call(client, 'skylines_operation_get', { selector, source: 'history' }); assert.notEqual(history.isError, true);
    const result = history.structuredContent.result;
    assert.deepEqual(history.structuredContent.selector, selector);
    assert.equal(result.kind, 'history'); assert.equal(result.access, 'offlineLease'); assert.equal(result.lookup.kind, 'found');
    assert.equal(result.integrity, 'sha256Verified'); assert.equal(result.source.producerInstanceId, ready.instanceId);
    assert.equal(result.source.producerCoreManifestSha256, f.manifestSha256);
    const record = result.lookup.record;
    assert.equal(record.operationId, accepted.operationId); assert.deepEqual(record.submissionKey, key);
    assert.equal(record.originInstanceId, ready.instanceId); assert.equal(record.state, 'cancelled');
    assert.equal(record.mayHaveApplied, false); assert.equal(record.durability, 'durable');
    assert.equal(record.load.physicalTaskState, 'notStarted'); assert.equal(record.load.loadReturnValue, null);
    assert.equal(record.load.isObservationCurrent, false);
    const byId = await call(client, 'skylines_operation_get', { selector: { kind: 'operationId', operationId: accepted.operationId }, source: 'history' });
    assert.notEqual(byId.isError, true); assert.deepEqual(byId.structuredContent.result.source, result.source);
    assert.deepEqual(byId.structuredContent.result.lookup.record, record);
    await client.close(); await transports[0].close();
    const nextClient = await connect();
    const unavailable = await call(nextClient, 'skylines_operation_get', { selector, source: 'live' });
    assert.equal(unavailable.isError, true); assert.equal(unavailable.structuredContent.error.code, 'GAME_UNAVAILABLE');
    assert.deepEqual(unavailable.structuredContent.error.selector, selector);
    assert.equal(Object.hasOwn(unavailable.structuredContent.error, 'mayHaveApplied'), false);
  } finally {
    const closedClients = await Promise.allSettled(clients.map(client => client.close()));
    const closedLaunchers = await Promise.allSettled(transports.map(transport => transport.close()));
    if (!stopped) { probe.stdin.end(); fail(new Error('Runtime fixture did not complete its required normal stop.')); }
    try { await within(allClosed, 5000); }
    finally { lines.close(); cleanup(f); }
    for (const result of [...closedClients, ...closedLaunchers]) if (result.status === 'rejected') throw result.reason;
  }
});

test('完整MCP保留：真实退休→官方SDK两种selector新reason，原false与显式history不混源', { timeout: 40000 }, async () => {
  const f = await fixture(true);
  const token = randomBytes(32).toString('hex');
  const probe = spawn(join(root, 'artifacts/dotnet/bin/SkylinesLink.RuntimeProbe/debug/SkylinesLink.RuntimeProbe.exe'), ['--serve'], { windowsHide: true, stdio: 'pipe' });
  const streams = [probe.stdin, probe.stdout, probe.stderr];
  const streamClose = streams.map(stream => new Promise(resolveClose => stream.once('close', resolveClose)));
  const probeClose = new Promise(resolveClose => probe.once('close', (code, signal) => resolveClose({ code, signal })));
  const allClosed = Promise.all([probeClose, ...streamClose]);
  const lines = createInterface({ input: probe.stdout, crlfDelay: Infinity });
  const iterator = lines[Symbol.asyncIterator]();
  let runtimeFailure, stdoutBytes = 0, stderrBytes = 0, stopped = false, client, transport;
  const fail = error => { runtimeFailure ??= error; if (probe.exitCode === null && probe.signalCode === null) probe.kill(); };
  for (const stream of streams) stream.on('error', () => fail(new Error('Runtime retention fixture stream failed.')));
  probe.on('error', () => fail(new Error('Runtime retention fixture failed to start.')));
  probe.stdout.on('data', chunk => { stdoutBytes += chunk.length; if (stdoutBytes > 65536) fail(new Error('Runtime retention output bound exceeded.')); });
  probe.stderr.on('data', chunk => { stderrBytes += chunk.length; if (stderrBytes > 4096) fail(new Error('Runtime retention diagnostic bound exceeded.')); });
  const read = async () => {
    const line = await within(iterator.next(), 5000);
    if (runtimeFailure) throw runtimeFailure;
    if (line.done || line.value.includes(token)) throw new Error('Runtime retention response unavailable or exposed a synthetic credential.');
    return JSON.parse(line.value);
  };
  const command = async value => {
    await new Promise((resolveWrite, reject) => probe.stdin.write(`${JSON.stringify(value)}\n`, error => error ? reject(error) : resolveWrite()));
    return read();
  };
  const call = async (name, args) => {
    const result = await within(client.callTool({ name, arguments: args }));
    if (JSON.stringify(result).includes(token)) throw new Error('MCP retention result exposed a synthetic credential.');
    assert.deepEqual(JSON.parse(result.content[0].text), result.structuredContent); return result;
  };
  const byId = operationId => ({ kind: 'operationId', operationId });
  const byKey = key => ({ kind: 'submissionKey', submissionKey: key });
  try {
    const ready = await command({ privateDirectory: join(f.directory, 'connection'), environmentId: f.environment,
      coreManifestSha256: f.manifestSha256, profileId: 'History.FullChain', internalToken: token, externalHttpToken: null,
      initialize: true, operationProbe: true, deadlineMilliseconds: 5000 });
    assert.equal(ready.kind, 'ready');
    transport = historyLauncherTransport(f);
    client = new Client({ name: 'full-retention-chain', version: '1' }, { capabilities: {}, versionNegotiation: { mode: { pin: '2026-07-28' } } });
    await within(client.connect(transport));
    assert.deepEqual((await client.listTools()).tools.map(tool => tool.name).sort(), productToolNames);
    const exhausted = await command({ command: 'context' }); assert.equal(exhausted.kind, 'write-context');
    let first;
    for (let index = 0; index < 8; index++) {
      const completed = await command({ command: 'submit-complete-false', writeContextId: exhausted.writeContextId, requestId: `退休配额/😀/${index}` });
      assert.equal(completed.kind, 'completed-false'); assert.equal(completed.state, 'failed');
      assert.equal(completed.durability, 'durable'); assert.equal(completed.loadReturnValue, false);
      first ??= completed;
    }
    const current = await command({ command: 'context' }); assert.equal(current.kind, 'write-context');
    const keptKey = { writeContextId: current.writeContextId, requestId: '仍可受理/false' };
    const kept = await command({ command: 'submit-complete-false', ...keptKey }); assert.equal(kept.kind, 'completed-false');
    const originalKey = { writeContextId: first.writeContextId, requestId: first.requestId };
    const before = await call('skylines_operation_get', { selector: byId(first.operationId), source: 'live' });
    assert.notEqual(before.isError, true); assert.equal(before.structuredContent.result.kind, 'live');
    assert.equal(before.structuredContent.result.lookup.load.loadReturnValue, false);
    const retired = await command({ command: 'retire' }); assert.deepEqual(retired, { kind: 'retirement', outcome: 'Installed' });
    for (const selector of [byId(first.operationId), byKey(originalKey)]) {
      const result = await call('skylines_operation_get', { selector, source: 'live' });
      assert.notEqual(result.isError, true);
      assert.deepEqual(result.structuredContent, { selector, result: { kind: 'live', instanceId: ready.instanceId,
        lookup: { kind: 'unavailable', reason: 'RETENTION_NOT_COVERED' } } });
    }
    const known = await call('skylines_operation_get', { selector: byKey(keptKey) });
    assert.notEqual(known.isError, true); assert.equal(known.structuredContent.result.kind, 'live');
    assert.equal(known.structuredContent.result.lookup.operationId, kept.operationId);
    assert.equal(known.structuredContent.result.lookup.state, 'failed'); assert.equal(known.structuredContent.result.lookup.mayHaveApplied, true);
    assert.equal(known.structuredContent.result.lookup.durability, 'durable'); assert.equal(known.structuredContent.result.lookup.load.loadReturnValue, false);
    const missingSelector = byKey({ writeContextId: current.writeContextId, requestId: 'not-created' });
    const missing = await call('skylines_operation_get', { selector: missingSelector, source: 'live' });
    assert.notEqual(missing.isError, true); assert.equal(missing.structuredContent.result.kind, 'live');
    assert.deepEqual(missing.structuredContent.result.lookup, { kind: 'absent' });
    const history = await call('skylines_operation_get', { selector: missingSelector, source: 'history' });
    assert.notEqual(history.isError, true); assert.equal(history.structuredContent.result.kind, 'history');
    assert.equal(history.structuredContent.result.access, 'activeAuthority'); assert.deepEqual(history.structuredContent.selector, missingSelector);
    assert.deepEqual(history.structuredContent.result.lookup, { kind: 'unavailable', reason: 'HISTORY_NOT_COVERED' });
    await new Promise((resolveWrite, reject) => probe.stdin.write('revoke\n', error => error ? reject(error) : resolveWrite()));
    assert.equal((await read()).kind, 'revoked');
    const denied = await call('skylines_operation_get', { selector: byId(kept.operationId), source: 'live' });
    assert.equal(denied.isError, true); assert.equal(denied.structuredContent.error.code, 'UNAUTHORIZED');
    assert.deepEqual(denied.structuredContent.error.selector, byId(kept.operationId));
    await client.close(); await transport.close();
    probe.stdin.end('stop\n');
    const reply = await read(); assert.equal(reply.kind, 'stopped');
    assert.equal(reply.connected, 0); assert.equal(reply.authenticated, 0);
    assert.equal(reply.ownedPayloadCount, 0); assert.equal(reply.ownedPayloadCapacity, 0);
    const [exit] = await within(allClosed, 5000);
    if (runtimeFailure) throw runtimeFailure;
    assert.equal(exit.code, 0); assert.equal(exit.signal, null); assert.equal(stderrBytes, 0); stopped = true;
  } finally {
    try { if (client) await client.close(); if (transport) await transport.close(); }
    finally {
      if (!stopped) { probe.stdin.end(); fail(new Error('Runtime retention fixture did not complete its required normal stop.')); }
      try { await within(allClosed, 5000); }
      finally { lines.close(); cleanup(f); }
    }
  }
});

test('启动器在创建Node前拒绝不支持的内部Core契约', { timeout: 20000 }, async () => {
  const f = await fixture();
  try {
    const manifest = Buffer.from(JSON.stringify({ productId: 'skylineslink', version: '0.1.0-dev.0',
      sourceCommit: 'synthetic-probe', contractVersion: 'unsupported', files: f.files }));
    await writeFile(join(f.core, 'core-manifest.json'), manifest);
    await writeFile(join(f.directory, 'activation.json'), JSON.stringify({ productId: 'skylineslink',
      version: '0.1.0-dev.0', coreManifestSha256: hash(manifest) }));
    assert.throws(() => execFileSync(launcher, ['--profile', 'probe', '--environment', f.environment],
      { windowsHide: true, stdio: 'pipe', timeout: 5000 }), error => error.status === 1 && error.signal === null);
    await assert.rejects(readFile(join(f.directory, 'fixture-ready.json')), { code: 'ENOENT' });
  } finally { cleanup(f); }
});

test('已核核心通过；损坏与路径穿越拒绝', { timeout: 20000 }, async () => {
  const f = await fixture();
  try {
    const result = execFileSync(setup, ['verify-core', f.core], { windowsHide: true, encoding: 'utf8' });
    assert.ok(result.includes(`${f.files.length} 个文件校验一致`));
    await writeFile(join(f.directory, 'activation.json'), JSON.stringify({ productId: 'skylineslink', version: '0.1.0-dev.0', coreManifestSha256: '0'.repeat(64) }));
    assert.throws(() => execFileSync(launcher, ['--profile', 'probe', '--environment', f.environment], { windowsHide: true, stdio: 'pipe' }));
    await writeFile(join(f.core, 'host/host.mjs'), 'corrupted');
    assert.throws(() => execFileSync(setup, ['verify-core', f.core], { windowsHide: true, stdio: 'pipe' }));
    f.files[0].path = '../outside.exe';
    await writeFile(join(f.core, 'core-manifest.json'), JSON.stringify({ productId: 'skylineslink', version: '0.1.0-dev.0', sourceCommit: 'synthetic-probe', contractVersion: '1', files: f.files }));
    assert.throws(() => execFileSync(setup, ['verify-core', f.core], { windowsHide: true, stdio: 'pipe' }));
  } finally { cleanup(f); }
});

for (const pipeMode of ['pipe', 'overlapped']) test(`标准流 ${pipeMode} 透明、EOF和退出码保留，正常退出回收后代`, { timeout: 20000 }, async () => {
  const f = await fixture();
  const run = start(f, { pipeMode });
  try {
    const ready = await run.ready;
    assert.deepEqual(ready.args.slice(0, 8), ['--transport', 'stdio', '--profile', 'Probe.With_Id', '--environment', f.environment, '--core-manifest-sha256', f.manifestSha256]);
    assert.deepEqual(ready.args.slice(8, 10), ['--history-reader', join(f.core, 'desktop/launcher/SkylinesLink.Launcher.exe')]);
    assert.equal(ready.args[10], '--stdio-relay');
    assert.match(ready.args[11], /^[a-f0-9]{32}$/);
    assert.equal(ready.args.length, 12);
    const bytes = Buffer.from([0, 10, 13, 0xe4, 0xb8, 0xad, 0xff]);
    run.child.stdin.end(bytes);
    assert.equal((await run.exit)[0], 23);
    assert.deepEqual(Buffer.concat(run.output), bytes);
    await requireGone(ready.pid);
    await requireGone(ready.grandchild);
  } finally {
    if (run.child.exitCode === null) { run.child.kill(); await run.exit; }
    cleanup(f);
  }
});

for (const pipeMode of ['pipe', 'overlapped']) for (const eofAfterStart of [false, true]) test(`启动器共用 relay 的真实 ${pipeMode} stdout 停读${eofAfterStart ? '后 EOF' : ''}：截止并回收`, { timeout: 20000 }, async () => {
  const f = await fixture();
  // The default probe observes the same relay's internal completion. NativeAOT
  // verification must exercise the actual supplied Launcher, whose code 74 is
  // returned only after both workers and its child have really terminated.
  const observeInternal = process.env.SKYLINESLINK_TEST_LAUNCHER === undefined;
  const run = start(f, { profile: 'Probe.SlowReader', drainOutput: false, pipeMode, relayReport: observeInternal });
  try {
    const ready = await run.ready;
    run.child.stdin.write('bounded-output\n');
    const evidence = await within((async () => {
      const deadline = performance.now() + 1200;
      while (performance.now() < deadline) {
        try { return JSON.parse(await readFile(join(f.directory, 'fixture-backpressure.json'), 'utf8')); }
        catch (error) { if (error.code !== 'ENOENT') throw error; }
        await sleep(10);
      }
      throw new Error('有限输出没有产生真实背压证据');
    })(), 1500);
    if (eofAfterStart) run.child.stdin.end();
    const [code, signal] = await within(run.exit, 10000);
    assert.equal(signal, null);
    assert.equal(code, 74, '输出超时必须返回明确的传输失败');
    assert.equal(evidence.timerExecuted, true, 'Node 事件循环不能被外侧同步 stdout 卡死');
    assert.equal(evidence.requestedBytes, 524288);
    assert.ok(evidence.pendingBytes > 0 && evidence.completedWrites < 8, '有限夹具必须真正触发在途背压');
    if (observeInternal) {
      const report = JSON.parse(await readFile(run.reportPath, 'utf8'));
      assert.equal(report.code, 74);
      assert.equal(report.cause, 'STDIO_OUTPUT_TIMEOUT');
      assert.equal(report.workersJoined, true);
      assert.equal(report.remainingBufferBytes, 0);
      assert.equal(report.childExited, true);
    }
    assert.equal(Buffer.concat(run.diagnostics).length, 0, '受控 Node 不得通过同步 stderr 另开写入旁路');
    await requireGone(ready.pid);
    await requireGone(ready.grandchild);
  } finally {
    if (run.child.exitCode === null && run.child.signalCode === null) { run.child.kill(); await run.exit; }
    run.child.stdin.destroy(); run.child.stdout.destroy(); run.child.stderr.destroy();
    cleanup(f);
  }
});

test('启动器异常终止由Job释放本次子进程树', { timeout: 20000 }, async () => {
  const f = await fixture();
  const run = start(f);
  try {
    const ready = await run.ready;
    run.child.kill();
    await run.exit;
    await requireGone(ready.pid);
    await requireGone(ready.grandchild);
  } finally {
    if (run.child.exitCode === null && run.child.signalCode === null) { run.child.kill(); await run.exit; }
    cleanup(f);
  }
});

async function preparationFixture() {
  const environment = `probe-${randomBytes(12).toString('hex')}`;
  const directory = join(process.env.USERPROFILE, '.skylineslink', 'environments', environment);
  const source = join(directory, '离线来源 空格');
  await mkdir(join(source, 'host'), { recursive: true });
  await writeFile(join(directory, '.owned-probe'), environment);
  const content = Buffer.from('独立离线准备夹具\n');
  await writeFile(join(source, 'host/内容.txt'), content);
  const manifest = Buffer.from(JSON.stringify({ productId: 'skylineslink', version: '0.1.0-probe', sourceCommit: 'synthetic-probe', contractVersion: '2',
    files: [{ path: 'host/内容.txt', size: content.length, sha256: hash(content) }] }));
  await writeFile(join(source, 'core-manifest.json'), manifest);
  const activation = JSON.stringify({ productId: 'skylineslink', version: 'prior-probe', coreManifestSha256: '1'.repeat(64) });
  await writeFile(join(directory, 'activation.json'), activation);
  const destination = join(directory, 'releases', '0.1.0-probe');
  return { environment, directory, source, content, manifest, activation, stage: destination, destination };
}

const prepare = f => execFileSync(setup, ['prepare-core', f.source, f.environment.toUpperCase()], { windowsHide: true, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
const activate = f => execFileSync(setup, ['activate-core', f.source, f.environment.toUpperCase()], { windowsHide: true, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
const activeCore = f => JSON.parse(execFileSync(setup, ['active-core', f.environment.toUpperCase()], { windowsHide: true, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }));
const activationRecord = f => ({ productId: 'skylineslink', version: JSON.parse(f.manifest.toString()).version, coreManifestSha256: hash(f.manifest) });
const installLauncher = f => execFileSync(setup, ['install-launcher', f.source, f.environment.toUpperCase()], { windowsHide: true, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
const launcherStatus = f => execFileSync(setup, ['launcher-status', f.source, f.environment.toUpperCase()], { windowsHide: true, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
const initializeCore = f => execFileSync(setup, ['initialize-core', f.source, f.environment.toUpperCase()], { windowsHide: true, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });

async function activationFixture({ nativeLauncher } = {}) {
  const f = await preparationFixture();
  const paths = [
    'LICENSE', 'SOURCE.txt', 'source/SkylinesLink-source.zip', 'runtime/node.exe', 'runtime/Node.LICENSE.txt',
    'host/host.mjs', 'host/host.mjs.LEGAL.txt', 'host/host.THIRD_PARTY_NOTICES.txt', 'mod/managed-components.json',
    ...['Contracts', 'Application', 'Infrastructure', 'Mod'].map(name => `mod/SkylinesLink.${name}.dll`),
    ...['launcher', 'setup'].flatMap(name => [`desktop/${name}/SkylinesLink.${name === 'launcher' ? 'Launcher' : 'Setup'}.exe`,
      ...['DOTNET.LICENSE.txt', 'DOTNET.THIRD-PARTY-NOTICES.txt', 'DOTNET.SOURCES.txt'].map(file => `desktop/${name}/${file}`)]),
  ];
  const files = [];
  const runtime = nativeLauncher ? await verifyNodeRuntime(process.execPath) : undefined;
  // NativeAOT execution uses real Launcher/Node/fixture-host only. Other bytes
  // remain synthetic file-set inputs and are never loaded as Mod or Setup code.
  for (const path of paths) {
    let source;
    if (nativeLauncher) {
      if (path === 'runtime/node.exe') source = process.execPath;
      else if (path === 'runtime/Node.LICENSE.txt') source = runtime.license.sourcePath;
      else if (path === 'host/host.mjs') source = join(import.meta.dirname, 'fixture-host.mjs');
      else if (path === 'desktop/launcher/SkylinesLink.Launcher.exe') source = nativeLauncher;
      else if (path.startsWith('desktop/launcher/')) source = join(dirname(nativeLauncher), path.slice('desktop/launcher/'.length));
    }
    const bytes = source ? await readFile(source) : Buffer.from(`Synthetic activation payload: ${path}\n`);
    await mkdir(dirname(join(f.source, path)), { recursive: true });
    await writeFile(join(f.source, path), bytes);
    files.push({ path, size: bytes.length, sha256: hash(bytes) });
  }
  const manifest = Buffer.from(JSON.stringify({ ...JSON.parse(f.manifest.toString()), files }));
  await writeFile(join(f.source, 'core-manifest.json'), manifest);
  return { ...f, manifest };
}

async function launcherCandidate(f, version, payload) {
  const path = 'desktop/launcher/SkylinesLink.Launcher.exe';
  const previous = JSON.parse(f.manifest.toString());
  const manifest = Buffer.from(JSON.stringify({ ...previous, version, files: previous.files.map(file =>
    file.path === path ? { path, size: payload.length, sha256: hash(payload) } : file) }));
  await writeFile(join(f.source, path), payload);
  await writeFile(join(f.source, 'core-manifest.json'), manifest);
  const destination = join(f.directory, 'releases', version);
  return { ...f, manifest, destination, stage: destination };
}

async function seedPreparation(f) {
  await mkdir(join(f.stage, 'host'), { recursive: true });
  await writeFile(join(f.stage, 'preparation-manifest.json'), f.manifest);
}

test('离线准备：白名单、中文空格路径、重复复用，保留原激活和连接归属', async () => {
  const f = await preparationFixture();
  try {
    await writeFile(join(f.source, 'unlisted.private'), 'fixture-only-do-not-copy');
    assert.match(prepare(f), /^prepared\r?\n/);
    assert.deepEqual(await readFile(join(f.destination, 'host/内容.txt')), f.content);
    assert.deepEqual((await readdir(f.destination)).sort(), ['core-manifest.json', 'host']);
    assert.match(prepare(f), /^already-prepared\r?\n/);
    assert.equal(await readFile(join(f.directory, 'activation.json'), 'utf8'), f.activation);
    assert.equal((await readdir(f.directory)).includes('connection'), false);
  } finally { cleanup(f); }
});

test('离线准备：清单相符的半写文件重建，损坏元数据拒绝猜测', async () => {
  const f = await preparationFixture();
  try {
    await seedPreparation(f);
    await writeFile(join(f.stage, 'host/内容.txt'), 'half-written');
    assert.match(prepare(f), /^prepared\r?\n/);
    assert.deepEqual(await readFile(join(f.destination, 'host/内容.txt')), f.content);
  } finally { cleanup(f); }
  const corrupt = await preparationFixture();
  try {
    await seedPreparation(corrupt);
    await writeFile(join(corrupt.stage, 'preparation-manifest.json'), '{incomplete');
    assert.throws(() => prepare(corrupt));
    assert.equal(await readFile(join(corrupt.stage, 'preparation-manifest.json'), 'utf8'), '{incomplete');
    assert.equal(await readFile(join(corrupt.directory, 'activation.json'), 'utf8'), corrupt.activation);
    assert.equal((await readdir(corrupt.destination)).includes('core-manifest.json'), false);
  } finally { cleanup(corrupt); }
});

test('离线准备：同版不同清单、成品额外文件均拒绝，不覆盖或删旧内容', async () => {
  const f = await preparationFixture();
  try {
    prepare(f);
    const changed = JSON.parse(f.manifest.toString());
    changed.sourceCommit = 'different-source-probe';
    await writeFile(join(f.source, 'core-manifest.json'), JSON.stringify(changed));
    assert.throws(() => prepare(f));
    assert.deepEqual(await readFile(join(f.destination, 'core-manifest.json')), f.manifest);
    await writeFile(join(f.source, 'core-manifest.json'), f.manifest);
    await writeFile(join(f.destination, 'unknown-file'), 'do-not-delete');
    assert.throws(() => prepare(f));
    assert.equal(await readFile(join(f.destination, 'unknown-file'), 'utf8'), 'do-not-delete');
    assert.equal(await readFile(join(f.directory, 'activation.json'), 'utf8'), f.activation);
  } finally { cleanup(f); }
});

test('离线准备：硬链接半写目标拒绝截断，未知暂存文件拒绝清理', async () => {
  const f = await preparationFixture();
  try {
    await seedPreparation(f);
    const sentinel = join(f.directory, 'sentinel.txt');
    await writeFile(sentinel, 'preserve-sentinel');
    await link(sentinel, join(f.stage, 'host/内容.txt'));
    assert.throws(() => prepare(f));
    assert.equal(await readFile(sentinel, 'utf8'), 'preserve-sentinel');
    assert.equal((await readdir(f.destination)).includes('core-manifest.json'), false);
  } finally { cleanup(f); }
  const extra = await preparationFixture();
  try {
    await seedPreparation(extra);
    await writeFile(join(extra.stage, 'unknown-file'), 'do-not-delete');
    assert.throws(() => prepare(extra));
    assert.equal(await readFile(join(extra.stage, 'unknown-file'), 'utf8'), 'do-not-delete');
    assert.equal(await readFile(join(extra.directory, 'activation.json'), 'utf8'), extra.activation);
  } finally { cleanup(extra); }
});

async function boundaryProbe(mode, directory) {
  const probe = join(root, 'artifacts/dotnet/bin/SkylinesLink.ProcessProbe/debug/SkylinesLink.ProcessProbe.exe');
  const child = spawn(probe, [mode, directory], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  const exit = once(child, 'exit');
  const lines = createInterface({ input: child.stdout });
  const iterator = lines[Symbol.asyncIterator]();
  const read = async () => (await within(iterator.next())).value;
  try {
    assert.equal(await read(), 'held');
    return { child, read, stop: async () => { child.stdin.end(); await within(exit); lines.close(); } };
  } catch (error) { child.kill(); await exit; lines.close(); throw error; }
}

test('离线准备：发布清单时仍固定目录和载荷句柄，禁止并发替换或改写', async () => {
  const f = await preparationFixture();
  let probe;
  try {
    const directory = join(f.directory, 'guard-probe');
    await mkdir(directory);
    await writeFile(join(directory, 'payload.txt'), 'preserve-payload');
    probe = await boundaryProbe('preparation-boundary', directory);
    await assert.rejects(rename(directory, join(f.directory, 'replaced-guard')));
    await assert.rejects(writeFile(join(directory, 'preparation-manifest.json'), 'bad'));
    await assert.rejects(writeFile(join(directory, 'payload.txt'), 'bad'));
    probe.child.stdin.write('publish\n');
    assert.equal(await probe.read(), 'published');
    await assert.rejects(writeFile(join(directory, 'core-manifest.json'), 'bad'));
    await assert.rejects(writeFile(join(directory, 'payload.txt'), 'bad'));
    await probe.stop(); probe = undefined;
    assert.equal(await readFile(join(directory, 'core-manifest.json'), 'utf8'), 'manifest-probe');
    assert.equal(await readFile(join(directory, 'payload.txt'), 'utf8'), 'preserve-payload');
  } finally { if (probe) await probe.stop(); cleanup(f); }
});

test('离线准备：并行Setup明确失败，连接长租约不阻塞版本准备', async () => {
  const f = await preparationFixture();
  let holder;
  try {
    holder = await boundaryProbe('hold-file', join(f.directory, 'setup.lock'));
    assert.throws(() => prepare(f), error => /open-environment/.test(error.stderr));
    assert.equal(await readFile(join(f.directory, 'activation.json'), 'utf8'), f.activation);
    await holder.stop(); holder = undefined;
    await mkdir(join(f.directory, 'connection'));
    holder = await boundaryProbe('hold-file', join(f.directory, 'connection/authority.lock'));
    assert.match(prepare(f), /^prepared\r?\n/);
    assert.equal(await readFile(join(f.directory, 'activation.json'), 'utf8'), f.activation);
  } finally { if (holder) await holder.stop(); cleanup(f); }
});

test('显式激活：首次发布、同版幂等和已丢旧载荷的替换均可核实际活动值', { timeout: 20000 }, async () => {
  const f = await activationFixture();
  try {
    await unlink(join(f.directory, 'activation.json'));
    assert.match(prepare(f), /^prepared\r?\n/);
    await assert.rejects(readFile(join(f.directory, 'activation.json')), { code: 'ENOENT' });
    assert.match(activate(f), /^activated\r?\n/);
    const expected = activationRecord(f), published = await readFile(join(f.directory, 'activation.json'));
    assert.deepEqual(JSON.parse(published), expected);
    assert.deepEqual(activeCore(f), expected);
    assert.match(activate(f), /^already-active\r?\n/);
    assert.deepEqual(await readFile(join(f.directory, 'activation.json')), published);
    await assert.rejects(readFile(join(f.directory, 'activation.next')), { code: 'ENOENT' });
    assert.equal((await readdir(f.directory)).includes('connection'), false);

    await writeFile(join(f.directory, 'activation.json'), f.activation);
    await assert.rejects(readdir(join(f.directory, 'releases', 'prior-probe')), { code: 'ENOENT' });
    assert.match(activate(f), /^activated\r?\n/);
    assert.deepEqual(activeCore(f), expected, 'a valid old record does not require an intact old release to select the verified new core');
    await assert.rejects(readFile(join(f.directory, 'activation.next')), { code: 'ENOENT' });
  } finally { cleanup(f); }
});

test('显式激活：未准备、损坏和冲突不发布，也不把遗留槽当恢复任务', { timeout: 20000 }, async () => {
  const f = await activationFixture();
  const slot = 'uncommitted previous attempt';
  try {
    await writeFile(join(f.directory, 'activation.next'), slot);
    assert.throws(() => activate(f), error => error.status === 1 && /verify-prepared/.test(error.stderr));
    assert.equal(await readFile(join(f.directory, 'activation.json'), 'utf8'), f.activation);
    assert.equal(await readFile(join(f.directory, 'activation.next'), 'utf8'), slot);
    prepare(f);
    const payload = join(f.destination, 'host/host.mjs');
    await writeFile(payload, 'corrupted prepared payload');
    assert.throws(() => activate(f), error => error.status === 1 && /verify-prepared/.test(error.stderr));
    assert.equal(await readFile(payload, 'utf8'), 'corrupted prepared payload');
    assert.equal(await readFile(join(f.directory, 'activation.json'), 'utf8'), f.activation);
    await copyFile(join(f.source, 'host/host.mjs'), payload);

    const incomplete = JSON.parse(f.manifest.toString());
    incomplete.files = incomplete.files.filter(file => file.path !== 'desktop/setup/DOTNET.SOURCES.txt');
    await writeFile(join(f.source, 'core-manifest.json'), JSON.stringify(incomplete));
    assert.throws(() => activate(f), error => error.status === 1 && /verify-source/.test(error.stderr));
    assert.equal(await readFile(join(f.directory, 'activation.next'), 'utf8'), slot);
    await writeFile(join(f.source, 'core-manifest.json'), f.manifest);

    for (const changed of [{ ...activationRecord(f), coreManifestSha256: '2'.repeat(64) },
      { ...activationRecord(f), version: activationRecord(f).version.toUpperCase() }]) {
      const conflict = JSON.stringify(changed);
      await writeFile(join(f.directory, 'activation.json'), conflict);
      assert.throws(() => activate(f), error => error.status === 1 && /inspect-activation/.test(error.stderr));
      assert.equal(await readFile(join(f.directory, 'activation.json'), 'utf8'), conflict);
    }
    const corrupt = '{"incomplete":';
    await writeFile(join(f.directory, 'activation.json'), corrupt);
    assert.throws(() => activate(f), error => error.status === 1 && /inspect-activation/.test(error.stderr));
    assert.equal(await readFile(join(f.directory, 'activation.json'), 'utf8'), corrupt);
    assert.equal(await readFile(join(f.directory, 'activation.next'), 'utf8'), slot);
  } finally { cleanup(f); }
});

test('显式激活：按游戏绑定共享模式持只读pin，允许同版核验并阻止版本切换', { timeout: 20000 }, async () => {
  const f = await activationFixture();
  let holder;
  try {
    prepare(f); activate(f);
    const previous = await readFile(join(f.directory, 'activation.json')), expected = activationRecord(f);
    const leftover = 'partial activation scratch';
    await writeFile(join(f.directory, 'activation.next'), leftover);
    holder = await boundaryProbe('hold-read', join(f.directory, 'activation.json'));
    assert.match(activate(f), /^already-active\r?\n/);
    assert.deepEqual(await readFile(join(f.directory, 'activation.json')), previous);
    assert.equal(await readFile(join(f.directory, 'activation.next'), 'utf8'), leftover);

    const manifest = Buffer.from(JSON.stringify({ ...JSON.parse(f.manifest.toString()), version: '0.2.0-probe' }));
    const next = { ...f, manifest, stage: join(f.directory, 'releases', '0.2.0-probe'), destination: join(f.directory, 'releases', '0.2.0-probe') };
    await writeFile(join(next.source, 'core-manifest.json'), manifest);
    prepare(next);
    assert.throws(() => activate(next), error => error.status === 1 && /publish-activation/.test(error.stderr)
      && /发布已尝试/.test(error.stderr));
    assert.deepEqual(await readFile(join(f.directory, 'activation.json')), previous);
    assert.deepEqual(activeCore(f), expected);
    assert.deepEqual(JSON.parse(await readFile(join(f.directory, 'activation.next'), 'utf8')), activationRecord(next));
    await holder.stop(); holder = undefined;
    assert.deepEqual(activeCore(f), expected, 'releasing the pin does not apply a retained slot');
    assert.deepEqual(JSON.parse(await readFile(join(f.directory, 'activation.next'), 'utf8')), activationRecord(next));
    assert.match(activate(next), /^activated\r?\n/);
    assert.deepEqual(activeCore(next), activationRecord(next));
    await assert.rejects(readFile(join(f.directory, 'activation.next')), { code: 'ENOENT' });
  } finally { if (holder) await holder.stop(); cleanup(f); }
});

test('显式激活：安装锁和唯一工作槽拒绝占用、硬链接及目录，保留原对象', { timeout: 30000 }, async () => {
  for (const name of ['setup.lock', 'activation.next']) for (const kind of ['occupied', 'hardlink', 'directory']) {
    const f = await activationFixture();
    let holder;
    try {
      prepare(f);
      const path = join(f.directory, name), marker = `preserve ${name} ${kind}`;
      if (name === 'setup.lock') await unlink(path);
      const sentinel = join(f.directory, 'sentinel.txt');
      if (kind === 'occupied') {
        await writeFile(path, marker);
        holder = await boundaryProbe('hold-file', path);
      } else if (kind === 'hardlink') {
        await writeFile(sentinel, marker); await link(sentinel, path);
      } else await mkdir(path);
      const step = name === 'setup.lock' ? 'open-environment' : 'write-activation';
      assert.throws(() => activate(f), error => error.status === 1 && error.stderr.includes(step), `${name} ${kind}`);
      if (holder) { await holder.stop(); holder = undefined; }
      assert.equal(await readFile(join(f.directory, 'activation.json'), 'utf8'), f.activation);
      if (kind === 'directory') assert.deepEqual(await readdir(path), []);
      else assert.equal(await readFile(path, 'utf8'), marker);
      if (kind === 'hardlink') assert.equal(await readFile(sentinel, 'utf8'), marker);
    } finally { if (holder) await holder.stop(); cleanup(f); }
  }
});

test('稳定入口：已准备Core首装与幂等，不激活核心或创建授权', { timeout: 20000 }, async () => {
  const f = await activationFixture();
  const target = join(f.directory, 'launcher/SkylinesLink.Launcher.exe');
  const slot = join(f.directory, 'launcher/launcher.next');
  try {
    const manifest = JSON.parse(f.manifest.toString());
    manifest.files.find(file => file.path === 'desktop/launcher/SkylinesLink.Launcher.exe').path =
      'desktop/launcher/skylineslink.launcher.exe';
    f.manifest = Buffer.from(JSON.stringify(manifest));
    await writeFile(join(f.source, 'core-manifest.json'), f.manifest);
    assert.throws(() => installLauncher(f), error => error.status === 1 && /verify-prepared/.test(error.stderr));
    prepare(f);
    const before = (await readdir(f.directory)).sort();
    assert.equal(launcherStatus(f), 'not-installed');
    assert.deepEqual((await readdir(f.directory)).sort(), before, 'read-only inspection does not create the stable directory');
    assert.match(installLauncher(f), /^installed\r?\n/);
    const bytes = await readFile(join(f.source, 'desktop/launcher/SkylinesLink.Launcher.exe'));
    assert.deepEqual(await readFile(target), bytes);
    assert.equal(launcherStatus(f), 'matches-core');
    await assert.rejects(readFile(slot), { code: 'ENOENT' });
    await writeFile(slot, 'inert old partial slot');
    assert.match(installLauncher(f), /^already-installed\r?\n/);
    assert.deepEqual(await readFile(target), bytes);
    assert.equal(await readFile(slot, 'utf8'), 'inert old partial slot', 'equal installation does not resume an old work slot');
    assert.equal(await readFile(join(f.directory, 'activation.json'), 'utf8'), f.activation);
    assert.equal((await readdir(f.directory)).includes('connection'), false);
  } finally { cleanup(f); }
});

test('稳定入口：同版来源与准备集不符时拒绝，保留既有入口和未知文件', { timeout: 20000 }, async () => {
  const f = await activationFixture();
  const target = join(f.directory, 'launcher/SkylinesLink.Launcher.exe');
  try {
    prepare(f); installLauncher(f);
    const bytes = await readFile(target);
    const unknown = join(f.directory, 'launcher/user-note.txt');
    await writeFile(unknown, 'preserve this unowned note');
    const conflict = await launcherCandidate(f, JSON.parse(f.manifest.toString()).version, Buffer.concat([bytes, Buffer.from('different-build')]));
    assert.throws(() => installLauncher(conflict), error => error.status === 1 && /verify-prepared/.test(error.stderr));
    assert.deepEqual(await readFile(target), bytes);
    assert.equal(await readFile(unknown, 'utf8'), 'preserve this unowned note');
    await writeFile(join(f.source, 'desktop/launcher/SkylinesLink.Launcher.exe'), bytes);
    await writeFile(join(f.source, 'core-manifest.json'), f.manifest);
    const prepared = join(f.destination, 'desktop/launcher/SkylinesLink.Launcher.exe');
    await writeFile(prepared, 'damaged prepared payload');
    assert.throws(() => installLauncher(f), error => error.status === 1 && /verify-prepared/.test(error.stderr));
    assert.equal(await readFile(prepared, 'utf8'), 'damaged prepared payload', 'installation never repairs a conflicting prepared release');
    assert.deepEqual(await readFile(target), bytes);
    assert.equal(await readFile(unknown, 'utf8'), 'preserve this unowned note');
    assert.equal(await readFile(join(f.directory, 'activation.json'), 'utf8'), f.activation);
    assert.equal((await readdir(f.directory)).includes('connection'), false);
  } finally { cleanup(f); }
});

test('稳定入口：只读pin阻止发布，唯一工作槽仅经显式重试重写', { timeout: 30000 }, async () => {
  const f = await activationFixture();
  const target = join(f.directory, 'launcher/SkylinesLink.Launcher.exe');
  const slot = join(f.directory, 'launcher/launcher.next');
  let holder;
  try {
    prepare(f); installLauncher(f);
    const old = await readFile(target);
    await writeFile(slot, 'half-written inert work');
    holder = await boundaryProbe('hold-read', target);
    assert.match(installLauncher(f), /^already-installed\r?\n/);
    assert.equal(await readFile(slot, 'utf8'), 'half-written inert work');
    const payload = Buffer.from('Synthetic new-version launcher bytes, never executed.\n');
    const next = await launcherCandidate(f, '0.2.0-probe', payload);
    prepare(next);
    assert.equal(launcherStatus(next), 'different');
    assert.throws(() => installLauncher(next), error => error.status === 1 && /publish-launcher/.test(error.stderr));
    assert.deepEqual(await readFile(target), old);
    assert.deepEqual(await readFile(slot), payload, 'a real failed publication leaves the one flushed candidate slot');
    await holder.stop(); holder = undefined;
    assert.equal(launcherStatus(next), 'different', 'releasing a reader does not automatically publish its retained slot');
    assert.deepEqual(await readFile(target), old);
    holder = await boundaryProbe('hold-file', join(f.directory, 'setup.lock'));
    assert.throws(() => installLauncher(next), error => error.status === 1 && /open-environment/.test(error.stderr));
    assert.deepEqual(await readFile(slot), payload);
    await holder.stop(); holder = undefined;
    await unlink(slot);
    const sentinel = join(f.directory, 'launcher-slot-sentinel.txt');
    await writeFile(sentinel, 'preserve linked sentinel'); await link(sentinel, slot);
    assert.throws(() => installLauncher(next), error => error.status === 1 && /write-launcher/.test(error.stderr));
    assert.equal(await readFile(sentinel, 'utf8'), 'preserve linked sentinel');
    await unlink(slot); await mkdir(slot);
    assert.throws(() => installLauncher(next), error => error.status === 1 && /write-launcher/.test(error.stderr));
    assert.deepEqual(await readdir(slot), []);
    await rmdir(slot); await writeFile(slot, 'partial interrupted copy');
    assert.match(installLauncher(next), /^installed\r?\n/);
    assert.deepEqual(await readFile(target), payload);
    assert.equal(launcherStatus(next), 'matches-core');
    await assert.rejects(readFile(slot), { code: 'ENOENT' });
    assert.equal(await readFile(join(f.directory, 'activation.json'), 'utf8'), f.activation);
    assert.equal((await readdir(f.directory)).includes('connection'), false);
  } finally { if (holder) await holder.stop(); cleanup(f); }
});

test('稳定入口NativeAOT实进程：运行中拒绝替换，EOF收束后显式更新并重启', {
  timeout: 60000,
  skip: launcherKind !== 'nativeaot' ? '稳定入口仅安装单文件NativeAOT；当前managed测试形态明确跳过实进程安装' : false,
}, async () => {
  const f = await activationFixture({ nativeLauncher: launcher });
  const target = join(f.directory, 'launcher/SkylinesLink.Launcher.exe');
  const slot = join(f.directory, 'launcher/launcher.next');
  let run;
  try {
    prepare(f); activate(f); installLauncher(f);
    const active = await readFile(join(f.directory, 'activation.json'));
    // Only this owned fixture's readiness file is removed; a previous PID can
    // never be treated as proof that the next stable-entry process is ready.
    await unlink(join(f.directory, 'fixture-ready.json')).catch(error => { if (error.code !== 'ENOENT') throw error; });
    run = start({ ...f, core: f.destination, manifestSha256: hash(f.manifest) }, { entryPath: target });
    const ready = await run.ready;
    assert.deepEqual(ready.args.slice(0, 8), ['--transport', 'stdio', '--profile', 'Probe.With_Id', '--environment', f.environment,
      '--core-manifest-sha256', hash(f.manifest)]);
    // This case measures installation, execution-image occupancy and restart.
    // The separate full-chain tests qualify the current history-reader ABI;
    // an explicitly supplied historical AOT input is not a current product Core.
    assert.match(installLauncher(f), /^already-installed\r?\n/);
    const original = await readFile(target);
    // An overlay byte changes only this fixture's copied executable. Its build
    // identity remains historical; this is an update-mechanism probe, not a new product build.
    const payload = Buffer.concat([original, Buffer.from([0x53])]);
    const next = await launcherCandidate(f, '0.2.0-probe', payload);
    prepare(next);
    assert.throws(() => installLauncher(next), error => error.status === 1 && /publish-launcher/.test(error.stderr));
    assert.deepEqual(await readFile(target), original);
    assert.deepEqual(await readFile(slot), payload);
    assert.equal(launcherStatus(next), 'different');
    process.kill(ready.pid, 0); process.kill(ready.grandchild, 0);
    const bytes = Buffer.from([0, 10, 13, 0xe4, 0xb8, 0xad, 0xff]);
    run.child.stdin.end(bytes);
    assert.equal((await within(run.exit))[0], 23);
    assert.deepEqual(Buffer.concat(run.output), bytes, 'the pinned old session remains usable after a refused update');
    await requireGone(ready.pid); await requireGone(ready.grandchild);
    run = undefined;
    assert.equal(launcherStatus(next), 'different', 'ending the process does not apply a retained launcher slot');
    assert.match(installLauncher(next), /^installed\r?\n/);
    assert.equal(launcherStatus(next), 'matches-core');
    assert.deepEqual(await readFile(target), payload);
    await assert.rejects(readFile(slot), { code: 'ENOENT' });
    assert.deepEqual(await readFile(join(f.directory, 'activation.json')), active, 'stable entry installation does not switch the active Core');
    activate(next);
    await unlink(join(f.directory, 'fixture-ready.json'));
    run = start({ ...next, core: next.destination, manifestSha256: hash(next.manifest) }, { entryPath: target });
    const restarted = await run.ready;
    assert.equal(restarted.args[7], hash(next.manifest));
    const echo = Buffer.from('stable fixture restarted through explicit Core activation\n');
    run.child.stdin.end(echo);
    assert.equal((await within(run.exit))[0], 23);
    assert.deepEqual(Buffer.concat(run.output), echo);
    await requireGone(restarted.pid); await requireGone(restarted.grandchild);
    run = undefined;
    assert.equal((await readdir(f.directory)).includes('connection'), false);
  } finally {
    if (run) {
      if (run.child.exitCode === null && run.child.signalCode === null) run.child.kill();
      await within(run.exit);
    }
    cleanup(f);
  }
});

test('首次初始化：首装、幂等、同活动值修复入口与已准备目录作为来源', { timeout: 30000 }, async () => {
  const f = await activationFixture();
  const record = join(f.directory, 'activation.json');
  const target = join(f.directory, 'launcher/SkylinesLink.Launcher.exe');
  try {
    await unlink(record);
    assert.equal((await readdir(f.directory)).includes('releases'), false);
    assert.match(initializeCore(f), /^initialized\r?\n/);
    assert.deepEqual(activeCore(f), activationRecord(f));
    assert.equal(launcherStatus(f), 'matches-core');
    const active = await readFile(record), entry = await readFile(target);
    const payloads = ['core-manifest.json', ...JSON.parse(f.manifest.toString()).files.map(file => file.path)];
    const prepared = await Promise.all(payloads.map(path => readFile(join(f.destination, path))));
    assert.match(initializeCore(f), /^already-initialized\r?\n/);
    assert.deepEqual(await readFile(record), active);
    assert.deepEqual(await readFile(target), entry);
    await unlink(target);
    assert.match(initializeCore(f), /^entry-repaired\r?\n/);
    assert.deepEqual(await readFile(record), active, 'repairing the missing entry never republishes the matching active record');
    assert.deepEqual(await readFile(target), entry);
    await unlink(record);
    assert.match(initializeCore({ ...f, source: f.destination }), /^initialized\r?\n/);
    for (let i = 0; i < payloads.length; i++)
      assert.deepEqual(await readFile(join(f.destination, payloads[i])), prepared[i], 'source equal to prepared destination is never self-truncated');
    assert.deepEqual(activeCore(f), activationRecord(f));
    await assert.rejects(readFile(join(f.directory, 'activation.next')), { code: 'ENOENT' });
    await assert.rejects(readFile(join(f.directory, 'launcher/launcher.next')), { code: 'ENOENT' });
    const damaged = join(f.destination, 'desktop/setup/SkylinesLink.Setup.exe');
    const bytes = await readFile(damaged); bytes[0] ^= 1; await writeFile(damaged, bytes);
    const beforeFailure = await readFile(record);
    assert.throws(() => initializeCore(f), error => error.status === 1 && /verify-prepared/.test(error.stderr) &&
      /本次未尝试发布固定入口/.test(error.stderr) && /本次未尝试发布核心激活记录/.test(error.stderr));
    assert.deepEqual(await readFile(record), beforeFailure);
    assert.deepEqual(await readFile(target), entry);
    assert.deepEqual(await readFile(damaged), bytes, 'a matching active tuple cannot excuse or silently repair corrupt prepared bytes');
    assert.equal((await readdir(f.directory)).includes('connection'), false);
  } finally { cleanup(f); }
});

test('首次初始化：不同、损坏、目录或硬链接活动记录先拒，保留所有业务文件', { timeout: 30000 }, async () => {
  for (const kind of ['different', 'same-version-other-hash', 'bad-json', 'directory', 'hardlink']) {
    const f = await activationFixture();
    const record = join(f.directory, 'activation.json');
    const target = join(f.directory, 'launcher/SkylinesLink.Launcher.exe');
    const slot = join(f.directory, 'launcher/launcher.next');
    try {
      let content = f.activation;
      let sentinel;
      if (kind === 'same-version-other-hash') content = JSON.stringify({ ...activationRecord(f), coreManifestSha256: '0'.repeat(64) });
      else if (kind === 'bad-json') content = '{unfinished';
      if (kind === 'directory') { await unlink(record); await mkdir(record); }
      else if (kind === 'hardlink') {
        sentinel = join(f.directory, 'activation-sentinel.json');
        content = JSON.stringify(activationRecord(f));
        await writeFile(sentinel, content); await unlink(record); await link(sentinel, record);
      } else await writeFile(record, content);
      await mkdir(dirname(target)); await writeFile(target, 'preserve stable entry'); await writeFile(slot, 'preserve inert slot');
      assert.throws(() => initializeCore(f), error => error.status === 1 && /inspect-existing-activation/.test(error.stderr) &&
        /本次未尝试发布固定入口/.test(error.stderr) && /本次未尝试发布核心激活记录/.test(error.stderr), kind);
      if (kind === 'directory') assert.deepEqual(await readdir(record), []);
      else assert.equal(await readFile(record, 'utf8'), content);
      if (sentinel) assert.equal(await readFile(sentinel, 'utf8'), content);
      assert.equal(await readFile(target, 'utf8'), 'preserve stable entry');
      assert.equal(await readFile(slot, 'utf8'), 'preserve inert slot');
      assert.equal((await readdir(f.directory)).includes('releases'), false, 'early refusal creates no requested release tree');
      assert.equal((await readdir(f.directory)).includes('connection'), false);
    } finally { cleanup(f); }
  }
});

test('首次初始化：入口发布或激活工作槽失败保留真实阶段，释放后仅显式重试完成', { timeout: 30000 }, async () => {
  for (const point of ['entry-publication', 'activation-slot']) {
    const f = await activationFixture();
    const record = join(f.directory, 'activation.json');
    const target = join(f.directory, 'launcher/SkylinesLink.Launcher.exe');
    const entrySlot = join(f.directory, 'launcher/launcher.next');
    const activationSlot = join(f.directory, 'activation.next');
    let holder;
    try {
      await unlink(record);
      const expected = await readFile(join(f.source, 'desktop/launcher/SkylinesLink.Launcher.exe'));
      if (point === 'entry-publication') {
        await mkdir(dirname(target)); await writeFile(target, 'old incomplete entry');
        holder = await boundaryProbe('hold-read', target);
      } else {
        await writeFile(activationSlot, 'occupied activation slot');
        holder = await boundaryProbe('hold-file', activationSlot);
      }
      const step = point === 'entry-publication' ? /publish-launcher/ : /write-activation/;
      assert.throws(() => initializeCore(f), error => error.status === 1 && step.test(error.stderr) &&
        /固定入口发布已尝试/.test(error.stderr) && /本次未尝试发布核心激活记录/.test(error.stderr));
      execFileSync(setup, ['verify-core', f.destination], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
      await assert.rejects(readFile(record), { code: 'ENOENT' });
      if (point === 'entry-publication') {
        assert.equal(await readFile(target, 'utf8'), 'old incomplete entry');
        assert.deepEqual(await readFile(entrySlot), expected);
      } else assert.deepEqual(await readFile(target), expected, 'the entry really published before activation slot admission failed');
      await holder.stop(); holder = undefined;
      await assert.rejects(readFile(record), { code: 'ENOENT' });
      if (point === 'activation-slot') assert.equal(await readFile(activationSlot, 'utf8'), 'occupied activation slot');
      assert.match(initializeCore(f), /^initialized\r?\n/);
      assert.deepEqual(activeCore(f), activationRecord(f));
      assert.equal(launcherStatus(f), 'matches-core');
      await assert.rejects(readFile(entrySlot), { code: 'ENOENT' });
      await assert.rejects(readFile(activationSlot), { code: 'ENOENT' });
      assert.equal((await readdir(f.directory)).includes('connection'), false);
    } finally { if (holder) await holder.stop(); cleanup(f); }
  }
});

test('首次初始化：锁争用不写业务，后来选中的不同核心不能被旧请求重试回退', { timeout: 30000 }, async () => {
  const f = await activationFixture();
  const record = join(f.directory, 'activation.json');
  const target = join(f.directory, 'launcher/SkylinesLink.Launcher.exe');
  const slot = join(f.directory, 'launcher/launcher.next');
  let holder;
  try {
    await unlink(record);
    holder = await boundaryProbe('hold-file', join(f.directory, 'setup.lock'));
    assert.throws(() => initializeCore(f), error => error.status === 1 && /open-environment/.test(error.stderr) &&
      /本次未尝试发布固定入口/.test(error.stderr) && /本次未尝试发布核心激活记录/.test(error.stderr));
    const busyFiles = await readdir(f.directory);
    assert.equal(busyFiles.includes('releases'), false); assert.equal(busyFiles.includes('launcher'), false);
    assert.equal(busyFiles.includes('activation.json'), false); assert.equal(busyFiles.includes('connection'), false);
    await holder.stop(); holder = undefined;
    const original = await readFile(join(f.source, 'desktop/launcher/SkylinesLink.Launcher.exe'));
    const later = await launcherCandidate(f, '0.2.0-probe', Buffer.concat([original, Buffer.from('later-selection')]));
    assert.match(initializeCore(later), /^initialized\r?\n/);
    const selected = await readFile(record), installed = await readFile(target);
    await writeFile(join(f.source, 'desktop/launcher/SkylinesLink.Launcher.exe'), original);
    await writeFile(join(f.source, 'core-manifest.json'), f.manifest);
    await writeFile(slot, 'old inert retry must not win');
    assert.throws(() => initializeCore(f), error => error.status === 1 && /inspect-existing-activation/.test(error.stderr) &&
      /本次未尝试发布固定入口/.test(error.stderr) && /本次未尝试发布核心激活记录/.test(error.stderr));
    assert.deepEqual(await readFile(record), selected);
    assert.deepEqual(activeCore(f), activationRecord(later));
    assert.deepEqual(await readFile(target), installed);
    assert.equal(await readFile(slot, 'utf8'), 'old inert retry must not win');
    assert.deepEqual(await readdir(join(f.directory, 'releases')), ['0.2.0-probe']);
    assert.equal((await readdir(f.directory)).includes('connection'), false);
  } finally { if (holder) await holder.stop(); cleanup(f); }
});
