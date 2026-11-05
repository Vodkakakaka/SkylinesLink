using System;
using Newtonsoft.Json;
using SkylinesLink.Infrastructure.Installation;

namespace SkylinesLink.Mod.Runtime
{
    // Display choices only; lifecycle and completion facts remain with their owners.
    internal enum ConnectionNotice
    {
        Starting, Applying, CheckingInstalled, InvalidProfile, CleanupIncomplete, Unavailable,
        ConfigurationFailed, ProfileApplied, ServiceRunning, EnvironmentBusy, PrepareCancelled,
        PrepareVerifiedIncomplete, PrepareDifferentCore, PrepareTimeout, PrepareFailed,
        InstallationUnavailable, NotConfigured, WorkFinishing, Stopping, CheckingFiles,
        CheckingConnectionFiles, Preparing, FinishingPreparation, StoppingPreparation,
        PreferencesUnavailable, Stopped, ClientFieldsUnavailable
    }

    internal static class ConnectionSettingsText
    {
        internal static string Format(ConnectionNotice notice, string? localeId)
            => IsSimplifiedChinese(localeId) ? Chinese(notice) : English(notice);

        // The caller selects this projection only for a completed, non-Ready result.
        // Readback facts precede timeout wording so a partial publication is not hidden.
        internal static ConnectionNotice PreparationFailure(SetupInitializationResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (result.ProcessCause == "CANCELLED") return ConnectionNotice.PrepareCancelled;
            if (result.Installation == PreparedInstallationState.MatchesSource) return ConnectionNotice.PrepareVerifiedIncomplete;
            if (result.Installation == PreparedInstallationState.DifferentCore) return ConnectionNotice.PrepareDifferentCore;
            if (result.ProcessCause == "TIMEOUT" || result.ProcessCause == "OUTPUT_DRAIN_TIMEOUT") return ConnectionNotice.PrepareTimeout;
            return ConnectionNotice.PrepareFailed;
        }

        internal static string ClientFields(ClientLaunchConfiguration configuration, string? localeId)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            string arguments = JsonConvert.SerializeObject(configuration.Arguments);
            return IsSimplifiedChinese(localeId)
                ? "字段使用当前填写的连接名称，请先应用设置。\n传输方式: stdio\n启动程序: " + configuration.Command + "\n参数: " + arguments
                : "These fields use the entered profile name. Apply the settings first.\nTransport: stdio\nCommand: " + configuration.Command + "\nArguments: " + arguments;
        }

        private static bool IsSimplifiedChinese(string? localeId)
            => string.Equals(localeId, "zh-HANS", StringComparison.OrdinalIgnoreCase);

        private static string English(ConnectionNotice notice) => notice switch
        {
            ConnectionNotice.Starting => "Connection is starting.",
            ConnectionNotice.Applying => "Applying connection settings.",
            ConnectionNotice.CheckingInstalled => "Checking the installed mod and connection.",
            ConnectionNotice.InvalidProfile => "Use 1–64 English letters, digits, dots, dashes or underscores. The name cannot end with a dot.",
            ConnectionNotice.CleanupIncomplete => "The connection has not fully closed. Restart the game before reconnecting.",
            ConnectionNotice.Unavailable => "Connection is unavailable. Apply the settings to retry.",
            ConnectionNotice.ConfigurationFailed => "Connection configuration failed. Its saved result will be checked on the next explicit attempt.",
            ConnectionNotice.ProfileApplied => "Connection profile settings applied.",
            ConnectionNotice.ServiceRunning => "Connection service is running. Client access uses saved profile permissions.",
            ConnectionNotice.EnvironmentBusy => "This connection is in use by another instance. Apply the settings to retry after it closes.",
            ConnectionNotice.PrepareCancelled => "Connection preparation was cancelled. Apply the settings when you are ready to retry.",
            ConnectionNotice.PrepareVerifiedIncomplete => "Preparation did not finish normally. The connection files were verified. Apply the settings to retry.",
            ConnectionNotice.PrepareDifferentCore => "The active mod installation changed during preparation. Check the enabled version and restart the game.",
            ConnectionNotice.PrepareTimeout => "Connection preparation took too long. Check the installation, then apply the settings to retry.",
            ConnectionNotice.PrepareFailed => "Connection preparation did not finish. Check the mod installation, then apply the settings to retry.",
            ConnectionNotice.InstallationUnavailable => "The connection files are unavailable or incompatible. Complete installation before connecting.",
            ConnectionNotice.NotConfigured => "Connection is not configured or could not be opened. Apply the settings to retry.",
            ConnectionNotice.WorkFinishing => "Connection work is still finishing. Please wait.",
            ConnectionNotice.Stopping => "Connection is stopping.",
            ConnectionNotice.CheckingFiles => "Checking the mod files.",
            ConnectionNotice.CheckingConnectionFiles => "Checking the AI connection files.",
            ConnectionNotice.Preparing => "Preparing the AI connection.",
            ConnectionNotice.FinishingPreparation => "Finishing connection preparation.",
            ConnectionNotice.StoppingPreparation => "Preparation is still stopping. Wait before retrying.",
            ConnectionNotice.PreferencesUnavailable => "Saved preferences could not be loaded; the fields show defaults.",
            ConnectionNotice.Stopped => "Connection stopped.",
            ConnectionNotice.ClientFieldsUnavailable => "Enter a valid connection profile name above to show the client setup fields.",
            _ => "Connection status is unavailable."
        };

        private static string Chinese(ConnectionNotice notice) => notice switch
        {
            ConnectionNotice.Starting => "正在启动连接。",
            ConnectionNotice.Applying => "正在应用连接配置。",
            ConnectionNotice.CheckingInstalled => "正在检查模组安装和连接。",
            ConnectionNotice.InvalidProfile => "连接名称需为 1–64 个英文字母、数字、点、短横线或下划线，且不能以点结尾。",
            ConnectionNotice.CleanupIncomplete => "连接尚未完全关闭，请重启游戏后再连接。",
            ConnectionNotice.Unavailable => "连接暂不可用，请点击“应用连接配置”重试。",
            ConnectionNotice.ConfigurationFailed => "连接配置未能完成。下次点击“应用连接配置”时会先核对已保存的结果。",
            ConnectionNotice.ProfileApplied => "连接名称和权限已应用。",
            ConnectionNotice.ServiceRunning => "连接服务已启动，客户端按已保存的连接权限访问。",
            ConnectionNotice.EnvironmentBusy => "此连接正被另一个实例使用，请在其关闭后点击“应用连接配置”重试。",
            ConnectionNotice.PrepareCancelled => "连接准备已取消，需要重试时请点击“应用连接配置”。",
            ConnectionNotice.PrepareVerifiedIncomplete => "准备未正常完成，连接文件已通过核验。请点击“应用连接配置”重试。",
            ConnectionNotice.PrepareDifferentCore => "准备期间连接使用的模组安装发生了变化，请检查启用的版本后重启游戏。",
            ConnectionNotice.PrepareTimeout => "连接准备耗时过长，请检查模组安装后点击“应用连接配置”重试。",
            ConnectionNotice.PrepareFailed => "连接准备未能完成，请检查模组安装后点击“应用连接配置”重试。",
            ConnectionNotice.InstallationUnavailable => "连接文件不可用或不兼容，请先完成安装。",
            ConnectionNotice.NotConfigured => "连接尚未配置或暂时无法开启，请点击“应用连接配置”重试。",
            ConnectionNotice.WorkFinishing => "连接操作仍在收尾，请稍候。",
            ConnectionNotice.Stopping => "正在关闭连接。",
            ConnectionNotice.CheckingFiles => "正在检查模组文件。",
            ConnectionNotice.CheckingConnectionFiles => "正在核验 AI 连接文件。",
            ConnectionNotice.Preparing => "正在准备 AI 连接。",
            ConnectionNotice.FinishingPreparation => "连接准备正在收尾。",
            ConnectionNotice.StoppingPreparation => "连接准备尚未完全停止，请等待收尾完成后再重试。",
            ConnectionNotice.PreferencesUnavailable => "无法读取已保存的偏好设置，当前显示默认值。",
            ConnectionNotice.Stopped => "连接已停止。",
            ConnectionNotice.ClientFieldsUnavailable => "请先在上方填写有效的连接名称，再查看客户端接入字段。",
            _ => "暂时无法获取连接状态。"
        };
    }
}
