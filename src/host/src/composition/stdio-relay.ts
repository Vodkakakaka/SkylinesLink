import { Socket } from 'node:net';

export function relayPipeNames(nonce: string) {
  if (!/^[0-9a-f]{32}$/.test(nonce)) throw new Error('MCP relay identity is invalid.');
  const prefix = `\\\\.\\pipe\\skylineslink-stdio-${nonce}`;
  return { input: `${prefix}-in`, output: `${prefix}-out` };
}

export interface StdioRelay {
  input: Socket;
  output: Socket;
  close(): Promise<void>;
}

/** Launcher owns both servers and verifies this child's PID. Names are not authorization. */
export async function connectStdioRelay(nonce: string, deadlineMs = 5000, signal?: AbortSignal): Promise<StdioRelay> {
  const names = relayPipeNames(nonce);
  if (!Number.isSafeInteger(deadlineMs) || deadlineMs < 1 || deadlineMs > 5000) throw new Error('MCP relay startup deadline is invalid.');
  if (signal?.aborted) throw new Error('MCP relay startup was cancelled.');
  const input = new Socket(), output = new Socket();
  const sockets = [input, output];
  let closing: Promise<void> | undefined;
  let active = false;
  let rejectStartup!: (error: Error) => void;
  const failed = new Promise<never>((_resolve, reject) => { rejectStartup = reject; });
  const closed = sockets.map(socket => new Promise<void>(resolve => socket.once('close', resolve)));
  const close = () => closing ??= Promise.resolve().then(async () => {
    active = false;
    for (const socket of sockets) socket.destroy();
    await Promise.all(closed);
  });
  const stop = () => {
    rejectStartup(new Error('MCP relay connection ended.'));
    void close();
  };
  const connect = (socket: Socket, path: string) => new Promise<void>(resolve => {
    socket.once('connect', resolve);
    socket.on('error', stop);
    socket.once('close', () => { if (active) stop(); else rejectStartup(new Error('MCP relay connection ended.')); });
    socket.once('end', stop);
    socket.connect(path);
  });
  // The output channel carries bytes only toward Launcher. Unexpected reverse traffic ends the pair.
  output.on('data', stop);
  const abort = () => stop();
  signal?.addEventListener('abort', abort, { once: true });
  const deadline = setTimeout(stop, deadlineMs);
  try {
    await Promise.race([Promise.all([connect(input, names.input), connect(output, names.output)]), failed]);
    if (input.destroyed || output.destroyed || signal?.aborted) throw new Error('MCP relay startup did not complete.');
    active = true;
    return { input, output, close };
  } catch {
    await close();
    throw new Error('MCP relay startup failed.');
  } finally {
    clearTimeout(deadline);
    signal?.removeEventListener('abort', abort);
  }
}
