import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { lstat, mkdir, readFile, realpath, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { archiveFiles } from './core.mjs';
import { rejectPrivatePrefixes, validateSourcePath } from './core-policy.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const maximumArchive = 64 * 1024 * 1024;
const required = [
  'README.md', 'README.zh-CN.md', 'LICENSE', 'package.json', 'pnpm-lock.yaml',
  'global.json', 'SkylinesLink.slnx', 'docs/architecture.md', 'docs/development/preview.md',
  'src/host/package.json', 'src/game/SkylinesLink.Mod/SkylinesLink.Mod.csproj',
  'src/game/SkylinesLink.Application/SkylinesLink.Application.csproj',
  'src/game/SkylinesLink.Infrastructure/SkylinesLink.Infrastructure.csproj',
  'contracts/cs/SkylinesLink.Contracts/SkylinesLink.Contracts.csproj',
];
function git(...args) {
  const result = spawnSync('git', args, { cwd: root, windowsHide: true, maxBuffer: maximumArchive });
  if (result.error || result.status !== 0) throw new Error('SOURCE_PREVIEW_GIT_FAILED');
  return result.stdout;
}
function text(...args) { return git(...args).toString('utf8').trim(); }
function clean() {
  if (text('status', '--porcelain=v1', '--untracked-files=all')) throw new Error('SOURCE_PREVIEW_DIRTY');
}
function digest(bytes) { return createHash('sha256').update(bytes).digest('hex'); }

// This is the reviewed public source tree, never an exclusion-based archive of
// an internal branch. Validate every tracked path and every archived blob.
export async function buildSourcePreview() {
  clean();
  const sourceCommit = text('rev-parse', 'HEAD');
  const sourceTree = text('rev-parse', 'HEAD^{tree}');
  const blobs = new Map();
  for (const entry of git('ls-tree', '-r', '-z', 'HEAD').toString('utf8').split('\0').filter(Boolean)) {
    const row = /^(\d+) blob ([a-f0-9]{40})\t([\s\S]+)$/.exec(entry);
    if (!row) throw new Error('SOURCE_PREVIEW_TREE');
    const [, mode, blob, path] = row;
    validateSourcePath(path, mode);
    if (/^(?:openspec|docs\/(?:research|requirements)|\.agents|\.dsh)(?:\/|$)/i.test(path)
      || /^(?:AGENTS\.md|docs\/development\/foundation-validation\.md)$/i.test(path))
      throw new Error('SOURCE_PREVIEW_INTERNAL_MATERIAL');
    blobs.set(path, blob);
  }
  if (required.some(path => !blobs.has(path))) throw new Error('SOURCE_PREVIEW_INCOMPLETE');
  const pkg = JSON.parse(await readFile(join(root, 'package.json'), 'utf8'));
  if (pkg.name !== 'skylineslink' || !/^\d+\.\d+\.\d+-dev\.\d+$/.test(pkg.version))
    throw new Error('SOURCE_PREVIEW_VERSION');
  const privatePrefixes = [root, process.env.USERPROFILE].filter(Boolean);
  for (const [path, blob] of blobs) {
    if (path.endsWith('.png')) continue;
    const bytes = git('cat-file', 'blob', blob);
    rejectPrivatePrefixes(bytes, privatePrefixes);
    if (/(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----)/.test(bytes.toString('utf8')))
      throw new Error('SOURCE_PREVIEW_SENSITIVE_CONTENT');
  }
  const archive = git('archive', '--format=zip', sourceCommit);
  archiveFiles(archive, blobs);
  clean();
  if (text('rev-parse', 'HEAD') !== sourceCommit) throw new Error('SOURCE_PREVIEW_CHANGED');
  const name = `SkylinesLink-${pkg.version}-source.zip`;
  const manifest = {
    formatVersion: 1, productId: 'skylineslink', productVersion: pkg.version,
    distribution: 'developer-source-preview', sourceCommit, sourceTree, sourceFiles: blobs.size,
    archive: { path: name, size: archive.length, sha256: digest(archive) },
    playerInstallationPackage: false, gameRuntimeValidatedByPackaging: false,
  };
  const metadata = Buffer.from(`${JSON.stringify(manifest, null, 2)}\n`);
  const output = join(root, 'artifacts/source-preview');
  const artifacts = join(root, 'artifacts');
  await mkdir(artifacts, { recursive: true });
  const directory = await lstat(artifacts);
  if (!directory.isDirectory() || directory.isSymbolicLink()
    || (await realpath(artifacts)).toLowerCase() !== artifacts.toLowerCase())
    throw new Error('SOURCE_PREVIEW_OUTPUT_REDIRECTED');
  // Refuse existing output; a rerun must not replace an already reviewed asset.
  await mkdir(output);
  await writeFile(join(output, name), archive, { flag: 'wx' });
  await writeFile(join(output, 'source-manifest.json'), metadata, { flag: 'wx' });
  await writeFile(join(output, 'SHA256SUMS.txt'),
    `${digest(archive)}  ${name}\n${digest(metadata)}  source-manifest.json\n`, { flag: 'wx' });
  return manifest;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { console.log(JSON.stringify(await buildSourcePreview())); }
  catch (error) {
    console.error(JSON.stringify({ code: /^(?:SOURCE_PREVIEW|CORE)_[A-Z_]+$/.test(error.message) ? error.message : 'SOURCE_PREVIEW_FAILED' }));
    process.exitCode = 1;
  }
}
