import { startStdioHost } from '../mcp/transports/stdio.ts';
import { DiscoveredGameClient } from '../game-client/discovered-game-client.ts';
import { parseHostOptions } from './options.ts';
import { connectStdioRelay, type StdioRelay } from './stdio-relay.ts';
import type { StdioHost } from '../mcp/transports/stdio.ts';
import { NativeHistoryReader } from '../game-client/history-reader.ts';

async function main() {
  let options: ReturnType<typeof parseHostOptions>;
  try { options = parseHostOptions(process.argv.slice(2)); }
  catch { process.exitCode = 2; return; }
  let relay: StdioRelay | undefined;
  let host: StdioHost | undefined;
  const stopped = new AbortController();
  const stop = () => {
    stopped.abort();
    void (host?.close() ?? relay?.close())?.catch(() => { process.exitCode = 4; });
  };
  process.once('SIGINT', stop);
  process.once('SIGTERM', stop);
  try {
    relay = await connectStdioRelay(options.stdioRelay, 5000, stopped.signal);
    const game = new DiscoveredGameClient({ ...options, userProfile: process.env.USERPROFILE ?? '' },
      options.historyReader ? { historyReader: new NativeHistoryReader(options.historyReader) } : {});
    host = startStdioHost({ game }, relay.input, relay.output);
    void host.closed.then(
      () => { process.off('SIGINT', stop); process.off('SIGTERM', stop); },
      () => { process.exitCode = 4; process.off('SIGINT', stop); process.off('SIGTERM', stop); },
    );
  } catch {
    process.exitCode = stopped.signal.aborted ? 0 : 3;
    try { await relay?.close(); } catch { process.exitCode = 4; }
    process.off('SIGINT', stop); process.off('SIGTERM', stop);
  }
}

// Launcher owns user-facing diagnostics. No standard-stream getter is touched by this entry.
await main();
