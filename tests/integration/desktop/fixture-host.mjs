import { spawn } from 'node:child_process';
import { createConnection } from 'node:net';
import { writeFile, rename } from 'node:fs/promises';
import { parseArgs } from 'node:util';
import { fileURLToPath } from 'node:url';

// Synthetic byte endpoint for the real native Launcher. It never connects to a game.
const { values } = parseArgs({ options: Object.fromEntries(['transport', 'profile', 'environment', 'core-manifest-sha256', 'history-reader', 'stdio-relay'].map(name => [name, { type: 'string' }])) });
const nonce = values['stdio-relay'];
if (values.transport !== 'stdio' || !/^[a-f0-9]{32}$/.test(nonce ?? '')) throw new Error('Explicit relay required');
const input = createConnection(`\\\\.\\pipe\\skylineslink-stdio-${nonce}-in`);
const output = createConnection(`\\\\.\\pipe\\skylineslink-stdio-${nonce}-out`);
const connected = stream => new Promise((resolve, reject) => { stream.once('connect', resolve); stream.once('error', reject); });
await Promise.all([connected(input), connected(output)]);
const directory = new URL('../../../', import.meta.url);
async function evidence(name, value) {
  const target = fileURLToPath(new URL(name, directory));
  const temporary = target + '.tmp';
  await writeFile(temporary, JSON.stringify(value));
  await rename(temporary, target);
}
const grandchild = spawn(process.execPath, ['-e', 'setInterval(()=>{},1000)'], { stdio: 'ignore', windowsHide: true });
grandchild.unref();
await evidence('fixture-ready.json', { pid: process.pid, grandchild: grandchild.pid, args: process.argv.slice(2) });
input.on('error', () => {});
output.on('error', () => {});
if (values.profile === 'Probe.SlowReader') {
  let started = false;
  input.on('data', () => {
    if (started) return;
    started = true;
    const payload = Buffer.alloc(65536, 0x53);
    let completed = 0;
    for (let i = 0; i < 8; i++) output.write(payload, () => { completed++; });
    setTimeout(() => { void evidence('fixture-backpressure.json', { timerExecuted: true, requestedBytes: 8 * payload.length, completedWrites: completed, pendingBytes: output.writableLength }); }, 40);
  });
  output.once('close', () => { input.destroy(); process.exitCode = 0; });
} else {
  input.pipe(output, { end: false });
  input.once('end', () => {
    output.end(() => { input.destroy(); output.destroy(); process.exitCode = 23; });
  });
}
