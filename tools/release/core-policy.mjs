import { isAbsolute } from 'node:path';

export function meetsMinimumVersion(actual, minimum) {
  const current = /^v?(\d+)\.(\d+)\.(\d+)$/.exec(actual);
  const baseline = /^>=(\d+)\.(\d+)\.(\d+)$/.exec(minimum);
  if (!current || !baseline) return false;
  for (let i = 1; i <= 3; i++) {
    const difference = Number(current[i]) - Number(baseline[i]);
    if (difference !== 0) return difference > 0;
  }
  return true;
}

export const managedNames = ['SkylinesLink.Contracts', 'SkylinesLink.Application', 'SkylinesLink.Infrastructure', 'SkylinesLink.Mod'];
const brandingSources = new Set([
  'assets/branding/mark.svg', 'assets/branding/mark-monochrome.svg', 'assets/branding/app-icon.svg',
  'assets/branding/wordmark-en.svg', 'assets/branding/wordmark-zh-CN.svg',
  'assets/branding/app-icon-1024.png', 'assets/branding/app-icon-256.png',
  'assets/branding/wordmark-en.png', 'assets/branding/wordmark-zh-CN.png', 'assets/branding/city-link-concept.png',
]);
const internalEdges = {
  'SkylinesLink.Contracts': [],
  'SkylinesLink.Application': ['SkylinesLink.Contracts'],
  'SkylinesLink.Infrastructure': ['SkylinesLink.Contracts', 'SkylinesLink.Application'],
  'SkylinesLink.Mod': ['SkylinesLink.Contracts', 'SkylinesLink.Application', 'SkylinesLink.Infrastructure'],
};
const framework = new Set(['mscorlib', 'netstandard', 'System', 'System.Core', 'Newtonsoft.Json']);
const game = new Set(['Game', 'Colossal.Core', 'Colossal.IO.AssetDatabase', 'Colossal.Logging', 'Colossal.Localization', 'Colossal.PSI.Common', 'Unity.Burst', 'Unity.Collections', 'Unity.Entities', 'Unity.Mathematics', 'UnityEngine.CoreModule']);
export function validateManagedClosure(records, assemblyVersion, informationalVersion) {
  if (records.length !== managedNames.length || new Set(records.map(x => x.name)).size !== managedNames.length) throw new Error('CORE_MANAGED_SET');
  for (const record of records) {
    if (!managedNames.includes(record.name) || record.file !== `${record.name}.dll` || record.managed !== true
      || record.version !== assemblyVersion || record.informationalVersion !== informationalVersion
      || record.buildMode !== null || record.probeTypePresent !== false || !/^[a-f0-9]{32}$/.test(record.moduleId)
      || record.moduleId === '0'.repeat(32)) throw new Error('CORE_MANAGED_IDENTITY');
    for (const dependency of record.references) {
      if (internalEdges[record.name].includes(dependency) || framework.has(dependency)) continue;
      if (record.name === 'SkylinesLink.Mod' && game.has(dependency)) continue;
      throw new Error('CORE_ASSEMBLY_REFERENCE');
    }
  }
}

export function requireInspectedBytes(inspected, copied) {
  if (inspected.size !== copied.size || inspected.sha256 !== copied.sha256) throw new Error('CORE_INSPECTED_BYTES_CHANGED');
}

export function managedComponentIndex(assemblies, copiedFiles) {
  const copied = new Map(copiedFiles.map(file => [file.path, file]));
  if (copied.size !== copiedFiles.length) throw new Error('CORE_COPY_PATHS');
  if (assemblies.length !== managedNames.length || new Set(assemblies.map(record => record.name)).size !== managedNames.length)
    throw new Error('CORE_MANAGED_SET');
  const components = managedNames.map(name => {
    const image = assemblies.find(record => record.name === name), path = `mod/${name}.dll`;
    if (!image || image.file !== `${name}.dll` || !/^[a-f0-9]{32}$/.test(image.moduleId) || image.moduleId === '0'.repeat(32)
      || !copied.has(path)) throw new Error('CORE_MANAGED_IDENTITY');
    requireInspectedBytes(image, copied.get(path));
    return { path, moduleId: image.moduleId };
  });
  return { formatVersion: 1, productId: 'skylineslink', components };
}

export function validateOutputInventory(kind, names) {
  if (!['mod', 'host', 'Launcher', 'Setup'].includes(kind)) throw new Error('CORE_UNKNOWN_ROLE');
  const expected = kind === 'mod' ? new Set([
    ...managedNames.flatMap(name => [`${name}.dll`, `${name}.pdb`]),
    'SkylinesLink.Mod_win_x86_64.dll', 'SkylinesLink.Mod_win_x86_64.pdb',
    'SkylinesLink.Mod_mac_x86_64.bundle', 'SkylinesLink.Mod_linux_x86_64.so',
  ]) : kind === 'host' ? new Set(['host.mjs', 'host.mjs.LEGAL.txt', 'host.THIRD_PARTY_NOTICES.txt'])
    // A fresh native publish also emits symbols for the statically linked, owned
    // Deployment project. Build symbols are recognized here but never copied to Core.
    : new Set([`SkylinesLink.${kind}.exe`, `SkylinesLink.${kind}.pdb`, 'SkylinesLink.Deployment.pdb', 'DOTNET.LICENSE.txt', 'DOTNET.THIRD-PARTY-NOTICES.txt', 'DOTNET.SOURCES.txt']);
  if (names.some(name => !expected.has(name))) throw new Error('CORE_UNEXPECTED_BUILD_OUTPUT');
}

export function validateSourcePath(path, mode) {
  if (!['100644', '100755'].includes(mode) || isAbsolute(path) || path.includes('\\') || path.split('/').some(x => x === '..' || x === '')) throw new Error('CORE_SOURCE_PATH');
  if (/(^|\/)(?:\.local|\.git|node_modules|artifacts|bin|obj|game-libs|decompiled|research-inputs)(\/|$)/i.test(path)) throw new Error('CORE_PRIVATE_SOURCE');
  const name = path.split('/').at(-1);
  const special = new Set(['LICENSE', '.editorconfig', '.gitattributes', '.gitignore', '.gitkeep', '.node-version', '.nvmrc', '.npmrc']);
  if (!special.has(name) && !brandingSources.has(path) && !/\.(?:cs|csproj|json|md|mjs|ts|ps1|slnx|props|targets|template|yaml|yml|config)$/i.test(name)
    && !/^(?:src\/host|tools\/release)\/licenses\/[^/]+\.txt$/.test(path)) throw new Error('CORE_SOURCE_EXTENSION');
}

export function rejectPrivatePrefixes(bytes, prefixes) {
  const strings = [bytes.toString('utf8'), bytes.toString('utf16le'), bytes.subarray(1).toString('utf16le')].map(text => text.toLowerCase());
  for (const prefix of prefixes) for (const value of [prefix, prefix.replaceAll('\\', '/')])
    if (value && strings.some(text => text.includes(value.toLowerCase()))) throw new Error('CORE_LOCAL_PATH_PREFIX');
}
