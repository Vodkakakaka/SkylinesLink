using System;
using SkylinesLink.Deployment;
using SkylinesLink.Launcher;

if (args.Length > 0 && (args[0] == "--read-history" || args[0] == "--read-history-v2" || args[0] == "--read-history-v3"))
    return args.Length == 1 ? await HistoryReadCommand.RunAsync(args[0] switch
    { "--read-history-v3" => 3, "--read-history-v2" => 2, _ => 1 }) : (int)HistoryReadCode.InvalidRequest;

try
{
    string? profile = null;
    var environment = "default";
    var environmentSeen = false;
    for (var i = 0; i < args.Length; i += 2)
    {
        if (i + 1 == args.Length) throw new ArgumentException();
        if (args[i] == "--profile" && profile is null) profile = args[i + 1];
        else if (args[i] == "--environment" && !environmentSeen) { environment = args[i + 1]; environmentSeen = true; }
        else throw new ArgumentException();
    }
    if (profile is null) throw new ArgumentException();
    CorePackage.ValidateIdentifier(profile);
    environment = CorePackage.NormalizeEnvironment(environment);
    using var package = CorePackage.OpenActive(environment);
    package.RequireSupportedContract();
    var node = package.RequireFile("runtime/node.exe");
    var host = package.RequireFile("host/host.mjs");
    var historyReader = package.RequireFile("desktop/launcher/SkylinesLink.Launcher.exe");
    return OwnedProcess.Run(node, new[] { host, "--transport", "stdio", "--profile", profile, "--environment", environment, "--core-manifest-sha256", package.ManifestSha256,
        "--history-reader", historyReader }, package.Root);
}
catch (Exception)
{
    // 标准错误同样可能被调用方永久堵住；由退出码报告，不引入同步日志旁路。
    return 1;
}
