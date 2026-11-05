#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Globalization;
using Colossal.Core;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Newtonsoft.Json.Linq;

namespace SkylinesLink.Mod.Native
{
    // Qualifies the actual ordinary root against a prepared private developer
    // environment. It neither applies a profile nor verifies rendered UI layout.
    internal sealed class NormalSettingsProbe
    {
        private readonly object sync = new object();
        private readonly IMod mod;
        private readonly UpdateSystem updateSystem;
        private readonly string environment, expectedVersion, expectedModuleId;
        private readonly Action<string>? report;
        private readonly JArray samples = new JArray();
        private NormalModSession? root;
        private JObject? afterStop;
        private bool startAttempted, stopping, resourcesReleased;
        private int lastSampleFrame = -1;
        private string? failureCode, exceptionType;

        internal NormalSettingsProbe(IMod mod, UpdateSystem updateSystem, string environment,
            string expectedVersion, Action<string>? report)
        {
            this.mod = mod ?? throw new ArgumentNullException(nameof(mod));
            this.updateSystem = updateSystem ?? throw new ArgumentNullException(nameof(updateSystem));
            if (!environment.StartsWith("probe-", StringComparison.Ordinal)) throw new ArgumentException("A probe environment is required.");
            this.environment = environment;
            this.expectedVersion = expectedVersion ?? throw new ArgumentNullException(nameof(expectedVersion));
            expectedModuleId = mod.GetType().Assembly.ManifestModule.ModuleVersionId.ToString("N");
            this.report = report;
        }

        internal bool ResourcesReleased { get { lock (sync) return resourcesReleased; } }
        internal bool ChecksPassed { get { lock (sync) return Passed; } }
        internal string? FailureCode { get { lock (sync) return failureCode; } }
        internal string? ExceptionType { get { lock (sync) return exceptionType; } }
        private bool Passed => resourcesReleased && samples.Count == 2 && afterStop != null && failureCode == null;

        // Host failures may arrive from its evidence worker. SDK cleanup is
        // deliberately deferred to Step on the main thread.
        internal void RequestStop() { lock (sync) stopping = true; }

        internal void Step(bool hostStopping)
        {
            RequireMainThread();
            lock (sync)
            {
                if (resourcesReleased) return;
                if (hostStopping)
                {
                    stopping = true;
                    if (samples.Count != 2) RecordFailure("NORMAL_SETTINGS_SAMPLES_INCOMPLETE", null);
                }
                try
                {
                    if (!stopping)
                    {
                        var manager = GameManager.instance;
                        Check(manager != null && manager.state == GameManager.State.WorldReady
                            && manager.gameMode == GameMode.MainMenu && !manager.isGameLoading, "NORMAL_SETTINGS_MENU_REQUIRED");
                        if (!startAttempted)
                        {
                            startAttempted = true;
                            // Retain partial startup before calling any SDK registration.
                            root = NormalModSession.ForSettingsProbe(mod, updateSystem, environment, report);
                            root.Start();
                        }
                        int frame = UnityEngine.Time.frameCount;
                        if (root != null && !root.IsBusy() && frame != lastSampleFrame && samples.Count < 2)
                        {
                            var sample = Capture();
                            // Preserve a failing native observation as well as a
                            // passing one; the fixed two-slot budget is unchanged.
                            samples.Add(sample);
                            lastSampleFrame = frame;
                            CheckCommon(sample);
                            Check(Bool(sample, "settingsRegistryObserved", true) && Bool(sample, "settingsRegistered", true),
                                "NORMAL_SETTINGS_REGISTRATION_NOT_OBSERVED");
                            Check(Text(sample, "loadedVersion") == expectedVersion && Text(sample, "boundProductVersion") == expectedVersion,
                                "NORMAL_SETTINGS_BOUND_VERSION_MISMATCH");
                            Check(Bool(sample, "ownAssetCaptured", true) && Text(sample, "ownAssetModuleId") == expectedModuleId
                                && !string.IsNullOrWhiteSpace(Text(sample, "ownAssetPath")), "NORMAL_SETTINGS_OWN_ASSET_UNAVAILABLE");
                            Check(!string.IsNullOrWhiteSpace(Text(sample, "status")) && !string.IsNullOrWhiteSpace(Text(sample, "clientFields")),
                                "NORMAL_SETTINGS_TEXT_UNAVAILABLE");
                            var labels = sample["labels"] as JObject;
                            foreach (string label in new[] { "Profile", "Status", "LoadedVersion", "AIClientConnection" })
                                Check(labels != null && !string.IsNullOrWhiteSpace(Text(labels, label)), "NORMAL_SETTINGS_LABEL_UNAVAILABLE");
                            CheckMultilineWidgets(sample);
                            if (samples.Count == 2) stopping = true;
                        }
                    }
                }
                catch (CheckFailure error) { RecordFailure(error.Code, error.GetType().Name); stopping = true; }
                catch (Exception error) { RecordFailure("NORMAL_SETTINGS_OBSERVATION_FAILED", error.GetType().Name); stopping = true; }
                if (stopping) AdvanceStop();
            }
        }

        private void AdvanceStop()
        {
            if (root == null) { resourcesReleased = true; return; }
            try
            {
                root.RequestStop();
                if (!root.IsFullyReleased) return;
                var observation = Capture();
                afterStop = observation;
                // A held flag or a missing OptionsUISystem cannot prove removal.
                Check(Bool(observation, "settingsRegistryObserved", true) && Bool(observation, "settingsRegistered", false),
                    "NORMAL_SETTINGS_REMOVAL_NOT_OBSERVED");
                Check(observation["multilineWidgets"] is JObject widgets && widgets.Count == 0,
                    "NORMAL_SETTINGS_WIDGET_REMOVAL_NOT_OBSERVED");
                resourcesReleased = true;
                CheckCommon(observation);
            }
            catch (CheckFailure error) { RecordFailure(error.Code, error.GetType().Name); }
            catch (Exception error) { RecordFailure("NORMAL_SETTINGS_CLOSE_OBSERVATION_FAILED", error.GetType().Name); }
        }

        private JObject Capture()
        {
            var sample = root!.ReadSettingsProbe();
            sample["sampleFrame"] = UnityEngine.Time.frameCount;
            sample["sampledAtUtc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
            return sample;
        }

        private void CheckCommon(JObject sample)
        {
            Check(Text(sample, "environmentId") == environment, "NORMAL_SETTINGS_ENVIRONMENT_MISMATCH");
            Check(Bool(sample, "preferencesRead", false), "NORMAL_SETTINGS_PREFERENCES_NOT_ISOLATED");
            Check(sample["acceptedConfigurationRequests"]?.Type == JTokenType.Integer
                && (long)sample["acceptedConfigurationRequests"]! == 0, "NORMAL_SETTINGS_CONFIGURATION_REQUESTED");
        }

        private static void CheckMultilineWidgets(JObject sample)
        {
            var widgets = sample["multilineWidgets"] as JObject;
            Check(widgets != null && widgets.Count == 2, "NORMAL_SETTINGS_MULTILINE_UNAVAILABLE");
            foreach (string property in new[] { "Status", "AIClientConnection" })
            {
                var widget = widgets![property] as JObject;
                Check(widget != null && Bool(widget, "dynamicDisplayBound", true)
                    && Bool(widget, "cachedLiteral", true) && Bool(widget, "freshLiteral", true)
                    && !string.IsNullOrWhiteSpace(Text(widget, "cachedText"))
                    && !string.IsNullOrWhiteSpace(Text(widget, "freshText")), "NORMAL_SETTINGS_MULTILINE_NOT_BOUND");
                // Status may legitimately have an older SDK-cached value. Only
                // the stable client fields are compared with the current getter.
                if (property == "AIClientConnection")
                    Check(Bool(widget!, "matchesClientFields", true), "NORMAL_SETTINGS_CLIENT_WIDGET_MISMATCH");
            }
        }

        // Only ordinary copied values leave the main-thread sample. No SDK
        // registry, IMod, asset or setting object is handed to the evidence worker.
        internal JObject Read()
        {
            lock (sync)
                return new JObject {
                    ["phase"] = resourcesReleased ? "Stopped" : stopping ? "Stopping" : startAttempted ? "Observing" : "NotStarted",
                    ["checksPassed"] = Passed, ["resourcesReleased"] = resourcesReleased,
                    ["sampleCount"] = samples.Count, ["samples"] = samples.DeepClone(),
                    ["afterStop"] = afterStop == null ? JValue.CreateNull() : afterStop.DeepClone(),
                    ["failureCode"] = failureCode == null ? JValue.CreateNull() : new JValue(failureCode),
                    ["exceptionType"] = exceptionType == null ? JValue.CreateNull() : new JValue(exceptionType)
                };
        }

        private void RecordFailure(string code, string? type)
        {
            if (failureCode != null) return;
            failureCode = code;
            exceptionType = type == null || type.Length <= 160 ? type : type.Substring(0, 160);
        }

        private static bool Bool(JObject sample, string name, bool value)
            => sample[name]?.Type == JTokenType.Boolean && (bool)sample[name]! == value;
        private static string? Text(JObject sample, string name)
            => sample[name]?.Type == JTokenType.String ? (string?)sample[name] : null;
        private static void Check(bool condition, string code) { if (!condition) throw new CheckFailure(code); }
        private static void RequireMainThread()
        { if (!MainThreadDispatcher.isMainThread) throw new InvalidOperationException("The settings probe requires the main thread."); }
        private sealed class CheckFailure : Exception
        { internal CheckFailure(string code) { Code = code; } internal string Code { get; } }
    }
}
#endif
