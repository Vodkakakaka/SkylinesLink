param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$IntegrationProbe
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../../src/game/SkylinesLink.Mod/SkylinesLink.Mod.csproj'
$previousRollForward = $env:DOTNET_ROLL_FORWARD
try {
    # 官方后处理器目标为 net6.0。只为本次构建允许使用现有较新稳定运行时，
    # 实际后处理失败仍按原退出码失败，不修改工具文件或机器级运行策略。
    $env:DOTNET_ROLL_FORWARD = 'Major'
    $buildArguments = @('build', $project, '--configuration', $Configuration, '--nologo')
    if ($IntegrationProbe) { $buildArguments += '-p:SkylinesLinkIntegrationProbe=true' }
    & dotnet @buildArguments
    $buildExitCode = $LASTEXITCODE
} finally {
    $env:DOTNET_ROLL_FORWARD = $previousRollForward
}
exit $buildExitCode
