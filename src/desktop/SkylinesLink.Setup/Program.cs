using System;
using System.IO;
using System.Text;
using System.Text.Json;
using SkylinesLink.Deployment;
using SkylinesLink.Deployment.Generated;
using SkylinesLink.Setup;

Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
if (!((args.Length == 2 && (args[0] == "verify-core" || args[0] == "active-core")) ||
    (args.Length == 3 && (args[0] is "prepare-core" or "activate-core" or "install-launcher" or "launcher-status" or "initialize-core"))))
{
    Console.Error.WriteLine("支持 verify-core <核心制品目录>、prepare-core/activate-core/install-launcher/launcher-status/initialize-core <核心制品目录> <环境标识> 或 active-core <环境标识>；initialize-core仅初始化缺失或核验相同活动核心。");
    return 2;
}
bool activationReturned = false;
bool launcherReturned = false;
try
{
    if (args[0] == "initialize-core")
    {
        var initialized = CoreInitialization.Initialize(args[1], args[2]);
        launcherReturned = initialized.LauncherPublicationAttempted;
        activationReturned = initialized.ActivationPublicationAttempted;
        Console.WriteLine(initialized.Status);
        Console.WriteLine("完整核心与固定入口已核验；许可与源码保留在同环境版本目录。连接授权与游戏可用性须分别核验。");
        return 0;
    }
    if (args[0] == "install-launcher")
    {
        var installed = LauncherInstallation.Install(args[1], args[2]);
        launcherReturned = installed == "installed";
        Console.WriteLine(installed);
        Console.WriteLine("稳定入口已核验；对应许可与源码保留在同环境的版本目录。核心激活与连接授权须分别核验。");
        return 0;
    }
    if (args[0] == "launcher-status")
    {
        Console.WriteLine(LauncherInstallation.Inspect(args[1], args[2]));
        return 0;
    }
    if (args[0] == "activate-core")
    {
        var activated = CoreActivation.Activate(args[1], args[2]);
        activationReturned = activated == "activated";
        Console.WriteLine(activated);
        Console.WriteLine("活动核心已核验；连接配置、游戏部署与实际可用性须分别核验。");
        return 0;
    }
    if (args[0] == "active-core")
    {
        using var active = CorePackage.OpenActive(args[1]);
        active.RequireCompletePayload();
        using var output = new Utf8JsonWriter(Console.OpenStandardOutput());
        DeploymentContract.WriteActivationRecord(output, active.CreateActivationRecord());
        return 0;
    }
    if (args[0] == "prepare-core")
    {
        Console.WriteLine(CorePreparation.Prepare(args[1], args[2]));
        Console.WriteLine("核心文件已准备，尚未激活；来源、平台许可及游戏可用性须分别核验。");
        return 0;
    }
    using var package = CorePackage.Open(args[1]);
    Console.WriteLine($"核心清单与 {package.Manifest.Files.Count} 个文件校验一致；此结果不代表来源、平台许可或游戏验收通过。");
    return 0;
}
catch (Exception error)
{
    var step = error switch
    {
        PreparationException preparation => preparation.Step,
        ActivationException activation => activation.Step,
        LauncherInstallationException launcher => launcher.Step,
        InitializationException initialization => initialization.Step,
        _ => args[0]
    };
    var cause = error.InnerException ?? error;
    var code = cause is System.ComponentModel.Win32Exception native ? native.NativeErrorCode : cause.HResult;
    string result;
    if (args[0] == "initialize-core")
    {
        bool entryAttempted = launcherReturned || error is InitializationException { LauncherPublicationAttempted: true };
        bool coreAttempted = activationReturned || error is InitializationException { ActivationPublicationAttempted: true };
        result = (entryAttempted ? "固定入口发布已尝试；" : "本次未尝试发布固定入口；") +
            (coreAttempted ? "核心激活发布已尝试。" : "本次未尝试发布核心激活记录。") +
            "请分别用launcher-status与active-core核实际结果，不依据失败回执回退或重放。";
    }
    else if (args[0] == "install-launcher")
        result = launcherReturned || error is LauncherInstallationException { PublicationAttempted: true }
            ? "稳定入口发布已尝试，请用launcher-status核实际结果；核心激活记录未由此命令改变。"
            : "本次未发布稳定入口，核心激活记录未由此命令改变。";
    else if (args[0] == "launcher-status")
        result = "本次只读核查未修改稳定入口或核心激活记录。";
    else
        result = activationReturned || error is ActivationException { PublicationAttempted: true }
            ? "发布已尝试，请用active-core核当前活动值，不能据失败回执假定未切换。" : "本次未发布激活记录。";
    Console.Error.WriteLine($"核心制品操作失败（{step}/{cause.GetType().Name}/{code:X8}）；请检查内容冲突、文件占用及路径边界。{result}");
    return 1;
}
