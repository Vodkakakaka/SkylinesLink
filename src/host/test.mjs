import { build } from 'esbuild';
import { readdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { spawn } from 'node:child_process';

// Product-process tests launch this bundle, not the separately bundled test code.
// Build it from the same source first so stale artifacts cannot mask or invent regressions.
await import('./build.mjs');

const entries = (await readdir('../../tests/host')).filter(name => name.endsWith('.ts'));
await build({
  entryPoints: entries.map(name => `../../tests/host/${name}`),
  outdir: '../../artifacts/host-tests', outExtension: { '.js': '.mjs' },
  bundle: true, platform: 'node', format: 'esm', target: 'node26',
  nodePaths: [resolve('node_modules')], sourcemap: false, legalComments: 'none',
  tsconfigRaw: { compilerOptions: { verbatimModuleSyntax: true } },
  banner: { js: "import { createRequire } from 'node:module'; const require = createRequire(import.meta.url);" },
});
const selected = process.argv.slice(2);
const tests = entries.filter(name => name.endsWith('.test.ts') && (selected.length === 0 || selected.includes(name))).map(name => resolve('../../artifacts/host-tests', name.replace(/\.ts$/, '.mjs')));
if (tests.length === 0) throw new Error('No matching Host tests.');
const child = spawn(process.execPath, ['--test', '--test-concurrency=1', ...tests], { stdio: 'inherit', windowsHide: true });
child.once('error', () => { process.exitCode = 1; });
child.once('exit', code => { process.exitCode = code ?? 1; });
