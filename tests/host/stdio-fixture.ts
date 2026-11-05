import { startStdioHost } from '../../src/host/src/mcp/transports/stdio.ts';
import { fixtureCapabilities, MemoryGame } from './fixture.ts';

startStdioHost({ game: new MemoryGame(), capabilities: fixtureCapabilities }, process.stdin, process.stdout);
