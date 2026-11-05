#requires -Version 7.4
param(
  [Parameter(Mandatory=$true)][ValidateSet('prepare','inspect')][string]$Mode,
  [Parameter(Mandatory=$true)][string]$EnvironmentName,
  [ValidateSet('codex','dsh')][string]$Client = 'codex'
)
$ErrorActionPreference = 'Stop'
if ($EnvironmentName -notmatch '^probe-[a-f0-9]{24}$') { throw 'Invalid owned environment name.' }
$profileRoot = [Environment]::GetFolderPath('UserProfile')
$environmentRoot = [IO.Path]::Combine($profileRoot, '.skylineslink', 'environments', $EnvironmentName)
$walk = [IO.Path]::GetDirectoryName($environmentRoot)
while ($walk) {
  if (Test-Path -LiteralPath $walk) {
    if ((Get-Item -LiteralPath $walk).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Fixture ancestors must not be reparse points.' }
  }
  $walk = [IO.Path]::GetDirectoryName($walk)
}
if ($Mode -eq 'prepare') {
  if (Test-Path -LiteralPath $environmentRoot) { throw 'Fixture must be new.' }
  if ($Client -eq 'codex') {
    $defaultUserConfig = [IO.Path]::Combine($profileRoot, '.codex', 'config.toml')
    $defaultUserConfigPresent = Test-Path -LiteralPath $defaultUserConfig
    $configWalk = [IO.Path]::Combine($environmentRoot, 'codex-cwd')
    while ($configWalk) {
      if (Test-Path -LiteralPath (Join-Path $configWalk '.git')) { throw 'Fixture must not be inside an existing repository.' }
      $candidateConfig = [IO.Path]::Combine($configWalk, '.codex', 'config.toml')
      if ($candidateConfig -ne $defaultUserConfig -and (Test-Path -LiteralPath $candidateConfig)) { throw 'Fixture ancestors contain project configuration.' }
      $configWalk = [IO.Path]::GetDirectoryName($configWalk)
    }
    # Only existence is inspected. CODEX_HOME plus project_root_markers=[] exclude the normal user file.
  }
  $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
  $security = New-Object Security.AccessControl.DirectorySecurity
  $security.SetOwner($identity.User)
  $security.SetAccessRuleProtection($true, $false)
  $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
  foreach ($sid in @($identity.User, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
    $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', $inherit, 'None', 'Allow')
    [void]$security.AddAccessRule($rule)
  }
  [void][IO.FileSystemAclExtensions]::CreateDirectory($security, $environmentRoot)
  [IO.File]::WriteAllText((Join-Path $environmentRoot '.owned-probe'), $EnvironmentName, [Text.UTF8Encoding]::new($false))
  $directoryNames = if ($Client -eq 'codex') { @('codex-home','codex-cwd','codex-sqlite','codex-logs') } else { @('dsh-home','dsh-cwd','dsh-agents-home') }
  foreach ($name in $directoryNames) {
    [void][IO.FileSystemAclExtensions]::CreateDirectory($security, (Join-Path $environmentRoot $name))
  }
  $owner = (Get-Acl -LiteralPath $environmentRoot).GetOwner([Security.Principal.SecurityIdentifier])
  if ($owner -ne $identity.User) { throw 'Fixture owner does not match current user.' }
  $result = @{ profileRoot=$profileRoot; environmentRoot=$environmentRoot; ownedEnvironment=$EnvironmentName }
  if ($Client -eq 'codex') { $result.defaultUserConfigPresent = $defaultUserConfigPresent }
  $result | ConvertTo-Json -Compress
  exit 0
}
if ((Get-Content -LiteralPath (Join-Path $environmentRoot '.owned-probe') -Raw) -ne $EnvironmentName) { throw 'Fixture ownership mismatch.' }
# Only non-secret process metadata; never request CommandLine or inspect another client's config.
$processes = @(Get-CimInstance -Query 'SELECT ProcessId,ParentProcessId,ExecutablePath,CreationDate FROM Win32_Process' |
  Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($environmentRoot + '\', [StringComparison]::OrdinalIgnoreCase) } |
  ForEach-Object { @{ pid=[int]$_.ProcessId; parentPid=[int]$_.ParentProcessId; path=$_.ExecutablePath; startedUtc=$_.CreationDate.ToUniversalTime().ToString('o') } })
@{ processes=$processes } | ConvertTo-Json -Depth 4 -Compress
