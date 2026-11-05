param([Parameter(Mandatory=$true)][string]$ProbeRoot)
$ErrorActionPreference = 'Stop'
$slProbeTarget = [IO.Path]::GetFullPath($ProbeRoot)
$slProbeParent = [IO.Path]::Combine([Environment]::GetFolderPath('UserProfile'), '.skylineslink', 'environments')
if ([IO.Path]::GetDirectoryName($slProbeTarget) -ne $slProbeParent -or [IO.Path]::GetFileName($slProbeTarget) -notmatch '^probe-[a-f0-9]{24}$') { throw 'Probe cleanup target is outside its owned environment.' }
if (-not (Test-Path -LiteralPath $slProbeTarget)) { exit 0 }
$slProbeWalk = $slProbeTarget
while ($slProbeWalk) {
  $slProbeItem = Get-Item -LiteralPath $slProbeWalk
  if ($slProbeItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Probe cleanup refuses a reparse point.' }
  $slProbeWalk = [IO.Path]::GetDirectoryName($slProbeWalk)
}
$slProbeMarker = Join-Path $slProbeTarget '.owned-probe'
if ((Get-Content -LiteralPath $slProbeMarker -Raw) -ne [IO.Path]::GetFileName($slProbeTarget)) { throw 'Probe ownership marker is missing.' }
if (Get-ChildItem -LiteralPath $slProbeTarget -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Probe cleanup refuses nested reparse points.' }
Remove-Item -LiteralPath $slProbeTarget -Recurse
if (Test-Path -LiteralPath $slProbeTarget) { throw 'Probe cleanup did not complete.' }
