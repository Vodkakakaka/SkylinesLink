using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using Colossal;

namespace SkylinesLink.Mod.Settings
{
    /// <summary>只持已生成的文案键和值，不保留设置对象或连接回调。</summary>
    public sealed class ConnectionLocaleSource : IDictionarySource
    {
        private static readonly IReadOnlyDictionary<string, string> Empty =
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
        private IReadOnlyDictionary<string, string> entries;

        private ConnectionLocaleSource(string localeId, Dictionary<string, string> entries)
        {
            LocaleId = localeId;
            this.entries = new ReadOnlyDictionary<string, string>(entries);
        }

        public string LocaleId { get; }

        public static IReadOnlyList<ConnectionLocaleSource> Create(ConnectionSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return new[]
            {
                new ConnectionLocaleSource("en-US", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [settings.GetSettingsLocaleID()] = "SkylinesLink",
                    [settings.GetOptionTabLocaleID(ConnectionSettings.TabName)] = "Connection",
                    [settings.GetOptionGroupLocaleID(ConnectionSettings.GroupName)] = "Connection",
                    [settings.GetOptionLabelLocaleID(nameof(ConnectionSettings.LoadedVersion))] = "Loaded mod version",
                    [settings.GetOptionDescLocaleID(nameof(ConnectionSettings.LoadedVersion))] = "The mod version loaded by this game session. Installation and connection status are shown separately.",
                    [settings.GetOptionLabelLocaleID(nameof(ConnectionSettings.Profile))] = "Connection profile",
                    [settings.GetOptionDescLocaleID(nameof(ConnectionSettings.Profile))] = "Use the same profile name in your AI client. Apply the settings to make changes effective.",
                    [settings.GetOptionLabelLocaleID(nameof(ConnectionSettings.AllowCityChanges))] = "Allow city changes",
                    [settings.GetOptionDescLocaleID(nameof(ConnectionSettings.AllowCityChanges))] = "Disabled requests read-only access. Enabled allows this connection to change the city. Apply the settings to make this effective.",
                    [settings.GetOptionLabelLocaleID(nameof(ConnectionSettings.ApplyConnection))] = "Apply connection settings",
                    [settings.GetOptionDescLocaleID(nameof(ConnectionSettings.ApplyConnection))] = "Apply the selected profile name and permissions.",
                    [settings.GetOptionLabelLocaleID(nameof(ConnectionSettings.Status))] = "Connection status",
                    [settings.GetOptionDescLocaleID(nameof(ConnectionSettings.Status))] = "Current connection state and any issue that needs attention.",
                    [settings.GetOptionLabelLocaleID(nameof(ConnectionSettings.AIClientConnection))] = "AI client setup fields",
                    [settings.GetOptionDescLocaleID(nameof(ConnectionSettings.AIClientConnection))] = "Add a local stdio MCP connection in your AI client using these fields. Configuration formats vary by client. No credentials are included, and displaying fields does not confirm client access."
                }),
                new ConnectionLocaleSource("zh-HANS", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [settings.GetSettingsLocaleID()] = "天际互联 · SkylinesLink",
                    [settings.GetOptionTabLocaleID(ConnectionSettings.TabName)] = "连接",
                    [settings.GetOptionGroupLocaleID(ConnectionSettings.GroupName)] = "连接",
                    [settings.GetOptionLabelLocaleID(nameof(ConnectionSettings.LoadedVersion))] = "已加载模组版本",
                    [settings.GetOptionDescLocaleID(nameof(ConnectionSettings.LoadedVersion))] = "当前游戏会话实际加载的模组版本。安装和连接状态另行显示。",
                    [settings.GetOptionLabelLocaleID(nameof(ConnectionSettings.Profile))] = "连接名称",
                    [settings.GetOptionDescLocaleID(nameof(ConnectionSettings.Profile))] = "在 AI 客户端中使用同一连接名称。点击“应用连接配置”后生效。",
                    [settings.GetOptionLabelLocaleID(nameof(ConnectionSettings.AllowCityChanges))] = "允许修改城市",
                    [settings.GetOptionDescLocaleID(nameof(ConnectionSettings.AllowCityChanges))] = "关闭时请求只读权限；开启时允许此连接修改城市。点击“应用连接配置”后生效。",
                    [settings.GetOptionLabelLocaleID(nameof(ConnectionSettings.ApplyConnection))] = "应用连接配置",
                    [settings.GetOptionDescLocaleID(nameof(ConnectionSettings.ApplyConnection))] = "应用当前填写的连接名称和权限。",
                    [settings.GetOptionLabelLocaleID(nameof(ConnectionSettings.Status))] = "连接状态",
                    [settings.GetOptionDescLocaleID(nameof(ConnectionSettings.Status))] = "当前连接状态，以及需要处理的问题。",
                    [settings.GetOptionLabelLocaleID(nameof(ConnectionSettings.AIClientConnection))] = "AI 客户端接入字段",
                    [settings.GetOptionDescLocaleID(nameof(ConnectionSettings.AIClientConnection))] = "在 AI 客户端中添加本地 stdio MCP 连接并填写这些值。不同客户端的配置格式可能不同。字段不包含凭据，显示字段不代表客户端已经获得访问权限。"
                })
            };
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
            => Volatile.Read(ref entries);

        // Native language reload calls Unload and then reads this source again.
        // In-memory text owns no locale assets to release; stopping is explicit.
        public void Unload() { }

        public void Deactivate() => Interlocked.Exchange(ref entries, Empty);
    }
}
