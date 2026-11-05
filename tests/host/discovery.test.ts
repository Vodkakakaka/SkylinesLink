import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, writeFile, readFile, unlink, rmdir, rename, symlink } from 'node:fs/promises';
import { resolve, dirname } from 'node:path';
import { randomBytes, randomUUID } from 'node:crypto';
import { discoverConnection, readAuthorizationSubjectCandidate, type DiscoverySelection } from '../../src/host/src/game-client/discovery.ts';
import { DiscoveredGameClient } from '../../src/host/src/game-client/discovered-game-client.ts';
import { GameClientError } from '../../src/host/src/game-client/game-client.ts';

const errorCode = (code: string) => (error: unknown) => error instanceof GameClientError && error.code === code;

test('发现严格预期发行/代际/完整snapshot，profile精确选择，且只读不扩权', { timeout: 5000 }, async () => {
  const base = resolve('../../artifacts/host-fixtures', randomUUID());
  const directory = resolve(base, '.skylineslink/environments/fixture/connection'); await mkdir(directory, { recursive: true });
  const selection: DiscoverySelection = { environment: 'fixture', profile: 'Codex', coreManifestSha256: 'a'.repeat(64), userProfile: base };
  const token = randomBytes(32).toString('hex');
  const discovery = { productId: 'skylineslink', environmentId: 'fixture', instanceId: 'fixture-instance', authorityEpoch: 2,
    pipeName: 'skylineslink-offline-fixture', productVersion: '0.1.0-dev.0', contractVersion: '2', coreManifestSha256: selection.coreManifestSha256, processId: process.pid };
  const authorization = { formatVersion: 2, productId: 'skylineslink', environmentId: 'fixture', recordRevision: 3, authorityEpoch: 2,
    profiles: [{ profileId: 'Codex', profileInstanceId: 'a'.repeat(32), grantEpoch: 3, scopeMask: 1, internalToken: token, externalHttpToken: null }] };
  const put = async (file: string, value: unknown) => writeFile(resolve(directory, file), JSON.stringify(value));
  const lazy = new DiscoveredGameClient(selection);
  try {
    await assert.rejects(lazy.call('system.context', '2', {}, new AbortController().signal), errorCode('GAME_UNAVAILABLE'));
    await put('discovery.json', { ...discovery, coreManifestSha256: 'b'.repeat(64) });
    await assert.rejects(discoverConnection(selection), errorCode('INCOMPATIBLE_COMPONENT')); // authorization is absent: no secret read before version check
    await put('discovery.json', discovery); await put('authorization.json', authorization);
    const selected = await discoverConnection(selection); assert.equal(selected.token, token); assert.equal(selected.grantEpoch, 3);
    assert.equal(selected.profileInstanceId, authorization.profiles[0]!.profileInstanceId);
    const expected = { environmentId: 'fixture', profileId: 'Codex', authorityEpoch: 2, grantEpoch: 3 };
    const candidate = await readAuthorizationSubjectCandidate(selection, expected);
    assert.deepEqual(candidate, { ...expected, profileInstanceId: authorization.profiles[0]!.profileInstanceId });
    assert.equal(Object.isFrozen(candidate), true); assert.equal(JSON.stringify(candidate).includes(token), false);
    for (const changed of [{ grantEpoch: 4 }, { authorityEpoch: 3 }, { profileInstanceId: 'b'.repeat(32) }])
      await assert.rejects(readAuthorizationSubjectCandidate(selection, { ...expected, ...changed }), errorCode('SUBJECT_CHANGED'));
    await put('authorization.json', { ...authorization, recordRevision: 5,
      profiles: [{ ...authorization.profiles[0], profileInstanceId: 'b'.repeat(32), grantEpoch: 5 }] });
    const recreated = await discoverConnection(selection);
    assert.equal(recreated.profileInstanceId, 'b'.repeat(32), 'same profile name returns its currently validated incarnation, never a derived name/epoch identity');
    assert.equal(recreated.grantEpoch, 5); await put('authorization.json', authorization);
    await assert.rejects(lazy.call('system.context', '2', {}, new AbortController().signal), errorCode('GAME_UNAVAILABLE')); // failed session does not retry after files appear
    await assert.rejects(discoverConnection({ ...selection, profile: 'codex' }), errorCode('CONNECTION_NOT_CONFIGURED'));
    await assert.rejects(discoverConnection({ ...selection, environment: '../outside' }));
    await assert.rejects(discoverConnection({ ...selection, environment: 'FIXTURE' }));
    await put('authorization.json', { ...authorization, authorityEpoch: 1 }); await assert.rejects(discoverConnection(selection), errorCode('STALE_DISCOVERY'));
    await put('authorization.json', { ...authorization, recordRevision: 1 }); await assert.rejects(discoverConnection(selection), errorCode('UNSAFE_CONNECTION_STORE'));
    await put('authorization.json', { ...authorization, profiles: [...authorization.profiles, { ...authorization.profiles[0], profileId: '尾点.' }] }); await assert.rejects(discoverConnection(selection), errorCode('UNSAFE_CONNECTION_STORE'));
    await put('authorization.json', { ...authorization, profiles: [{ ...authorization.profiles[0], scopeMask: 0 }] }); await assert.rejects(discoverConnection(selection), errorCode('UNAUTHORIZED'));
    for (const profileInstanceId of ['A'.repeat(32), 'g'.repeat(32)]) {
      await put('authorization.json', { ...authorization, profiles: [{ ...authorization.profiles[0], profileInstanceId }] });
      await assert.rejects(discoverConnection(selection), errorCode('UNSAFE_CONNECTION_STORE'));
    }
    await put('authorization.json', { ...authorization, profiles: [...authorization.profiles, { ...authorization.profiles[0], profileId: 'Other' }] });
    await assert.rejects(discoverConnection(selection), errorCode('UNSAFE_CONNECTION_STORE'));
    const legacy = { productId: authorization.productId, environmentId: authorization.environmentId,
      recordRevision: authorization.recordRevision, authorityEpoch: authorization.authorityEpoch,
      profiles: authorization.profiles.map(({ profileInstanceId: _unused, ...profile }) => profile) };
    await put('authorization.json', legacy);
    const legacyBytes = await readFile(resolve(directory, 'authorization.json'));
    await assert.rejects(discoverConnection(selection), errorCode('INCOMPATIBLE_AUTHORIZATION'));
    assert.deepEqual(await readFile(resolve(directory, 'authorization.json')), legacyBytes, 'discovery does not migrate or rewrite legacy identities');
    await writeFile(resolve(directory, 'authorization.json'), Buffer.alloc(65537, 32)); await assert.rejects(discoverConnection(selection), errorCode('UNSAFE_CONNECTION_STORE'));
    await put('authorization.json', authorization);
    const moved = resolve(base, 'moved-connection'); await rename(directory, moved); await symlink(moved, directory, 'junction');
    try { await assert.rejects(discoverConnection(selection), errorCode('UNSAFE_CONNECTION_STORE')); }
    finally { await rmdir(directory); await rename(moved, directory); }
  } finally {
    await lazy.close();
    for (const file of ['authorization.json', 'discovery.json']) { try { await unlink(resolve(directory, file)); } catch (error) { if (!(error instanceof Error && 'code' in error && error.code === 'ENOENT')) throw error; } }
    for (let current = directory; ; current = dirname(current)) { await rmdir(current); if (current === base) break; }
  }
});
