import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import { openSync, readSync, closeSync, fstatSync } from 'node:fs';
import { readFile, writeFile, stat, readdir } from 'node:fs/promises';
import { createServer } from 'node:http';
import { dirname, isAbsolute, join, resolve, relative } from 'node:path';
import { parseArgs } from 'node:util';

const supportedVersion = '0.2.1-rc.5';
const supportedBuildCommit = 'd4db512a969fccf0c5cc7ec8fe38e9c1d27af916';
const usage = `Manual Windows DSH application check (synthetic local provider, no real model or game).

node tests/integration/clients/dsh-readonly.mjs
  --client-bin <absolute-DeepSeek-Harness.exe>
  --client-asar <absolute-app.asar>
  --expected-client-version <exact-version>
  --expected-build-commit <40-lowercase-hex>
  --core-root <absolute-core-source>
  --expected-manifest-sha256 <64-lowercase-hex>
  --expected-source-commit <40-lowercase-hex>
  --candidate-kind <managed|nativeaot>
  --runtime-probe <absolute-SkylinesLink.RuntimeProbe.exe>
  [--pwsh-bin <absolute-pwsh.exe>]

Supports only reviewed DSH ${supportedVersion}, build ${supportedBuildCommit},
with the pinned bundled source/YAML hashes. Other versions are rejected.
Creates one owned environment and runs the official headless application.
Does not install, build, fetch, or select a default candidate.
--help and --self-test require no other options and start no process or service.
`;
let args;
try {
  const parsed = parseArgs({ strict: true, allowPositionals: false, tokens: true, options: {
    'self-test': { type: 'boolean' }, help: { type: 'boolean' },
    'client-bin': { type: 'string' }, 'client-asar': { type: 'string' },
    'expected-client-version': { type: 'string' }, 'expected-build-commit': { type: 'string' },
    'core-root': { type: 'string' }, 'expected-manifest-sha256': { type: 'string' },
    'expected-source-commit': { type: 'string' }, 'candidate-kind': { type: 'string' },
    'runtime-probe': { type: 'string' }, 'pwsh-bin': { type: 'string' },
  } });
  args = parsed.values;
  assert.equal(new Set(parsed.tokens.map(token => token.name)).size, parsed.tokens.length);
  assert.equal(process.platform, 'win32');
  if (args.help || args['self-test']) assert.equal(parsed.tokens.length, 1);
  else {
    for (const name of ['client-bin', 'client-asar', 'expected-client-version', 'expected-build-commit', 'core-root',
      'expected-manifest-sha256', 'expected-source-commit', 'candidate-kind', 'runtime-probe']) assert.ok(typeof args[name] === 'string' && args[name].length > 0);
    for (const name of ['client-bin', 'client-asar', 'core-root', 'runtime-probe']) assert.ok(isAbsolute(args[name]));
    if (args['pwsh-bin'] !== undefined) assert.ok(isAbsolute(args['pwsh-bin']));
    assert.equal(args['expected-client-version'], supportedVersion); assert.equal(args['expected-build-commit'], supportedBuildCommit);
    assert.match(args['expected-manifest-sha256'], /^[a-f0-9]{64}$/); assert.match(args['expected-source-commit'], /^[a-f0-9]{40}$/);
    assert.ok(['managed', 'nativeaot'].includes(args['candidate-kind']));
  }
} catch { console.error('Invalid arguments or unsupported client/platform. Use --help.'); process.exit(2); }
if (args.help) { console.log(usage); process.exit(0); }

// These remain undefined during the pure self-test; there is no default install or Core target.
const core = args['core-root'];
const expectedCommit = args['expected-source-commit'];
const expectedHash = args['expected-manifest-sha256'];
const expectedDshVersion = args['expected-client-version'];
const expectedDshCommit = args['expected-build-commit'];
const executable = args['client-bin'];
const asar = args['client-asar'];
const carrier = asar === undefined ? undefined : join(asar, 'dsh/node_modules/@deepseek-ai/dsh-desktop-host/lib/cli.js');
const runtimeExe = args['runtime-probe'];
const pwsh = args['pwsh-bin'] ?? 'pwsh.exe';
const helper = join(import.meta.dirname, 'fixture.ps1');
const parser = join(import.meta.dirname, 'dsh-config-reader.cjs');
const model = 'skylineslink-readonly-fixture';
const syntheticKey = 'skylineslink-local-fixture-not-a-real-credential';
const namespace = 'mcp__skylineslink_probe__';
const contextTool = namespace + 'skylines_context';
const operationTool = namespace + 'skylines_operation_get';
const operationInput = { selector: { kind: 'operationId', operationId: '仅隔离检查-不存在-操作' } };
const calls = [
  { id: 'sl_dsh_read_context', name: contextTool, input: {} },
  { id: 'sl_dsh_read_operation', name: operationTool, input: operationInput },
  { id: 'sl_dsh_read_revoked', name: contextTool, input: {} },
];
const finalText = 'SKYLINESLINK_DSH_READONLY_FIXTURE_COMPLETE';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const normalize = path => resolve(path).toLowerCase();
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const disabledIds = ['credentials', 'authorization', 'deepseek-account', 'llm-deepseek-account', 'llm-pi-ai',
  'agent-instructions', 'skill-filesystem', 'session-title-llm', 'session-telemetry-otel', 'hmr'];
const sourcePins = {
  'dsh-desktop-host/lib/cli.js': 'ed8bd3ca840d67599bde74f940cdb1714f7b7a98cecd166705a42a661e5cd1b7',
  'dsh/lib/bin.js': '80112efe368efb1a39720c7ada0af00442e5131fb2744edaaff8a3e8bb499e6f',
  'dsh/lib/profile-boot-C14i-8IN.js': '32d6345d7457e7c19c18bad07a85856e7ef7c560d52a1da042d68981ee127b4d',
  'dsh/lib/dump-config-CWs_HdOc.js': '3e0c04ec1fa1e54f1ca9e3aba35a8e4e747685e0318c63ab6265a8a6bde2bf00',
  'dsh-app-boot/lib/index.js': '5135179ac810e4f626f32ec3bdc3293dc7d2028e8aa92e5e0c006f1a33b78e76',
  'dsh-home-paths/lib/index.js': '9877a6117c5def69b61fae12f70a7a7400894a9dc4a9f7069ce4cc58aeb66e9d',
  'dsh-headless/lib/index.js': '5402e990dd82d33bccee722e220b518e7f26f82acb79ed707bfebc8eb5c3bead',
  'dsh-headless/lib/json-stream-BA-F3lfb.js': 'b6ede8da01119f5caf4baa8051ce8bcd8b956b5b163cf58af91d583b80028c0c',
  'dsh-headless/cordis.patch.yml': 'f4d5f53f070089a7fc5209bd47c9ae4e85b99d042cbe76be88ab1ed63dfb8fc8',
  'dsh-mcp-client/lib/index.js': '64dfc9127a47d9d453d72022b47d3b8d8a5c82cad756ac1114b09faf1a956120',
  'dsh-llm-deepseek/lib/index.js': '21fedb71f8d9aafea73b35ee13bc73737a8271d7ffa261697d4208e8563196cb',
  'dsh-llm-deepseek-api-key/lib/index.js': '67f9cbc26ce0ea07009e0c36ee4a7cc058542cdf85b873549885922646cacd35',
  'dsh-base/cordis.patch.yml': 'a2cc57868a4d016609801e28781262515483dde29b41c2742d9a3b27a6f52f77',
};

function checkInstallation() {
  const fd = openSync(asar, 'r');
  try {
    const length = fstatSync(fd).size, prefix = Buffer.alloc(16);
    assert.equal(readSync(fd, prefix, 0, 16, 0), 16);
    const headerSize = prefix.readUInt32LE(12), payload = 8 + prefix.readUInt32LE(4);
    assert.ok(headerSize > 0 && headerSize <= 16 * 1024 * 1024 && payload >= headerSize + 16);
    const raw = Buffer.alloc(headerSize); assert.equal(readSync(fd, raw, 0, raw.length, 16), raw.length);
    const tree = JSON.parse(raw.toString('utf8'));
    function entry(path) { let node = tree; for (const part of path.split('/')) node = node?.files?.[part]; return node; }
    function read(path) {
      const e = entry(path); assert.ok(e && !e.unpacked && !e.link && Number.isSafeInteger(e.size) && e.size <= 4 * 1024 * 1024);
      const offset = payload + Number(e.offset); assert.ok(Number.isSafeInteger(offset) && offset >= payload && offset + e.size <= length);
      const bytes = Buffer.alloc(e.size); assert.equal(readSync(fd, bytes, 0, bytes.length, offset), bytes.length); return bytes;
    }
    const pkg = JSON.parse(read('package.json').toString('utf8'));
    assert.equal(pkg.name, '@deepseek-ai/dsh-desktop'); assert.equal(pkg.version, expectedDshVersion);
    assert.equal(pkg.dshBuildCommit, expectedDshCommit); assert.equal(pkg.dshBuildDirty, false);
    for (const [path, expected] of Object.entries(sourcePins)) assert.equal(hash(read('dsh/node_modules/@deepseek-ai/' + path)), expected);
    const yamlRoot = 'dsh/node_modules/js-yaml'; const records = [];
    function walk(node, path) {
      for (const [name, child] of Object.entries(node.files)) {
        const next = path + '/' + name;
        if (child.files) walk(child, next);
        else if (/\.js$|\/package\.json$/.test(next)) { const bytes = read(next); records.push({ path: next.slice(yamlRoot.length + 1), size: bytes.length, sha256: hash(bytes) }); }
        assert.ok(records.length <= 64);
      }
    }
    walk(entry(yamlRoot), yamlRoot); records.sort((a, b) => a.path.localeCompare(b.path, 'en'));
    assert.equal(records.length, 29); assert.equal(hash(JSON.stringify(records)), '11bcbad3c1e476a7a9ce14f90e49fa8b13b91d8621a60ba3ba74af54eb6f13ca');
    assert.equal(JSON.parse(read(yamlRoot + '/package.json')).version, '4.3.2');
    return { version: pkg.version, buildCommit: pkg.dshBuildCommit, inspectedEntries: Object.keys(sourcePins).length, yamlVersion: '4.3.2', yamlTreeSha256: hash(JSON.stringify(records)) };
  } finally { closeSync(fd); }
}
function overlayFor(baseURL, launcher, cwd, environment) {
  return [
    ...disabledIds.map(id => ({ id, disabled: true })),
    { id: 'tools', config: { mode: 'native' } },
    { id: 'agent-default-model', config: { provider: 'deepseek-official', model } },
    { id: 'llm-deepseek', config: { baseURL, apiKeyEnv: 'SKYLINESLINK_SYNTHETIC_KEY', thinking: 'disabled', reasoningEffort: 'off',
      maxTokens: 1024, defaultContextWindow: 65536, streamIdleTimeoutMs: 5000, models: [{ id: model, inputModalities: ['text'] }] } },
    { id: 'approval', config: { policy: 'never' } },
    { id: 'sandbox-policy', config: { mode: 'read-only', workspaceRoot: cwd } },
    { insert: [{ id: 'skylineslink-readonly', name: '@deepseek-ai/dsh-mcp-client', config: { serverName: 'skylineslink_probe', transport: 'stdio',
      command: launcher, args: ['--profile', 'Dsh.ReadOnly', '--environment', environment], cwd, toolCallTimeoutMs: 10000, failOnStartupError: true, reconnect: { enabled: false } } }] },
  ];
}
function validateSummary(summary, overlay) {
  assert.ok(summary && Array.isArray(summary.rows) && summary.rows.length === summary.rowCount && summary.rowCount <= 256);
  const rows = new Map(summary.rows.map(row => [row.id, row])); assert.equal(rows.size, summary.rowCount);
  for (const patch of overlay) {
    const expected = patch.insert?.[0] ?? patch; const row = rows.get(expected.id); assert.ok(row);
    if (expected.disabled === true) assert.equal(row.disabled, true);
    else { assert.ok(row.disabled === undefined || row.disabled === false); assert.deepEqual(row.config, expected.config); }
    if (expected.name) assert.equal(row.name, expected.name);
  }
  assert.deepEqual(summary.rows.filter(row => row.name === '@deepseek-ai/dsh-mcp-client').map(row => row.id), ['skylineslink-readonly']);
  assert.equal(rows.get('headless-runner')?.name, '@deepseek-ai/dsh-headless');
  assert.equal(rows.get('headless-startup')?.name, '@deepseek-ai/dsh-headless/startup');
  assert.ok(!summary.rows.some(row => /dsh-(?:web-app|desktop-host|sdk-jsonrpc-server|acp)/.test(row.name)));
}
function sseFor(round) {
  assert.ok(round >= 1 && round <= 4);
  const call = calls[round - 1];
  const block = call ? { type: 'tool_use', id: call.id, name: call.name, input: {} } : { type: 'text', text: '' };
  const events = [
    { type: 'message_start', message: { id: 'sl_fixture_message_' + round, type: 'message', role: 'assistant', model, content: [], stop_reason: null, usage: { input_tokens: 1, output_tokens: 1 } } },
    { type: 'content_block_start', index: 0, content_block: block },
    { type: 'content_block_delta', index: 0, delta: call ? { type: 'input_json_delta', partial_json: JSON.stringify(call.input) } : { type: 'text_delta', text: finalText } },
    { type: 'content_block_stop', index: 0 },
    { type: 'message_delta', delta: { stop_reason: call ? 'tool_use' : 'end_turn', stop_sequence: null }, usage: { output_tokens: 1 } },
    { type: 'message_stop' },
  ];
  const bytes = Buffer.from(events.map(event => `event: ${event.type}\ndata: ${JSON.stringify(event)}\n\n`).join(''));
  assert.ok(bytes.length <= 16384); return bytes;
}
function textContent(content) {
  if (typeof content === 'string') return content;
  assert.ok(Array.isArray(content) && content.length > 0 && content.every(block => block.type === 'text' && typeof block.text === 'string'));
  return content.map(block => block.text).join('');
}
function verifyResult(index, text, error, instanceId) {
  assert.ok(typeof text === 'string' && Buffer.byteLength(text) <= 8192);
  if (index < 2) {
    assert.notEqual(error, true); const value = JSON.parse(text);
    if (index === 0) { assert.equal(value.instanceId, instanceId); assert.equal(value.gameState, 'menu'); assert.equal(value.world, null); }
    else assert.deepEqual(value, { lookup: { kind: 'absent' } });
  } else {
    assert.equal(error, true); const value = JSON.parse(text.replace(/^Error: /, ''));
    assert.equal(value.error.code, 'UNAUTHORIZED'); assert.ok(!Object.hasOwn(value.error, 'mayHaveApplied'));
  }
}
function inspectRequest(body, round, instanceId) {
  assert.equal(body.model, model); assert.equal(body.stream, true); assert.ok(Array.isArray(body.messages) && body.messages.length <= 32);
  assert.ok(Array.isArray(body.tools) && body.tools.length <= 256);
  const ours = body.tools.filter(tool => typeof tool.name === 'string' && tool.name.startsWith(namespace));
  assert.deepEqual(ours.map(tool => tool.name).sort(), [contextTool, operationTool].sort());
  assert.equal(ours.find(tool => tool.name === contextTool).input_schema.type, 'object');
  const operationSchema = ours.find(tool => tool.name === operationTool).input_schema;
  assert.equal(operationSchema.type, 'object'); assert.deepEqual(operationSchema.required, ['selector']); assert.ok(operationSchema.properties.selector);
  const toolCalls = [], results = [];
  for (const message of body.messages) {
    assert.ok(Array.isArray(message.content));
    for (const block of message.content) {
      if (block.type === 'tool_use') { assert.equal(message.role, 'assistant'); toolCalls.push(block); }
      if (block.type === 'tool_result') { assert.equal(message.role, 'user'); results.push(block); }
    }
  }
  assert.equal(toolCalls.length, round - 1); assert.equal(results.length, round - 1);
  for (let i = 0; i < round - 1; i++) {
    assert.equal(toolCalls[i].id, calls[i].id); assert.equal(toolCalls[i].name, calls[i].name); assert.deepEqual(toolCalls[i].input, calls[i].input);
    assert.equal(results[i].tool_use_id, calls[i].id); verifyResult(i, textContent(results[i].content), results[i].is_error, instanceId);
  }
}

if (args['self-test']) {
  const overlay = overlayFor('http://127.0.0.1:12345/v1', 'C:\\fixture\\Launcher.exe', 'C:\\fixture\\cwd', 'probe-' + '0'.repeat(24));
  const summary = { rows: [...overlay.map(p => p.insert?.[0] ?? { ...p, name: '@fixture/' + p.id }),
    { id: 'headless-runner', name: '@deepseek-ai/dsh-headless' }, { id: 'headless-startup', name: '@deepseek-ai/dsh-headless/startup' }] };
  summary.rowCount = summary.rows.length; validateSummary(summary, overlay);
  const bad = structuredClone(summary); bad.rows.find(row => row.id === 'llm-deepseek').config.baseURL = 'https://api.deepseek.com/anthropic';
  assert.throws(() => validateSummary(bad, overlay));
  const duplicate = structuredClone(summary); duplicate.rows.push({ id: 'extra', name: '@deepseek-ai/dsh-mcp-client' }); duplicate.rowCount++;
  assert.throws(() => validateSummary(duplicate, overlay));
  const expression = structuredClone(summary); expression.rows.find(row => row.id === 'credentials').disabled = { __unresolvedDshExpression: 'true' };
  assert.throws(() => validateSummary(expression, overlay));
  for (let round = 1; round <= 4; round++) assert.ok(sseFor(round).toString().endsWith('event: message_stop\ndata: {"type":"message_stop"}\n\n'));
  verifyResult(2, 'Error: {"error":{"code":"UNAUTHORIZED"}}', true, 'fixture');
  assert.throws(() => verifyResult(2, 'Error: {"error":{"code":"UNAUTHORIZED","mayHaveApplied":false}}', true, 'fixture'));
  console.log('Pure fixture checks passed; no process, service, or client session started.'); process.exit(0);
}
const environment = 'probe-' + randomBytes(12).toString('hex'), internalToken = randomBytes(32).toString('hex');
const metadata = { candidateCommit: expectedCommit, candidateKindDeclared: args['candidate-kind'], candidateManifestSha256: expectedHash,
  clientVersion: expectedDshVersion, dshBuildCommit: expectedDshCommit, realModelCalled: false, syntheticProvider: true, gameStarted: false,
  passed: false, stage: 'preflight', ownedEnvironment: environment, applicationDisposerSuccessProven: false };
let fixture, runtime, app, provider, ready, failure, failureResolve;
let requestCount = 0, responseCount = 0, requestBytes = 0, observedProducts = [], appExit, runtimeStopped = false, providerClosed = false;
let closingProvider = false, runtimeBusy = false, activeRequests = 0, totalConnections = 0;
const sockets = new Set(), handlers = new Set(), children = new Set(), initialHeaderTimers = new Map();
const fatal = new Promise(resolve => { failureResolve = resolve; });
function fail(code) { if (failure === undefined) { failure = code; failureResolve(); } }
async function within(promise, ms, label, watchFailure = true) {
  let timer;
  try {
    return await Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(label)), ms); }),
      ...(watchFailure ? [fatal.then(() => { throw new Error('LatchedFailure'); })] : [])]);
  } finally { clearTimeout(timer); }
}
function cleanEnv(dsh = false) {
  const env = {};
  for (const key of ['SystemRoot', 'WINDIR', 'SystemDrive', 'TEMP', 'TMP', 'PATH', 'PATHEXT', 'COMSPEC', 'PROCESSOR_ARCHITECTURE', 'NUMBER_OF_PROCESSORS'])
    if (process.env[key] !== undefined) env[key] = process.env[key];
  if (fixture) env.USERPROFILE = fixture.profileRoot;
  if (dsh) Object.assign(env, { ELECTRON_RUN_AS_NODE: '1', DSH_HOME: join(fixture.environmentRoot, 'dsh-home'),
    DSH_AGENTS_HOME: join(fixture.environmentRoot, 'dsh-agents-home'), DSH_TELEMETRY_DISABLED: '1', SKYLINESLINK_SYNTHETIC_KEY: syntheticKey });
  return env;
}
function ownChild(exe, argv, { mode = 'text', onLine, env = cleanEnv(), cwd = fixture?.environmentRoot } = {}) {
  const child = spawn(exe, argv, { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'], env, cwd });
  children.add(child);
  const closed = new Promise(resolve => child.once('close', (code, signal) => { children.delete(child); resolve({ code, signal }); }));
  child.once('error', () => fail('ChildSpawnFailed'));
  const decoder = new TextDecoder('utf-8', { fatal: true }), errorDecoder = new TextDecoder('utf-8', { fatal: true });
  let output = '', tail = '', secretSuffix = '', errTail = '', outBytes = 0, errBytes = 0, ended = false, errorEnded = false, rejected = false;
  const queued = [], waiting = [];
  function reject() { rejected = true; fail('ChildOutputRejected'); for (const waiter of waiting.splice(0)) waiter.reject(new Error('OutputRejected')); }
  child.stdout.on('data', chunk => {
    try {
      outBytes += chunk.length; assert.ok(outBytes <= 1048576); if (rejected) return;
      const decoded = decoder.decode(chunk, { stream: true });
      const inspected = secretSuffix + decoded; assert.ok(!inspected.includes(internalToken)); secretSuffix = inspected.slice(-internalToken.length + 1);
      tail += decoded;
      if (mode === 'text') { output += tail; tail = ''; return; }
      for (let end = tail.indexOf('\n'); end >= 0; end = tail.indexOf('\n')) {
        const line = tail.slice(0, end).replace(/\r$/, ''); tail = tail.slice(end + 1); assert.ok(Buffer.byteLength(line) <= 65536);
        if (onLine) onLine(JSON.parse(line));
        else if (waiting.length) waiting.shift().resolve(line);
        else { queued.push(line); assert.ok(queued.length <= 64); }
      }
    } catch { reject(); }
  });
  child.stderr.on('data', chunk => {
    try {
      errBytes += chunk.length; assert.ok(errBytes <= 65536);
      const text = errTail + errorDecoder.decode(chunk, { stream: true }); assert.ok(!text.includes(internalToken));
      assert.ok(!/transport closure could not be confirmed|server shutdown may be incomplete/i.test(text));
      errTail = text.slice(-256);
    } catch { reject(); }
  });
  child.stdout.once('end', () => {
    try { tail += decoder.decode(); if (mode === 'text') { output += tail; tail = ''; } else assert.equal(tail, ''); } catch { reject(); }
    ended = true; for (const waiter of waiting.splice(0)) waiter.reject(new Error('OutputEnded'));
  });
  child.stderr.once('end', () => { try { const last = errTail + errorDecoder.decode(); assert.ok(!last.includes(internalToken)); } catch { reject(); } errorEnded = true; });
  for (const stream of [child.stdin, child.stdout, child.stderr]) stream.on('error', reject);
  return { child, closed, text: () => output, stderrBytes: () => errBytes, outputAccepted: () => ended && errorEnded && !rejected,
    next: () => rejected ? Promise.reject(new Error('OutputRejected')) : queued.length ? Promise.resolve(queued.shift()) : ended ? Promise.reject(new Error('OutputEnded')) : new Promise((resolve, reject) => waiting.push({ resolve, reject })),
    send: value => child.stdin.write(typeof value === 'string' ? value + '\n' : JSON.stringify(value) + '\n') };
}
async function command(exe, argv, options = {}, input) {
  const owned = ownChild(exe, argv, options);
  if (input === undefined) owned.child.stdin.end(); else owned.child.stdin.end(input);
  try {
    const exit = await within(owned.closed, 15000, 'CommandTimeout');
    assert.equal(exit.code, 0); assert.equal(exit.signal, null); assert.equal(owned.outputAccepted(), true); assert.equal(owned.stderrBytes(), 0);
    return owned.text();
  } catch (error) {
    fail('CommandFailed'); owned.child.kill(); await within(owned.closed, 5000, 'CommandKillClose', false).catch(() => fail('CommandRemains')); throw error;
  }
}
async function readBounded(path, maximum) { const info = await stat(path); assert.ok(info.isFile() && info.size <= maximum); const bytes = await readFile(path); assert.equal(bytes.length, info.size); return bytes; }
function inspectProcesses() {
  const data = execFileSync(pwsh, ['-NoProfile', '-NonInteractive', '-File', helper, '-Mode', 'inspect', '-Client', 'dsh', '-EnvironmentName', environment],
    { windowsHide: true, encoding: 'utf8', timeout: 10000, maxBuffer: 65536, env: cleanEnv() });
  const processes = JSON.parse(data).processes; assert.ok(Array.isArray(processes) && processes.length <= 32); return processes;
}
async function runtimeCommand(value, watchFailure = true) {
  assert.equal(runtimeBusy, false); runtimeBusy = true;
  try { runtime.send(value); return JSON.parse(await within(runtime.next(), 5000, 'RuntimeReplyTimeout', watchFailure)); }
  finally { runtimeBusy = false; }
}
const projection = { sessionCount: 0, calls: 0, results: 0, finals: 0, turnStarts: 0, turnEnds: 0, stepStarts: 0, stepEnds: 0, texts: 0 };
function headlessEvent(event) {
  assert.ok(event && typeof event === 'object' && event.truncated !== true && projection.finals === 0);
  if (event.type === 'session') {
    projection.sessionCount++; assert.equal(projection.sessionCount, 1); assert.equal(normalize(event.cwd), normalize(join(fixture.environmentRoot, 'dsh-cwd')));
    assert.ok(typeof event.sessionId === 'string' && event.sessionId.startsWith('session-')); return;
  }
  assert.equal(projection.sessionCount, 1);
  if (event.type === 'tool_call') {
    const expected = calls[projection.calls++]; assert.ok(expected); assert.equal(event.callId, expected.id); assert.equal(event.tool, expected.name); assert.deepEqual(event.input, expected.input); return;
  }
  if (event.type === 'tool_result') {
    const index = projection.results++; assert.ok(index < 3 && projection.calls === index + 1); assert.equal(event.callId, calls[index].id);
    assert.equal(event.status, index === 2 ? 'error' : 'completed'); verifyResult(index, event.result, event.status === 'error', ready.instanceId); return;
  }
  if (event.type === 'status') {
    if (event.phase === 'turn_start') { assert.equal(++projection.turnStarts, 1); return; }
    if (event.phase === 'turn_end') { assert.equal(++projection.turnEnds, 1); assert.equal(event.reason.kind, 'completed'); return; }
    if (event.phase === 'step_start') { assert.ok(++projection.stepStarts <= 4); return; }
    if (event.phase === 'step_end') { assert.ok(++projection.stepEnds <= 4); return; }
    assert.fail('Unexpected status');
  }
  if (event.type === 'text') { assert.equal(++projection.texts, 1); assert.equal(event.text, finalText); return; }
  if (event.type === 'final') { assert.equal(event.text, finalText); assert.equal(projection.results, 3); assert.equal(projection.turnEnds, 1); projection.finals++; return; }
  assert.fail('Unexpected headless event');
}
async function providerRequest(req, res, launcher) {
  let timer, admitted = false;
  try {
    clearTimeout(initialHeaderTimers.get(req.socket)); initialHeaderTimers.delete(req.socket);
    assert.equal(closingProvider, false); assert.equal(failure, undefined); assert.equal(activeRequests, 0); activeRequests++; admitted = true;
    assert.ok(++requestCount <= 4); const round = requestCount;
    metadata.providerRound = round; metadata.providerStage = 'request-headers';
    assert.equal(req.method, 'POST'); assert.equal(req.url, '/v1/messages'); assert.equal(req.headers['x-api-key'], syntheticKey);
    assert.ok(/^application\/json(?:;|$)/.test(req.headers['content-type'] ?? '')); assert.equal(req.headers['anthropic-version'], '2023-06-01');
    timer = setTimeout(() => { fail('ProviderRequestTimeout'); req.destroy(); res.destroy(); }, 15000);
    metadata.providerStage = 'request-body'; const chunks = []; let bytes = 0;
    for await (const chunk of req) { bytes += chunk.length; requestBytes += chunk.length; assert.ok(bytes <= 1048576 && requestBytes <= 4194304); chunks.push(chunk); }
    const text = new TextDecoder('utf-8', { fatal: true }).decode(Buffer.concat(chunks)); assert.ok(!text.includes(internalToken));
    metadata.providerStage = 'history-and-tools'; inspectRequest(JSON.parse(text), round, ready.instanceId);
    if (round === 1) { const metrics = await runtimeCommand('metrics'); assert.equal(metrics.connected, 0); assert.equal(metrics.authenticated, 0); metadata.lazyDiscoveryConfirmed = true; }
    if (round === 3) {
      metadata.providerStage = 'product-processes';
      observedProducts = inspectProcesses();
      assert.equal(observedProducts.filter(p => normalize(p.path) === normalize(launcher)).length, 1);
      assert.equal(observedProducts.filter(p => normalize(p.path) === normalize(join(dirname(launcher), '../../runtime/node.exe'))).length, 1);
      assert.equal(observedProducts.length, 2);
      metadata.providerStage = 'revoke';
      assert.equal((await runtimeCommand('revoke')).kind, 'revoked'); metadata.revoked = true;
    }
    metadata.providerStage = 'response'; res.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-store' });
    await new Promise((resolve, reject) => { res.once('finish', resolve); res.once('error', reject); res.once('close', () => { if (!res.writableFinished) reject(new Error('ResponseClosed')); }); res.end(sseFor(round)); });
    responseCount++; metadata.providerStage = 'response-finished';
  } catch { fail('ProviderSequenceRejected'); req.destroy(); res.destroy(); }
  finally { clearTimeout(timer); if (admitted) activeRequests--; }
}

try {
  metadata.installation = checkInstallation();
  const manifestBytes = await readBounded(join(core, 'core-manifest.json'), 65536); assert.equal(hash(manifestBytes), expectedHash);
  const manifest = JSON.parse(manifestBytes); assert.equal(manifest.sourceCommit, expectedCommit); assert.equal(typeof manifest.version, 'string');
  metadata.candidateVersion = manifest.version;
  const setup = join(core, 'desktop/setup/SkylinesLink.Setup.exe');
  const setupEntries = manifest.files.filter(file => file.path === 'desktop/setup/SkylinesLink.Setup.exe'); assert.equal(setupEntries.length, 1);
  const setupBytes = await readBounded(setup, 64 * 1024 * 1024); assert.equal(setupBytes.length, setupEntries[0].size); assert.equal(hash(setupBytes), setupEntries[0].sha256);
  metadata.runtimeProbeSha256 = hash(await readBounded(runtimeExe, 16 * 1024 * 1024));
  fixture = JSON.parse(execFileSync(pwsh, ['-NoProfile', '-NonInteractive', '-File', helper, '-Mode', 'prepare', '-Client', 'dsh', '-EnvironmentName', environment],
    { windowsHide: true, encoding: 'utf8', timeout: 10000, maxBuffer: 65536, env: cleanEnv() }));
  assert.equal(fixture.ownedEnvironment, environment);
  const cwd = join(fixture.environmentRoot, 'dsh-cwd'), home = join(fixture.environmentRoot, 'dsh-home');
  assert.deepEqual(await readdir(home), []); assert.deepEqual(await readdir(cwd), []); assert.deepEqual(await readdir(join(fixture.environmentRoot, 'dsh-agents-home')), []);
  const actualVersion = await command(executable, ['--expose-internals', carrier, '--version'], { env: cleanEnv(true), cwd }); assert.equal(actualVersion.trim(), expectedDshVersion);
  metadata.stage = 'prepare-core';
  execFileSync(setup, ['prepare-core', core, environment], { windowsHide: true, encoding: 'utf8', env: cleanEnv(), timeout: 30000, maxBuffer: 65536 });
  const prepared = join(fixture.environmentRoot, 'releases', manifest.version), launcher = join(prepared, 'desktop/launcher/SkylinesLink.Launcher.exe');
  assert.equal(hash(await readBounded(join(prepared, 'core-manifest.json'), 65536)), expectedHash);
  await writeFile(join(fixture.environmentRoot, 'activation.json'), JSON.stringify({ productId: 'skylineslink', version: manifest.version, coreManifestSha256: expectedHash }), { flag: 'wx' });
  // Reserving the listener here gives the final composed overlay one exact loopback port.
  provider = createServer((req, res) => {
    const task = providerRequest(req, res, launcher); handlers.add(task); task.finally(() => handlers.delete(task)).catch(() => fail('ProviderHandlerFailed'));
  });
  provider.maxConnections = 2; provider.headersTimeout = 5000; provider.requestTimeout = 15000;
  provider.on('connection', socket => {
    sockets.add(socket); socket.once('close', () => { sockets.delete(socket); clearTimeout(initialHeaderTimers.get(socket)); initialHeaderTimers.delete(socket); }); socket.on('error', () => {});
    if (++totalConnections > 8) { fail('ProviderConnectionBudget'); socket.destroy(); return; }
    initialHeaderTimers.set(socket, setTimeout(() => { fail('ProviderInitialHeaderTimeout'); socket.destroy(); }, 5000));
  });
  provider.on('clientError', (_error, socket) => { fail('ProviderHttpRejected'); socket.destroy(); });
  provider.on('error', () => fail('ProviderServerFailed'));
  await within(new Promise(resolve => provider.listen({ host: '127.0.0.1', port: 0, exclusive: true }, resolve)), 5000, 'ProviderListenTimeout');
  const baseURL = `http://127.0.0.1:${provider.address().port}/v1`;
  const overlay = overlayFor(baseURL, launcher, cwd, environment), overlayPath = join(fixture.environmentRoot, 'dsh-overlay.json');
  await writeFile(overlayPath, JSON.stringify(overlay, null, 2), { flag: 'wx' });
  const overlayHash = hash(await readBounded(overlayPath, 65536));
  metadata.stage = 'composition-preflight';
  const dump = await command(executable, ['--expose-internals', carrier, '--profile', 'headless', '--patch', overlayPath, '--dump-config'], { env: cleanEnv(true), cwd });
  const summary = JSON.parse(await command(executable, [parser, asar], { env: cleanEnv(true), cwd }, dump));
  validateSummary(summary, overlay); metadata.compositionRows = summary.rowCount; metadata.compositionVerified = true;
  assert.equal(requestCount, 0); assert.equal(hash(await readBounded(overlayPath, 65536)), overlayHash);
  runtime = ownChild(runtimeExe, ['--serve'], { mode: 'json' }); metadata.runtimeProbePid = runtime.child.pid;
  ready = await runtimeCommand({ initialize: true, operationProbe: false, internalToken, externalHttpToken: null, profileId: 'Dsh.ReadOnly',
    environmentId: environment, coreManifestSha256: expectedHash, privateDirectory: join(fixture.environmentRoot, 'connection'), deadlineMilliseconds: 5000 });
  assert.equal(ready.kind, 'ready'); assert.equal(ready.coreManifestSha256, expectedHash); assert.equal(ready.environmentId, environment);
  metadata.stage = 'headless-application';
  app = ownChild(executable, ['--expose-internals', carrier, '--profile', 'headless', '--patch', overlayPath, '--json', 'Run the fixed local read-only integration sequence.'],
    { mode: 'json', onLine: headlessEvent, env: cleanEnv(true), cwd }); metadata.headlessPid = app.child.pid;
  app.child.stdin.end();
  appExit = await within(app.closed, 60000, 'HeadlessApplicationTimeout');
  assert.equal(appExit.code, 0); assert.equal(appExit.signal, null); assert.equal(app.outputAccepted(), true);
  assert.equal(requestCount, 4); assert.equal(responseCount, 4); assert.equal(projection.calls, 3); assert.equal(projection.results, 3);
  assert.equal(projection.finals, 1); assert.equal(projection.turnStarts, 1); assert.equal(projection.turnEnds, 1); assert.equal(projection.stepStarts, 4); assert.equal(projection.stepEnds, 4);
  assert.equal(hash(await readBounded(overlayPath, 65536)), overlayHash); checkInstallation();
  metadata.stage = 'external-resource-close';
} catch (error) {
  fail(error instanceof assert.AssertionError ? 'AssertionFailed' : 'StageFailed');
} finally {
  closingProvider = true;
  if (app && !appExit) {
    fail('HeadlessDidNotComplete'); app.child.kill();
    appExit = await within(app.closed, 5000, 'HeadlessKillClose', false).catch(() => { fail('HeadlessStreamsRemain'); return null; });
  }
  if (app && !app.outputAccepted()) fail('HeadlessOutputRejected');
  if (provider) {
    const closed = new Promise(resolve => provider.close(error => { if (error) fail('ProviderCloseFailed'); resolve(); }));
    provider.closeIdleConnections();
    try { await within(closed, 2000, 'ProviderCloseTimeout', false); }
    catch { fail('ProviderHadToDestroySockets'); for (const socket of sockets) socket.destroy(); await within(closed, 5000, 'ProviderForcedCloseTimeout', false).catch(() => fail('ProviderRemains')); }
    try { await within(Promise.allSettled([...handlers]), 10000, 'ProviderHandlersRemain', false); assert.equal(activeRequests, 0); assert.equal(sockets.size, 0); providerClosed = !provider.listening; }
    catch { fail('ProviderOwnersRemain'); }
  }
  if (fixture) {
    try {
      for (let i = 0; i < 20; i++) {
        const remaining = inspectProcesses(); metadata.remainingProductProcesses = remaining.map(({ pid, parentPid, path, startedUtc }) => ({ pid, parentPid, relativePath: relative(fixture.environmentRoot, path), startedUtc }));
        if (remaining.length === 0) break;
        if (i === 19) fail('ProductProcessesRemain'); await delay(100);
      }
    } catch { fail('ProductInspectionFailed'); }
  }
  if (runtime) {
    try {
      const metrics = await runtimeCommand('metrics', false); assert.equal(metrics.connected, 0); assert.equal(metrics.authenticated, 0); assert.equal(metrics.ownedPayloadCount, 0); assert.equal(metrics.ownedPayloadCapacity, 0);
      const stopped = await runtimeCommand('stop', false); assert.equal(stopped.kind, 'stopped'); assert.equal(stopped.connected, 0); assert.equal(stopped.ownedPayloadCount, 0); assert.equal(stopped.ownedPayloadCapacity, 0);
      runtime.child.stdin.end(); const exit = await within(runtime.closed, 5000, 'RuntimeCloseTimeout', false); assert.equal(exit.code, 0); assert.equal(exit.signal, null); assert.equal(runtime.outputAccepted(), true); runtimeStopped = true;
    } catch { fail('RuntimeDidNotStop'); runtime.child.kill(); await within(runtime.closed, 5000, 'RuntimeKillClose', false).catch(() => fail('RuntimeStreamsRemain')); }
  }
  if (runtimeStopped) {
    try { assert.equal(hash(await readBounded(runtimeExe, 16 * 1024 * 1024)), metadata.runtimeProbeSha256); metadata.runtimeProbeUnchanged = true; }
    catch { fail('RuntimeImageChanged'); }
  }
  if (children.size) fail('OwnedChildrenRemain');
  metadata.headlessExit = appExit ?? null; metadata.headlessStderrBytes = app?.stderrBytes() ?? null;
  metadata.providerClosed = providerClosed; metadata.runtimeStopped = runtimeStopped; metadata.requests = requestCount; metadata.responses = responseCount;
  metadata.requestBytes = requestBytes; metadata.observedProductProcessCount = observedProducts.length; metadata.projection = projection;
  metadata.passed = failure === undefined && appExit?.code === 0 && appExit.signal === null && runtimeStopped && providerClosed && observedProducts.length === 2;
  metadata.failureCategory = failure ?? null;
  if (fixture) {
    try { await writeFile(join(fixture.environmentRoot, 'dsh-validation-result.json'), JSON.stringify(metadata, null, 2), { flag: 'wx' }); }
    catch { metadata.passed = false; metadata.failureCategory ??= 'ReportWriteFailed'; }
  }
  console.log(JSON.stringify(metadata)); process.exitCode = metadata.passed ? 0 : 1;
}
