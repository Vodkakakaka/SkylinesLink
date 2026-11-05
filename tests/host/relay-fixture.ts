import { createServer, type Socket } from 'node:net';
import { spawn, type ChildProcess } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { ReadBuffer, serializeMessage, type Transport } from '@modelcontextprotocol/server';
import { relayPipeNames } from '../../src/host/src/composition/stdio-relay.ts';

export async function within<T>(work: Promise<T>, ms: number): Promise<T> {
  let timer: NodeJS.Timeout | undefined;
  try { return await Promise.race([work, new Promise<never>((_resolve, reject) => { timer = setTimeout(() => reject(new Error('Owned relay fixture deadline expired.')), ms); })]); }
  finally { clearTimeout(timer); }
}

/** Test-only asynchronous pipe servers. This does not prove Native Launcher ACL/PID checks. */
export async function createRelayServers(nonce = randomBytes(16).toString('hex')) {
  const names = relayPipeNames(nonce);
  const sockets = new Set<Socket>();
  const make = async (path: string) => {
    let accept!: (socket: Socket) => void;
    const connected = new Promise<Socket>(resolve => { accept = resolve; });
    let accepted = false;
    const server = createServer(socket => {
      socket.on('error', () => {});
      if (accepted) { socket.destroy(); return; }
      accepted = true; sockets.add(socket); socket.once('close', () => sockets.delete(socket)); accept(socket);
    });
    await new Promise<void>((resolve, reject) => { server.once('error', reject); server.listen(path, resolve); });
    return { server, connected };
  };
  const input = await make(names.input);
  let output: Awaited<ReturnType<typeof make>>;
  try { output = await make(names.output); }
  catch (error) { await new Promise<void>(resolve => input.server.close(() => resolve())); throw error; }
  let closing: Promise<void> | undefined;
  return { nonce, names, input: input.connected, output: output.connected,
    close: () => closing ??= (async () => {
      const closed = [...sockets].map(socket => new Promise<void>(resolve => { socket.once('close', resolve); socket.destroy(); }));
      await Promise.all([...closed, ...[input.server, output.server].map(server => new Promise<void>(resolve => server.close(() => resolve())))]);
    })(),
  };
}

export async function startRelayChild(file: string, args: string[], options: { env?: NodeJS.ProcessEnv; ipc?: boolean } = {}) {
  const servers = await createRelayServers();
  let child: ChildProcess;
  try { child = spawn(process.execPath, [file, ...args, '--stdio-relay', servers.nonce], {
    windowsHide: true, stdio: options.ipc ? ['ignore', 'ignore', 'ignore', 'ipc'] : 'ignore',
    ...(options.env ? { env: options.env } : {}),
  }); } catch (error) { await servers.close(); throw error; }
  const exit = new Promise<{ code: number | null; signal: NodeJS.Signals | null }>((resolve, reject) => {
    child.once('error', reject); child.once('exit', (code, signal) => resolve({ code, signal }));
  });
  let closing: Promise<void> | undefined;
  const cleanup = async () => {
    if (child.exitCode === null && child.signalCode === null) child.kill();
    await exit.catch(() => {}); await servers.close();
  };
  try {
    const [input, output] = await within(Promise.race([
      Promise.all([servers.input, servers.output]),
      exit.then(() => { throw new Error('Owned relay child exited before both connections.'); }),
    ]), 5000);
    return { child, input, output, exit, nonce: servers.nonce,
      close: () => closing ??= (async () => {
        try {
          input.end();
          const result = await within(exit, 1500);
          if (result.code !== 0 || result.signal !== null) throw new Error('Owned relay child did not exit normally.');
        } finally { await cleanup(); }
      })(),
      cleanup,
    };
  } catch (error) { await cleanup(); throw error; }
}

export function relayClientTransport(relay: Awaited<ReturnType<typeof startRelayChild>>): Transport {
  const reader = new ReadBuffer({ maxBufferSize: 131072 });
  let receivedBytes = 0;
  let started = false;
  const transport: Transport = {
    start: async () => {
      if (started) throw new Error('Fixture transport cannot be restarted.');
      started = true;
      relay.output.on('data', (chunk: Buffer) => {
        try {
          receivedBytes += chunk.length;
          if (receivedBytes > 4 * 1024 * 1024) throw new Error('Fixture response budget exceeded.');
          reader.append(chunk);
          for (let message = reader.readMessage(); message !== null; message = reader.readMessage()) transport.onmessage?.(message);
        } catch (error) {
          transport.onerror?.(error instanceof Error ? error : new Error('Invalid fixture response.'));
          void relay.close().catch(() => {});
        }
      });
      relay.output.once('close', () => { reader.clear(); transport.onclose?.(); });
    },
    send: async message => {
      const bytes = serializeMessage(message);
      if (Buffer.byteLength(bytes) > 65536) throw new Error('Fixture request budget exceeded.');
      await new Promise<void>((resolve, reject) => relay.input.write(bytes, error => error ? reject(error) : resolve()));
    },
    close: () => relay.close(),
  };
  return transport;
}
