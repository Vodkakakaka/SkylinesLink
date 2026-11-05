[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$previousLauncher = $env:SKYLINESLINK_TEST_LAUNCHER
$previousSetup = $env:SKYLINESLINK_TEST_SETUP
$previousLauncherKind = $env:SKYLINESLINK_TEST_LAUNCHER_KIND
Push-Location -LiteralPath $repository
try {
    $evidence = @()
    foreach ($name in @('Launcher', 'Setup')) {
        $directory = Join-Path $repository "artifacts/nativeaot/$($name.ToLowerInvariant())"
        $file = Join-Path $directory "SkylinesLink.$name.exe"
        if (!(Test-Path -LiteralPath $file)) { throw 'Build NativeAOT outputs before running this verification.' }
        if (@(Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Name -match '\.(dll|deps\.json|runtimeconfig\.json)$' }).Count -gt 0) {
            throw 'NativeAOT directory unexpectedly contains managed runtime inputs.'
        }
        $bytes = [IO.File]::ReadAllBytes($file)
        $header = [BitConverter]::ToInt32($bytes, 60) + 24
        $isPe64 = [BitConverter]::ToUInt16($bytes, $header) -eq 0x20b
        $clrRva = [BitConverter]::ToUInt32($bytes, $header + 112 + 14 * 8)
        $clrSize = [BitConverter]::ToUInt32($bytes, $header + 116 + 14 * 8)
        if (!$isPe64 -or $clrRva -ne 0 -or $clrSize -ne 0) { throw 'Unexpected managed or non-x64 image.' }
        # 使用 BCL PE reader 核真实 CodeView，不能只扫描源码后宣称二进制已脱敏。
        $pe = [System.Reflection.PortableExecutable.PEReader]::new([IO.File]::OpenRead($file))
        $pdbNames = @()
        try {
            foreach ($entry in $pe.ReadDebugDirectory()) {
                if ($entry.Type -ne [System.Reflection.PortableExecutable.DebugDirectoryEntryType]::CodeView) { continue }
                $pdb = $pe.ReadCodeViewDebugDirectoryData($entry).Path
                if (!$pdb -or [IO.Path]::GetFileName($pdb) -cne $pdb -or $pdb -match '[\\/:]') {
                    throw 'NativeAOT CodeView contains a directory or invalid PDB name.'
                }
                $pdbNames += $pdb
            }
        }
        finally { $pe.Dispose() }
        $texts = @([Text.Encoding]::UTF8.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1))
        $prefixes = @($repository, [Environment]::GetFolderPath('UserProfile'))
        foreach ($prefix in $prefixes) {
            foreach ($variant in @($prefix, $prefix.Replace('\', '/'))) {
                if ($texts.Where({ $_.Contains($variant, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
                    throw 'NativeAOT image contains a local workspace or user-directory prefix.'
                }
            }
        }
        $evidence += [ordered]@{ component = $name; bytes = $bytes.Length; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(); pe64 = $isPe64; clrHeaderRva = $clrRva; clrHeaderBytes = $clrSize; pdbReferences = $pdbNames; localPathPrefixCheckPassed = $true }
        if ($name -eq 'Launcher') { $env:SKYLINESLINK_TEST_LAUNCHER = $file } else { $env:SKYLINESLINK_TEST_SETUP = $file }
    }
    $env:SKYLINESLINK_TEST_LAUNCHER_KIND = 'nativeaot'
    $slTestOutput = @(& node --test --test-reporter=tap tests/integration/desktop/launcher.test.mjs tests/integration/desktop/history.test.mjs tests/integration/desktop/history-runtime.test.mjs)
    $slTestExit = $LASTEXITCODE
    $slTestOutput | Where-Object { $_ -match '^(ok |not ok |# (tests|pass|fail|cancelled|skipped|todo|duration))' } | Write-Output
    if ($slTestExit -ne 0) { $slTestOutput | Write-Output; throw 'NativeAOT desktop integration failed.' }
    $slCountLines = @($slTestOutput | Where-Object { $_ -match '^# tests [0-9]+$' })
    if ($slCountLines.Count -ne 1 -or $slCountLines[0] -notmatch '^# tests ([1-9][0-9]*)$') { throw 'Missing actual integration test count.' }
    $slIntegrationCount = [int]$Matches[1]
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { $isElevated = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
    finally { $identity.Dispose() }
    [ordered]@{ kind = 'nativeaot-local-integration'; components = $evidence; integrationTests = $slIntegrationCount; isElevated = $isElevated; cleanMachineValidated = $false; ordinaryUserValidated = !$isElevated; gameValidated = $false } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath 'artifacts/nativeaot/validation.json' -Encoding utf8NoBOM
}
finally {
    $env:SKYLINESLINK_TEST_LAUNCHER = $previousLauncher
    $env:SKYLINESLINK_TEST_SETUP = $previousSetup
    $env:SKYLINESLINK_TEST_LAUNCHER_KIND = $previousLauncherKind
    Pop-Location
}
