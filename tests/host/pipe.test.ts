import { test } from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { NamedPipeGameClient } from '../../src/host/src/game-client/named-pipe-client.ts';
import { discoverConnection } from '../../src/host/src/game-client/discovery.ts';
import { GameClientError } from '../../src/host/src/game-client/game-client.ts';
import { capabilities, ContractValidationError, type ContextV2 } from '../../contracts/ts/index.ts';
import { startRuntimeFixture } from './runtime-fixture.ts';

test('Node ↔ .NET Framework 4.8：真实管道认证、上下文代际、撤销及释放（当前进程权限）', { timeout: 20000 }, async () => {
  const fixture = await startRuntimeFixture();
  const { ready, token, coreManifestSha256 } = fixture;
  let client: NamedPipeGameClient | undefined;
  try {
    const connection = await discoverConnection({ environment: 'probe', profile: 'probe', coreManifestSha256, userProfile: fixture.base });
    assert.equal(connection.pipeName, ready.pipeName); assert.equal(connection.token, token);
    assert.match(connection.profileInstanceId, /^[a-f0-9]{32}$/);
    await assert.rejects(NamedPipeGameClient.connect({ ...connection, token: randomBytes(32).toString('hex') }), GameClientError);
    for (const stale of [{ instanceId: 'previous-instance' }, { authorityEpoch: connection.authorityEpoch + 1 }]) {
      await assert.rejects(NamedPipeGameClient.connect({ ...connection, ...stale }), (error: unknown) => error instanceof GameClientError && error.code === 'STALE_DISCOVERY');
    }
    await assert.rejects(NamedPipeGameClient.connect({ ...connection, coreManifestSha256: 'b'.repeat(64) }), (error: unknown) => error instanceof GameClientError && error.code === 'INCOMPATIBLE_COMPONENT');
    client = await NamedPipeGameClient.connect(connection);
    assert.equal('token' in client.connection, false);
    assert.equal(client.connection.profileInstanceId, connection.profileInstanceId, 'the validated Auth2 incarnation survives the real Wire2 connection as local metadata');
    const before = await client.call('system.context', '2', {}, new AbortController().signal) as Record<string, unknown>;
    assert.equal(before.instanceId, ready.instanceId); assert.equal(before.world, null); assert.equal(before.contractVersion, '2');
    const selector = { kind: 'operationId', operationId: 'missing' };
    const activeQuery = capabilities.find(capability => capability.id === 'operations.get')!;
    assert.deepEqual(await client.call(activeQuery.id, activeQuery.schemaRevision, { selector }, new AbortController().signal),
      { selector, result: { kind: 'live', instanceId: ready.instanceId, lookup: { kind: 'absent' } } });
    for (const revision of ['4', '3']) await assert.rejects(client.call('operations.get', revision, { selector }, new AbortController().signal),
      (error: unknown) => error instanceof ContractValidationError && error.issues.some(issue => issue.code === 'SCHEMA_REVISION'));
    assert.equal(client.usage.pending, 0, 'old revision is rejected by the registered-input boundary before writing a frame');
    const loaded = await fixture.command('load-generation'); assert.equal(loaded.kind, 'loaded');
    const after = await client.call('system.context', '2', {}, new AbortController().signal) as ContextV2;
    assert.equal(after.world?.generation, String(loaded.generation)); assert.equal(after.gameState, 'ready');
    assert.equal(after.world?.cityId, null, 'verified loaded scope remains available without a persistent city ID');
    await assert.rejects(client.call('system.context', '1', {}, new AbortController().signal),
      (error: unknown) => error instanceof ContractValidationError && error.issues.some(issue => issue.code === 'SCHEMA_REVISION'));
    const usage = await fixture.command('metrics');
    assert.ok(Number(usage.peakIngressPayloadCount) > 0); assert.ok(Number(usage.peakIngressPayloadCapacity) > 0);
    assert.ok(Number(usage.peakEgressPayloadCount) > 0); assert.ok(Number(usage.peakEgressPayloadCapacity) > 0);
    assert.equal((await fixture.command('revoke')).kind, 'revoked');
    await assert.rejects(client.call('system.context', '2', {}, new AbortController().signal), (error: unknown) => error instanceof GameClientError && error.code === 'UNAUTHORIZED');
    await client.close(); assert.deepEqual(client.usage, { pending: 0, pendingBytes: 0, frameBytes: 0, closed: true });
  } finally {
    await client?.close(); await fixture.stop();
  }
});

test('Node ↔ net48默认Legacy：Read可查Stop但不能变更，Control也明确unavailable而非虚构absent', { timeout: 15000 }, async () => {
  const selector = { taskId: 'known-original-task', expectedControlVersion: 1 };
  for (const control of [false, true]) {
    const fixture = await startRuntimeFixture({ operationProbe: control });
    let client: NamedPipeGameClient | undefined;
    try {
      const connection = await discoverConnection({ environment: 'probe', profile: 'probe',
        coreManifestSha256: fixture.coreManifestSha256, userProfile: fixture.base });
      client = await NamedPipeGameClient.connect(connection);
      assert.deepEqual(await client.call('tasks.stop.get', '1', selector, new AbortController().signal), { selector,
        result: { kind: 'live', instanceId: fixture.ready.instanceId, lookup: { kind: 'unavailable', receipt: null, reason: 'TASKS_UNAVAILABLE' } } });
      if (control) assert.deepEqual(await client.call('tasks.stop', '1', selector, new AbortController().signal),
        { selector, lookup: { kind: 'unavailable', receipt: null, reason: 'TASKS_UNAVAILABLE' } });
      else await assert.rejects(client.call('tasks.stop', '1', selector, new AbortController().signal),
        (error: unknown) => error instanceof GameClientError && error.code === 'UNAUTHORIZED');
    } finally { await client?.close(); await fixture.stop(); }
  }
});
