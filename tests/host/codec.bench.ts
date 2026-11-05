import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFile, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { performance } from 'node:perf_hooks';
import { PassThrough } from 'node:stream';
import { setImmediate as yieldTurn } from 'node:timers/promises';
import { Client } from '@modelcontextprotocol/client';
import { deserializeMessage, ReadBuffer, serializeMessage, type CallToolResult, type JSONRPCMessage, type Transport } from '@modelcontextprotocol/server';
import { capabilities, schemaIds, validateContract } from '../../contracts/ts/index.ts';
import { startStdioHost } from '../../src/host/src/mcp/transports/stdio.ts';
import { checkRawJson, measureJson } from '../../src/host/src/shared/json-budget.ts';
import { probeLimits, TransportBudgetError } from '../../src/host/src/shared/limits.ts';
import { MemoryGame, syntheticContext, syntheticLoadQueries } from './fixture.ts';

// Finite codec observations only: no game, network load, forced GC, heap tuning or latency gate.
const sampling = { warmup: 20, timed: 120, yieldEvery: 10, turnWarmup: 3, turnTimed: 40 } as const;
let sink: unknown;

function summarize(samples: number[]) {
  assert.ok(samples.length > 0 && samples.every(value => Number.isFinite(value) && value >= 0));
  const sorted = [...samples].sort((a, b) => a - b);
  const percentile = (p: number) => sorted[Math.ceil(sorted.length * p) - 1]!;
  return { count: sorted.length, p50Ms: percentile(0.5), p95Ms: percentile(0.95), p99Ms: percentile(0.99), worstObservedMs: sorted.at(-1)! };
}

async function measure(run: () => unknown) {
  await yieldTurn();
  for (let i = 0; i < sampling.warmup; i++) {
    sink = run();
    if ((i + 1) % sampling.yieldEvery === 0) await yieldTurn();
  }
  const samples: number[] = [];
  for (let i = 0; i < sampling.timed; i++) {
    const begin = performance.now();
    sink = run();
    samples.push(performance.now() - begin);
    if ((i + 1) % sampling.yieldEvery === 0) await yieldTurn();
  }
  return summarize(samples);
}

// Metadata comes from known fixture values, outside all timed regions. This is not a JSON parser.
function shape(value: unknown) {
  let tokens = 0; let maxContainerDepth = 0; let maxValueDepth = 0;
  const visit = (item: unknown, valueDepth: number, containerDepth: number) => {
    tokens++;
    maxValueDepth = Math.max(maxValueDepth, valueDepth);
    if (item === null || typeof item !== 'object') return;
    maxContainerDepth = Math.max(maxContainerDepth, containerDepth + 1);
    if (Array.isArray(item)) {
      for (const child of item) visit(child, valueDepth + 1, containerDepth + 1);
    } else {
      for (const child of Object.values(item)) {
        tokens++; // One key token, then its value token.
        visit(child, valueDepth + 1, containerDepth + 1);
      }
    }
  };
  visit(value, 0, 0);
  return { utf8JsonBytes: Buffer.byteLength(JSON.stringify(value)), tokens, maxContainerDepth, maxValueDepth };
}

function textAtBytes(unit: string, targetBytes: number): Record<string, unknown> {
  const overhead = Buffer.byteLength(JSON.stringify({ text: '' }));
  const unitBytes = Buffer.byteLength(JSON.stringify(unit)) - 2;
  const payloadBytes = targetBytes - overhead;
  assert.ok(payloadBytes >= 0 && unitBytes > 0);
  return { text: unit.repeat(Math.floor(payloadBytes / unitBytes)) + 'a'.repeat(payloadBytes % unitBytes) };
}

function nestedArray(depth: number): unknown {
  let value: unknown = 0;
  for (let i = 0; i < depth; i++) value = [value];
  return value;
}

function toolResult(result: Record<string, unknown>): CallToolResult {
  return { structuredContent: result, content: [{ type: 'text', text: JSON.stringify(result) }] };
}

function toolMessage(result: Record<string, unknown>) {
  return { jsonrpc: '2.0' as const, id: 1, result: toolResult(result) };
}

function encodeToolResult(result: Record<string, unknown>): string {
  // Existing handler + send decorator synchronous encoding stages, without dispatch/IO.
  measureJson(result, probeLimits);
  const response = toolResult(result);
  measureJson(response, probeLimits);
  const message = { jsonrpc: '2.0' as const, id: 1, result: response };
  measureJson(message, probeLimits);
  return serializeMessage(message);
}

function nearLimitToolResult(unit: string): Record<string, unknown> {
  // Size the duplicated text + structuredContent envelope, not merely its inner result.
  let low = 0; let high = probeLimits.messageBytes;
  while (low < high) {
    const middle = Math.ceil((low + high) / 2);
    const result = { text: unit.repeat(middle) };
    if (Buffer.byteLength(serializeMessage(toolMessage(result))) <= probeLimits.messageBytes - 32) low = middle;
    else high = middle - 1;
  }
  return { text: unit.repeat(low) };
}

function expectRejection(run: () => unknown, kind: 'syntax' | 'budget'): string {
  try { run(); }
  catch (error) {
    if (kind === 'syntax' ? error instanceof SyntaxError : error instanceof TransportBudgetError) return kind;
    throw error;
  }
  throw new Error('Expected ' + kind + ' rejection was not observed.');
}

async function measureTurn(run: () => unknown, batchSize: number, warmup: number = sampling.turnWarmup, timed: number = sampling.turnTimed) {
  const delay: number[] = []; const work: number[] = []; const afterWork: number[] = [];
  for (let i = 0; i < warmup + timed; i++) {
    const observation = await new Promise<{ delay: number; work: number; afterWork: number }>(done => {
      const scheduled = performance.now();
      let began = scheduled; let ended = scheduled;
      setImmediate(() => {
        const fired = performance.now();
        done({ delay: fired - scheduled, work: ended - began, afterWork: fired - ended });
      });
      began = performance.now();
      for (let item = 0; item < batchSize; item++) sink = run();
      ended = performance.now();
    });
    if (i >= warmup) {
      delay.push(observation.delay); work.push(observation.work); afterWork.push(observation.afterWork);
    }
  }
  return { batchSize, sentinelDelay: summarize(delay), synchronousWork: summarize(work), postWorkSchedulingDelay: summarize(afterWork) };
}

async function findRepositoryRoot(): Promise<string> {
  let directory = process.cwd();
  for (;;) {
    try {
      const manifest = JSON.parse(await readFile(resolve(directory, 'package.json'), 'utf8')) as { name?: string };
      if (manifest.name === 'skylineslink') return directory;
    } catch (error) {
      if (!(error instanceof Error) || !('code' in error) || error.code !== 'ENOENT') throw error;
    }
    const parent = dirname(directory);
    if (parent === directory) throw new Error('Run the benchmark from within the SkylinesLink checkout.');
    directory = parent;
  }
}

async function sdkRound(modern: boolean, timed: boolean) {
  const batchSize = Math.min(8, probeLimits.activeRequests);
  const ids = Object.keys(syntheticLoadQueries);
  assert.equal(ids.length, 4);
  const game = new MemoryGame();
  const input = new PassThrough(), output = new PassThrough();
  const reader = new ReadBuffer({ maxBufferSize: probeLimits.messageBytes + 1 });
  const frames: Buffer[] = []; const requests: JSONRPCMessage[] = []; const responses: JSONRPCMessage[] = [];
  const pending: Array<Promise<CallToolResult>> = [];
  const perRequestMs: number[] = [];
  let collecting = false; let inputChunks = 0; let responseBytes = 0;
  let queued!: () => void;
  let queueDeadline: NodeJS.Timeout | undefined;
  let sentinelHandle: NodeJS.Immediate | undefined;
  let finishSentinel: ((delay: number) => void) | undefined;
  const stamp = () => timed ? performance.now() : 0;
  const transport: Transport = {
    start: async () => {},
    send: async message => {
      const frame = Buffer.from(serializeMessage(message));
      if (!collecting) { input.write(frame); return; }
      assert.ok('method' in message && message.method === 'tools/call', 'Unexpected discovery/notification during the buffered tool batch.');
      frames.push(frame); requests.push(message);
      assert.ok(frames.length <= batchSize);
      if (frames.length === batchSize) queued();
    },
    close: async () => { input.end(); },
  };
  input.on('data', () => { if (collecting) inputChunks++; });
  output.once('close', () => { reader.clear(); transport.onclose?.(); });
  output.on('data', (chunk: Buffer) => {
    if (collecting) responseBytes += chunk.length;
    reader.append(chunk);
    for (let message = reader.readMessage(); message !== null; message = reader.readMessage()) {
      if (collecting) responses.push(message);
      transport.onmessage?.(message);
    }
  });
  const host = startStdioHost({ game }, input, output);
  const client = new Client({ name: 'offline-sdk-codec-benchmark', version: '1' }, {
    capabilities: {}, versionNegotiation: { mode: modern ? { pin: '2026-07-28' } : 'legacy' },
  });
  try {
    await client.connect(transport, { timeout: 2000 });
    const protocol = client.getNegotiatedProtocolVersion();
    assert.equal(protocol, modern ? '2026-07-28' : '2025-11-25');
    const tools = await client.listTools(undefined, { timeout: 2000 });
    assert.deepEqual(tools.tools.map(tool => tool.name).sort(), capabilities.map(capability => capability.toolName).sort());
    // Discovery and outputSchema compilation are deliberately primed before the measured batch.
    await client.callTool({ name: 'skylines_operation_get', arguments: { selector: { kind: 'operationId', operationId: ids[0]! } } }, { timeout: 2000 });
    await yieldTurn();
    const ready = new Promise<void>((resolve, reject) => {
      queued = resolve;
      queueDeadline = setTimeout(() => reject(new Error('SDK did not produce the finite expected batch.')), 2000);
    });
    collecting = true;
    const batchStarted = stamp();
    for (let i = 0; i < batchSize; i++) {
      const began = stamp();
      pending.push(client.callTool({ name: 'skylines_operation_get', arguments: {
        selector: { kind: 'operationId', operationId: ids[i % ids.length]! },
      } }, { timeout: 2000 }).then(result => { if (timed) perRequestMs.push(performance.now() - began); return result; }));
    }
    const replies = Promise.all(pending);
    void replies.catch(() => {}); // Cleanup below always observes every request even if batching fails.
    await ready;
    clearTimeout(queueDeadline); queueDeadline = undefined;
    const chunk = Buffer.concat(frames);
    assert.ok(chunk.length <= probeLimits.messageBytes * 2);
    const scheduled = stamp();
    const sentinel = new Promise<number>(resolve => {
      finishSentinel = resolve;
      sentinelHandle = setImmediate(() => { sentinelHandle = undefined; resolve(timed ? performance.now() - scheduled : 0); });
    });
    const flushed = stamp();
    input.write(chunk); // The actual Host input receives exactly one chunk containing all eight frames.
    const inputWriteMs = timed ? performance.now() - flushed : 0;
    let batchRoundTripMs = 0; let flushToRepliesMs = 0;
    const [values, sentinelDelayMs] = await Promise.all([
      replies.then(values => {
        if (timed) { const completed = performance.now(); batchRoundTripMs = completed - batchStarted; flushToRepliesMs = completed - flushed; }
        return values;
      }), sentinel,
    ]);
    collecting = false;
    // Assertions and metadata encoding happen after both replies and sentinel have completed.
    assert.equal(inputChunks, 1);
    assert.equal(requests.length, batchSize); assert.equal(responses.length, batchSize);
    assert.equal(new Set(requests.map(request => 'id' in request ? request.id : undefined)).size, batchSize);
    for (let i = 0; i < values.length; i++) {
      const result = values[i]!;
      assert.notEqual(result.isError, true);
      assert.deepEqual(result.structuredContent, syntheticLoadQueries[ids[i % ids.length]!]);
      assert.equal(result.content[0]?.type, 'text');
      assert.deepEqual(JSON.parse((result.content[0] as { text: string }).text), result.structuredContent);
    }
    assert.equal(game.calls.filter(id => id === 'operations.get').length, batchSize + 1);
    return { protocol, batchSize, inputChunks, requestChunkUtf8Bytes: chunk.length, responseStreamUtf8Bytes: responseBytes,
      requestMessages: requests.map(shape), responseMessages: responses.map(shape),
      perRequestMs, batchRoundTripMs, flushToRepliesMs, inputWriteMs, sentinelDelayMs,
    };
  } finally {
    collecting = false;
    clearTimeout(queueDeadline);
    if (sentinelHandle) clearImmediate(sentinelHandle);
    finishSentinel?.(0);
    const closing = await Promise.allSettled([client.close(), host.close()]);
    await Promise.allSettled(pending);
    assert.ok(closing.every(result => result.status === 'fulfilled'), 'In-memory SDK session cleanup failed.');
    assert.deepEqual(host.transport.usage, { activeRequests: 0, sendBytes: 0, sendCount: 0, closed: true });
  }
}

async function sdkPathBenchmark(preflight: boolean) {
  const paths = [...fingerprintPaths, 'src/host/src/mcp/transports/stdio.ts',
    'contracts/schemas/operation-query-v3.json', 'contracts/schemas/operation-lookup-v3.json',
    'contracts/ts/index.ts', 'contracts/ts/runtime.ts'];
  const hashes = await fingerprints(paths);
  const codecPath = resolve(repositoryRoot, 'artifacts/host-codec-benchmark.json');
  const preservedCodec = await readFile(codecPath).catch(error => { if (error.code === 'ENOENT') return undefined; throw error; });
  const clientPackage = JSON.parse(await readFile(resolve(repositoryRoot, 'src/host/node_modules/@modelcontextprotocol/client/package.json'), 'utf8')) as { version: string };
  const runs = { warmup: 3, timed: 20, requestsPerRound: Math.min(8, probeLimits.activeRequests) };
  const protocols = [];
  const sentinelBaseline = preflight ? undefined : await measureTurn(() => undefined, 0, runs.warmup, runs.timed);
  for (const modern of [false, true]) {
    const observations: Awaited<ReturnType<typeof sdkRound>>[] = [];
    const rounds = preflight ? 1 : runs.warmup + runs.timed;
    for (let i = 0; i < rounds; i++) {
      const result = await sdkRound(modern, !preflight && i >= runs.warmup);
      if (preflight || i >= runs.warmup) observations.push(result);
      await yieldTurn();
    }
    const first = observations[0]!;
    const range = (values: number[]) => ({ min: Math.min(...values), max: Math.max(...values) });
    const messageRange = (messages: ReturnType<typeof shape>[]) => ({ utf8JsonBytes: range(messages.map(value => value.utf8JsonBytes)),
      tokens: range(messages.map(value => value.tokens)), maxContainerDepth: range(messages.map(value => value.maxContainerDepth)),
      maxValueDepth: range(messages.map(value => value.maxValueDepth)) });
    protocols.push({ protocol: first.protocol, rounds: observations.length, requestsPerRound: first.batchSize,
      inputChunksPerRound: range(observations.map(value => value.inputChunks)),
      requestChunkUtf8Bytes: range(observations.map(value => value.requestChunkUtf8Bytes)),
      responseStreamUtf8Bytes: range(observations.map(value => value.responseStreamUtf8Bytes)),
      requestMessages: messageRange(observations.flatMap(value => value.requestMessages)),
      responseMessages: messageRange(observations.flatMap(value => value.responseMessages)),
      ...(preflight ? {} : { perRequestRoundTrip: summarize(observations.flatMap(value => value.perRequestMs)),
        completeBatchRoundTrip: summarize(observations.map(value => value.batchRoundTripMs)),
        flushToAllReplies: summarize(observations.map(value => value.flushToRepliesMs)),
        inputWriteSynchronous: summarize(observations.map(value => value.inputWriteMs)),
        sentinelDelay: summarize(observations.map(value => value.sentinelDelayMs)) }),
      cleanup: 'Every initialized session closed with activeRequests=sendBytes=sendCount=0; every request promise was observed.',
    });
  }
  assert.deepEqual(await fingerprints(paths), hashes, 'Source changed during the SDK path observation.');
  const codecAfter = await readFile(codecPath).catch(error => { if (error.code === 'ENOENT') return undefined; throw error; });
  assert.deepEqual(codecAfter, preservedCodec, 'The earlier codec artifact must remain byte-identical.');
  const results = {
    formatRevision: 1, mode: preflight ? 'sdk-path-preflight-only' : 'full-in-memory-sdk-path', observedAtUtc: new Date().toISOString(),
    node: process.version, v8: process.versions.v8, platform: process.platform, architecture: process.arch,
    sdk: { server: sdk.version, client: clientPackage.version, publicEntries: ['Client', 'ReadBuffer', 'serializeMessage', 'startStdioHost via public StdioServerTransport/serveStdio'] },
    sampling: preflight ? { roundsPerProtocol: 1, timed: false } : runs, limits: probeLimits, sourceHashes: hashes,
    preservedCodecArtifactSha256: preservedCodec ? createHash('sha256').update(preservedCodec).digest('hex') : null,
    workload: 'Eight existing operations.get revision 4 calls alternate four schema-valid live lookup:3 envelopes with full-load facts. Two current read capabilities only; no probe.submit, history disk IO or expanded business schema.',
    sentinelBaseline, protocols,
    includes: 'Official Client callTool and output-schema validation, public stdio framing, real Host raw scanner/admission/server input-output validation and handler, MemoryGame lookup, full text+structured encoding, response parsing and Client request correlation.',
    excludes: 'Fresh-session construction, protocol discovery/handshake, listTools and one output-schema priming call, cleanup, metadata counting, assertion checks after completion, OS pipes, real named-pipe game client, game execution and heap peaks.',
    timing: 'Per-request and batch round trips start before Client.callTool; input.write flush begins after all eight Client frames are buffered. Sentinel is scheduled immediately before that one write; assertions wait until both replies and sentinel finish. Capture counters, clock reads and promise handlers add measurement overhead; no overhead subtraction.',
    limitations: [
      'Each round is a fresh initialized session to preserve the actual 64-message/window limit without altering probeLimits. This is not sustained throughput or a long-lived-session saturation claim.',
      'The fixed representative live lookup facts are small; this sample is not a 64KiB business payload or a history-reader benchmark. Transport-size synthetic codec corners remain in the separate prior artifact.',
      'These are finite full in-memory SDK path observations, not all-input, all-transport, OS, game-frame or hard real-time bounds. The representative fixture is not a mathematical maximum over all schema strings.',
      'The custom client Transport uses public SDK framing on PassThrough streams. It exercises byte framing but is not the OS StdioClientTransport.',
      'SetImmediate delay includes SDK promise work and scheduling; it is not a pure CPU measurement. No GC or heap settings are changed.',
    ],
  };
  if (!preflight) await writeFile(resolve(repositoryRoot, 'artifacts/host-sdk-path-benchmark.json'), JSON.stringify(results, null, 2) + '\n');
  process.stdout.write(JSON.stringify(results) + '\n');
}

const repositoryRoot = await findRepositoryRoot();
const fingerprintPaths = [
  'tests/host/codec.bench.ts', 'tests/host/fixture.ts', 'src/host/src/shared/json-budget.ts',
  'src/host/src/shared/limits.ts', 'src/host/src/mcp/server.ts',
  'src/host/src/mcp/transports/bounded-transport.ts', 'contracts/schemas/context.json',
];
const fingerprints = async (paths = fingerprintPaths) => Object.fromEntries(await Promise.all(paths.map(async path => [
  path, createHash('sha256').update(await readFile(resolve(repositoryRoot, path))).digest('hex'),
])));
const sourceHashes = await fingerprints();
const sdk = JSON.parse(await readFile(resolve(repositoryRoot, 'src/host/node_modules/@modelcontextprotocol/server/package.json'), 'utf8')) as { version: string };
if (process.argv.includes('--sdk-path')) {
  await sdkPathBenchmark(process.argv.includes('--check'));
} else {
  assert.ok(validateContract(schemaIds.Context, syntheticContext).valid, 'The existing offline Context fixture must satisfy its generated contract.');

  const targetBytes = probeLimits.messageBytes - 8;
  const escapedUnit = '\\"\n\t\u0000';
  const acceptedValues = [
    { id: 'near-limit-utf8-text', value: textAtBytes('界', targetBytes) },
    { id: 'near-limit-escaped-text', value: textAtBytes(escapedUnit, targetBytes) },
    { id: 'container-depth-limit', value: nestedArray(probeLimits.jsonDepth) },
    { id: 'near-token-limit-wide-object', value: Object.fromEntries(Array.from({ length: Math.floor((probeLimits.jsonTokens - 1) / 2) }, (_, i) => ['p' + i, i % 10])) },
    { id: 'token-limit-small-number-array', value: Array.from({ length: probeLimits.jsonTokens - 1 }, (_, i) => i % 10) },
  ];
  const accepted = acceptedValues.map(sample => {
    const bytes = Buffer.from(JSON.stringify(sample.value));
    const metadata = shape(sample.value);
    assert.equal(metadata.utf8JsonBytes, bytes.length);
    assert.deepEqual(JSON.parse(checkRawJson(bytes, probeLimits)), sample.value);
    const estimatedOutputBytes = measureJson(sample.value, probeLimits);
    assert.ok(estimatedOutputBytes >= bytes.length && estimatedOutputBytes <= probeLimits.messageBytes);
    return { ...sample, bytes, metadata, estimatedOutputBytes };
  });
  assert.equal(accepted[0]!.metadata.utf8JsonBytes, targetBytes);
  assert.equal(accepted[1]!.metadata.utf8JsonBytes, targetBytes);
  assert.equal(accepted[2]!.metadata.maxContainerDepth, probeLimits.jsonDepth);
  assert.equal(accepted[3]!.metadata.tokens, probeLimits.jsonTokens - 1);
  assert.equal(accepted[4]!.metadata.tokens, probeLimits.jsonTokens);

  const malformed = Buffer.concat([accepted[0]!.bytes, Buffer.from('!')]);
  // The scanner budgets structure only; JSON.parse is responsible for this final-byte rejection.
  checkRawJson(malformed, probeLimits);
  expectRejection(() => JSON.parse(checkRawJson(malformed, probeLimits)), 'syntax');
  const overTokenValue = Array.from({ length: probeLimits.jsonTokens }, (_, i) => i % 10);
  const overTokenBytes = Buffer.from(JSON.stringify(overTokenValue));
  expectRejection(() => checkRawJson(overTokenBytes, probeLimits), 'budget');
  expectRejection(() => measureJson(overTokenValue, probeLimits), 'budget');

  const fullMessages = [
    { id: 'context-menu-contract-fixture', workload: 'Existing schema-valid offline menu Context; not an observation of a running game.', result: { ...syntheticContext } },
    { id: 'synthetic-near-limit-utf8-envelope', workload: 'Synthetic envelope corner; neither current production read capability returns this payload.', result: nearLimitToolResult('界') },
    { id: 'synthetic-near-limit-escaped-envelope', workload: 'Synthetic envelope corner; neither current production read capability returns this payload.', result: nearLimitToolResult(escapedUnit) },
  ].map(sample => {
    const message = toolMessage(sample.result);
    const encoded = encodeToolResult(sample.result);
    assert.deepEqual(deserializeMessage(encoded), message); // Public SDK round trip, outside timing.
    assert.ok(encoded.endsWith('\n'));
    assert.ok(Buffer.byteLength(encoded) <= probeLimits.messageBytes);
    return { ...sample, message, metadata: {
      innerResult: shape(sample.result), callToolResult: shape(message.result), jsonRpcMessage: shape(message),
      sdkLineUtf8Bytes: Buffer.byteLength(encoded), estimatedMessageBytes: measureJson(message, probeLimits),
    } };
  });

  if (process.argv.includes('--check')) {
    process.stdout.write(JSON.stringify({ mode: 'fixture-preflight-only', sdkVersion: sdk.version, limits: probeLimits,
      accepted: accepted.map(sample => ({ id: sample.id, ...sample.metadata, estimatedOutputBytes: sample.estimatedOutputBytes })),
      rejected: { syntaxAtFinalByte: malformed.length, finalTokenBudget: shape(overTokenValue) },
      fullMessages: fullMessages.map(sample => ({ id: sample.id, ...sample.metadata })),
    }) + '\n');
    process.exit(0);
  }

  await yieldTurn();
  const processMemoryBefore = process.memoryUsage();
  const timingOverhead = await measure(() => undefined);
  const inputResults = [];
  for (const sample of accepted) {
    inputResults.push({ id: sample.id, status: 'accepted', metadata: sample.metadata, estimatedOutputBytes: sample.estimatedOutputBytes,
      scanAndParse: await measure(() => JSON.parse(checkRawJson(sample.bytes, probeLimits))),
      measureAndStringify: await measure(() => { measureJson(sample.value, probeLimits); return JSON.stringify(sample.value); }),
    });
  }
  const rejectionResults = [
    { id: 'invalid-final-byte-after-valid-prefix', status: 'rejected', stage: 'JSON.parse after successful complete structural scan',
      metadata: { ...accepted[0]!.metadata, utf8JsonBytes: malformed.length, tokens: accepted[0]!.metadata.tokens + 1, invalidSuffixBytes: 1 },
      note: 'Token/depth metadata describes the known fixture prefix plus one primitive suffix; no replacement parser is used.',
      scanAndParse: await measure(() => expectRejection(() => JSON.parse(checkRawJson(malformed, probeLimits)), 'syntax')),
      measureAndStringify: { status: 'not-applicable', reason: 'Malformed raw JSON has no corresponding ordinary JSON value.' },
    },
    { id: 'token-limit-plus-final-number', status: 'rejected', stage: 'Raw scanner reaches the final numeric token; output measurer rejects during array reservation',
      metadata: shape(overTokenValue),
      scanAndParse: await measure(() => expectRejection(() => JSON.parse(checkRawJson(overTokenBytes, probeLimits)), 'budget')),
      measureAndStringify: await measure(() => expectRejection(() => { measureJson(overTokenValue, probeLimits); return JSON.stringify(overTokenValue); }, 'budget')),
    },
  ];
  const messageResults = [];
  for (const sample of fullMessages) {
    messageResults.push({ id: sample.id, workload: sample.workload, status: 'accepted', metadata: sample.metadata,
      completeToolEncoding: await measure(() => encodeToolResult(sample.result)),
      sdkSerializeOnly: await measure(() => serializeMessage(sample.message)),
      plainStringifyOnly: await measure(() => JSON.stringify(sample.message)),
    });
  }

  const batchSize = Math.min(8, probeLimits.activeRequests);
  const sentinel = {
    emptyBaseline: await measureTurn(() => undefined, 0),
    wideObjectScanAndParseBatch: await measureTurn(() => JSON.parse(checkRawJson(accepted[3]!.bytes, probeLimits)), batchSize),
    numberArrayScanAndParseBatch: await measureTurn(() => JSON.parse(checkRawJson(accepted[4]!.bytes, probeLimits)), batchSize),
    fullSyntheticEnvelopeBatch: await measureTurn(() => encodeToolResult(fullMessages[1]!.result), batchSize),
    note: 'Each sentinel is scheduled before a finite synchronous codec batch. This is not SDK dispatch, a real raw chunk pump, or a bound on messages per SDK turn. No baseline subtraction is applied.',
  };
  const processMemoryAfter = process.memoryUsage();
  assert.deepEqual(await fingerprints(), sourceHashes, 'A relevant source changed during sampling; discard mixed evidence and rerun after coordination.');
  const results = {
    formatRevision: 2, observedAtUtc: new Date().toISOString(),
    node: process.version, v8: process.versions.v8, platform: process.platform, architecture: process.arch,
    sdk: { package: '@modelcontextprotocol/server', version: sdk.version, entry: 'top-level public serializeMessage / deserializeMessage' },
    scope: 'Finite isolated codec observations; current production read outputs are small, synthetic limit payloads are not production workload evidence.',
    sampling, limits: probeLimits, sourceHashes,
    measurement: { timingOverhead, clock: 'performance.now monotonic milliseconds; nearest-rank percentiles',
      completeEncodingOrder: ['measure inner result', 'stringify inner result once for text content', 'measure CallToolResult including structuredContent and text', 'construct JSON-RPC result envelope', 'measure envelope as BoundedTransport does', 'SDK public serializeMessage'],
      includes: 'Two clock reads, callback/assignment overhead; rejection cases include exception construction/catching.',
      excludes: 'Fixture construction, metadata counting, schema validation, SDK round-trip assertions, yielding between batches, artifact IO, network/game work, protocol dispatch and schema validation in a real SDK request.',
      metadata: 'tokens count containers, primitive/string values and object keys; maxContainerDepth counts root container as 1; maxValueDepth counts root value as 0. Output byte estimates may conservatively exceed actual JSON bytes.',
    },
    accepted: inputResults, rejected: rejectionResults, fullMessages: messageResults, sentinel,
    processMemoryBefore, processMemoryAfter,
    limitations: [
      'Reported maxima are observations under these finite samples, not hard real-time, event-loop, game-frame or operating-system scheduling bounds.',
      'Complete tool encoding reproduces synchronous encoding stages only; it does not execute the handler, admission lifecycle, SDK dispatch, HTTP or IO.',
      'The two current read capabilities do not produce the synthetic near-64KiB payloads; the Context fixture is offline and not a measured live-game result.',
      'Memory readings are process-wide before/after samples after fixture construction, not heap peaks, per-object ownership, allocation counts or a wire-to-heap bound.',
      'No GC was forced and no heap flags changed; ordinary GC and concurrent system activity can affect tails. Source hashes do not eliminate OS scheduling noise.',
      'Privilege level is not measured here; no ordinary-user compatibility claim is made. No credentials, machine name or absolute local path is recorded.',
    ],
  };
  await writeFile(resolve(repositoryRoot, 'artifacts/host-codec-benchmark.json'), JSON.stringify(results, null, 2) + '\n');
  process.stdout.write(JSON.stringify(results) + '\n');
  void sink;
}
