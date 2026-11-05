using System;
using System.Collections.Generic;
using SkylinesLink.Application.Operations;
using SkylinesLink.Shared.Installation;

namespace SkylinesLink.Infrastructure.Installation
{
    /// <summary>供客户端填写的stdio字段；纯值描述，不证明安装、授权或连接状态。</summary>
    public sealed class ClientLaunchConfiguration
    {
        private ClientLaunchConfiguration(string environment, string profile)
        {
            Command = CoreInstallationPaths.StableLauncherPath(environment);
            Arguments = Array.AsReadOnly(new[] { "--environment", environment, "--profile", profile });
        }

        public string Command { get; }
        public IReadOnlyList<string> Arguments { get; }

        public static ClientLaunchConfiguration Create(string environment, string profile)
        {
            string normalizedEnvironment = AuthorizationState.NormalizeEnvironment(environment);
            AuthorizationState.ValidateIdentifier(profile);
            return new ClientLaunchConfiguration(normalizedEnvironment, profile);
        }
    }
}
