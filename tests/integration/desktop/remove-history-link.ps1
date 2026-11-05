param([Parameter(Mandatory=$true)][string]$ProbeRoot)
$ErrorActionPreference = 'Stop'
$historyProbeRoot = [IO.Path]::GetFullPath($ProbeRoot)
$historyProbeParent = [IO.Path]::Combine([Environment]::GetFolderPath('UserProfile'), '.skylineslink', 'environments')
if ([IO.Path]::GetDirectoryName($historyProbeRoot) -ne $historyProbeParent -or [IO.Path]::GetFileName($historyProbeRoot) -notmatch '^probe-[a-f0-9]{24}$') { throw 'History fixture path invalid.' }
if ((Get-Content -LiteralPath (Join-Path $historyProbeRoot '.owned-probe') -Raw) -cne [IO.Path]::GetFileName($historyProbeRoot)) { throw 'History fixture ownership missing.' }
$historyProbeAncestor = $historyProbeRoot
while ($historyProbeAncestor) {
    if ((Get-Item -LiteralPath $historyProbeAncestor).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'History fixture ancestor is redirected.' }
    $historyProbeAncestor = [IO.Path]::GetDirectoryName($historyProbeAncestor)
}
$historyProbeLink = Join-Path $historyProbeRoot 'connection'
$historyProbeOriginal = Join-Path $historyProbeRoot 'connection-original'
if (!((Get-Item -LiteralPath $historyProbeLink).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Expected owned junction missing.' }
if ((Get-Item -LiteralPath $historyProbeOriginal).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Original fixture is redirected.' }
Remove-Item -LiteralPath $historyProbeLink
Move-Item -LiteralPath $historyProbeOriginal -Destination $historyProbeLink
