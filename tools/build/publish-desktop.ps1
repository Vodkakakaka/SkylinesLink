[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows) { throw 'The current desktop target requires Windows.' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location -LiteralPath $repository
try {
    foreach ($name in @('Launcher', 'Setup')) {
        $project = "src/desktop/SkylinesLink.$name/SkylinesLink.$name.csproj"
        $output = "artifacts/nativeaot/$($name.ToLowerInvariant())"
        & dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --property:PublishAot=true --property:RestoreLockedMode=true --output $output --nologo
        if ($LASTEXITCODE -ne 0) { throw "NativeAOT publication failed for $name." }
        if (!(Test-Path -LiteralPath (Join-Path $output "SkylinesLink.$name.exe"))) { throw "NativeAOT output is missing for $name." }
        & (Join-Path $repository 'tools/release/write-nativeaot-notices.ps1') -ProjectName $name
    }
}
finally { Pop-Location }
