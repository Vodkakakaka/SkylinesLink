#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using Colossal.Core;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Operations;
using SkylinesLink.Mod.Runtime;
using SkylinesLink.Mod.Settings;

namespace SkylinesLink.Mod.Native
{
    public sealed partial class NormalModSession
    {
        internal const string RuntimeScopeProbeChanged = "NORMAL_CONNECTION_RUNTIME_SCOPE_CHANGED";

        // A private qualification entry, not a product environment switch.
        internal static NormalModSession ForSettingsProbe(IMod mod, UpdateSystem updateSystem, string environment, Action<string>? report)
        {
            environment = AuthorizationState.NormalizeEnvironment(environment);
            if (!environment.StartsWith("probe-", StringComparison.Ordinal)) throw new ArgumentException("A probe environment is required.");
            return new NormalModSession(mod, updateSystem, report, environment, false);
        }

        // Exercise the existing trusted setting action without asking the SDK
        // to serialize a preference fragment in this isolated qualification.
        internal void ApplySettingsProbe(string profile)
        {
            RequireMainThread();
            if (readPreferences || !environment.StartsWith("probe-", StringComparison.Ordinal) || IsBusy())
                throw new InvalidOperationException("The isolated settings action is unavailable.");
            AuthorizationState.ValidateIdentifier(profile);
            var page = settings ?? throw new InvalidOperationException("The settings page is unavailable.");
            page.Profile = profile;
            page.AllowCityChanges = false;
            page.ApplyConnection = true;
        }

        // Bind expected identities once, but read the existing runtime authority
        // on every invocation. UI diagnostics are observations, not a control port.
        internal Action BindRuntimeScopeProbe(string expectedEnvironment, string expectedVersion,
            string expectedProfile, string expectedInstanceId, long expectedGeneration)
        {
            RequireMainThread();
            InstalledGameConnection source;
            ConnectionSettings page;
            lock (sync)
            {
                source = connection ?? throw new InvalidOperationException(RuntimeScopeProbeChanged);
                page = settings ?? throw new InvalidOperationException(RuntimeScopeProbeChanged);
            }
            return () =>
            {
                RequireMainThread();
                var observed = source.Read();
                var context = source.Context.Read();
                bool currentScope = observed.Phase == GameConnectionPhase.Active && !observed.NativeLeaseRetained
                    && !source.Context.IsStopped && context.State == "ready"
                    && context.InstanceId == expectedInstanceId && context.Generation == expectedGeneration
                    && source.ProductVersion == expectedVersion;
                lock (sync)
                {
                    if (!currentScope || !ReferenceEquals(connection, source) || !ReferenceEquals(settings, page)
                        || readPreferences || environment != expectedEnvironment || page.Environment != expectedEnvironment
                        || page.LoadedVersion != expectedVersion || acceptedConfigurationRequests != 1
                        || page.Profile != expectedProfile || page.AllowCityChanges)
                        throw new InvalidOperationException(RuntimeScopeProbeChanged);
                }
            };
        }

        internal JObject ReadSettingsProbe()
        {
            RequireMainThread();
            var page = settings ?? throw new InvalidOperationException("The settings page is unavailable.");
            var labels = new JObject();
            var dictionary = GameManager.instance.localizationManager.activeDictionary;
            foreach (string property in new[] { nameof(ConnectionSettings.Profile), nameof(ConnectionSettings.Status),
                nameof(ConnectionSettings.LoadedVersion), nameof(ConnectionSettings.AIClientConnection) })
            {
                bool found = dictionary.TryGetValue(page.GetOptionLabelLocaleID(property), out string text);
                labels[property] = found ? new JValue(text) : JValue.CreateNull();
            }
            var source = connection;
            var observed = source?.Read();
            var context = source?.Context.Read();
            var world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
            var options = world == null || !world.IsCreated ? null : world.GetExistingSystemManaged<Game.UI.Menu.OptionsUISystem>();
            bool registered = false;
            Game.UI.Menu.OptionsUISystem.Page? ownPage = null;
            if (options != null)
            {
                // Read the qualified SDK registry only in this development probe.
                // The public void API does not report whether a page was added.
                var registry = typeof(Game.UI.Menu.OptionsUISystem).GetProperty("pages",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(options)
                    as System.Collections.IDictionary;
                if (registry == null) throw new InvalidOperationException("The settings registry shape is unsupported.");
                registered = registry.Contains(page.id);
                if (registered) ownPage = registry[page.id] as Game.UI.Menu.OptionsUISystem.Page
                    ?? throw new InvalidOperationException("The own settings page shape is unsupported.");
            }
            return new JObject {
                ["environmentId"] = page.Environment, ["localeId"] = ReadLocale(), ["loadedVersion"] = page.LoadedVersion,
                ["profileDraft"] = page.Profile, ["cityChangesDraft"] = page.AllowCityChanges,
                ["status"] = page.Status, ["clientFields"] = page.AIClientConnection,
                ["labels"] = labels, ["preferencesRead"] = readPreferences,
                ["settingsRegistrationMayBeHeld"] = settingsMayBeHeld,
                ["settingsRegistryObserved"] = options != null,
                ["settingsRegistered"] = registered,
                ["multilineWidgets"] = ReadOwnMultilineWidgets(ownPage, page),
                ["acceptedConfigurationRequests"] = acceptedConfigurationRequests,
                ["connectionPhase"] = observed == null ? JValue.CreateNull() : new JValue(observed.Phase.ToString()),
                ["connectionFailure"] = observed?.FailureCode == null ? JValue.CreateNull() : new JValue(observed.FailureCode),
                ["connectionFullyReleased"] = observed?.FullyReleased ?? false,
                ["nativeLeaseRetained"] = observed?.NativeLeaseRetained ?? false,
                ["contextInstanceId"] = context == null ? JValue.CreateNull() : new JValue(context.InstanceId),
                ["contextGeneration"] = context == null ? JValue.CreateNull() : new JValue(context.Generation),
                ["contextState"] = context == null ? JValue.CreateNull() : new JValue(context.State),
                ["contextStopped"] = source?.Context.IsStopped ?? false,
                ["boundProductVersion"] = source?.ProductVersion == null ? JValue.CreateNull() : new JValue(source.ProductVersion),
                ["ownAssetCaptured"] = bundledOrigin != null,
                ["ownAssetPath"] = bundledOrigin == null ? JValue.CreateNull() : new JValue(bundledOrigin.AssetPath),
                ["ownAssetPackaged"] = bundledOrigin?.Packaged ?? false,
                ["ownAssetModuleId"] = bundledOrigin == null ? JValue.CreateNull() : new JValue(bundledOrigin.ModuleId.ToString("N"))
            };
        }

        private static JObject ReadOwnMultilineWidgets(Game.UI.Menu.OptionsUISystem.Page? ownPage, ConnectionSettings page)
        {
            var result = new JObject();
            if (ownPage == null) return result;
            if (ownPage.sections.Count > 8) throw new InvalidOperationException("The own settings page exceeds its probe budget.");
            int examined = 0;
            foreach (var section in ownPage.sections)
            foreach (var option in section.options)
            {
                if (++examined > 64) throw new InvalidOperationException("The own settings options exceed their probe budget.");
                string? property = option.widget.path == page.id + ".ConnectionSettings.Status" ? nameof(ConnectionSettings.Status) :
                    option.widget.path == page.id + ".ConnectionSettings.AIClientConnection" ? nameof(ConnectionSettings.AIClientConnection) : null;
                if (property == null) continue;
                if (result[property] != null || !(option.widget is Game.UI.Widgets.MultilineText multiline))
                    throw new InvalidOperationException("The own multiline widget shape is unsupported.");
                var action = multiline.displayNameAction;
                var cached = multiline.displayName;
                string method = property == nameof(ConnectionSettings.Status) ? "GetStatusDisplayName" : "GetClientConnectionDisplayName";
                bool bound = action != null && ReferenceEquals(action.Target, page) &&
                    action.Method.DeclaringType == typeof(ConnectionSettings) && action.Method.Name == method;
                if (!bound) throw new InvalidOperationException("The own multiline display binding is unsupported.");
                var fresh = action!();
                result[property] = new JObject {
                    ["dynamicDisplayBound"] = bound,
                    ["cachedLiteral"] = cached.id == null, ["cachedText"] = cached.value == null ? JValue.CreateNull() : new JValue(cached.value),
                    ["freshLiteral"] = fresh.id == null, ["freshText"] = fresh.value == null ? JValue.CreateNull() : new JValue(fresh.value),
                    ["matchesClientFields"] = property != nameof(ConnectionSettings.AIClientConnection) || fresh.value == page.AIClientConnection
                };
            }
            return result;
        }
    }
}
#endif
