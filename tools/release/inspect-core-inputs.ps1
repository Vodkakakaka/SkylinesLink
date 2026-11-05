[CmdletBinding()]
param([Parameter(Mandatory)][string]$ModDirectory, [Parameter(Mandatory)][string]$LauncherDirectory, [Parameter(Mandatory)][string]$SetupDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Read PE/ECMA-335 metadata only. Never load an assembly or run its initialization.
function Read-Image([string]$Path, [bool]$Managed, [bool]$AllowVirtualPdb) {
    $stream = [IO.File]::OpenRead($Path)
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $headers = $pe.PEHeaders
        if ($Managed -ne $pe.HasMetadata) { throw 'CORE_IMAGE_KIND' }
        if (!$Managed -and ($headers.CoffHeader.Machine -ne [System.Reflection.PortableExecutable.Machine]::Amd64 -or $null -ne $headers.CorHeader)) { throw 'CORE_NATIVE_TARGET' }
        $debugNames = @()
        foreach ($entry in $pe.ReadDebugDirectory()) {
            if ($entry.Type -ne [System.Reflection.PortableExecutable.DebugDirectoryEntryType]::CodeView) { continue }
            $debugPath = $pe.ReadCodeViewDebugDirectoryData($entry).Path
            $virtual = $AllowVirtualPdb -and $debugPath.StartsWith('/_/', [StringComparison]::Ordinal)
            if (!$debugPath -or (!$virtual -and ([IO.Path]::GetFileName($debugPath) -cne $debugPath -or $debugPath -match '[\\/:]'))) { throw 'CORE_PRIVATE_DEBUG_PATH' }
            $debugNames += [IO.Path]::GetFileName($debugPath)
        }
        $record = [ordered]@{ file = [IO.Path]::GetFileName($Path); managed = $Managed; debugFileNames = $debugNames }
        if ($Managed) {
            $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            $definition = $metadata.GetAssemblyDefinition()
            $record['name'] = $metadata.GetString($definition.Name)
            $record['version'] = $definition.Version.ToString()
            $record['moduleId'] = $metadata.GetGuid($metadata.GetModuleDefinition().Mvid).ToString('N')
            $record['references'] = @($metadata.AssemblyReferences | ForEach-Object { $metadata.GetString($metadata.GetAssemblyReference($_).Name) } | Sort-Object)
            $record['probeTypePresent'] = @($metadata.TypeDefinitions | Where-Object { $metadata.GetString($metadata.GetTypeDefinition($_).Name) -eq 'MenuProbeSession' }).Count -ne 0
            $information = @()
            $modes = @()
            foreach ($handle in $definition.GetCustomAttributes()) {
                $attribute = $metadata.GetCustomAttribute($handle)
                if ($attribute.Constructor.Kind -ne [System.Reflection.Metadata.HandleKind]::MemberReference) { continue }
                $member = $metadata.GetMemberReference([System.Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
                if ($member.Parent.Kind -ne [System.Reflection.Metadata.HandleKind]::TypeReference) { continue }
                $type = $metadata.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle]$member.Parent)
                if ($metadata.GetString($type.Namespace) -ne 'System.Reflection') { continue }
                $name = $metadata.GetString($type.Name)
                if ($name -notin @('AssemblyInformationalVersionAttribute', 'AssemblyMetadataAttribute')) { continue }
                $blob = $metadata.GetBlobReader($attribute.Value)
                if ($blob.ReadUInt16() -ne 1) { throw 'CORE_ASSEMBLY_ATTRIBUTE' }
                $first = $blob.ReadSerializedString()
                if ($name -eq 'AssemblyInformationalVersionAttribute') { $information += $first }
                else { $second = $blob.ReadSerializedString(); if ($first -eq 'SkylinesLink.BuildMode') { $modes += $second } }
                if ($blob.ReadUInt16() -ne 0 -or $blob.RemainingBytes -ne 0) { throw 'CORE_ASSEMBLY_ATTRIBUTE' }
            }
            if ($information.Count -ne 1 -or $modes.Count -gt 1) { throw 'CORE_ASSEMBLY_ATTRIBUTE' }
            $record['informationalVersion'] = $information[0]
            $record['buildMode'] = if ($modes.Count -eq 1) { $modes[0] } else { $null }
        }
        elseif ([IO.Path]::GetExtension($Path) -eq '.exe') { $record['informationalVersion'] = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion }
        # Bind the metadata to bytes under the SAME read-only file handle. Copying must match this hash.
        $record['size'] = $stream.Length
        $stream.Position = 0
        $record['sha256'] = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream))
        return $record
    }
    finally { $pe.Dispose() }
}

$managed = @('SkylinesLink.Contracts', 'SkylinesLink.Application', 'SkylinesLink.Infrastructure', 'SkylinesLink.Mod')
$assemblies = @($managed | ForEach-Object { Read-Image (Join-Path $ModDirectory ($_ + '.dll')) $true $true })
$native = @(
    (Read-Image (Join-Path $LauncherDirectory 'SkylinesLink.Launcher.exe') $false $false),
    (Read-Image (Join-Path $SetupDirectory 'SkylinesLink.Setup.exe') $false $false)
)
$burst = Join-Path $ModDirectory 'SkylinesLink.Mod_win_x86_64.dll'
if (Test-Path -LiteralPath $burst) { $native += Read-Image $burst $false $false }
[ordered]@{ assemblies = $assemblies; native = $native; windowsBurstPresent = (Test-Path -LiteralPath $burst) } | ConvertTo-Json -Depth 8 -Compress
