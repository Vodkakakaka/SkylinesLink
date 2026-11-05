import { spawn, execFileSync } from 'node:child_process';
import { randomBytes, createHash } from 'node:crypto';
import { readFile, writeFile, readdir, access } from 'node:fs/promises';
import { resolve, join, isAbsolute, relative } from 'node:path';
import { parseArgs } from 'node:util';
import { StringDecoder } from 'node:string_decoder';
import assert from 'node:assert/strict';

const usage = `Manual Windows Codex MCP check (no model turn or game).

node tests/integration/clients/codex-readonly.mjs
  --client-bin <absolute-codex.exe>
  --expected-client-version <exact-version-output>
  --core-root <absolute-prepared-core-source>
  --expected-manifest-sha256 <64-lowercase-hex>
  --expected-source-commit <40-lowercase-hex>
  --candidate-kind <managed|nativeaot>
  --runtime-probe <absolute-SkylinesLink.RuntimeProbe.exe>
  [--pwsh-bin <absolute-pwsh.exe>]

Requires an existing reviewed candidate; does not build, install, or fetch a client.
Creates one owned environment under the actual Windows user profile.
Use --help to print this text without creating files or starting a process.
`;
let options;
try {
  ({ values: options } = parseArgs({ strict: true, allowPositionals: false, options: {
    help: { type: 'boolean' },
    'client-bin': { type: 'string' }, 'expected-client-version': { type: 'string' },
    'core-root': { type: 'string' }, 'expected-manifest-sha256': { type: 'string' },
    'expected-source-commit': { type: 'string' }, 'candidate-kind': { type: 'string' },
    'runtime-probe': { type: 'string' }, 'pwsh-bin': { type: 'string' },
  } }));
  if (options.help) { console.log(usage); process.exit(0); }
  for (const name of ['client-bin', 'expected-client-version', 'core-root', 'expected-manifest-sha256', 'expected-source-commit', 'candidate-kind', 'runtime-probe'])
    assert.ok(typeof options[name] === 'string' && options[name].length > 0);
  for (const name of ['client-bin', 'core-root', 'runtime-probe']) assert.ok(isAbsolute(options[name]));
  if (options['pwsh-bin'] !== undefined) assert.ok(isAbsolute(options['pwsh-bin']));
  assert.match(options['expected-manifest-sha256'], /^[a-f0-9]{64}$/);
  assert.match(options['expected-source-commit'], /^[a-f0-9]{40}$/);
  assert.match(options['expected-client-version'], /^codex-cli [^\r\n]{1,128}$/);
  assert.ok(['managed', 'nativeaot'].includes(options['candidate-kind']));
  assert.equal(process.platform, 'win32');
} catch {
  // Never echo unknown arguments; they might contain data that does not belong in a report.
  console.error('Invalid arguments or unsupported platform. Use --help.');
  process.exit(2);
}
const core = resolve(options['core-root']);
const expectedCommit = options['expected-source-commit'];
const expectedHash = options['expected-manifest-sha256'];
const codex = resolve(options['client-bin']);
const expectedVersion = options['expected-client-version'];
const runtimeExe = resolve(options['runtime-probe']);
const pwsh = options['pwsh-bin'] ?? 'pwsh.exe';
const helper = join(import.meta.dirname, 'fixture.ps1');
const environment = 'probe-' + randomBytes(12).toString('hex');
const profile = 'Codex.ReadOnly';
const internalToken = randomBytes(32).toString('hex');
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const normalize = path => resolve(path).toLowerCase();
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const metadata = { candidateCommit: expectedCommit, candidateKindDeclared: options['candidate-kind'], candidateManifestSha256: expectedHash, clientVersion: expectedVersion, modelTurnSent: false, gameStarted: false, passed: false, stage: 'preflight', ownedEnvironment: environment };
let fixture, runtime, app, appExitedNormally = false, runtimeStopped = false;
let knownProductProcesses = [];
let failure;

function cleanEnvironment() {
  const clean = {};
  // Read only documented OS path values, never enumerate/copy credential variables.
  for (const key of ['SystemRoot', 'WINDIR', 'SystemDrive', 'TEMP', 'TMP', 'PATH', 'PATHEXT', 'COMSPEC', 'PROCESSOR_ARCHITECTURE', 'NUMBER_OF_PROCESSORS'])
    if (process.env[key] !== undefined) clean[key] = process.env[key];
  if (fixture) { clean.USERPROFILE = fixture.profileRoot; clean.CODEX_HOME = join(fixture.environmentRoot, 'codex-home'); }
  return clean;
}
async function within(promise, milliseconds, stage) {
  let timer;
  try { return await Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(stage)), milliseconds); })]); }
  finally { clearTimeout(timer); }
}
function ownProcess(executable, args, options = {}) {
  const child = spawn(executable, args, { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'], env: cleanEnvironment(), ...options });
  const exit = new Promise(resolve => { child.once('exit', (code, signal) => resolve({ code, signal })); child.once('error', () => resolve({ code: null, signal: 'spawn-error' })); });
  const closed = new Promise(resolve => child.once('close', (code, signal) => resolve({ code, signal })));
  const waiting = [], lines = [];
  const decoder = new StringDecoder('utf8');
  const stderrDecoder = new StringDecoder('utf8');
  let buffered = '', stderrTail = '', stdoutBytes = 0, stderrBytes = 0, ended = false, streamFailure;
  const fail = () => {
    if (streamFailure) return;
    streamFailure = new Error('child-output-budget');
    for (const entry of waiting.splice(0)) entry.reject(streamFailure);
    try { child.stdin.end(); } catch { /* Cleanup still owns the process. */ }
  };
  child.stderr.on('data', bytes => {
    stderrBytes += bytes.length;
    const inspected = stderrTail + stderrDecoder.write(bytes);
    if (stderrBytes > 65536 || inspected.includes(internalToken)) fail();
    stderrTail = inspected.slice(-internalToken.length + 1);
  });
  child.stdout.on('data', bytes => {
    if (streamFailure) return;
    stdoutBytes += bytes.length;
    if (stdoutBytes > 1048576) { fail(); return; }
    buffered += decoder.write(bytes);
    if (Buffer.byteLength(buffered) > 1048576) { fail(); return; }
    for (let newline = buffered.indexOf('\n'); newline >= 0; newline = buffered.indexOf('\n')) {
      const line = buffered.slice(0, newline).replace(/\r$/, ''); buffered = buffered.slice(newline + 1);
      if (line.includes(internalToken)) { fail(); return; }
      if (waiting.length) waiting.shift().resolve(line); else lines.push(line);
      if (lines.length > 256) { fail(); return; }
    }
  });
  child.stdout.on('end', () => {
    buffered += decoder.end();
    if (buffered.length !== 0) fail();
    ended = true;
    for (const entry of waiting.splice(0)) entry.reject(new Error('child-output-ended'));
  });
  child.stdout.on('error', fail); child.stderr.on('error', fail);
  child.stdin.on('error', () => fail());
  return {
    child, exit, closed,
    next: () => streamFailure ? Promise.reject(streamFailure) : lines.length ? Promise.resolve(lines.shift()) : ended ? Promise.reject(new Error('child-output-ended')) : new Promise((resolve, reject) => waiting.push({ resolve, reject })),
    send: value => child.stdin.write(typeof value === 'string' ? value + '\n' : JSON.stringify(value) + '\n'),
    stderrBytes: () => stderrBytes,
    outputAccepted: () => ended && streamFailure === undefined,
  };
}
function inspectProcesses() {
  return JSON.parse(execFileSync(pwsh, ['-NoProfile', '-NonInteractive', '-File', helper, '-Mode', 'inspect', '-EnvironmentName', environment], { windowsHide: true, encoding: 'utf8', timeout: 10000, env: cleanEnvironment() })).processes;
}
async function runtimeCommand(value) {
  runtime.send(value);
  return JSON.parse(await within(runtime.next(), 5000, 'runtime-response-timeout'));
}
let rpcId = 0;
async function rpc(method, params) {
  const allowed = ['initialize', 'config/read', 'thread/start', 'mcpServerStatus/list', 'mcpServer/tool/call'];
  assert.ok(allowed.includes(method));
  const id = ++rpcId;
  app.send({ id, method, params });
  return await within((async () => {
    while (true) {
      const message = JSON.parse(await app.next());
      if (message.id === id) {
        if (message.error) {
          metadata.failedRpc = method;
          metadata.rpcErrorCode = Number.isSafeInteger(message.error.code) ? message.error.code : null;
          throw new Error('rpc-' + method.replaceAll('/', '-') + '-failed');
        }
        return message.result;
      }
      // No prompt, login, permission, model or shell request is ever answered automatically.
      if (message.id !== undefined || /^turn\//.test(message.method ?? '')) throw new Error('unexpected-client-request-or-turn');
    }
  })(), 15000, 'rpc-' + method.replaceAll('/', '-') + '-timeout');
}

try {
  const manifestBytes = await readFile(join(core, 'core-manifest.json'));
  assert.equal(hash(manifestBytes), expectedHash);
  const manifest = JSON.parse(manifestBytes.toString('utf8'));
  assert.equal(manifest.sourceCommit, expectedCommit); assert.equal(typeof manifest.version, 'string'); assert.ok(Array.isArray(manifest.files));
  metadata.candidateVersion = manifest.version;
  await access(runtimeExe);
  metadata.runtimeProbeSha256 = hash(await readFile(runtimeExe));
  fixture = JSON.parse(execFileSync(pwsh, ['-NoProfile', '-NonInteractive', '-File', helper, '-Mode', 'prepare', '-EnvironmentName', environment], { windowsHide: true, encoding: 'utf8', env: cleanEnvironment(), timeout: 10000 }));
  metadata.defaultUserConfigPresent = fixture.defaultUserConfigPresent;
  assert.equal(execFileSync(codex, ['--version'], { windowsHide: true, encoding: 'utf8', env: cleanEnvironment(), cwd: join(fixture.environmentRoot, 'codex-cwd'), timeout: 5000 }).trim(), expectedVersion);
  metadata.stage = 'prepare-core';
  const setup = join(core, 'desktop/setup/SkylinesLink.Setup.exe');
  const setupRecord = manifest.files.filter(file => file.path === 'desktop/setup/SkylinesLink.Setup.exe');
  assert.equal(setupRecord.length, 1);
  const setupBytes = await readFile(setup);
  assert.equal(setupBytes.length, setupRecord[0].size); assert.equal(hash(setupBytes), setupRecord[0].sha256);
  execFileSync(setup, ['prepare-core', core, environment], { windowsHide: true, encoding: 'utf8', env: cleanEnvironment(), timeout: 30000 });
  const prepared = join(fixture.environmentRoot, 'releases', manifest.version);
  assert.equal(hash(await readFile(join(prepared, 'core-manifest.json'))), expectedHash);
  await writeFile(join(fixture.environmentRoot, 'activation.json'), JSON.stringify({ productId: 'skylineslink', version: manifest.version, coreManifestSha256: expectedHash }), { flag: 'wx' });
  const launcher = join(prepared, 'desktop/launcher/SkylinesLink.Launcher.exe');
  runtime = ownProcess(runtimeExe, ['--serve'], { cwd: fixture.environmentRoot });
  metadata.runtimeProbePid = runtime.child.pid;
  const ready = await runtimeCommand({ initialize: true, operationProbe: false, internalToken, externalHttpToken: null, profileId: profile, environmentId: environment,
    coreManifestSha256: expectedHash, privateDirectory: join(fixture.environmentRoot, 'connection'), deadlineMilliseconds: 5000 });
  assert.equal(ready.kind, 'ready');
  metadata.stage = 'start-private-app-server';
  const home = join(fixture.environmentRoot, 'codex-home'), cwd = join(fixture.environmentRoot, 'codex-cwd');
  const sqlite = join(fixture.environmentRoot, 'codex-sqlite'), logs = join(fixture.environmentRoot, 'codex-logs');
  const config = { cli_auth_credentials_store: 'file', sqlite_home: sqlite, log_dir: logs, project_root_markers: [], history: { persistence: 'none' }, analytics: { enabled: false }, features: { apps: false, plugins: false } };
  const overrides = ['-c', 'cli_auth_credentials_store="file"', '-c', `sqlite_home=${JSON.stringify(sqlite)}`, '-c', `log_dir=${JSON.stringify(logs)}`, '-c', 'project_root_markers=[]', '-c', 'history.persistence="none"', '-c', 'analytics.enabled=false', '--disable', 'apps', '--disable', 'plugins'];
  app = ownProcess(codex, ['app-server', '--listen', 'stdio://', '--strict-config', ...overrides], { cwd, env: { ...cleanEnvironment(), CODEX_HOME: home } });
  metadata.appServerPid = app.child.pid;
  const initialized = await rpc('initialize', { clientInfo: { name: 'skylineslink_isolated_probe', version: '0.1.0' }, capabilities: { experimentalApi: false } });
  assert.equal(normalize(initialized.codexHome), normalize(home));
  app.send({ method: 'initialized' });
  const effective = (await rpc('config/read', { cwd, includeLayers: false })).config;
  assert.equal(normalize(effective.sqlite_home), normalize(sqlite)); assert.equal(normalize(effective.log_dir), normalize(logs));
  assert.equal(effective.cli_auth_credentials_store, 'file'); assert.equal(effective.features.apps, false); assert.equal(effective.features.plugins, false);
  assert.equal(effective.analytics.enabled, false);
  assert.deepEqual(effective.project_root_markers, []);
  assert.deepEqual(effective.mcp_servers ?? {}, {});
  const servers = { skylineslink_probe: { command: launcher, args: ['--profile', profile, '--environment', environment], enabled: true, required: true,
    enabled_tools: ['skylines_context', 'skylines_operation_get'], startup_timeout_sec: 15, tool_timeout_sec: 10 } };
  metadata.stage = 'private-thread';
  const started = await rpc('thread/start', { cwd, ephemeral: true, sandbox: 'read-only', approvalPolicy: 'never', config: { ...config, mcp_servers: servers } });
  assert.equal(started.thread.ephemeral, true); assert.equal(typeof started.thread.id, 'string');
  if (started.instructionSources) assert.deepEqual(started.instructionSources, []);
  const threadId = started.thread.id;
  const inventory = await rpc('mcpServerStatus/list', { threadId, detail: 'toolsAndAuthOnly', limit: 100 });
  assert.equal(inventory.data.length, 1); assert.equal(inventory.data[0].name, 'skylineslink_probe');
  const toolNames = Object.values(inventory.data[0].tools).map(tool => tool.name).sort();
  assert.deepEqual(toolNames, ['skylines_context', 'skylines_operation_get']);
  assert.ok(inventory.nextCursor === null || inventory.nextCursor === undefined);
  const lazy = await runtimeCommand('metrics'); assert.equal(lazy.connected, 0); assert.equal(lazy.authenticated, 0);
  const call = (tool, args) => rpc('mcpServer/tool/call', { threadId, server: 'skylineslink_probe', tool, arguments: args });
  metadata.stage = 'read-context';
  const context = await call('skylines_context', {});
  assert.notEqual(context.isError, true); assert.equal(context.structuredContent.instanceId, ready.instanceId); assert.equal(context.structuredContent.gameState, 'menu');
  metadata.stage = 'read-operation';
  const operation = await call('skylines_operation_get', { selector: { kind: 'operationId', operationId: 'isolated-unknown-operation' } });
  assert.notEqual(operation.isError, true); assert.deepEqual(operation.structuredContent, { lookup: { kind: 'absent' } });
  knownProductProcesses = inspectProcesses();
  assert.equal(knownProductProcesses.filter(p => normalize(p.path) === normalize(launcher)).length, 1);
  assert.equal(knownProductProcesses.filter(p => normalize(p.path) === normalize(join(prepared, 'runtime/node.exe'))).length, 1);
  metadata.stage = 'revoke';
  assert.equal((await runtimeCommand('revoke')).kind, 'revoked');
  const rejected = await call('skylines_context', {});
  assert.equal(rejected.isError, true); assert.equal(rejected.structuredContent.error.code, 'UNAUTHORIZED');
  metadata.stage = 'close-app-server';
} catch (error) {
  // No exception text/config/token or arbitrary server message is persisted.
  failure = error instanceof Error ? error.name : 'UnknownError';
} finally {
  if (app) {
    try { app.child.stdin.end(); } catch { failure ??= 'AppServerStdinCloseFailed'; }
    try { const exit = await within(app.closed, 10000, 'app-close-timeout'); appExitedNormally = exit.code === 0 && exit.signal === null; }
    catch { app.child.kill(); try { await within(app.closed, 5000, 'app-killed-close-timeout'); } catch { failure ??= 'AppServerStreamsRemain'; } }
    if (!appExitedNormally) failure ??= 'AppServerDidNotExitNormally';
    if (!app.outputAccepted()) failure ??= 'AppServerOutputRejected';
    metadata.appServerStderrBytes = app.stderrBytes();
  }
  if (fixture) {
    try {
      for (let attempt = 0; attempt < 20; attempt++) {
        const remaining = inspectProcesses();
        metadata.remainingProductProcesses = remaining.map(({ pid, parentPid, path, startedUtc }) => ({ pid, parentPid, relativePath: relative(fixture.environmentRoot, path), startedUtc }));
        if (remaining.length === 0) break;
        if (attempt === 19) failure ??= 'ProductProcessesRemain';
        await delay(100);
      }
    } catch { failure ??= 'ProductProcessInspectionFailed'; }
  }
  if (runtime) {
    try {
      const stopped = await runtimeCommand('stop');
      assert.equal(stopped.kind, 'stopped'); assert.equal(stopped.connected, 0); assert.equal(stopped.ownedPayloadCount, 0); assert.equal(stopped.ownedPayloadCapacity, 0);
      runtime.child.stdin.end(); const exit = await within(runtime.closed, 5000, 'runtime-close-timeout'); assert.equal(exit.code, 0); assert.equal(runtime.outputAccepted(), true); runtimeStopped = true;
    } catch { failure ??= 'RuntimeDidNotStop'; runtime.child.kill(); try { await within(runtime.closed, 5000, 'runtime-killed-close-timeout'); } catch { failure ??= 'RuntimeStreamsRemain'; } }
  }
  metadata.passed = failure === undefined && appExitedNormally && runtimeStopped && knownProductProcesses.length === 2;
  metadata.failureCategory = failure ?? null;
  metadata.appServerExitedNormally = appExitedNormally; metadata.runtimeStopped = runtimeStopped;
  metadata.observedProductProcessCount = knownProductProcesses.length;
  if (fixture) {
    try {
      metadata.privateHomeTopLevelEntries = await readdir(join(fixture.environmentRoot, 'codex-home'));
      await writeFile(join(fixture.environmentRoot, 'codex-validation-result.json'), JSON.stringify(metadata, null, 2), { flag: 'wx' });
    } catch { metadata.passed = false; metadata.failureCategory ??= 'ReportWriteFailed'; }
  }
  console.log(JSON.stringify(metadata));
  process.exitCode = metadata.passed ? 0 : 1;
}
