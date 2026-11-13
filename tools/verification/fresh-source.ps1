[CmdletBinding()]
param(
    [switch]$Worker,
    [string]$SnapshotRoot,
    [string]$EvidenceRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-JsonFile([string]$Path, $Value) {
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Assert-Contained([string]$Root, [string]$Path) {
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $full = [IO.Path]::GetFullPath($Path)
    if (!$full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Owned path containment failed.' }
    return $full
}

function Read-OwnedTree([string]$Root) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
            $null = Assert-Contained $Root $entry.FullName
            $entry
            if ($entry.PSIsContainer -and !($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                $pending.Push($entry.FullName)
            }
        }
    }
}

if ($Worker) {
    $SnapshotRoot = [IO.Path]::GetFullPath($SnapshotRoot)
    $EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
    $null = Assert-Contained $EvidenceRoot $SnapshotRoot
    if ((Split-Path $EvidenceRoot -Leaf) -notmatch '^fresh-source-[a-f0-9]{32}$') { throw 'Unexpected evidence directory.' }
    Set-Location -LiteralPath $SnapshotRoot
    $cleared = @(Get-ChildItem Env: | Where-Object Name -like 'CSII_*' | Select-Object -ExpandProperty Name)
    foreach ($name in $cleared) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
    # 只影响本次子进程；不调整用户/机器环境变量或已有包缓存。
    $env:RestoreLockedMode = 'true'
    $steps = @(
        @{ Name = 'restore-pnpm'; Command = 'pnpm'; Arguments = @('install', '--frozen-lockfile', '--offline') },
        @{ Name = 'restore-managed'; Command = 'dotnet'; Arguments = @('restore', 'SkylinesLink.slnx', '--locked-mode', '--configfile', 'NuGet.Config', '--nologo') },
        @{ Name = 'generate'; Command = 'pnpm'; Arguments = @('generate') },
        @{ Name = 'typecheck'; Command = 'pnpm'; Arguments = @('typecheck') },
        @{ Name = 'build-host'; Command = 'pnpm'; Arguments = @('build:host') },
        @{ Name = 'build-managed'; Command = 'pnpm'; Arguments = @('build:managed', '--no-restore') },
        @{ Name = 'check-generated'; Command = 'pnpm'; Arguments = @('check:generated') }
    )
    $results = [Collections.Generic.List[object]]::new()
    foreach ($step in $steps) {
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $tail = [Collections.Generic.Queue[string]]::new()
        $commandArguments = $step.Arguments
        & $step.Command @commandArguments 2>&1 | ForEach-Object {
            $tail.Enqueue([string]$_)
            if ($tail.Count -gt 25) { $null = $tail.Dequeue() }
        }
        $code = $LASTEXITCODE
        $record = [ordered]@{ name = $step.Name; exitCode = $code; milliseconds = $clock.ElapsedMilliseconds }
        if ($code -ne 0) {
            $record['diagnostic'] = @($tail | ForEach-Object {
                # 仅保留脱敏诊断，不持久化本机路径、账户或环境变量值。
                ([string]$_ -replace '(?i)[a-z]:[\\/][^\s"<>|]*', '<local-path>') -replace '(?i)file:///[^\s"<>|]*', '<local-path>'
            })
        }
        $results.Add($record)
        Write-JsonFile (Join-Path $EvidenceRoot 'steps.json') @{ clearedGameVariableCount = $cleared.Count; remainingGameVariableCount = @(Get-ChildItem Env: | Where-Object Name -like 'CSII_*').Count; steps = @($results.ToArray()) }
        Write-Output ($record | ConvertTo-Json -Compress)
        if ($code -ne 0) { exit $code }
    }
    exit 0
}

$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$artifacts = Join-Path $sourceRoot 'artifacts'
$runRoot = Join-Path $artifacts ('fresh-source-' + [Guid]::NewGuid().ToString('N'))
$exportRoot = Join-Path $runRoot 'source'
$null = Assert-Contained $artifacts $runRoot
$null = Assert-Contained $runRoot $exportRoot
$ancestor = $artifacts
while ($ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse output ancestor refused.' }
    $ancestor = Split-Path $ancestor -Parent
}
New-Item -ItemType Directory -Path $exportRoot | Out-Null
$rootFiles = @('.editorconfig', '.gitattributes', '.gitignore', '.node-version', '.nvmrc', 'Directory.Build.props', 'Directory.Build.targets', 'global.json', 'NuGet.Config', 'package.json', 'pnpm-lock.yaml', 'pnpm-workspace.yaml', 'SkylinesLink.slnx')
$sourceExtensions = @('.cs', '.csproj', '.json', '.mjs', '.ts', '.ps1', '.md', '.template', '.yaml', '.props', '.targets', '.slnx')
$copied = [Collections.Generic.List[object]]::new()
$report = [ordered]@{ kind = 'fresh-source-export'; gitCheckout = $false; status = 'incomplete'; gameStarted = $false; deployed = $false; filesystemSandbox = $false; dependencyMode = 'existing toolchain and locked package caches'; sourceFiles = 0; childExitCode = $null; cleanup = 'pending' }
Write-JsonFile (Join-Path $runRoot 'source-manifest.json') @()
try {
    Push-Location -LiteralPath $sourceRoot
    try {
        $candidates = @(& rg --files contracts src tests tools)
        if ($LASTEXITCODE -ne 0) { throw 'Source discovery failed.' }
    }
    finally { Pop-Location }
    $files = @($rootFiles + @($candidates | Where-Object {
        $_ -notmatch '(^|[\\/])(\.local|\.git|node_modules|private|game-libs|decompiled|research-inputs|artifacts|bin|obj|dist)([\\/]|$)' -and
        ([IO.Path]::GetExtension($_) -in $sourceExtensions -or $_ -match '^(src[\\/]host|tools[\\/]release)[\\/]licenses[\\/][^\\/]+\.txt$' -or $_ -match '(^|[\\/])\.gitattributes$')
    }) | Sort-Object -Unique)
    foreach ($relative in $files) {
        $original = Assert-Contained $sourceRoot (Join-Path $sourceRoot $relative)
        $destination = Assert-Contained $exportRoot (Join-Path $exportRoot $relative)
        $ancestor = $original
        while ($ancestor.Length -ge $sourceRoot.Length) {
            if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse source input refused.' }
            $ancestor = Split-Path $ancestor -Parent
        }
        $hash = (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash.ToLowerInvariant()
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -ErrorAction SilentlyContinue | Out-Null
        Copy-Item -LiteralPath $original -Destination $destination
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw 'Source changed while being exported.' }
        $copied.Add([ordered]@{ path = $relative.Replace('\', '/'); sha256 = $hash; bytes = (Get-Item -LiteralPath $destination).Length })
    }
    $report.sourceFiles = $copied.Count
    Write-JsonFile (Join-Path $runRoot 'source-manifest.json') @($copied.ToArray())
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $exportRoot
    foreach ($argument in @('-NoProfile', '-File', $PSCommandPath, '-Worker', '-SnapshotRoot', $exportRoot, '-EvidenceRoot', $runRoot)) { $start.ArgumentList.Add($argument) }
    foreach ($name in @($start.Environment.Keys | Where-Object { $_ -like 'CSII_*' })) { $start.Environment.Remove($name) | Out-Null }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $deadline = [DateTime]::UtcNow.AddMinutes(10)
        while (!$process.WaitForExit(1000)) {
            if ([DateTime]::UtcNow -gt $deadline) { $process.Kill($true); $process.WaitForExit(); throw 'Owned build exceeded its deadline.' }
        }
        $report.childExitCode = $process.ExitCode
        $null = $stdout.GetAwaiter().GetResult()
        $errorOutput = $stderr.GetAwaiter().GetResult()
        if ($errorOutput.Length -gt 0) { $report['workerStderrPresent'] = $true }
    }
    finally { $process.Dispose() }
    if ($report.childExitCode -ne 0) { throw 'An export build step failed; see sanitized steps.json.' }
    $report['exportSourceChanges'] = @($copied | Where-Object { (Get-FileHash -LiteralPath (Join-Path $exportRoot $_.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $_.sha256 } | Select-Object -ExpandProperty path)
    if ($report.exportSourceChanges.Count -gt 0) { throw 'The exported source or a locked dependency manifest changed during generation/build.' }
    $changed = @($copied | Where-Object { (Get-FileHash -LiteralPath (Join-Path $sourceRoot $_.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $_.sha256 } | Select-Object -ExpandProperty path)
    $report['sourceChangedDuringBuild'] = $changed
    if ($changed.Count -gt 0) { throw 'Source changed concurrently; the export is not a stable verification input.' }
    $report.status = 'passed'
}
catch { $report['failureType'] = $_.Exception.GetType().Name; $report.status = 'failed' }
finally {
    if ((Get-Item -LiteralPath $exportRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Owned export root was replaced by a reparse point; refusing traversal.' }
    $entries = @(Read-OwnedTree $exportRoot)
    $built = @($entries | Where-Object { !$_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } | ForEach-Object {
        $link = [bool]($_.Attributes -band [IO.FileAttributes]::ReparsePoint)
        [ordered]@{ path = [IO.Path]::GetRelativePath($exportRoot, $_.FullName).Replace('\', '/'); kind = $(if ($link) { 'reparse-point-not-followed' } else { 'file' }); sha256 = $(if (!$link) { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }); bytes = $(if (!$link) { $_.Length } else { $null }) }
    })
    Write-JsonFile (Join-Path $runRoot 'build-manifest.json') $built
    # 先移除链接本身，再删普通文件和空目录；不递归越过 pnpm/store 链接。
    try {
        $null = Assert-Contained $runRoot $exportRoot
        foreach ($entry in @($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })) { Remove-Item -LiteralPath $entry.FullName -ErrorAction Stop }
        foreach ($entry in @($entries | Where-Object { !$_.PSIsContainer -and !($_.Attributes -band [IO.FileAttributes]::ReparsePoint) })) { Remove-Item -LiteralPath $entry.FullName -ErrorAction Stop }
        foreach ($entry in @($entries | Where-Object { $_.PSIsContainer -and !($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } | Sort-Object { $_.FullName.Length } -Descending)) { Remove-Item -LiteralPath $entry.FullName -ErrorAction Stop }
        Remove-Item -LiteralPath $exportRoot -ErrorAction Stop
        $report.cleanup = 'source-and-build-output-removed'
    }
    catch { $report.cleanup = 'owned-export-retained'; $report['cleanupFailureType'] = $_.Exception.GetType().Name }
    $report['buildManifestEntries'] = $built.Count
    $report['sourceManifestSha256'] = (Get-FileHash -LiteralPath (Join-Path $runRoot 'source-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    $report['buildManifestSha256'] = (Get-FileHash -LiteralPath (Join-Path $runRoot 'build-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-JsonFile (Join-Path $runRoot 'report.json') $report
    Write-Output ([ordered]@{ evidence = [IO.Path]::GetRelativePath($sourceRoot, $runRoot).Replace('\', '/'); result = $report } | ConvertTo-Json -Depth 12)
}
if ($report.status -ne 'passed') { exit 1 }
