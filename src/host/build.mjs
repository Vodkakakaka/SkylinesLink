import { build } from 'esbuild';
import { createHash } from 'node:crypto';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { dirname, resolve, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('.', import.meta.url));
const licenseRoot = resolve(root, 'licenses');
const manifest = JSON.parse(await readFile(resolve(licenseRoot, 'manifest.json'), 'utf8'));
if (manifest.formatVersion !== 1) throw new Error('Unsupported Host license manifest.');
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const reviewed = new Map(manifest.packages.map(entry => [entry.name, entry]));
if (reviewed.size !== manifest.packages.length) throw new Error('Duplicate Host license entry.');

// Audit the actual output in memory before writing any candidate artifact.
const result = await build({
  absWorkingDir: root,
  entryPoints: ['src/composition/main.ts'],
  outfile: '../../artifacts/host/host.mjs',
  bundle: true,
  platform: 'node',
  target: 'node26',
  format: 'esm',
  sourcemap: false,
  write: false,
  metafile: true,
  legalComments: 'external',
  tsconfigRaw: { compilerOptions: { verbatimModuleSyntax: true } },
  banner: { js: "// SkylinesLink Host: mechanically bundled and tree-shaken; see host.THIRD_PARTY_NOTICES.txt.\nimport { createRequire } from 'node:module'; const require = createRequire(import.meta.url);" },
});

const inputs = new Set(Object.values(result.metafile.outputs).flatMap(output =>
  Object.entries(output.inputs).filter(([, value]) => value.bytesInOutput > 0).map(([path]) => path)));
const packageRoots = new Map();
const embeddedMarkers = new Set();
const inputHashes = new Map();
for (const input of inputs) {
  const path = resolve(root, input).replaceAll('\\', '/');
  const separator = '/node_modules/';
  const index = path.lastIndexOf(separator);
  if (index < 0) continue;
  const pieces = path.slice(index + separator.length).split('/');
  const name = pieces[0].startsWith('@') ? pieces.slice(0, 2).join('/') : pieces[0];
  const packageRoot = resolve(root, path.slice(0, index + separator.length), name);
  if (packageRoots.has(name) && packageRoots.get(name) !== packageRoot) throw new Error(`Multiple bundled versions of ${name}.`);
  packageRoots.set(name, packageRoot);
  const content = await readFile(resolve(root, input));
  inputHashes.set(resolve(root, input), hash(content));
  for (const match of content.toString('utf8').matchAll(/\/\/#region .*node_modules\/\.pnpm\/([^/]+)\/node_modules\//g)) embeddedMarkers.add(match[1]);
}

for (const [name, packageRoot] of packageRoots) {
  const entry = reviewed.get(name);
  const actual = JSON.parse(await readFile(resolve(packageRoot, 'package.json'), 'utf8'));
  if (!entry || entry.role !== 'direct-input' || actual.name !== name || actual.version !== entry.version || actual.license !== entry.license) {
    throw new Error(`Unreviewed bundled package or version: ${name}.`);
  }
  if (hash(await readFile(resolve(packageRoot, 'LICENSE'))) !== entry.licenseSha256) throw new Error(`Installed license changed: ${name}.`);
}
const expectedMarkers = new Set(manifest.packages.filter(entry => entry.role === 'sdk-embedded').map(entry => entry.embeddedMarker));
if (embeddedMarkers.size !== expectedMarkers.size || [...embeddedMarkers].some(marker => !expectedMarkers.has(marker))) {
  throw new Error('SDK embedded dependency evidence changed; review Host notices.');
}
for (const evidence of manifest.embeddedEvidence) {
  const packageRoot = packageRoots.get(evidence.package);
  if (!packageRoot || reviewed.get(evidence.package)?.version !== evidence.version
    || inputHashes.get(resolve(packageRoot, evidence.path)) !== evidence.sha256) throw new Error('SDK embedded source evidence changed.');
}

const sections = ['SkylinesLink Host third-party notices', manifest.scope,
  'Upstream code in host.mjs is mechanically bundled and tree-shaken. Upstream source packages were not edited. Original license texts follow; these notices do not replace the licenses of SkylinesLink or other Core components.'];
for (const entry of manifest.packages) {
  if (entry.role === 'direct-input' && !packageRoots.has(entry.name)) throw new Error(`Reviewed input no longer present: ${entry.name}.`);
  if (!['direct-input', 'sdk-embedded'].includes(entry.role) || !/^[A-Za-z0-9._-]+\.txt$/.test(entry.licenseFile)) throw new Error('Invalid Host license entry.');
  const text = await readFile(resolve(licenseRoot, entry.licenseFile));
  if (hash(text) !== entry.licenseSha256) throw new Error(`Original license is missing or changed: ${entry.name}.`);
  sections.push(`===== ${entry.name}@${entry.version} =====\nPackage license metadata: ${entry.license}\nSource: ${entry.sourceTarball}\nSource integrity: ${entry.sourceIntegrity}\nOriginal file: ${entry.sourceLicensePath}\nCoverage: ${entry.role === 'sdk-embedded' ? 'conservative complete license for SDK prebundled module' : 'package contributing to this bundle'}\n\n${text.toString('utf8')}`);
}
const outputRoot = resolve(root, '../../artifacts/host');
await mkdir(outputRoot, { recursive: true });
for (const output of result.outputFiles) {
  if (dirname(output.path) !== outputRoot) throw new Error(`Unexpected Host output: ${relative(root, output.path)}.`);
  await writeFile(output.path, output.contents);
}
await writeFile(resolve(outputRoot, 'host.THIRD_PARTY_NOTICES.txt'), sections.join('\n\n') + '\n');
