using System;
using System.Collections.Generic;

namespace SkylinesLink.Shared.Installation
{
    /// <summary>当前完整Core的必要角色；桌面准备与同包执行前使用同一集合。</summary>
    internal static class CorePayloadFiles
    {
        internal static readonly IReadOnlyList<string> Required = Array.AsReadOnly(new[] {
            "LICENSE", "SOURCE.txt", "source/SkylinesLink-source.zip",
            "runtime/node.exe", "runtime/Node.LICENSE.txt",
            "host/host.mjs", "host/host.mjs.LEGAL.txt", "host/host.THIRD_PARTY_NOTICES.txt",
            "mod/managed-components.json", "mod/SkylinesLink.Contracts.dll", "mod/SkylinesLink.Application.dll",
            "mod/SkylinesLink.Infrastructure.dll", "mod/SkylinesLink.Mod.dll",
            "desktop/launcher/SkylinesLink.Launcher.exe", "desktop/launcher/DOTNET.LICENSE.txt",
            "desktop/launcher/DOTNET.THIRD-PARTY-NOTICES.txt", "desktop/launcher/DOTNET.SOURCES.txt",
            "desktop/setup/SkylinesLink.Setup.exe", "desktop/setup/DOTNET.LICENSE.txt",
            "desktop/setup/DOTNET.THIRD-PARTY-NOTICES.txt", "desktop/setup/DOTNET.SOURCES.txt"
        });
    }
}
