import { createReadStream } from 'node:fs';
import { readFile, stat } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const directory = dirname(fileURLToPath(import.meta.url));

async function sha256(path) {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(path)) hash.update(chunk);
  return hash.digest('hex');
}

/** Read-only input verification. Does not execute Node, install files, or contact a server. */
export async function verifyNodeRuntime(nodeExe) {
  const inputs = JSON.parse(await readFile(join(directory, 'runtime-inputs.json'), 'utf8'));
  if (inputs.formatVersion !== 1) throw new Error('Unsupported runtime input record.');
  const expected = inputs.node;
  const executable = await stat(nodeExe);
  if (!executable.isFile() || await sha256(nodeExe) !== expected.exeSha256)
    throw new Error('Node executable does not match the reviewed win-x64 distribution.');
  const sourcePath = join(directory, 'licenses', expected.licenseFile);
  const license = await stat(sourcePath);
  if (!license.isFile() || license.size !== expected.licenseSize || await sha256(sourcePath) !== expected.licenseSha256)
    throw new Error('Node original license is missing or changed.');
  return {
    version: expected.version, sha256: expected.exeSha256, size: executable.size,
    license: { sourcePath, relativePath: 'runtime/Node.LICENSE.txt', sha256: expected.licenseSha256, size: license.size },
  };
}
