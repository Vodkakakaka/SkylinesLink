import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import { archiveFiles } from './core.mjs';
import { managedNames, managedComponentIndex, rejectPrivatePrefixes, requireInspectedBytes, validateManagedClosure, validateOutputInventory, validateSourcePath } from './core-policy.mjs';

const root = resolve(import.meta.dirname, '../..');
const informationalVersion = '0.1.0-dev.0+' + 'a'.repeat(40);
const records = () => managedNames.map((name, index) => ({ name, file: `${name}.dll`, managed: true, version: '0.1.0.0', probeTypePresent: false,
  informationalVersion, buildMode: null, moduleId: 'a'.repeat(30) + String(index + 1).padStart(2, '0'),
  size: 4096 + index, sha256: 'b'.repeat(62) + String(index + 1).padStart(2, '0'),
  references: name === 'SkylinesLink.Mod' ? ['mscorlib', 'Game', 'Colossal.IO.AssetDatabase', 'SkylinesLink.Application'] : ['netstandard', 'Newtonsoft.Json'] }));

test('实际闭包策略拒绝probe、反向引用、未知依赖和错版身份', () => {
  validateManagedClosure(records(), '0.1.0.0', informationalVersion);
  for (const mutate of [
    value => { value[3].probeTypePresent = true; },
    value => { value[0].references.push('Game'); },
    value => { value[0].references.push('Colossal.IO.AssetDatabase'); },
    value => { value[3].references.push('SkylinesLink.Setup'); },
    value => { value[3].references.push('ThirdParty.Private.Mod'); },
    value => { value[1].version = '0.2.0.0'; },
    value => { value[0].file = 'other.dll'; },
    value => { value[0].informationalVersion = '0.1.0-other+' + 'a'.repeat(40); },
    value => { value[3].buildMode = 'integration-probe'; },
    value => { value[0].moduleId = ''; },
    value => { value[0].moduleId = 'A'.repeat(32); },
    value => { value[0].moduleId = '0'.repeat(32); },
    value => { value[1] = value[0]; },
  ]) { const value = records(); mutate(value); assert.throws(() => validateManagedClosure(value, '0.1.0.0', informationalVersion)); }
  const inspected = { size: 4096, sha256: 'a'.repeat(64) };
  requireInspectedBytes(inspected, { ...inspected });
  assert.throws(() => requireInspectedBytes(inspected, { size: 4096, sha256: 'b'.repeat(64) }), /CORE_INSPECTED_BYTES_CHANGED/);
});

test('托管索引仅从同一已核复制字节派生固定四角色，不复制版本或SHA', () => {
  const images = records();
  const copied = images.map(image => ({ path: `mod/${image.file}`, size: image.size, sha256: image.sha256 }));
  copied.push({ path: 'host/host.mjs', size: 999, sha256: 'c'.repeat(64) });
  const expected = { formatVersion: 1, productId: 'skylineslink', components: [
    { path: 'mod/SkylinesLink.Contracts.dll', moduleId: 'a'.repeat(30) + '01' },
    { path: 'mod/SkylinesLink.Application.dll', moduleId: 'a'.repeat(30) + '02' },
    { path: 'mod/SkylinesLink.Infrastructure.dll', moduleId: 'a'.repeat(30) + '03' },
    { path: 'mod/SkylinesLink.Mod.dll', moduleId: 'a'.repeat(30) + '04' },
  ] };
  assert.deepEqual(managedComponentIndex(images, copied), expected);
  assert.deepEqual(managedComponentIndex([...images].reverse(), [...copied].reverse()), expected);
  for (const [mutate, error] of [
    [(_images, files) => { files[0].sha256 = 'd'.repeat(64); }, /CORE_INSPECTED_BYTES_CHANGED/],
    [(_images, files) => { files[0].size++; }, /CORE_INSPECTED_BYTES_CHANGED/],
    [(assemblies) => { assemblies.pop(); }, /CORE_MANAGED_SET/],
    [(assemblies) => { assemblies[1] = assemblies[0]; }, /CORE_MANAGED_SET/],
    [(assemblies) => { assemblies[0].name = 'Another.Module'; }, /CORE_MANAGED_IDENTITY/],
    [(assemblies) => { assemblies[0].file = 'other.dll'; }, /CORE_MANAGED_IDENTITY/],
    [(assemblies) => { assemblies[0].moduleId = ''; }, /CORE_MANAGED_IDENTITY/],
    [(assemblies) => { assemblies[0].moduleId = 'A'.repeat(32); }, /CORE_MANAGED_IDENTITY/],
    [(assemblies) => { assemblies[0].moduleId = '0'.repeat(32); }, /CORE_MANAGED_IDENTITY/],
    [(_images, files) => { files.shift(); }, /CORE_MANAGED_IDENTITY/],
    [(_images, files) => { files.push({ ...files[0] }); }, /CORE_COPY_PATHS/],
  ]) {
    const changedImages = structuredClone(images), changedFiles = structuredClone(copied);
    mutate(changedImages, changedFiles);
    assert.throws(() => managedComponentIndex(changedImages, changedFiles), error);
  }
});

test('构建根白名单允许官方多平台输出，但禁止私有/游戏DLL混入', () => {
  validateOutputInventory('mod', [...managedNames.map(name => `${name}.dll`), 'SkylinesLink.Mod_win_x86_64.dll', 'SkylinesLink.Mod_linux_x86_64.so', 'SkylinesLink.Mod_mac_x86_64.bundle']);
  for (const name of ['Game.dll', 'Newtonsoft.Json.dll', 'authorization.json', 'MenuProbeSession.dll']) assert.throws(() => validateOutputInventory('mod', [name]));
  assert.throws(() => validateOutputInventory('Launcher', ['SkylinesLink.Setup.exe']));
  validateOutputInventory('Launcher', ['SkylinesLink.Launcher.exe', 'SkylinesLink.Launcher.pdb', 'SkylinesLink.Deployment.pdb']);
  assert.throws(() => validateOutputInventory('Launcher', ['ThirdParty.Private.pdb']));
  assert.throws(() => validateOutputInventory('host', ['host.mjs.map']));
});

test('源码输入排除链接与私有二进制，只接收明确品牌源素材', () => {
  for (const path of ['LICENSE', 'NuGet.Config', 'src/host/licenses/zod-4.6.7.LICENSE.txt', '.gitattributes', 'src/example.cs', 'assets/branding/mark.svg', 'assets/branding/city-link-concept.png', 'assets/branding/app-icon-256.png']) validateSourcePath(path, '100644');
  for (const [path, mode] of [['src/link.cs', '120000'], ['artifacts/private.json', '100644'], ['.local/state.md', '100644'], ['Game.dll', '100644'], ['screenshot.png', '100644'], ['assets/branding/screenshot.png', '100644'], ['assets/branding/private.svg', '100644'], ['assets/elsewhere/mark.svg', '100644'], ['../bad.cs', '100644']]) assert.throws(() => validateSourcePath(path, mode));
  for (const encoding of ['utf8', 'utf16le']) assert.throws(() => rejectPrivatePrefixes(Buffer.from('prefix C:\\Users\\SyntheticProbe\\work', encoding), ['C:\\Users\\SyntheticProbe']));
  rejectPrivatePrefixes(Buffer.from('/_/virtual-source.cs'), ['C:\\Users\\SyntheticProbe']);
});

test('真实git archive的成员与准确HEAD相同；损坏ZIP不当对应源码', () => {
  const archive = spawnSync('git', ['archive', '--format=zip', 'HEAD'], { cwd: root, windowsHide: true, maxBuffer: 64 * 1024 * 1024 });
  assert.equal(archive.status, 0);
  const expected = spawnSync('git', ['ls-tree', '-r', '--name-only', '-z', 'HEAD'], { cwd: root, windowsHide: true, encoding: 'utf8' });
  assert.equal(expected.status, 0);
  assert.deepEqual(archiveFiles(archive.stdout), expected.stdout.split('\0').filter(Boolean).sort());
  const tree = spawnSync('git', ['ls-tree', '-r', '-z', 'HEAD'], { cwd: root, windowsHide: true, encoding: 'utf8' });
  assert.equal(tree.status, 0);
  const blobs = new Map(tree.stdout.split('\0').filter(Boolean).map(entry => { const match = /^\d+ blob ([0-9a-f]{40})\t([\s\S]+)$/.exec(entry); assert.ok(match); return [match[2], match[1]]; }));
  assert.deepEqual(archiveFiles(archive.stdout, blobs), [...blobs.keys()].sort());
  // Central-directory members remain identical, but this different expected blob must fail.
  const changed = new Map(blobs); changed.set([...changed.keys()][0], '0'.repeat(40));
  assert.throws(() => archiveFiles(archive.stdout, changed), /CORE_SOURCE_ARCHIVE_CONTENT/);
  assert.throws(() => archiveFiles(archive.stdout.subarray(0, archive.stdout.length - 50)));
  assert.throws(() => archiveFiles(Buffer.alloc(8)));
});
