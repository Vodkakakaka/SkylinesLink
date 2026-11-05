import { spawn, execFile } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { open, realpath, lstat } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { dirname, isAbsolute, join, resolve } from 'node:path';
import { isDeepStrictEqual } from 'node:util';
import { canonicalEnvironment, isDeploymentIdentifier } from '../../../src/host/src/shared/identifiers.ts';

// Only the explicit private target selects an already-running game. No game startup,
// credentials, client settings, native writes, or production submit tools are used here.
const root = resolve(import.meta.dirname, '../../..');
const require = createRequire(join(root, 'src/host/package.json'));
const marker = 'SKYLINESLINK_READONLY_EXIT ';
const tools = ['skylines_context', 'skylines_operation_get'];
let stage = 'arguments';
const help = '用法：node tests/integration/game/readonly-client.mjs --target <私有目标配置绝对路径>\n只验证已启动且已 Ready 的指定菜单实例；不启动游戏，不修改客户端配置。参见同目录 README.md。\n';
class ProbeFailure extends Error { constructor(code) { super(code); this.code = code; } }
function check(value, code) { if (!value) throw new ProbeFailure(code); }
function equal(actual, expected, code) { check(isDeepStrictEqual(actual, expected), code); }
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

async function boundedFile(path, maximum) {
  check(isAbsolute(path), 'ABSOLUTE_PATH_REQUIRED');
  const before = await lstat(path, { bigint: true });
  check(before.isFile() && !before.isSymbolicLink() && before.size > 0n && before.size <= BigInt(maximum), 'FILE_BOUNDARY');
  check(resolve(await realpath(path)).toLowerCase() === resolve(path).toLowerCase(), 'FILE_REDIRECTED');
  const handle = await open(path, 'r');
  try {
    const opened = await handle.stat({ bigint: true });
    check(opened.dev === before.dev && opened.ino === before.ino && opened.size === before.size && opened.mtimeNs === before.mtimeNs, 'FILE_CHANGED');
    const buffer = Buffer.alloc(Number(opened.size) + 1);
    let offset = 0;
    while (offset < buffer.length) {
      const read = await handle.read(buffer, offset, buffer.length - offset, offset);
      if (read.bytesRead === 0) break;
      offset += read.bytesRead;
    }
    const after = await handle.stat({ bigint: true });
    check(offset === Number(opened.size) && after.size === opened.size && after.mtimeNs === opened.mtimeNs, 'FILE_CHANGED');
    return buffer.subarray(0, offset);
  } finally { await handle.close(); }
}

async function targetFrom(path, expectedHash) {
  const raw = await boundedFile(path, 16384);
  if (expectedHash !== undefined) check(hash(raw) === expectedHash, 'TARGET_CHANGED');
  const target = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(raw));
  const required = ['environment', 'profile', 'coreManifestSha256', 'expectedGamePID', 'expectedGameStartTimeUtc', 'productLauncherPath'];
  check(target !== null && typeof target === 'object' && !Array.isArray(target), 'TARGET_INVALID');
  check(required.every(key => Object.hasOwn(target, key)) && Object.keys(target).every(key => [...required, 'readySummaryPath'].includes(key)), 'TARGET_FIELDS');
  check(typeof target.environment === 'string' && canonicalEnvironment(target.environment) === target.environment, 'ENVIRONMENT_INVALID');
  check(typeof target.profile === 'string' && isDeploymentIdentifier(target.profile), 'PROFILE_INVALID');
  check(typeof target.coreManifestSha256 === 'string' && /^[0-9a-f]{64}$/.test(target.coreManifestSha256), 'CORE_HASH_INVALID');
  check(Number.isSafeInteger(target.expectedGamePID) && target.expectedGamePID > 0 && target.expectedGamePID <= 2147483647, 'GAME_PID_INVALID');
  check(typeof target.expectedGameStartTimeUtc === 'string' && /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{7}Z$/.test(target.expectedGameStartTimeUtc), 'GAME_START_TIME_INVALID');
  for (const key of ['productLauncherPath', 'readySummaryPath']) if (target[key] !== undefined)
    check(typeof target[key] === 'string' && target[key].length <= 1024 && isAbsolute(target[key]), 'TARGET_PATH_INVALID');
  check(/\.exe$/i.test(target.productLauncherPath), 'PRODUCT_LAUNCHER_REQUIRED');
  check(isAbsolute(process.env.USERPROFILE ?? ''), 'USERPROFILE_REQUIRED');
  return { target, digest: hash(raw) };
}

// Compile the existing production read boundary and generated validators in memory.
// This is test glue, not a second discovery/schema implementation or a product build.
async function contractHelpers() {
  const { build } = createRequire(join(root, 'package.json'))('esbuild');
  const result = await build({ stdin: { contents: `
export { readConnectionDocument } from './src/host/src/game-client/discovery.ts';
export { requireContract, schemaIds } from './contracts/ts/index.ts';
export { productVersion, internalContractVersion } from './src/host/src/shared/identity.ts';`, resolveDir: root },
    bundle: true, platform: 'node', format: 'esm', target: 'node26', write: false, logLevel: 'silent' });
  return import(`data:text/javascript;base64,${Buffer.from(result.outputFiles[0].text).toString('base64')}`);
}

async function processIdentity(pid) {
  const command = `$ErrorActionPreference='Stop'; $p=Get-Process -Id ${pid}; [ordered]@{processId=$p.Id;processName=$p.ProcessName;processStartTimeUtc=$p.StartTime.ToUniversalTime().ToString('o')}|ConvertTo-Json -Compress`;
  return new Promise((resolvePromise, reject) => {
    execFile(join(process.env.SystemRoot ?? 'C:\\Windows', 'System32/WindowsPowerShell/v1.0/powershell.exe'),
      ['-NoProfile', '-NonInteractive', '-Command', command], { windowsHide: true, timeout: 5000, maxBuffer: 4096, encoding: 'utf8' },
      (error, stdout) => {
        if (error) { reject(new ProbeFailure('GAME_PROCESS_UNVERIFIED')); return; }
        try { resolvePromise(JSON.parse(stdout)); } catch { reject(new ProbeFailure('GAME_PROCESS_UNVERIFIED')); }
      });
  });
}

async function verifyTarget(target, helpers, pinned) {
  const observed = await processIdentity(target.expectedGamePID);
  check(observed.processId === target.expectedGamePID && observed.processName === 'Cities2'
    && observed.processStartTimeUtc === target.expectedGameStartTimeUtc, 'GAME_PROCESS_CHANGED');
  const discoveryPath = join(process.env.USERPROFILE, '.skylineslink/environments', target.environment, 'connection/discovery.json');
  const discovery = await helpers.readConnectionDocument(discoveryPath, 4096);
  helpers.requireContract(helpers.schemaIds.EnvironmentDiscovery, discovery);
  check(discovery.environmentId === target.environment && discovery.processId === target.expectedGamePID
    && discovery.coreManifestSha256 === target.coreManifestSha256 && discovery.productVersion === helpers.productVersion
    && discovery.contractVersion === helpers.internalContractVersion, 'DISCOVERY_TARGET_MISMATCH');
  if (pinned) equal(discovery, pinned, 'DISCOVERY_CHANGED');
  return discovery;
}

async function verifyActivation(target, helpers, pinned) {
  const path = join(process.env.USERPROFILE, '.skylineslink/environments', target.environment, 'activation.json');
  const activation = await helpers.readConnectionDocument(path, 4096);
  helpers.requireContract(helpers.schemaIds.ActivationRecord, activation);
  check(activation.coreManifestSha256 === target.coreManifestSha256 && activation.version === helpers.productVersion, 'ACTIVATION_TARGET_MISMATCH');
  if (pinned) equal(activation, pinned, 'ACTIVATION_CHANGED');
  return activation;
}

async function readySummary(target, helpers, pinned) {
  if (!target.readySummaryPath) return undefined;
  const value = await helpers.readConnectionDocument(target.readySummaryPath, 16384);
  check(value?.schemaVersion === 1 && value.productId === 'skylineslink' && value.processId === target.expectedGamePID
    && value.processStartTimeUtc === target.expectedGameStartTimeUtc && value.contextInstanceId === pinned.instanceId
    && value.userDataPathVerified === true && value.gameState === 'menu' && value.world === null
    && value.lifecyclePhase === 'active' && value.windowHiddenVerified === true, 'READY_SUMMARY_MISMATCH');
  check(value.targetFrameRate === 30 && value.vSyncCount === 0 && value.runInBackground === true, 'READY_BACKGROUND_MISMATCH');
  for (const key of ['connectedCount', 'authenticatedCount', 'ownedPayloadCount', 'ownedPayloadCapacity'])
    check(Number.isSafeInteger(value[key]) && value[key] >= 0, 'READY_METRICS_INVALID');
  return { connectedCount: value.connectedCount, authenticatedCount: value.authenticatedCount,
    ownedPayloadCount: value.ownedPayloadCount, ownedPayloadCapacity: value.ownedPayloadCapacity };
}

function sameRepresentation(result) {
  check(result?.structuredContent !== undefined && result.content?.length === 1 && result.content[0].type === 'text', 'RESULT_REPRESENTATION');
  equal(JSON.parse(result.content[0].text), result.structuredContent, 'RESULT_TEXT_MISMATCH');
  return result.structuredContent;
}

// SDK owns this relay; the relay alone owns its direct product Launcher child. This lets
// us observe a real EOF/exit without reading private SDK fields or mistaking its
// 2-second SIGTERM fallback for graceful shutdown.
async function relay(path, digest) {
  const { target } = await targetFrom(path, digest);
  const child = spawn(target.productLauncherPath, ['--profile', target.profile, '--environment', target.environment],
    { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  let eof = false, forced = false, stderrBytes = 0, outputBytes = 0, inputBytes = 0, deadline;
  const stopOwned = () => { if (child.exitCode === null && child.signalCode === null) { forced = true; child.kill(); } };
  const lifetime = setTimeout(stopOwned, 25000);
  process.stdin.on('data', chunk => { inputBytes += chunk.length; if (inputBytes > 1024 * 1024) stopOwned(); });
  child.stdout.on('data', chunk => { outputBytes += chunk.length; if (outputBytes > 1024 * 1024) stopOwned(); });
  child.stderr.on('data', chunk => { stderrBytes += chunk.length; if (stderrBytes > 8192) stopOwned(); });
  child.stdin.on('error', stopOwned); child.stdout.on('error', stopOwned); process.stdout.on('error', stopOwned);
  process.stdin.once('end', () => { eof = true; deadline = setTimeout(stopOwned, 1500); });
  process.stdin.once('error', stopOwned);
  process.once('SIGTERM', stopOwned); process.once('SIGINT', stopOwned);
  process.stdin.pipe(child.stdin); child.stdout.pipe(process.stdout, { end: false });
  await new Promise((resolvePromise, reject) => {
    child.once('error', () => {
      clearTimeout(lifetime); clearTimeout(deadline); process.stdin.destroy();
      reject(new ProbeFailure('LAUNCHER_SPAWN_FAILED'));
    });
    child.once('close', (code, signal) => {
      clearTimeout(lifetime); clearTimeout(deadline);
      process.stdin.unpipe(child.stdin); process.stdin.destroy();
      process.stderr.write(marker + JSON.stringify({ launcherPid: child.pid, eof, forced, code, signal, stderrBytes }) + '\n');
      process.exitCode = eof && !forced && code === 0 && signal === null && stderrBytes === 0 ? 0 : 1;
      resolvePromise();
    });
  });
}

async function run(path) {
  stage = 'target';
  check(process.platform === 'win32', 'WINDOWS_REQUIRED');
  const { target, digest } = await targetFrom(path);
  const helpers = await contractHelpers();
  stage = 'preflight';
  const launcherHash = hash(await boundedFile(target.productLauncherPath, 16 * 1024 * 1024));
  const pinnedActivation = await verifyActivation(target, helpers);
  const pinned = await verifyTarget(target, helpers);
  const initialMetrics = await readySummary(target, helpers, pinned);
  if (initialMetrics) equal(initialMetrics, { connectedCount: 0, authenticatedCount: 0, ownedPayloadCount: 0, ownedPayloadCapacity: 0 }, 'TARGET_ALREADY_IN_USE');
  const { Client } = require('@modelcontextprotocol/client');
  const { StdioClientTransport } = require('@modelcontextprotocol/client/stdio');
  const opened = [];
  const closed = [];
  const start = async modern => {
    await verifyActivation(target, helpers, pinnedActivation);
    const transport = new StdioClientTransport({ command: process.execPath, args: [import.meta.filename, '--relay', path, digest], stderr: 'pipe' });
    const client = new Client({ name: 'skylineslink-real-menu-readonly-probe', version: '0.1.0' },
      { capabilities: {}, versionNegotiation: { mode: modern ? { pin: '2026-07-28' } : 'legacy' } });
    const item = { client, transport, stderr: '', modern };
    opened.push(item);
    transport.stderr.on('data', chunk => { if (item.stderr.length < 8192) item.stderr += chunk.toString('utf8'); else item.overflow = true; });
    await client.connect(transport);
    item.relayPid = transport.pid;
    check(Number.isSafeInteger(item.relayPid) && item.relayPid !== target.expectedGamePID, 'RELAY_PROCESS_EVIDENCE');
    equal(client.getNegotiatedProtocolVersion(), modern ? '2026-07-28' : '2025-11-25', 'PROTOCOL_MISMATCH');
    const catalog = (await client.listTools()).tools;
    equal(catalog.map(tool => tool.name).sort(), tools, 'TOOL_CATALOG_MISMATCH');
    check(catalog.every(tool => tool.annotations?.readOnlyHint === true && tool.annotations?.destructiveHint === false), 'TOOL_EFFECT_MISMATCH');
    return item;
  };
  const call = (item, name, args) => item.client.callTool({ name, arguments: args }, undefined, { timeout: 5000 });
  try {
    stage = 'connect';
    const legacy = await start(false); const modern = await start(true);
    for (const item of [legacy, modern]) {
      stage = item.modern ? 'context-2026' : 'context-2025';
      await verifyActivation(target, helpers, pinnedActivation);
      await verifyTarget(target, helpers, pinned);
      const result = await call(item, 'skylines_context', {});
      check(result.isError !== true, 'CONTEXT_READ_FAILED');
      const context = sameRepresentation(result); helpers.requireContract(helpers.schemaIds.Context, context);
      check(context.instanceId === pinned.instanceId && context.productVersion === helpers.productVersion
        && context.contractVersion === helpers.internalContractVersion && context.gameState === 'menu' && context.world === null, 'CONTEXT_NOT_PINNED_MENU');
      for (const operationId of [`readonly-absent-${randomUUID()}`, `只读不存在-🛣️-${randomUUID()}`]) {
        stage = item.modern ? 'lookup-2026' : 'lookup-2025';
        const lookup = await call(item, 'skylines_operation_get', { selector: { kind: 'operationId', operationId } });
        check(lookup.isError !== true, 'LOOKUP_READ_FAILED');
        const content = sameRepresentation(lookup); helpers.requireContract(helpers.schemaIds.OperationLookupV3, content);
        equal(content, { selector: { kind: 'operationId', operationId }, result: { kind: 'live', instanceId: pinned.instanceId,
          lookup: { kind: 'absent' } } }, 'HEALTHY_ABSENCE_NOT_PROVEN');
      }
      const unknownSelector = { kind: 'submissionKey', submissionKey: { writeContextId: `未签发-${randomUUID()}`, requestId: '只读查询-🧭' } };
      const unknown = await call(item, 'skylines_operation_get', { selector: unknownSelector });
      check(unknown.isError !== true, 'UNKNOWN_KEY_READ_FAILED');
      const unknownContent = sameRepresentation(unknown); helpers.requireContract(helpers.schemaIds.OperationLookupV3, unknownContent);
      equal(unknownContent, { selector: unknownSelector, result: { kind: 'live', instanceId: pinned.instanceId,
        lookup: { kind: 'unavailable', reason: 'CONTEXT_UNKNOWN' } } }, 'UNKNOWN_CONTEXT_NOT_ABSENT');
      stage = item.modern ? 'invalid-2026' : 'invalid-2025';
      for (const [name, args] of [['skylines_context', { extra: true }], ['skylines_operation_get', { operationId: 'old-v1-shape' }]]) {
        const rejected = await call(item, name, args);
        check(rejected.isError === true, 'INVALID_ARGUMENT_ACCEPTED');
      }
    }
    if (initialMetrics) {
      stage = 'observe-two-connections';
      let observedActive = false;
      for (let attempt = 0; attempt < 12; attempt++) {
        const current = await readySummary(target, helpers, pinned);
        if (current.connectedCount === 2 && current.authenticatedCount === 2) { observedActive = true; break; }
        await sleep(250);
      }
      check(observedActive, 'ACTIVE_GAME_CONNECTIONS_UNOBSERVED');
    }
  } finally {
    const failures = [];
    for (const item of opened.reverse()) {
      try {
        await item.client.close();
        check(!item.overflow, 'RELAY_DIAGNOSTIC_BUDGET');
        const lines = item.stderr.trim().split(/\r?\n/);
        check(lines.length === 1 && lines[0].startsWith(marker), 'RELAY_EXIT_EVIDENCE_MISSING');
        const evidence = JSON.parse(lines[0].slice(marker.length));
        check(evidence.eof === true && evidence.forced === false && evidence.code === 0 && evidence.signal === null && evidence.stderrBytes === 0, 'LAUNCHER_EOF_NOT_GRACEFUL');
        check(Number.isSafeInteger(evidence.launcherPid) && evidence.launcherPid !== target.expectedGamePID && evidence.launcherPid !== item.relayPid, 'LAUNCHER_PROCESS_EVIDENCE');
        let relayGone = false;
        try { process.kill(item.relayPid, 0); } catch (error) { if (error.code === 'ESRCH') relayGone = true; }
        check(relayGone, 'RELAY_EXIT_UNVERIFIED');
        closed.push({ protocol: item.modern ? '2026-07-28' : '2025-11-25', launcherPid: evidence.launcherPid, relayPid: item.relayPid, gracefulEof: true });
      } catch (error) { failures.push(error); }
    }
    if (failures.length) throw new ProbeFailure('OWNED_LAUNCHER_CLEANUP_UNVERIFIED');
  }
  stage = 'final-state';
  await verifyActivation(target, helpers, pinnedActivation);
  await verifyTarget(target, helpers, pinned);
  equal(hash(await boundedFile(target.productLauncherPath, 16 * 1024 * 1024)), launcherHash, 'PRODUCT_LAUNCHER_CHANGED');
  let resourceSummaryVerified = false;
  if (initialMetrics) {
    for (let attempt = 0; attempt < 12; attempt++) {
      const current = await readySummary(target, helpers, pinned);
      if (isDeepStrictEqual(current, initialMetrics)) { resourceSummaryVerified = true; break; }
      await sleep(250);
    }
    check(resourceSummaryVerified, 'GAME_CONNECTION_RESOURCES_REMAIN');
  }
  process.stdout.write(JSON.stringify({ kind: 'readonly-game-validation', passed: true, gamePID: target.expectedGamePID,
    instanceId: pinned.instanceId, gameState: 'menu', world: null, productLauncherSha256: launcherHash,
    protocols: ['2025-11-25', '2026-07-28'], tools, children: closed, resourceSummaryVerified,
    ordinaryUserVerified: false, actualCodexOrDshVerified: false, nativeLoadVerified: false }) + '\n');
}

try {
  const args = process.argv.slice(2);
  if (args.length === 1 && args[0] === '--help') process.stdout.write(help);
  else if (args.length === 2 && args[0] === '--target') await run(args[1]);
  else if (args.length === 3 && args[0] === '--relay') await relay(args[1], args[2]);
  else { process.stderr.write(help); process.exitCode = 2; }
} catch (error) {
  // Never print arbitrary child/SDK/file errors, input objects, paths or credentials.
  process.stderr.write(JSON.stringify({ kind: 'readonly-game-validation-failed', stage, code: error instanceof ProbeFailure ? error.code : 'PROBE_FAILED' }) + '\n');
  process.exitCode = 1;
}
