[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Launcher', 'Setup')][string]$ProjectName)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$inputs = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'runtime-inputs.json') | ConvertFrom-Json -AsHashtable
if ($inputs.formatVersion -ne 1) { throw 'Unsupported runtime input record.' }
$expected = $inputs.dotnet
$project = "SkylinesLink.$ProjectName"
$output = Join-Path $repository "artifacts/nativeaot/$($ProjectName.ToLowerInvariant())"
if (!(Test-Path -LiteralPath (Join-Path $output "$project.exe") -PathType Leaf)) { throw 'NativeAOT product output is missing.' }
$obj = Join-Path $repository "artifacts/dotnet/obj/$project"
$assets = Get-Content -Raw -LiteralPath (Join-Path $obj 'project.assets.json') | ConvertFrom-Json -AsHashtable
$lock = Get-Content -Raw -LiteralPath (Join-Path $repository "src/desktop/$project/packages.nativeaot.lock.json") | ConvertFrom-Json -AsHashtable

foreach ($package in $expected.compilerPackages) {
    $found = $false
    foreach ($group in $lock.dependencies.Values) {
        if ($group.Contains($package.id)) {
            $dependency = $group[$package.id]
            if ($dependency.resolved -ne $expected.version -or $dependency.contentHash -cne $package.contentHash) { throw 'NativeAOT compiler lock differs from the reviewed notice input.' }
            $found = $true
        }
    }
    $key = "$($package.id)/$($expected.version)"
    if (!$found -or !$assets.libraries.Contains($key) -or $assets.libraries[$key].sha512 -cne $package.contentHash) {
        throw 'Actual NativeAOT restore does not match the reviewed compiler package.'
    }
}

# Runtime packs are implicit SDK inputs and are not present in packages.lock.json.
# Inspect the actual compiler/linker responses rather than substituting compiler-tool notices.
$runtimePackages = @($expected.runtimePack, $expected.frameworkPack)
$runtimeFiles = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
$nugetRoot = $null
$managedCount = 0
$nativeCount = 0
$compilerReferences = [Collections.Generic.List[string]]::new()
foreach ($kind in @('managed', 'native')) {
    $rsp = if ($kind -eq 'managed') { "$project.ilc.rsp" } else { 'link.rsp' }
    $rspPath = Join-Path $obj "release_win-x64/native/$rsp"
    if ((Get-Item -LiteralPath $rspPath).Length -gt 2MB) { throw 'NativeAOT response exceeds the inspection budget.' }
    foreach ($line in [IO.File]::ReadLines($rspPath)) {
        $candidate = $line.Trim()
        if ($kind -eq 'managed') {
            if (!$candidate.StartsWith('-r:')) { continue }
            $candidate = $candidate.Substring(3)
        }
        $candidate = $candidate.Trim('"')
        if ($kind -eq 'managed') { $compilerReferences.Add($candidate) }
        if ($candidate -notmatch '^(?<root>.+)[\\/](?<id>microsoft\.netcore\.app\.runtime(?:\.nativeaot)?\.win-x64)[\\/](?<version>[^\\/]+)[\\/](?<entry>.+)$') { continue }
        if ($Matches.version -ne $expected.version) { throw 'Actual NativeAOT runtime version changed.' }
        $actualRoot = [IO.Path]::GetFullPath($Matches.root)
        if ($null -ne $nugetRoot -and !$actualRoot.Equals($nugetRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'NativeAOT uses multiple package roots.' }
        $nugetRoot = $actualRoot
        $entry = $Matches.entry.Replace('\', '/').Replace('//', '/')
        $runtimeFiles[[IO.Path]::GetFullPath($candidate)] = @{ PackageId=$Matches.id; Entry=$entry; Verified=$false }
        if ($kind -eq 'managed') { $managedCount++ } else { $nativeCount++ }
    }
}
if ($managedCount -eq 0 -or $nativeCount -eq 0) { throw 'Actual NativeAOT runtime inputs were not found.' }

foreach ($package in $runtimePackages + @($expected.compilerPackages)) {
    $packageDirectory = Join-Path $nugetRoot "$($package.id.ToLowerInvariant())/$($expected.version)"
    $archive = Join-Path $packageDirectory "$($package.id.ToLowerInvariant()).$($expected.version).nupkg"
    $archiveHash = [Convert]::ToBase64String([Convert]::FromHexString((Get-FileHash -Algorithm SHA512 -LiteralPath $archive).Hash))
    if ($archiveHash -cne $package.archiveSha512) { throw 'NuGet archive differs from the signature-verified input.' }
    $metadata = Get-Content -Raw -LiteralPath (Join-Path $packageDirectory '.nupkg.metadata') | ConvertFrom-Json -AsHashtable
    if ($metadata.contentHash -cne $package.contentHash -or $metadata.source -ne 'https://api.nuget.org/v3/index.json') { throw 'NuGet content hash/source differs from the reviewed input.' }
}

foreach ($package in $runtimePackages) {
  $runtimeArchive = Join-Path $nugetRoot "$($package.id.ToLowerInvariant())/$($expected.version)/$($package.id.ToLowerInvariant()).$($expected.version).nupkg"
  $zip = [IO.Compression.ZipFile]::OpenRead($runtimeArchive)
  try {
    foreach ($file in $runtimeFiles.GetEnumerator()) {
        if (!$file.Value.PackageId.Equals($package.id, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $entry = $zip.GetEntry($file.Value.Entry)
        if ($null -eq $entry -or (Get-Item -LiteralPath $file.Key).Length -ne $entry.Length) { throw 'An actual NativeAOT runtime input is missing or changed.' }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
        if ((Get-FileHash -LiteralPath $file.Key -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hash) { throw 'An actual NativeAOT runtime input differs from its verified archive.' }
        $file.Value.Verified = $true
    }
    if ($package.id -ne $expected.runtimePack.id) { continue }
    foreach ($notice in @(
        @{ File=$expected.licenseFile; Hash=$expected.licenseSha256; Size=$expected.licenseSize; Entry=$expected.runtimePack.licenseEntry; Target='DOTNET.LICENSE.txt' },
        @{ File=$expected.noticesFile; Hash=$expected.noticesSha256; Size=$expected.noticesSize; Entry=$expected.runtimePack.noticesEntry; Target='DOTNET.THIRD-PARTY-NOTICES.txt' }
    )) {
        $source = Join-Path $PSScriptRoot "licenses/$($notice.File)"
        if ((Get-Item -LiteralPath $source).Length -ne $notice.Size -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -cne $notice.Hash) { throw 'Original .NET notice input changed.' }
        $entry = $zip.GetEntry($notice.Entry)
        if ($null -eq $entry -or $entry.Length -ne $notice.Size) { throw 'Original .NET notice archive entry changed.' }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
        if ($hash -cne $notice.Hash) { throw 'Original .NET notice differs from the verified archive.' }
        [IO.File]::Copy($source, (Join-Path $output $notice.Target), $true)
    }
  }
  finally { $zip.Dispose() }
}

# ILC -r inputs can include native DLLs. Establish runtime archive/source/byte
# identity above before accepting a native input; never downgrade bad metadata.
$managedReferenceCount = 0
$nativeReferenceCount = 0
foreach ($candidate in $compilerReferences) {
    $referenceName = [IO.Path]::GetFileName($candidate)
    $reference = $null
    $referenceStream = $null
    $forbidden = $false
    $hasMetadata = $false
    try {
        $referenceStream = [IO.File]::OpenRead($candidate)
        $reference = [System.Reflection.PortableExecutable.PEReader]::new($referenceStream)
        $headers = $reference.PEHeaders
        if ($null -eq $headers.PEHeader) { throw 'Not a PE image.' }
        $hasMetadata = $reference.HasMetadata
        if ($hasMetadata) {
            if ($null -eq $headers.CorHeader) { throw 'Metadata has no CLR header.' }
            $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($reference)
            if (!$metadata.IsAssembly) { throw 'Compiler reference has no assembly definition.' }
            $names = @($metadata.GetString($metadata.GetAssemblyDefinition().Name))
            $names += @($metadata.AssemblyReferences | ForEach-Object { $metadata.GetString($metadata.GetAssemblyReference($_).Name) })
            $forbidden = @($names | Where-Object { $_ -match '^(Game|SkylinesLink\.(Contracts|Application|Infrastructure)|Newtonsoft\.Json)$' }).Count -ne 0
        }
        elseif ($null -ne $headers.CorHeader -or $headers.CoffHeader.Machine -ne [System.Reflection.PortableExecutable.Machine]::Amd64 -or $headers.PEHeader.Magic -ne [System.Reflection.PortableExecutable.PEMagic]::PE32Plus) {
            throw 'Not a native win-x64 PE image.'
        }
    }
    catch { throw "NativeAOT compiler reference PE/metadata unavailable: $referenceName" }
    finally {
        if ($null -ne $reference) { $reference.Dispose() }
        elseif ($null -ne $referenceStream) { $referenceStream.Dispose() }
    }
    if ($hasMetadata) {
        if ($forbidden) { throw "NativeAOT desktop has a forbidden managed reference in: $referenceName" }
        $managedReferenceCount++
    }
    else {
        $path = [IO.Path]::GetFullPath($candidate)
        if (!$runtimeFiles.ContainsKey($path)) { throw "NativeAOT native compiler reference is not a verified runtime native entry: $referenceName" }
        $bound = $runtimeFiles[$path]
        if (!$bound.Verified -or $runtimePackages.id -inotcontains $bound.PackageId -or $bound.Entry -cnotmatch '^runtimes/win-x64/native/[^/]+\.dll$') {
            throw "NativeAOT native compiler reference is not a verified runtime native entry: $referenceName"
        }
        $nativeReferenceCount++
    }
}

$sources = @"
.NET NativeAOT runtime $($expected.version), win-x64
Source: $($expected.repository)/tree/$($expected.commit)/src/runtime
Runtime package: $($expected.runtimePack.sourceArchive)
Framework package: $($expected.frameworkPack.sourceArchive)
Signed-package content hash (NuGet): $($expected.runtimePack.contentHash)
Whole archive SHA-512: $($expected.runtimePack.archiveSha512)
Framework signed-package content hash (NuGet): $($expected.frameworkPack.contentHash)
Framework whole archive SHA-512: $($expected.frameworkPack.archiveSha512)
The adjacent LICENSE and THIRD-PARTY-NOTICES are complete original runtime-package texts.
The notice collection is conservatively preserved; it is not a claim that every listed component is linked.
Compiler tools and the .NET SDK are build inputs and are not included in this output.
Actual compiler -r inputs: $managedReferenceCount managed; $nativeReferenceCount verified runtime native; forbidden desktop dependencies: 0.
Signature verification was recorded on $($expected.verification.date) for the pinned archive bytes.
This build checks those bytes and the actual runtime references; it does not perform fresh online signature validation.
This record covers the .NET runtime notice inputs, not a complete product or marketplace license review.
"@
[IO.File]::WriteAllText((Join-Path $output 'DOTNET.SOURCES.txt'), $sources.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "NativeAOT notices: $ProjectName; $managedCount runtime compiler inputs, $nativeCount runtime linker inputs verified."
Write-Host "NativeAOT desktop reference boundary: $ProjectName; $managedReferenceCount managed -r inputs, $nativeReferenceCount verified native -r inputs; forbidden references: 0."
