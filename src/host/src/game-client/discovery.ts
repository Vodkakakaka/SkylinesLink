import { lstat, open, realpath } from 'node:fs/promises';
import type { BigIntStats } from 'node:fs';
import { dirname, isAbsolute, join, parse, resolve } from 'node:path';
import { parseContractJson, requireContract, validateContract, schemaIds, type EnvironmentDiscovery, type AuthorizationSnapshotV2 } from '@skylineslink/contracts';
import { productVersion, internalContractVersion } from '../shared/identity.ts';
import { GameClientError } from './game-client.ts';
import { isLowerHex256 } from './auth-proof.ts';
import type { PipeConnection } from './named-pipe-client.ts';
import { canonicalEnvironment, isDeploymentIdentifier } from '../shared/identifiers.ts';
import type { ConnectionSubject } from './history-result.ts';

export interface DiscoverySelection {
  environment: string;
  profile: string;
  coreManifestSha256: string;
  /** Internal composition/test dependency, never loaded from discovery JSON or a credential path. */
  userProfile: string;
}

type Observation = { path: string; stat: BigIntStats };
const sameIdentity = (a: BigIntStats, b: BigIntStats) => a.dev === b.dev && a.ino === b.ino;
const unsafe = () => new GameClientError('UNSAFE_CONNECTION_STORE', '连接存储路径或读取身份不安全。');
const changed = () => new GameClientError('STALE_DISCOVERY', '连接记录在读取期间发生变化，请重新建立客户端会话。');

async function observePath(path: string): Promise<Observation[]> {
  const parts: Observation[] = [];
  for (let item = path; ; item = dirname(item)) {
    if (parts.length >= 64) throw unsafe();
    const stat = await lstat(item, { bigint: true });
    if (stat.isSymbolicLink() || (parts.length === 0 ? !stat.isFile() : !stat.isDirectory())) throw unsafe();
    parts.push({ path: item, stat });
    if (dirname(item) === item) break;
  }
  const actual = await realpath(path);
  if (resolve(actual).toLowerCase() !== resolve(path).toLowerCase()) throw unsafe();
  return parts;
}

/** Supplementary Node checks, not a Windows ACL or race-free reparse-point proof. */
export async function readConnectionDocument(path: string, maximumBytes: number): Promise<unknown> {
  if (!Number.isSafeInteger(maximumBytes) || maximumBytes < 1 || maximumBytes > 65536) throw unsafe();
  const before = await observePath(path);
  const original = before[0]!.stat;
  if (original.size < 2n || original.size > BigInt(maximumBytes)) throw unsafe();
  const handle = await open(path, 'r');
  let bytes: Buffer | undefined;
  try {
    const opened = await handle.stat({ bigint: true });
    if (!opened.isFile() || !sameIdentity(original, opened) || opened.size !== original.size || opened.mtimeNs !== original.mtimeNs) throw changed();
    bytes = Buffer.alloc(Number(opened.size) + 1);
    let offset = 0;
    while (offset < bytes.length) {
      const read = await handle.read(bytes, offset, bytes.length - offset, offset);
      if (read.bytesRead === 0) break;
      offset += read.bytesRead;
    }
    const afterRead = await handle.stat({ bigint: true });
    const after = await observePath(path);
    if (offset !== Number(opened.size) || afterRead.size !== opened.size || afterRead.mtimeNs !== opened.mtimeNs
      || after[0]!.stat.size !== opened.size || after[0]!.stat.mtimeNs !== opened.mtimeNs
      || before.length !== after.length || before.some((entry, index) => !sameIdentity(entry.stat, after[index]!.stat))) throw changed();
    return parseContractJson(new TextDecoder('utf-8', { fatal: true }).decode(bytes.subarray(0, offset)));
  } finally { bytes?.fill(0); await handle.close(); }
}

function connectionDirectory(selection: DiscoverySelection): string {
    if (canonicalEnvironment(selection.environment) !== selection.environment || !isDeploymentIdentifier(selection.profile)
      || !isLowerHex256(selection.coreManifestSha256)) throw unsafe();
    if (!isAbsolute(selection.userProfile) || !/^[A-Za-z]:[\\/]$/.test(parse(selection.userProfile).root)
      || selection.userProfile.length > 1024) throw unsafe();
    return join(selection.userProfile, '.skylineslink', 'environments', selection.environment, 'connection');
}

async function readAuthorizationProfile(selection: DiscoverySelection) {
    const directory = connectionDirectory(selection);
    let stored: unknown;
    try { stored = await readConnectionDocument(join(directory, 'authorization.json'), 65536); }
    catch (error) { if (isMissing(error)) throw new GameClientError('CONNECTION_NOT_CONFIGURED', '尚未建立此连接的授权配置。'); throw error; }
    if (validateContract(schemaIds.AuthorizationSnapshot, stored).valid)
      throw new GameClientError('INCOMPATIBLE_AUTHORIZATION', '授权存储缺少本构建要求的稳定身份；未修改原配置，请使用匹配发行。');
    requireContract(schemaIds.AuthorizationSnapshotV2, stored);
    const snapshot = stored as AuthorizationSnapshotV2;
    if (snapshot.environmentId !== selection.environment || snapshot.authorityEpoch > snapshot.recordRevision) throw unsafe();
    const ids = new Set<string>(), instances = new Set<string>();
    for (const profile of snapshot.profiles) {
      if (!isDeploymentIdentifier(profile.profileId) || ids.has(profile.profileId) || !/^[a-f0-9]{32}$/.test(profile.profileInstanceId)
        || instances.has(profile.profileInstanceId) || profile.grantEpoch > snapshot.recordRevision || !isLowerHex256(profile.internalToken)
        || (profile.externalHttpToken !== null && (!isLowerHex256(profile.externalHttpToken) || profile.externalHttpToken === profile.internalToken))) throw unsafe();
      ids.add(profile.profileId); instances.add(profile.profileInstanceId);
    }
    const profile = snapshot.profiles.find(candidate => candidate.profileId === selection.profile);
    if (!profile) throw new GameClientError('CONNECTION_NOT_CONFIGURED', '尚未建立此 profile 的授权配置。');
    if (profile.scopeMask === 0) throw new GameClientError('UNAUTHORIZED', '此连接授权已撤销。');
    return { snapshot, profile };
}

/** A candidate only. Native3 must still CAS all expected values under its lease. */
export async function readAuthorizationSubjectCandidate(selection: DiscoverySelection, expected: ConnectionSubject): Promise<ConnectionSubject> {
  try {
    const { snapshot, profile } = await readAuthorizationProfile(selection);
    if (expected.environmentId !== snapshot.environmentId || expected.profileId !== profile.profileId
      || expected.authorityEpoch !== snapshot.authorityEpoch || expected.grantEpoch !== profile.grantEpoch
      || expected.profileInstanceId !== undefined && expected.profileInstanceId !== profile.profileInstanceId) throw changed();
    return Object.freeze({ environmentId: snapshot.environmentId, profileId: profile.profileId, authorityEpoch: snapshot.authorityEpoch,
      grantEpoch: profile.grantEpoch, profileInstanceId: profile.profileInstanceId });
  } catch { throw new GameClientError('SUBJECT_CHANGED', '无法取得与当前历史主体相符的完整身份，请重新建立客户端会话。'); }
}

export async function discoverConnection(selection: DiscoverySelection): Promise<PipeConnection> {
  try {
    const directory = connectionDirectory(selection);
    let descriptor: unknown;
    try { descriptor = await readConnectionDocument(join(directory, 'discovery.json'), 4096); }
    catch (error) { if (isMissing(error)) throw new GameClientError('GAME_UNAVAILABLE', '当前没有可用的游戏连接发现记录。'); throw error; }
    requireContract(schemaIds.EnvironmentDiscovery, descriptor);
    const discovery = descriptor as EnvironmentDiscovery;
    if (discovery.environmentId !== selection.environment || !isLowerHex256(discovery.coreManifestSha256)) throw unsafe();
    if (discovery.productVersion !== productVersion || discovery.contractVersion !== internalContractVersion
      || discovery.coreManifestSha256 !== selection.coreManifestSha256) throw new GameClientError('INCOMPATIBLE_COMPONENT', '游戏与当前 Host 的发行不匹配，请使用同一核心发行。');
    const { snapshot, profile } = await readAuthorizationProfile(selection);
    if (snapshot.environmentId !== selection.environment || snapshot.authorityEpoch !== discovery.authorityEpoch) throw changed();
    return { pipeName: discovery.pipeName, profileId: profile.profileId, profileInstanceId: profile.profileInstanceId, token: profile.internalToken,
      instanceId: discovery.instanceId, environmentId: discovery.environmentId, authorityEpoch: discovery.authorityEpoch,
      grantEpoch: profile.grantEpoch, coreManifestSha256: selection.coreManifestSha256, productVersion, contractVersion: internalContractVersion };
  } catch (error) {
    if (error instanceof GameClientError) throw error;
    throw new GameClientError('CONNECTION_STORE_UNAVAILABLE', '连接记录无法安全读取或未满足当前契约。');
  }
}

function isMissing(error: unknown): boolean { return error instanceof Error && 'code' in error && error.code === 'ENOENT'; }
