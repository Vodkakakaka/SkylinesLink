import { constants, createReadStream } from 'node:fs';
import { copyFile, lstat, mkdir, readFile, readdir, realpath, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { dirname, join, relative, resolve } from 'node:path';
import { inflateRawSync } from 'node:zlib';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { verifyNodeRuntime } from './runtime-inputs.mjs';
import { managedNames, managedComponentIndex, meetsMinimumVersion, rejectPrivatePrefixes, requireInspectedBytes, validateManagedClosure, validateOutputInventory, validateSourcePath } from './core-policy.mjs';

const root = resolve(import.meta.dirname, '../..');
const maximumFile = 256 * 1024 * 1024;
function run(command, args, capture = true) {
  const result = spawnSync(command, args, { cwd: root, windowsHide: true, encoding: 'utf8',
    maxBuffer: 4 * 1024 * 1024, stdio: capture ? ['ignore', 'pipe', 'pipe'] : ['ignore', 'inherit', 'inherit'],
    env: { ...process.env, RestoreLockedMode: 'true' } });
  if (result.error || result.status !== 0) throw new Error(result.stderr?.match(/\bCORE_[A-Z_]+\b/)?.[0] ?? 'CORE_COMMAND_FAILED');
  return capture ? result.stdout.trim() : '';
}
function git(...args) { return run('git', args); }
function cleanCommit() {
  if (git('status', '--porcelain=v1', '--untracked-files=all') !== '') throw new Error('CORE_SOURCE_DIRTY');
  const commit = git('rev-parse', '--verify', 'HEAD');
  if (!/^[0-9a-f]{40}$/.test(commit)) throw new Error('CORE_SOURCE_COMMIT');
  return commit;
}
async function absent(path) {
  try { await lstat(path); throw new Error('CORE_EXISTING_BUILD_OUTPUT'); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
}
async function ordinary(path, directory = false) {
  const expected = resolve(path);
  for (let item = expected; ; item = dirname(item)) {
    const value = await lstat(item);
    if (value.isSymbolicLink() || (item === expected && !directory ? !value.isFile() : !value.isDirectory())) throw new Error('CORE_REPARSE_OR_FILE_KIND');
    if (dirname(item) === item) break;
  }
  if ((await realpath(expected)).toLowerCase() !== expected.toLowerCase()) throw new Error('CORE_REDIRECTED_INPUT');
  const value = await lstat(expected, { bigint: true });
  if (!directory && (value.size < 1n || value.size > BigInt(maximumFile))) throw new Error('CORE_FILE_BUDGET');
  return value;
}
async function fingerprint(path, privatePrefixes) {
  const before = await ordinary(path);
  const digest = createHash('sha256');
  let count = 0, tail = Buffer.alloc(0);
  const overlap = Math.max(1, ...privatePrefixes.map(value => value.length * 2 + 4));
  for await (const part of createReadStream(path, { highWaterMark: 65536 })) {
    count += part.length; if (count > maximumFile) throw new Error('CORE_FILE_BUDGET');
    digest.update(part);
    const joined = Buffer.concat([tail, part]); rejectPrivatePrefixes(joined, privatePrefixes);
    tail = Buffer.from(joined.subarray(Math.max(0, joined.length - overlap)));
  }
  const after = await ordinary(path);
  if (before.dev !== after.dev || before.ino !== after.ino || before.size !== after.size || before.mtimeNs !== after.mtimeNs || BigInt(count) !== after.size) throw new Error('CORE_INPUT_CHANGED');
  return { size: count, sha256: digest.digest('hex') };
}
async function inventory(path, kind) {
  await ordinary(path, true);
  const entries = await readdir(path, { withFileTypes: true });
  if (entries.some(entry => !entry.isFile())) throw new Error('CORE_UNEXPECTED_BUILD_OUTPUT');
  validateOutputInventory(kind, entries.map(entry => entry.name));
}

// Git ZIP is generated here, not accepted from outside. Check its exact file set so
// local export-ignore/substitution cannot silently remove matching source material.
export function archiveFiles(bytes, expectedBlobs) {
  if (bytes.length > 64 * 1024 * 1024) throw new Error('CORE_SOURCE_ARCHIVE_BUDGET');
  let end = bytes.length - 22;
  while (end >= Math.max(0, bytes.length - 65557) && bytes.readUInt32LE(end) !== 0x06054b50) end--;
  if (end < 0 || end < bytes.length - 65557 || bytes.readUInt16LE(end + 4) !== 0 || bytes.readUInt16LE(end + 6) !== 0) throw new Error('CORE_SOURCE_ARCHIVE');
  const count = bytes.readUInt16LE(end + 10), length = bytes.readUInt32LE(end + 12), offset = bytes.readUInt32LE(end + 16);
  if (count < 1 || count > 4096 || count !== bytes.readUInt16LE(end + 8) || offset + length !== end) throw new Error('CORE_SOURCE_ARCHIVE');
  const files = []; let expanded = 0;
  let cursor = offset;
  for (let i = 0; i < count; i++) {
    if (cursor + 46 > end || bytes.readUInt32LE(cursor) !== 0x02014b50) throw new Error('CORE_SOURCE_ARCHIVE');
    const size = bytes.readUInt16LE(cursor + 28), extra = bytes.readUInt16LE(cursor + 30), comment = bytes.readUInt16LE(cursor + 32);
    if (cursor + 46 + size + extra + comment > end) throw new Error('CORE_SOURCE_ARCHIVE');
    const name = new TextDecoder('utf-8', { fatal: true }).decode(bytes.subarray(cursor + 46, cursor + 46 + size));
    if (!name.endsWith('/')) {
      files.push(name);
      if (expectedBlobs) {
        const compressed = bytes.readUInt32LE(cursor + 20), uncompressed = bytes.readUInt32LE(cursor + 24), local = bytes.readUInt32LE(cursor + 42);
        const method = bytes.readUInt16LE(cursor + 10);
        expanded += uncompressed;
        if (!expectedBlobs.has(name) || uncompressed > 8 * 1024 * 1024 || expanded > 64 * 1024 * 1024 || local + 30 > offset
          || bytes.readUInt32LE(local) !== 0x04034b50 || (bytes.readUInt16LE(cursor + 8) & 1) !== 0 || ![0, 8].includes(method)) throw new Error('CORE_SOURCE_ARCHIVE_CONTENT');
        const start = local + 30 + bytes.readUInt16LE(local + 26) + bytes.readUInt16LE(local + 28);
        if (start + compressed > offset) throw new Error('CORE_SOURCE_ARCHIVE_CONTENT');
        const data = bytes.subarray(start, start + compressed);
        const plain = method === 0 ? data : inflateRawSync(data, { maxOutputLength: 8 * 1024 * 1024 });
        const blob = createHash('sha1').update(`blob ${plain.length}\0`).update(plain).digest('hex');
        if (plain.length !== uncompressed || blob !== expectedBlobs.get(name)) throw new Error('CORE_SOURCE_ARCHIVE_CONTENT');
      }
    }
    cursor += 46 + size + extra + comment;
  }
  if (cursor !== end || new Set(files).size !== files.length) throw new Error('CORE_SOURCE_ARCHIVE');
  return files.sort();
}

async function trackedInputs(tracked) {
  const known = new Set(tracked); let visited = 0;
  // pnpm dependencies are governed by the lock/license inputs; official Mod Library
  // is generated state explicitly excluded by the Mod project, not product source.
  async function walk(path) {
    for (const entry of await readdir(join(root, path), { withFileTypes: true })) {
      const child = `${path}/${entry.name}`;
      if (entry.name === 'node_modules' || child === 'src/game/SkylinesLink.Mod/Library') continue;
      if (++visited > 8192 || entry.isSymbolicLink()) throw new Error('CORE_UNTRACKED_INPUT');
      if (entry.isDirectory()) await walk(child);
      else if (/\.(?:cs|csproj|props|targets|resx|ts|mjs|js|json|ps1|template)$/i.test(entry.name) && !known.has(child)) throw new Error('CORE_UNTRACKED_INPUT');
    }
  }
  for (const directory of ['src', 'contracts', 'tools']) await walk(directory);
  const projects = ['contracts/cs/SkylinesLink.Contracts/SkylinesLink.Contracts.csproj',
    ...['Application', 'Infrastructure', 'Mod'].map(name => `src/game/SkylinesLink.${name}/SkylinesLink.${name}.csproj`),
    ...['Deployment', 'Launcher', 'Setup'].map(name => `src/desktop/SkylinesLink.${name}/SkylinesLink.${name}.csproj`)];
  let compileInputs = 0;
  for (const project of projects) {
    const evaluated = JSON.parse(run('dotnet', ['msbuild', project, '-nologo', '-p:Configuration=Release', '-p:SkylinesLinkIntegrationProbe=false', '-getItem:Compile,EmbeddedResource,AdditionalFiles']));
    for (const kind of ['Compile', 'EmbeddedResource', 'AdditionalFiles']) for (const item of evaluated.Items[kind]) {
      if (!known.has(relative(root, item.FullPath).replaceAll('\\', '/'))) throw new Error('CORE_UNTRACKED_COMPILE_INPUT');
      compileInputs++;
    }
  }
  return compileInputs;
}

async function helpers() {
  const { build } = createRequire(join(root, 'package.json'))('esbuild');
  const result = await build({ stdin: { contents: `export { requireContract, schemaIds } from './contracts/ts/index.ts'; export { productVersion, internalContractVersion } from './src/host/src/shared/identity.ts';`, resolveDir: root },
    bundle: true, platform: 'node', format: 'esm', target: 'node26', write: false, logLevel: 'silent' });
  return import(`data:text/javascript;base64,${Buffer.from(result.outputFiles[0].text).toString('base64')}`);
}

export async function buildCore() {
  if (process.platform !== 'win32') throw new Error('CORE_WINDOWS_REQUIRED');
  const sourceCommit = cleanCommit();
  for (const name of ['host', 'nativeaot', 'dotnet', 'developer-core']) await absent(join(root, 'artifacts', name));
  await ordinary(root, true);
  const pkg = JSON.parse(await readFile(join(root, 'package.json'), 'utf8'));
  if (!meetsMinimumVersion(process.version, pkg.engines.node)
    || !meetsMinimumVersion(run('pwsh', ['-NoProfile', '-NonInteractive', '-Command', 'pnpm --version']), pkg.engines.pnpm)) throw new Error('CORE_TOOL_VERSION');
  // nvm's stable command may be a managed junction. Resolve only this explicit input;
  // all ordinary source/Core paths keep their strict ancestor/reparse checks.
  const nodeExe = await realpath(process.execPath);
  await ordinary(nodeExe);
  const node = await verifyNodeRuntime(nodeExe);
  const privatePrefixes = [root, process.env.USERPROFILE ?? ''].filter(Boolean);
  const blobs = new Map();
  const tracked = git('ls-files', '--stage', '-z').split('\0').filter(Boolean).map(record => {
    const match = /^(\d+) ([0-9a-f]{40}) 0\t([\s\S]+)$/.exec(record);
    if (!match) throw new Error('CORE_SOURCE_INDEX');
    validateSourcePath(match[3], match[1]); blobs.set(match[3], match[2]); return match[3];
  }).sort();
  if (tracked.length < 1 || tracked.length > 4096) throw new Error('CORE_SOURCE_BUDGET');
  let sourceBytes = 0;
  for (const path of tracked) {
    // Empty .gitkeep files are allowed in source; they are not payload files.
    const file = join(root, path); const stat = await lstat(file);
    sourceBytes += stat.size;
    if (stat.size > 8 * 1024 * 1024 || sourceBytes > 64 * 1024 * 1024) throw new Error('CORE_SOURCE_BUDGET');
    if (stat.size === 0 && !stat.isSymbolicLink()) continue;
    await fingerprint(file, privatePrefixes);
    if (path.endsWith('.gitattributes') && /\bexport-(?:ignore|subst)\b/.test(await readFile(file, 'utf8'))) throw new Error('CORE_SOURCE_ARCHIVE_ATTRIBUTES');
  }
  if (cleanCommit() !== sourceCommit) throw new Error('CORE_SOURCE_CHANGED');
  const compileInputs = await trackedInputs(tracked);
  // Fresh build roots are mandatory; existing developer caches are never deleted/reused.
  run('pwsh', ['-NoProfile', '-NonInteractive', '-Command', 'pnpm check:generated; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; pnpm typecheck; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; pnpm build:host; exit $LASTEXITCODE'], false);
  run('pwsh', ['-NoProfile', '-NonInteractive', '-File', 'tools/build/publish-desktop.ps1'], false);
  run('pwsh', ['-NoProfile', '-NonInteractive', '-File', 'tools/build/build-mod.ps1', '-Configuration', 'Release'], false);
  if (cleanCommit() !== sourceCommit) throw new Error('CORE_SOURCE_CHANGED');
  if (await trackedInputs(tracked) !== compileInputs) throw new Error('CORE_COMPILE_INPUTS_CHANGED');

  const contract = await helpers();
  if (contract.productVersion !== pkg.version || !/^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$/.test(pkg.version)) throw new Error('CORE_PRODUCT_VERSION');
  const mod = join(root, 'artifacts/dotnet/bin/SkylinesLink.Mod/release');
  const host = join(root, 'artifacts/host'), launcher = join(root, 'artifacts/nativeaot/launcher'), setup = join(root, 'artifacts/nativeaot/setup');
  for (const [path, kind] of [[mod, 'mod'], [host, 'host'], [launcher, 'Launcher'], [setup, 'Setup']]) await inventory(path, kind);
  const images = JSON.parse(run('pwsh', ['-NoProfile', '-NonInteractive', '-File', 'tools/release/inspect-core-inputs.ps1', '-ModDirectory', mod, '-LauncherDirectory', launcher, '-SetupDirectory', setup]));
  const informationalVersion = `${pkg.version}+${sourceCommit}`;
  validateManagedClosure(images.assemblies, `${pkg.version.split('-')[0]}.0`, informationalVersion);
  if (images.native.some(image => image.file.endsWith('.exe') && image.informationalVersion !== informationalVersion)) throw new Error('CORE_NATIVE_VERSION');
  const inspected = new Map([...images.assemblies, ...images.native].map(image => [image.file, { size: image.size, sha256: image.sha256 }]));

  const output = join(root, 'artifacts/developer-core'), core = join(output, 'core');
  await mkdir(output); await mkdir(core);
  const sources = [
    ...managedNames.map(name => [join(mod, `${name}.dll`), `mod/${name}.dll`]),
    ...['host.mjs', 'host.mjs.LEGAL.txt', 'host.THIRD_PARTY_NOTICES.txt'].map(name => [join(host, name), `host/${name}`]),
    [nodeExe, 'runtime/node.exe'], [node.license.sourcePath, node.license.relativePath],
    [join(root, 'LICENSE'), 'LICENSE'],
  ];
  if (images.windowsBurstPresent) sources.push([join(mod, 'SkylinesLink.Mod_win_x86_64.dll'), 'mod/SkylinesLink.Mod_win_x86_64.dll']);
  for (const [name, directory] of [['Launcher', launcher], ['Setup', setup]])
    for (const file of [`SkylinesLink.${name}.exe`, 'DOTNET.LICENSE.txt', 'DOTNET.THIRD-PARTY-NOTICES.txt', 'DOTNET.SOURCES.txt']) sources.push([join(directory, file), `desktop/${name.toLowerCase()}/${file}`]);
  const files = [];
  for (const [source, path] of sources) {
    const before = await fingerprint(source, privatePrefixes), destination = join(core, path);
    const metadataBytes = inspected.get(path.split('/').at(-1));
    if (metadataBytes) requireInspectedBytes(metadataBytes, before);
    await mkdir(dirname(destination), { recursive: true }); await ordinary(dirname(destination), true);
    await copyFile(source, destination, constants.COPYFILE_EXCL);
    const copied = await fingerprint(destination, privatePrefixes);
    if (JSON.stringify(before) !== JSON.stringify(copied)) throw new Error('CORE_COPY_CHANGED');
    files.push({ path, ...copied });
  }
  await verifyNodeRuntime(join(core, 'runtime/node.exe'));
  const moduleIndex = managedComponentIndex(images.assemblies, files);
  contract.requireContract(contract.schemaIds.ManagedComponents, moduleIndex);
  const moduleIndexPath = 'mod/managed-components.json';
  await writeFile(join(core, moduleIndexPath), JSON.stringify(moduleIndex) + '\n', { flag: 'wx' });
  files.push({ path: moduleIndexPath, ...await fingerprint(join(core, moduleIndexPath), privatePrefixes) });
  await mkdir(join(core, 'source'));
  const archive = join(core, 'source/SkylinesLink-source.zip');
  run('git', ['archive', '--format=zip', `--output=${archive}`, sourceCommit]);
  if ((await ordinary(archive)).size > 64n * 1024n * 1024n) throw new Error('CORE_SOURCE_ARCHIVE_BUDGET');
  const archiveSet = archiveFiles(await readFile(archive), blobs);
  if (JSON.stringify(archiveSet) !== JSON.stringify(tracked)) throw new Error('CORE_SOURCE_ARCHIVE_SET');
  files.push({ path: 'source/SkylinesLink-source.zip', ...await fingerprint(archive, []) });
  const sourceNotice = `SkylinesLink / 天际互联\nDeveloper foundation candidate; not a usable initial release.\nDeclared tools and validation limits are documented in README.md and docs/architecture.md.\nEarlier checks do not qualify this specific artifact; ordinary-user, clean-machine and marketplace acceptance remain incomplete.\n\nProduct version: ${pkg.version}\nSource commit: ${sourceCommit}\nMatching source is included in source/SkylinesLink-source.zip. Original project source is under MPL-2.0; see LICENSE. Third-party original notices remain adjacent to their components.\nOfficial repository identity: https://github.com/Vodkakakaka/SkylinesLink\nCommit identity: https://github.com/Vodkakakaka/SkylinesLink/tree/${sourceCommit}\nThis local build does not upload the commit or assert that this URL is remotely available. Use the included source archive.\n\nThis directory is a developer candidate, not an installer or a Paradox Mods upload. No activation, authorization, client registration or game files were changed by this build.\n`;
  await writeFile(join(core, 'SOURCE.txt'), sourceNotice, { flag: 'wx' });
  files.push({ path: 'SOURCE.txt', ...await fingerprint(join(core, 'SOURCE.txt'), privatePrefixes) });
  files.sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
  const manifest = { productId: 'skylineslink', version: pkg.version, sourceCommit, contractVersion: contract.internalContractVersion, files };
  contract.requireContract(contract.schemaIds.CoreManifest, manifest);
  await writeFile(join(core, 'core-manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`, { flag: 'wx' });
  if (cleanCommit() !== sourceCommit) throw new Error('CORE_SOURCE_CHANGED');
  // This is the existing verified native consumer, not another manifest implementation.
  run(join(core, 'desktop/setup/SkylinesLink.Setup.exe'), ['verify-core', core], false);
  if (cleanCommit() !== sourceCommit) throw new Error('CORE_SOURCE_CHANGED');
  const manifestFact = await fingerprint(join(core, 'core-manifest.json'), privatePrefixes);
  const report = { kind: 'developer-core-candidate', sourceCommit, sourceTree: git('rev-parse', 'HEAD^{tree}'), version: pkg.version,
    platform: 'win-x64', coreManifestSha256: manifestFact.sha256, payloadFiles: files.length, sourceFiles: tracked.length, evaluatedCompileInputs: compileInputs,
    totalBytes: files.reduce((total, file) => total + file.size, 0), images, setupVerified: true, sourceArchiveIncluded: true,
    privacyCheck: 'tracked file policy, PE debug-path boundary and local workspace/user-prefix scan only; complete publication review still required',
    gameValidated: false, cleanMachineValidated: false, ordinaryUserValidated: false, marketplaceValidated: false, published: false };
  await writeFile(join(output, 'validation.json'), `${JSON.stringify(report, null, 2)}\n`, { flag: 'wx' });
  return report;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    if (process.argv.length !== 3 || process.argv[2] !== 'build') throw new Error('CORE_COMMAND_REQUIRED');
    const result = await buildCore();
    console.log(JSON.stringify({ kind: result.kind, sourceCommit: result.sourceCommit, coreManifestSha256: result.coreManifestSha256, payloadFiles: result.payloadFiles, published: false }));
  } catch (error) {
    console.error(JSON.stringify({ kind: 'developer-core-failed', code: /^CORE_[A-Z_]+$/.test(error.message) ? error.message : 'CORE_BUILD_FAILED' }));
    process.exitCode = 1;
  }
}
