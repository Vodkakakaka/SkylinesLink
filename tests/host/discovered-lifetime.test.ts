import { test } from 'node:test';
import assert from 'node:assert/strict';
import { setImmediate as immediate } from 'node:timers/promises';
import { DiscoveredGameClient, type LiveGameConnection } from '../../src/host/src/game-client/discovered-game-client.ts';
import { GameClientError } from '../../src/host/src/game-client/game-client.ts';
import type { PipeConnection } from '../../src/host/src/game-client/named-pipe-client.ts';
import { productVersion } from '../../src/host/src/shared/identity.ts';

const selection = { environment: 'fixture', profile: 'reader', coreManifestSha256: 'a'.repeat(64), userProfile: process.cwd() };
const connection: PipeConnection = { environmentId: 'fixture', profileId: 'reader', profileInstanceId: 'a'.repeat(32),
  authorityEpoch: 2, grantEpoch: 3, instanceId: 'fixture-instance', coreManifestSha256: selection.coreManifestSha256,
  productVersion, contractVersion: '2', pipeName: 'skylineslink-fixture', token: 'f'.repeat(64) };
const cancelled = (error: unknown) => error instanceof GameClientError && error.code === 'CANCELLED';
const signal = () => new AbortController().signal;

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(complete => { resolve = complete; });
  return { promise, resolve };
}

function fixture(phase: 'discovery' | 'connect') {
  const discovered = deferred<PipeConnection>(), authenticated = deferred<LiveGameConnection>();
  let discoveries = 0, connections = 0, calls = 0, closes = 0, connectingSignal: AbortSignal | undefined;
  const live: LiveGameConnection = {
    connection, usage: { closed: false },
    async call(_id, _revision, _args, waiting) {
      if (waiting.aborted) throw new GameClientError('CANCELLED', 'fixture');
      calls++; return { instanceId: connection.instanceId };
    },
    async close() { closes++; live.usage.closed = true; },
  };
  const game = new DiscoveredGameClient(selection, {
    discover: () => { discoveries++; return phase === 'discovery' ? discovered.promise : Promise.resolve(connection); },
    connect: (_connection, waiting) => {
      connections++; connectingSignal = waiting;
      return phase === 'connect' ? authenticated.promise : Promise.resolve(live);
    },
  });
  return { game, release: () => { discovered.resolve(connection); authenticated.resolve(live); },
    counts: () => ({ discoveries, connections, calls, closes }), connectingSignal: () => connectingSignal };
}

for (const phase of ['discovery', 'connect'] as const) {
  test(`取消首次${phase}等待不取消其他消费者或毒化后续查询`, async () => {
    const f = fixture(phase), first = new AbortController();
    const cancelledFirst = assert.rejects(f.game.call('system.context', '2', {}, first.signal), cancelled);
    const sibling = f.game.call('system.context', '2', {}, signal());
    const pending = Promise.allSettled([cancelledFirst, sibling]);
    try {
      await immediate(); first.abort(); f.release();
      await cancelledFirst;
      assert.deepEqual(await sibling, { instanceId: connection.instanceId });
      assert.deepEqual(await f.game.call('system.context', '2', {}, signal()), { instanceId: connection.instanceId });
      assert.equal(f.connectingSignal()?.aborted, false);
      assert.deepEqual(f.counts(), { discoveries: 1, connections: 1, calls: 2, closes: 0 });
    } finally { f.release(); await pending; await f.game.close(); }
    assert.equal(f.counts().closes, 1);
  });
}

test('预取消不创建共享尝试；全部消费者取消仍不取消Host拥有的连接', async () => {
  const f = fixture('discovery'), first = new AbortController(), second = new AbortController();
  first.abort();
  try {
    await assert.rejects(f.game.call('system.context', '2', {}, first.signal), cancelled);
    assert.equal(f.counts().discoveries, 0);
    const a = new AbortController();
    const rejectedA = assert.rejects(f.game.call('system.context', '2', {}, a.signal), cancelled);
    const rejectedB = assert.rejects(f.game.call('system.context', '2', {}, second.signal), cancelled);
    a.abort(); second.abort(); f.release();
    await Promise.all([rejectedA, rejectedB]);
    await f.game.call('system.context', '2', {}, signal());
    assert.deepEqual(f.counts(), { discoveries: 1, connections: 1, calls: 1, closes: 0 });
  } finally { f.release(); await f.game.close(); }
});

for (const phase of ['discovery', 'connect'] as const) {
  test(`Host关闭等待真实${phase}终结，迟到连接不执行查询`, async () => {
    const f = fixture(phase);
    const rejected = assert.rejects(f.game.call('system.context', '2', {}, signal()), cancelled);
    let closed = false;
    await immediate();
    const closing = f.game.close().then(() => { closed = true; });
    try {
      await immediate(); assert.equal(closed, false);
      if (phase === 'connect') assert.equal(f.connectingSignal()?.aborted, true);
      f.release(); await Promise.all([rejected, closing]);
      assert.deepEqual(f.counts(), { discoveries: 1, connections: phase === 'connect' ? 1 : 0,
        calls: 0, closes: phase === 'connect' ? 1 : 0 });
      await assert.rejects(f.game.call('system.context', '2', {}, signal()), cancelled);
    } finally { f.release(); await Promise.allSettled([rejected, closing]); }
  });
}
