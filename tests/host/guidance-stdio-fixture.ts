import { startStdioHost } from '../../src/host/src/mcp/transports/stdio.ts';
import { GameClientError } from '../../src/host/src/game-client/game-client.ts';

// Any accidental game route fails, including an attempted fallback from a local read.
startStdioHost({ game: {
  call: async () => { throw new GameClientError('OFFLINE_FIXTURE', 'The isolated game fixture is unavailable.'); },
  close: async () => {},
} }, process.stdin, process.stdout);
