import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { appendFile, readFile, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const checkIds = ['restore', 'build', 'test', 'packages', 'aot-build', 'aot-test', 'generated', 'source-package'];
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
function requireValue(condition, code) { if (!condition) throw new Error(`SOURCE_VALIDATION_${code}`); }

// A report of this workflow's actual results, separate from source packaging
// and from any earlier native experiment. It grants no game qualification.
export function createSourceValidationSummary({ manifest, nativeReportBytes, source, ci, checks }) {
  requireValue(manifest.formatVersion === 1 && manifest.productId === 'skylineslink' && manifest.distribution === 'developer-source-preview'
    && manifest.playerInstallationPackage === false && manifest.gameRuntimeValidatedByPackaging === false, 'MANIFEST');
  requireValue(/^[a-f0-9]{40}$/.test(source.commit) && /^[a-f0-9]{40}$/.test(source.tree)
    && /^\d+\.\d+\.\d+-dev\.\d+$/.test(source.productVersion)
    && manifest.sourceCommit === source.commit && manifest.sourceTree === source.tree
    && manifest.productVersion === source.productVersion, 'SOURCE_MISMATCH');
  requireValue(ci.serverUrl === 'https://github.com' && /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(ci.repository)
    && /^[1-9][0-9]*$/.test(ci.runId) && /^[1-9][0-9]*$/.test(ci.runAttempt), 'CI_IDENTITY');
  requireValue(checks && !Array.isArray(checks) && Object.keys(checks).length === checkIds.length
    && checkIds.every(id => checks[id] === 'success'), 'CHECKS_NOT_PASSED');
  const native = JSON.parse(nativeReportBytes.toString('utf8'));
  requireValue(native.kind === 'nativeaot-local-integration'
    && Number.isSafeInteger(native.integrationTests) && native.integrationTests > 0
    && typeof native.isElevated === 'boolean' && native.ordinaryUserValidated === !native.isElevated
    && native.cleanMachineValidated === false && native.gameValidated === false, 'NATIVE_REPORT');
  return {
    formatVersion: 1, productId: 'skylineslink', distribution: 'developer-source-preview',
    productVersion: source.productVersion, sourceCommit: source.commit, sourceTree: source.tree,
    ci: { provider: 'github-actions', repository: ci.repository, runId: ci.runId, runAttempt: ci.runAttempt,
      url: `${ci.serverUrl}/${ci.repository}/actions/runs/${ci.runId}/attempts/${ci.runAttempt}` },
    checks: checkIds.map(id => ({ id, result: 'passed' })),
    nativeAot: { reportSha256: digest(nativeReportBytes), integrationTests: native.integrationTests,
      isElevated: native.isElevated, ordinaryUserValidated: native.ordinaryUserValidated,
      cleanMachineValidated: native.cleanMachineValidated, gameValidated: native.gameValidated },
    limits: { playerInstallationPackage: false, officialModBuiltByThisRun: false,
      gameRuntimeValidatedByThisRun: false, playerInstallationValidated: false,
      silentOperationValidated: false, marketplaceDeliveryValidated: false },
  };
}

function git(...args) {
  const result = spawnSync('git', args, { cwd: root, windowsHide: true, encoding: 'utf8' });
  requireValue(!result.error && result.status === 0, 'GIT_FAILED');
  return result.stdout.trim();
}

async function writeSourceValidation() {
  requireValue(process.env.GITHUB_ACTIONS === 'true', 'CI_REQUIRED');
  const source = { commit: git('rev-parse', 'HEAD'), tree: git('rev-parse', 'HEAD^{tree}'),
    productVersion: JSON.parse(await readFile(join(root, 'package.json'), 'utf8')).version };
  requireValue(source.commit === process.env.GITHUB_SHA, 'CI_COMMIT_MISMATCH');
  const output = join(root, 'artifacts/source-preview');
  const metadata = await readFile(join(output, 'source-manifest.json'));
  const manifest = JSON.parse(metadata.toString('utf8'));
  const nativeReportBytes = await readFile(join(root, 'artifacts/nativeaot/validation.json'));
  const summary = createSourceValidationSummary({ manifest, nativeReportBytes, source,
    ci: { serverUrl: process.env.GITHUB_SERVER_URL, repository: process.env.GITHUB_REPOSITORY,
      runId: process.env.GITHUB_RUN_ID, runAttempt: process.env.GITHUB_RUN_ATTEMPT },
    checks: JSON.parse(process.env.SOURCE_PREVIEW_CHECKS ?? 'null') });
  const name = `SkylinesLink-${source.productVersion}-source.zip`;
  requireValue(manifest.archive?.path === name, 'ARCHIVE_IDENTITY');
  const archive = await readFile(join(output, name));
  requireValue(manifest.archive.size === archive.length && manifest.archive.sha256 === digest(archive), 'ARCHIVE_CHANGED');
  const sumsPath = join(output, 'SHA256SUMS.txt');
  const originalSums = `${digest(archive)}  ${name}\n${digest(metadata)}  source-manifest.json\n`;
  requireValue(await readFile(sumsPath, 'utf8') === originalSums, 'CHECKSUM_CHANGED');
  const bytes = Buffer.from(`${JSON.stringify(summary, null, 2)}\n`);
  // This is a new per-run directory. Existing summaries are never overwritten;
  // a failed write fails the job before the artifact upload step.
  await writeFile(join(output, 'validation-summary.json'), bytes, { flag: 'wx' });
  await appendFile(sumsPath, `${digest(bytes)}  validation-summary.json\n`);
  console.log(JSON.stringify({ kind: 'source-validation-summary', sourceCommit: source.commit,
    productVersion: source.productVersion, ciRunId: summary.ci.runId, artifactFiles: 4 }));
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { await writeSourceValidation(); }
  catch (error) {
    console.error(JSON.stringify({ code: /^SOURCE_VALIDATION_[A-Z_]+$/.test(error.message)
      ? error.message : 'SOURCE_VALIDATION_FAILED' }));
    process.exitCode = 1;
  }
}
