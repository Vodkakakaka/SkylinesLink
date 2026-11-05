import { createServer, type IncomingMessage, type ServerResponse, type Server } from 'node:http';
import { timingSafeEqual } from 'node:crypto';
import type { Socket } from 'node:net';
import { createMcpHandler, type AuthInfo, type JSONRPCMessage } from '@modelcontextprotocol/server';
import { toNodeHandler, type NodeIncomingMessageLike, type NodeServerResponseLike } from '@modelcontextprotocol/node';
import { createMcpServer, WorkBudget, type HostOptions } from '../server.ts';
import { checkRawJson } from '../../shared/json-budget.ts';
import { probeLimits, TransportBudgetError, validateLimits, type TransportLimits } from '../../shared/limits.ts';
import { ControlClassifier, LaneBudget, MessageRate, controlTransportProfile, setupMethod } from '../../shared/control-admission.ts';
import { measureJson } from '../../shared/json-budget.ts';

export interface HttpAuthorization {
  /** 外部客户端凭据，不能复用内部管道 token；由受保护配置的所有者提供。 */
  token: string;
  profileId: string;
  scopes: string[];
}

export interface HttpHostOptions extends HostOptions {
  authorization: HttpAuthorization;
  port: number;
  allowedOrigins?: readonly string[];
}

async function readBody(req: IncomingMessage, limits: TransportLimits): Promise<{ value: unknown; bytes: number }> {
  const declared = req.headers['content-length'];
  if (declared !== undefined && (!/^\d+$/.test(declared) || Number(declared) > limits.messageBytes)) throw new TransportBudgetError('请求体超过上限。');
  const chunks: Buffer[] = [];
  let total = 0;
  for await (const value of req) {
    const chunk = value as Buffer;
    total += chunk.length;
    if (total > limits.messageBytes) throw new TransportBudgetError('请求体超过上限。');
    chunks.push(chunk);
  }
  if (total === 0) return { value: undefined, bytes: 0 };
  return { value: JSON.parse(checkRawJson(Buffer.concat(chunks, total), limits)) as unknown, bytes: total };
}

function hasToken(req: IncomingMessage, token: string): boolean {
  const supplied = req.headers.authorization;
  if (typeof supplied !== 'string' || supplied.length !== token.length + 7 || !supplied.startsWith('Bearer ')) return false;
  const actual = Buffer.from(supplied.slice(7));
  const expected = Buffer.from(token);
  return actual.length === expected.length && timingSafeEqual(actual, expected);
}

export interface HttpHost {
  server: Server;
  url: URL;
  usage(): { requests: number; pipelines: number; connections: number; handlers: number };
  close(): Promise<void>;
}

export async function startHttpHost(options: HttpHostOptions): Promise<HttpHost> {
  const limits = options.limits ?? probeLimits;
  validateLimits(limits);
  if (options.authorization.token.length < 32 || options.authorization.token.length > 4096) throw new Error('HTTP 凭据长度不符合要求。');
  const sockets = new Set<Socket>();
  const socketClosing = new Set<Promise<void>>();
  const work = options.workBudget ?? new WorkBudget(limits.activeRequests);
  const classifier = new ControlClassifier(options.capabilities);
  const rate = new MessageRate(limits, classifier.binding !== undefined);
  const lanes = new LaneBudget(limits.activeRequests);
  const maximumRequests = limits.activeRequests + (classifier.binding ? controlTransportProfile.maximumControlRequests : 0)
    + controlTransportProfile.maximumSetupRequests;
  let setupRequests = 0;
  const handler = createMcpHandler(() => createMcpServer({ ...options, workBudget: work }), {
    legacy: 'stateless', maxRequestBodySize: limits.messageBytes, maxSubscriptions: 1, keepAliveMs: 0,
  });
  const nodeHandler = toNodeHandler(handler, { maxRequestBodySize: limits.messageBytes, onerror: () => {} });
  let requests = 0;
  let reservedSendBytes = 0;
  let windowStart = performance.now();
  let windowRequests = 0;
  let closed = false;
  let closing: Promise<void> | undefined;
  let listenPort = options.port;
  const responseDone = new Set<Promise<void>>();
  // A closed response does not prove body parsing / SDK adapter continuations ended.
  const pipelines = new Set<Promise<void>>();

  const server = createServer({ maxHeaderSize: 8192, requestTimeout: limits.receiveDeadlineMs, headersTimeout: limits.receiveDeadlineMs,
    // Node checks incomplete headers on this cadence; its 30s default defeats a short deadline.
    connectionsCheckingInterval: Math.max(1, Math.min(1000, Math.floor(limits.receiveDeadlineMs / 4))),
  }, (req, res) => {
    const now = performance.now();
    if (now - windowStart >= limits.windowMs) { windowStart = now; windowRequests = 0; }
    const rawMaximum = limits.messagesPerWindow + (classifier.binding ? controlTransportProfile.maximumControlMessagesPerWindow : 0);
    // Unknown input has a finite staging owner. It does not borrow the ordinary
    // eight full-response reservations before its complete body can be classified.
    let responseReservation = controlTransportProfile.maximumOutputMetadataBytes * 3;
    if (closed || ++windowRequests > rawMaximum || requests >= maximumRequests || pipelines.size >= maximumRequests || reservedSendBytes + responseReservation > limits.sendBytes) {
      req.socket.destroy();
      return;
    }
    requests++;
    reservedSendBytes += responseReservation;
    let resolveDone!: () => void;
    const done = new Promise<void>((resolve) => { resolveDone = resolve; });
    responseDone.add(done);
    let released = false;
    let pipelineFinished = false;
    let releaseLane: (() => void) | undefined;
    let controlBuffer: Buffer | undefined;
    const releaseOwned = () => {
      if (!released || !pipelineFinished) return;
      reservedSendBytes -= responseReservation; responseReservation = 0;
      releaseLane?.(); releaseLane = undefined;
      controlBuffer?.fill(0); controlBuffer = undefined;
    };
    const timer = setTimeout(() => { req.destroy(); res.destroy(); }, limits.receiveDeadlineMs + limits.sendDeadlineMs);
    const release = () => {
      if (released) return;
      released = true;
      clearTimeout(timer);
      requests--;
      responseDone.delete(done);
      resolveDone();
      releaseOwned();
    };
    res.once('finish', release);
    res.once('close', release);
    res.on('error', () => { res.destroy(); });
    const pipeline = (async () => {
      const host = req.headers.host;
      const allowedHosts = new Set([`127.0.0.1:${listenPort}`, `localhost:${listenPort}`]);
      if (!host || !allowedHosts.has(host.toLowerCase())) { res.writeHead(403).end(); return; }
      const origin = req.headers.origin;
      if (origin !== undefined && !(options.allowedOrigins ?? []).includes(origin)) { res.writeHead(403).end(); return; }
      if (!hasToken(req, options.authorization.token)) { res.writeHead(401, { 'WWW-Authenticate': 'Bearer' }).end(); return; }
      if (req.url !== '/mcp') { res.writeHead(404).end(); return; }
      if (req.method === 'POST' && !/^application\/json(?:\s*;|$)/i.test(req.headers['content-type'] ?? '')) { res.writeHead(415).end(); return; }
      const body = req.method === 'POST' ? await readBody(req, limits) : { value: undefined, bytes: 0 };
      const parsed = body.value;
      const key = typeof parsed === 'object' && parsed !== null && !Array.isArray(parsed)
        ? classifier.raw(parsed as JSONRPCMessage, body.bytes) : undefined;
      // Stateless HTTP has one MCP lifecycle per POST, not per pooled socket.
      // The setup allowance is nevertheless one actual owner across this Host.
      const setup = typeof parsed === 'object' && parsed !== null && !Array.isArray(parsed)
        && setupMethod(parsed as JSONRPCMessage) !== undefined;
      rate.accept(key !== undefined);
      try {
        if (setup && setupRequests < controlTransportProfile.maximumSetupRequests) {
          setupRequests++; let owned = true;
          releaseLane = () => { if (owned) { owned = false; setupRequests--; } };
        } else releaseLane = lanes.acquire(key);
      }
      catch { req.socket.destroy(); return; } // no second waiter or unreserved error reply
      const control = key !== undefined;
      const maximumOutput = control ? controlTransportProfile.maximumResponseBytes
        : setup ? controlTransportProfile.maximumSetupResponseBytes : limits.messageBytes;
      const nextReservation = maximumOutput * 3;
      if (reservedSendBytes - responseReservation + nextReservation > limits.sendBytes) { req.socket.destroy(); return; }
      reservedSendBytes += nextReservation - responseReservation; responseReservation = nextReservation;
      if (control || setup) controlBuffer = Buffer.alloc(maximumOutput);
      const auth: AuthInfo = { token: options.authorization.token, clientId: options.authorization.profileId, scopes: [...options.authorization.scopes] };
      const authorizedReq: NodeIncomingMessageLike = {
        ...(req.method !== undefined ? { method: req.method } : {}),
        ...(req.url !== undefined ? { url: req.url } : {}),
        headers: req.headers,
        auth,
        [Symbol.asyncIterator]: () => req[Symbol.asyncIterator](),
      };
      let written = 0;
      const destination: NodeServerResponseLike = {
        writeHead: (status, headers) => res.writeHead(status, headers),
        write: (chunk) => {
          written += typeof chunk === 'string' ? Buffer.byteLength(chunk) : chunk.byteLength;
          if (written > maximumOutput) { res.destroy(); throw new TransportBudgetError('响应超过字节上限。'); }
          if (controlBuffer) {
            const bytes = typeof chunk === 'string' ? Buffer.from(chunk) : chunk;
            controlBuffer.set(bytes, written - bytes.byteLength);
            return true;
          }
          return res.write(chunk);
        },
        end: (chunk) => {
          if (chunk !== undefined) destination.write(chunk);
          if (!controlBuffer) { res.end(); return; }
          try {
            const text = controlBuffer.toString('utf8', 0, written);
            const payloads = text.trimStart().startsWith('{') ? [text] : text.split('\n').filter(line => line.startsWith('data:')).map(line => line.slice(5).trim());
            if (!payloads.length) throw new TransportBudgetError('停止响应缺少完整MCP消息。');
            for (const payload of payloads) {
              const value = JSON.parse(payload) as { result?: { _meta?: unknown } };
              measureJson(value, { ...limits, messageBytes: maximumOutput });
              if (value.result?._meta !== undefined) measureJson(value.result._meta,
                { ...limits, messageBytes: controlTransportProfile.maximumOutputMetadataBytes });
            }
            res.end(controlBuffer.subarray(0, written));
          } catch { res.destroy(); }
        },
        on: (event, listener) => res.on(event, listener),
        get destroyed() { return res.destroyed; },
      };
      await nodeHandler(authorizedReq, destination, parsed);
      // 不在这里释放请求/响应额度：公开adapter返回也不能替代finish/close。
    })().catch((error: unknown) => {
      if (res.destroyed) return;
      if (!res.headersSent) { res.writeHead(error instanceof TransportBudgetError ? 413 : 400).end(); }
      else res.destroy();
    }).finally(() => { pipelines.delete(pipeline); pipelineFinished = true; releaseOwned(); });
    pipelines.add(pipeline);
  });
  server.maxConnections = limits.httpConnections;
  server.maxRequestsPerSocket = limits.messagesPerWindow;
  server.keepAliveTimeout = limits.receiveDeadlineMs;
  server.on('connection', (socket) => {
    sockets.add(socket);
    const closing = new Promise<void>((resolve) => socket.once('close', () => { sockets.delete(socket); socketClosing.delete(closing); resolve(); }));
    socketClosing.add(closing);
    socket.on('error', () => socket.destroy());
  });
  server.on('clientError', (_error, socket) => socket.destroy());
  await new Promise<void>((resolve, reject) => {
    server.once('error', reject);
    server.listen(options.port, '127.0.0.1', () => { server.off('error', reject); resolve(); });
  });
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error('HTTP 监听状态无效。');
  listenPort = address.port;
  return {
    server,
    url: new URL(`http://127.0.0.1:${listenPort}/mcp`),
    usage: () => ({ requests, pipelines: pipelines.size, connections: sockets.size, handlers: work.active }),
    close() {
      closing ??= (async () => {
        closed = true;
        const stopped = new Promise<void>((resolve) => server.close(() => resolve()));
        for (const socket of sockets) socket.destroy();
        // Start both shutdown paths: neither may depend on the other already having finished.
        const cleanup = await Promise.allSettled([Promise.resolve().then(() => handler.close()), Promise.resolve().then(() => options.game.close()),
          Promise.all(socketClosing), Promise.all(responseDone), stopped,
          work.waitIdle(), Promise.all(pipelines)]);
        if (cleanup.some(result => result.status === 'rejected')) throw new Error('HTTP 连接清理未全部成功。');
      })();
      return closing;
    },
  };
}
