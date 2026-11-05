import { startStdioHost } from '../../src/host/src/mcp/transports/stdio.ts';
import { probeLimits } from '../../src/host/src/shared/limits.ts';
import { MemoryGame } from './fixture.ts';
import { capabilities } from '../../contracts/ts/index.ts';
import { connectStdioRelay } from '../../src/host/src/composition/stdio-relay.ts';
import { getDefaultHighWaterMark, setDefaultHighWaterMark } from 'node:stream';

// Hidden test-only process. No game connection, filesystem credentials or production CLI.
const mode = process.argv[2];
if ((mode !== 'eof' && mode !== 'backpressure') || !process.send || process.argv[3] !== '--stdio-relay' || process.argv.length !== 5) throw new Error('Explicit fixture mode, relay and owned IPC channel required');
let gameClosed = 0;
const game = new MemoryGame(); game.close = async () => { gameClosed++; };
// This process owns both sockets. Keep pending OS writes observable even when
// one catalogue frame is below Node's default high-water mark; restore the
// process default before constructing the Host or any of its other streams.
const previousHighWaterMark = getDefaultHighWaterMark(false);
let relay: Awaited<ReturnType<typeof connectStdioRelay>>;
try {
  if (mode === 'backpressure') setDefaultHighWaterMark(false, 1);
  relay = await connectStdioRelay(process.argv[4]!);
} finally { setDefaultHighWaterMark(false, previousHighWaterMark); }
const { input, output } = relay;
const host = startStdioHost({ game, capabilities, limits: { ...probeLimits, sendDeadlineMs: 350 } }, input, output);
let finalizing = false, backpressureObserved = false, sampled = false;
let pendingSignature = '', pendingSince = 0, pendingSamples = 0;
const watch = setInterval(() => {
  if (!sampled) { sampled = true; process.send!({ kind: 'sample', pendingBytes: output.writableLength, needDrain: output.writableNeedDrain, ...host.transport.usage }); }
  const usage = host.transport.usage;
  if (!backpressureObserved && output.writableNeedDrain && output.writableLength > 0 && usage.sendCount > 0) {
    const signature = `${output.writableLength}/${usage.sendBytes}/${usage.sendCount}`;
    if (signature !== pendingSignature) { pendingSignature = signature; pendingSince = performance.now(); pendingSamples = 0; }
    pendingSamples++;
    const stablePendingMilliseconds = performance.now() - pendingSince;
    // write(false) alone only proves a JS threshold. The parent has stopped
    // consuming the real OS pipe; require an unchanged pending write and owned
    // output ledger across native event-loop observations before reporting it.
    if (stablePendingMilliseconds >= 60 && pendingSamples >= 2) {
      backpressureObserved = true;
      process.send!({ kind: 'backpressure', pendingBytes: output.writableLength, needDrain: output.writableNeedDrain,
        highWaterMark: output.writableHighWaterMark, stablePendingMilliseconds, pendingSamples, ...usage });
    }
  } else if (!backpressureObserved) {
    pendingSignature = ''; pendingSince = 0; pendingSamples = 0;
  }
  if (host.transport.usage.closed) void finish(false);
}, 15);
const safety = setTimeout(() => { process.send!({ kind: 'safety', pendingBytes: output.writableLength, needDrain: output.writableNeedDrain, ...host.transport.usage }); void finish(true); }, 2500);
async function finish(safetyExpired: boolean) {
  if (finalizing) return;
  finalizing = true; clearInterval(watch); clearTimeout(safety);
  try {
    await host.close();
    process.exitCode = safetyExpired ? 2 : 0;
    process.send!({ kind: 'closed', backpressureObserved, safetyExpired, gameClosed, gameCalls: game.calls.length,
      inputDestroyed: input.destroyed, outputDestroyed: output.destroyed, ...host.transport.usage }, () => process.disconnect!());
  } catch { process.exitCode = 3; process.disconnect!(); }
}
