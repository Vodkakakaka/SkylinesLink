using System;
using System.IO;
using System.Threading;
using Colossal.IO.AssetDatabase;
using Colossal.Json;
using Game.Modding;
using Game.Settings;
using Game.UI.Localization;
using SkylinesLink.Application.Operations;
using SkylinesLink.Infrastructure.Installation;
using SkylinesLink.Mod.Runtime;

namespace SkylinesLink.Mod.Settings
{
    /// <summary>连接配置草稿与只读状态；只有显式按钮才提交配置请求。</summary>
    [FileLocation("SkylinesLink/ConnectionSettings")]
    [SettingsUISection(ConnectionSettings.TabName, ConnectionSettings.GroupName)]
    public sealed class ConnectionSettings : ModSetting
    {
        internal const string TabName = "Connection";
        internal const string GroupName = "Connection";
        private Action<string, bool>? requestApply;
        private Func<string>? readStatus;
        private Func<bool>? isBusy;
        private Func<string>? readLocale;

        public ConnectionSettings(IMod mod, Action<string, bool> requestApply, Func<string> readStatus, Func<bool> isBusy,
            Func<string> readLocale, string loadedVersion, string environment = "default")
            : base(mod)
        {
            this.requestApply = requestApply ?? throw new ArgumentNullException(nameof(requestApply));
            this.readStatus = readStatus ?? throw new ArgumentNullException(nameof(readStatus));
            this.isBusy = isBusy ?? throw new ArgumentNullException(nameof(isBusy));
            this.readLocale = readLocale ?? throw new ArgumentNullException(nameof(readLocale));
            LoadedVersion = loadedVersion ?? throw new ArgumentNullException(nameof(loadedVersion));
            Environment = AuthorizationState.NormalizeEnvironment(environment);
        }

        [Exclude]
        internal string Environment { get; }

        [Exclude]
        public string LoadedVersion { get; }

        [SettingsUITextInput]
        public string Profile { get; set; } = "local";

        public bool AllowCityChanges { get; set; }

        // LoadSettings deserializes writable properties. Exclusion is required
        // so loading a settings file cannot invoke this explicit UI action.
        [Exclude]
        [SettingsUIButton]
        [SettingsUIDisableByCondition(typeof(ConnectionSettings), nameof(IsApplyDisabled))]
        public bool ApplyConnection
        {
            set
            {
                if (value) Volatile.Read(ref requestApply)?.Invoke(Profile, AllowCityChanges);
            }
        }

        [Exclude]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(ConnectionSettings), nameof(GetStatusDisplayName))]
        public string Status
        {
            get
            {
                var read = Volatile.Read(ref readStatus);
                return read == null ? ConnectionSettingsText.Format(ConnectionNotice.Stopped, Locale()) :
                    read() ?? ConnectionSettingsText.Format(ConnectionNotice.Unavailable, Locale());
            }
        }

        [Exclude]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(ConnectionSettings), nameof(GetClientConnectionDisplayName))]
        public string AIClientConnection
        {
            get
            {
                // A view of the current draft, not an installed/authorized claim.
                // Reading it does not apply settings or modify an AI client.
                try { return ConnectionSettingsText.ClientFields(ClientLaunchConfiguration.Create(Environment, Profile), Locale()); }
                catch (ArgumentException) { return ConnectionSettingsText.Format(ConnectionNotice.ClientFieldsUnavailable, Locale()); }
                catch (InvalidDataException) { return ConnectionSettingsText.Format(ConnectionNotice.ClientFieldsUnavailable, Locale()); }
            }
        }

        private LocalizedString GetStatusDisplayName() => LocalizedString.Value(Status);

        private LocalizedString GetClientConnectionDisplayName() => LocalizedString.Value(AIClientConnection);

        private string Locale() => Volatile.Read(ref readLocale)?.Invoke() ?? "en-US";

        private bool IsApplyDisabled()
        {
            var read = Volatile.Read(ref isBusy);
            return read == null || read();
        }

        public override void SetDefaults()
        {
            Profile = "local";
            AllowCityChanges = false;
        }

        // No native UI access. Root still owns in-flight requests and must
        // reject them once stopping; detaching does not cancel accepted work.
        public void Detach()
        {
            Interlocked.Exchange(ref requestApply, null);
            Interlocked.Exchange(ref readStatus, null);
            Interlocked.Exchange(ref isBusy, null);
            Interlocked.Exchange(ref readLocale, null);
        }
    }
}
