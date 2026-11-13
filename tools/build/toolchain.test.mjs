import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { resolve } from 'node:path';

const root = resolve(import.meta.dirname, '../..');
function resolvePacks(component, targetFramework, aot) {
  return JSON.parse(execFileSync('dotnet', ['msbuild',
    `src/desktop/SkylinesLink.${component}/SkylinesLink.${component}.csproj`,
    `-p:TargetFramework=${targetFramework}`, `-p:PublishAot=${aot}`, '-p:RuntimeIdentifier=win-x64',
    '-target:ProcessFrameworkReferences', '-getProperty:RuntimeFrameworkVersion',
    '-getItem:RuntimePack,KnownILCompilerPack,KnownILLinkPack'],
  { cwd: root, encoding: 'utf8', windowsHide: true, timeout: 30000, maxBuffer: 1048576 }));
}

for (const component of ['Launcher', 'Setup', 'Deployment']) {
  test(`${component}: NativeAOT resolves matching runtime, compiler and linker packs`, () => {
    const { Properties, Items } = resolvePacks(component, 'net10.0-windows', true);
    const runtime = Items.RuntimePack.find(pack => pack.Identity === 'Microsoft.NETCore.App.Runtime.win-x64');
    const compiler = Items.KnownILCompilerPack.find(pack => pack.TargetFramework === 'net10.0');
    const linker = Items.KnownILLinkPack.find(pack => pack.TargetFramework === 'net10.0');
    assert.ok(runtime && compiler && linker, 'the SDK must resolve all NativeAOT build packs');
    assert.equal(runtime.NuGetPackageVersion, compiler.ILCompilerPackVersion);
    assert.equal(runtime.NuGetPackageVersion, linker.ILLinkPackVersion);
    assert.equal(runtime.NuGetPackageVersion, Properties.RuntimeFrameworkVersion);
  });
}

for (const [framework, aot] of [['net10.0-windows', false], ['net9.0-windows', true]]) {
  test(`${framework}, PublishAot=${aot}: other runtime selection remains SDK-owned`, () => {
    const { Properties, Items } = resolvePacks('Launcher', framework, aot);
    assert.equal(Properties.RuntimeFrameworkVersion, '');
    assert.ok(Items.RuntimePack.some(pack => pack.Identity === 'Microsoft.NETCore.App.Runtime.win-x64'));
  });
}
