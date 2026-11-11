#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Colossal.Core;
using Colossal.PSI.Environment;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Operations;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Modules;
using SkylinesLink.Contracts;
using SkylinesLink.Mod.Connection;
using SkylinesLink.Mod.Runtime;
using UnityApplication = UnityEngine.Application;

namespace SkylinesLink.Mod.Native
{
    /// <summary>仅专用构建和单次请求启用；testCity及其显式组合测试新城，其余实验限菜单，不赋予Control或修改玩家持久设置。</summary>
    public sealed class MenuProbeSession
    {
        private readonly object sync = new object();
        private readonly string userProfile, actualUserData, launchRunId;
        private readonly string productVersion;
        private readonly int processId;
        private readonly string processStart;
        private readonly Unity.Entities.World? sourceWorld;
        private readonly UpdateSystem sourceUpdate;
        private readonly IMod sourceMod;
        private readonly Action<string>? report;
        private readonly Task initialization;
        private Guid updater;
        private Configuration? configuration;
        private PrivateConnectionStorage? evidence;
        private GameConnectionLifecycle? lifecycle;
        private ModuleRuntimeObservationBroker? modules;
        private readonly NativeModuleRuntimeCollector moduleCollector = new NativeModuleRuntimeCollector();
        private DefinitionContainerProbe? definitionContainer;
        private StraightDefinitionProbe? straightDefinition;
        private ScopedClearProbe? scopedClear;
        private MapIdentityProbe? mapIdentity;
        private TestCityLifecycleProbe? testCity;
        private NormalSettingsProbe? normalSettings;
        private NormalConnectionProbe? normalConnection;
        private HostPhase phase = HostPhase.Initializing;
        private StopIntent stopIntent;
        private Task<bool>? closing;
        private Task statusWork = Task.CompletedTask;
        private int? statusIoHResult;
        private bool writerActive, seenMenu, connectionReleased, reportedFailure;
        private string? failureCode, exceptionType;
        private string initializationStage = "starting";
        private string? initializationFailureSite;
        private NativeSnapshot? pendingSnapshot;
        private WindowHandle[] windows = Array.Empty<WindowHandle>();
        private bool ownsRendering;
        private int previousFrameRate, previousVSync;
        private bool previousBackground;
        private long nextSample, startedAt;

        private MenuProbeSession(string launchRunId, IMod mod, UpdateSystem updateSystem, Action<string>? report)
        {
            sourceMod = mod ?? throw new ArgumentNullException(nameof(mod));
            this.report = report;
            this.launchRunId = launchRunId;
            string information = typeof(global::SkylinesLink.Mod.Mod).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? throw new InvalidOperationException("The probe build identity is unavailable.");
            int separator = information.IndexOf('+');
            productVersion = separator < 0 ? information : information.Substring(0, separator);
            AuthorizationState.ValidateIdentifier(productVersion);
            sourceWorld = updateSystem.World;
            sourceUpdate = updateSystem;
            userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            actualUserData = EnvPath.kUserDataPath;
            using (var process = Process.GetCurrentProcess())
            { processId = process.Id; processStart = Utc(process.StartTime.ToUniversalTime()); }
            // 构造器只由 OnLoad 显式 new 的 Start 调用；不依赖 IMod 构造器运行。
            updater = MainThreadDispatcher.RegisterUpdater(Tick);
            initialization = Task.Run(InitializeAsync);
        }
        public static MenuProbeSession? TryStart(IMod mod, UpdateSystem updateSystem, Action<string>? report = null)
        {
            // Normal launches do not read stale probe files, register updaters,
            // change rendering, hide windows or schedule a game quit.
            if (!IntegrationProbeLaunch.TryReadRunId(Environment.GetCommandLineArgs(), out string runId)) return null;
            RequireMainThread(); return new MenuProbeSession(runId, mod, updateSystem, report);
        }

        public void RequestStop()
        {
            RequireMainThread();
            lock (sync) RequestHostStop(StopReason.ExternalRequest, denyQuit: true);
        }
        private async Task InitializeAsync()
        {
            try
            {
                string directory = Path.Combine(userProfile, ".skylineslink", "integration");
                if (!Directory.Exists(directory)) throw new ProbeFailure("PROBE_CONFIGURATION_MISSING");
                initializationStage = "configuration-storage";
                var storage = new PrivateConnectionStorage(directory);
                initializationStage = "configuration-read";
                Configuration config = Configuration.Read(storage, actualUserData, launchRunId);
                string connection = Path.Combine(userProfile, ".skylineslink", "environments", config.EnvironmentId, "connection");
                if (!Directory.Exists(Path.GetDirectoryName(connection))) throw new ProbeFailure("PROBE_ENVIRONMENT_MISSING");
                if (Directory.Exists(connection) || File.Exists(connection)) throw new ProbeFailure("PROBE_ENVIRONMENT_NOT_FRESH");
                string status = Path.Combine(directory, "menu-probe-" + config.RunId + ".status.json");
                storage.ValidateFile(status, true);
                if (File.Exists(status)) throw new ProbeFailure("PROBE_RUN_ALREADY_RECORDED");
                GameConnectionLifecycle created;
                lock (sync)
                {
                    configuration = config; evidence = storage; startedAt = Stopwatch.GetTimestamp();
                    if (stopIntent.Requested) return;
                    // Development-only baseline: retain the same menu/window/
                    // quit coordinator without ever creating connection owners.
                    if (config.ConnectionMode == "noOwner") return;
                    var connectionContext = new WorldContextTracker(Guid.NewGuid().ToString("N"));
                    connectionContext.MarkUnavailable();
                    created = new GameConnectionLifecycle(async cancellation =>
                    {
                        initializationStage = "connection-owner";
                        var owner = await ConnectionAuthorityOwner.CreateAsync(connection, config.EnvironmentId, config.CoreHash, productVersion,
                            new PipeLimits(4, 65536, 32, 8192, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)),
                            connectionContext, (operations, context, version) =>
                            {
                                var source = new ModuleRuntimeObservationBroker(context.Read().InstanceId);
                                lock (sync)
                                {
                                    if (stopIntent.Requested) { source.Stop(); throw new OperationCanceledException(); }
                                    modules = source;
                                }
                                return new SnapshotCapabilityDispatcher(context, version, operations, source);
                            }, true,
                            cancellationToken: cancellation).ConfigureAwait(false);
                        if (owner == null) return null;
                        try
                        {
                            cancellation.ThrowIfCancellationRequested();
                            // This launch request explicitly creates its isolated
                            // probe connection. Ordinary Mod startup never configures a profile.
                            await owner.Authority.EnsureProfileAsync(config.ProfileId, GrantScope.Read, false, HandshakeProof.RandomHex).ConfigureAwait(false);
                            return owner;
                        }
                        catch (Exception failure)
                        {
                            try { await owner.CloseAsync().ConfigureAwait(false); }
                            catch { throw new ConnectionInitializationException(failure, false, owner.NativeLeaseRetained); }
                            if (!owner.FullyReleased)
                                throw new ConnectionInitializationException(failure, false, owner.NativeLeaseRetained);
                            throw;
                        }
                    });
                    lifecycle = created;
                }
                if (!await created.StartAsync().ConfigureAwait(false))
                {
                    var state = created.Read();
                    lock (sync) if (!stopIntent.Requested) SetFailure(state.FailureCode ?? "PROBE_ACTIVATION_FAILED", state.ExceptionType);
                }
            }
            catch (Exception error)
            {
                lock (sync)
                {
                    // Only fixed stage and bounded method metadata; no exception message,
                    // arguments, source filenames, paths, credentials or arbitrary stack text.
                    var trace = new StackTrace(error, false);
                    var sites = new List<string>(4);
                    for (int index = 0; index < Math.Min(4, trace.FrameCount); index++)
                    {
                        var method = trace.GetFrame(index)?.GetMethod();
                        string site = (method?.DeclaringType?.FullName ?? "unknown") + "." + (method?.Name ?? "unknown");
                        sites.Add(site.Length <= 160 ? site : site.Substring(0, 160));
                    }
                    initializationFailureSite = initializationStage + ": " + string.Join(" -> ", sites);
                    SetFailure(error is ProbeFailure failure ? failure.Code : "PROBE_INITIALIZATION_FAILED", error.GetType().Name);
                }
            }
        }

        // One main-thread pump owns native progress and all published observations.
        // A stop is a request to drain; it is never cancellation of a native Task.
        private bool Tick()
        {
            RequireMainThread();
            lock (sync)
            {
                if (PumpFinished) return true;
                if (phase == HostPhase.WritingFinalStatus) return FinishFinalWrite();

                long now = Stopwatch.GetTimestamp();
                IntakeDeadline(now);
                // This remains first even after an auxiliary failure. The child
                // alone decides whether its native responsibility is released.
                AdvanceCity();
                AdvanceNormalSettings();
                AdvanceNormalConnection();
                if (phase == HostPhase.Initializing && configuration != null && !stopIntent.Requested)
                    phase = HostPhase.Running;

                if (phase == HostPhase.Running || phase == HostPhase.Draining)
                    ObserveHost();
                if (phase == HostPhase.Running) AdvanceRunningSession();
                if (stopIntent.Requested && (phase == HostPhase.Initializing || phase == HostPhase.Running))
                    phase = HostPhase.Draining;

                if (phase == HostPhase.Draining && CityFinished && NormalSettingsReleased && NormalConnectionFinished) StartClosing();
                if (phase == HostPhase.ClosingIo) ObserveClosing();

                if (phase != HostPhase.WritingFinalStatus && !PumpFinished) SampleWhenDue(now);
                ReportFailure();
                return PumpFinished;
            }
        }

        private void AdvanceCity()
        {
            if (testCity == null) return;
            if (normalConnection?.SuppressAutomaticReturn == true)
            {
                testCity.SuspendAutomaticReturnForNativeUnknown();
                return;
            }
            try { testCity.Step(stopIntent.Requested); }
            catch (Exception error) { SetFailure("PROBE_CITY_STEP_FAILED", error.GetType().Name); }
        }

        private void AdvanceNormalSettings()
        {
            if (normalSettings == null) return;
            try
            {
                normalSettings.Step(stopIntent.Requested);
                if (normalSettings.FailureCode is string code) SetFailure(code, normalSettings.ExceptionType);
            }
            catch (Exception error) { SetFailure("PROBE_NORMAL_SETTINGS_STEP_FAILED", error.GetType().Name); }
        }

        private void AdvanceNormalConnection()
        {
            if (normalConnection == null) return;
            try
            {
                normalConnection.Step(stopIntent.Requested);
                if (normalConnection.FailureCode is string code) SetFailure(code, normalConnection.ExceptionType);
            }
            catch (Exception error) { SetFailure("PROBE_NORMAL_CONNECTION_STEP_FAILED", error.GetType().Name); }
        }

        private void IntakeDeadline(long now)
        {
            var config = configuration;
            if (config == null || (phase != HostPhase.Initializing && phase != HostPhase.Running)) return;
            if (DateTime.UtcNow >= config.ExpiresAtUtc || (now - startedAt) / (double)Stopwatch.Frequency >= config.SessionSeconds)
                RequestHostStop(StopReason.Deadline, denyQuit: false);
        }

        private void AdvanceRunningSession()
        {
            var config = configuration!;
            try
            {
                var manager = GameManager.instance;
                bool menu = IsReadyMenu(manager);
                bool loading = manager != null && manager.isGameLoading;
                // Menu-only experiments stop on an external transition. A city
                // experiment owns its own source/context checks, including loss.
                if (seenMenu && !menu && testCity == null)
                    RequestHostStop(StopReason.LeftMenu, denyQuit: true);
                if (menu) seenMenu = true;
                UpdateConnectionContext(menu, loading);

                if (!stopIntent.Requested)
                {
                    if (menu && ReadOwnedWindows(out _)) RunSelectedExperiment(config);
                    CaptureRequestedModules(menu);
                }
                if (testCity != null && testCity.ResourcesReleased && config.Experiment != "normalCityOverview")
                    RequestHostStop(StopReason.ExperimentFinished, denyQuit: false);
                if (normalSettings != null && normalSettings.ChecksPassed && normalSettings.ResourcesReleased)
                    RequestHostStop(StopReason.ExperimentFinished, denyQuit: false);
                if (normalConnection != null && normalConnection.ChecksPassed && normalConnection.ResourcesReleased)
                    RequestHostStop(StopReason.ExperimentFinished, denyQuit: false);
            }
            catch (Exception error) { SetFailure("PROBE_NATIVE_OBSERVATION_FAILED", error.GetType().Name); }
        }

        private void RunSelectedExperiment(Configuration config)
        {
            switch (config.Experiment)
            {
                case "normalCityOverview":
                    if (normalConnection == null)
                    {
                        normalConnection = new NormalConnectionProbe(sourceMod, sourceUpdate, config.EnvironmentId,
                            config.ProfileId, productVersion, report, cityOverview: true);
                        AdvanceNormalConnection();
                    }
                    if (testCity == null && normalConnection.ReadyToLoadCity)
                    {
                        var city = new TestCityLifecycleProbe(sourceUpdate, config.TestMapMetadataGuid,
                            config.TestMapDataGuid, holdAfterQualification: true);
                        normalConnection.AttachCity(city);
                        testCity = city;
                    }
                    break;
                case "normalConnection":
                    if (normalConnection == null)
                    {
                        normalConnection = new NormalConnectionProbe(sourceMod, sourceUpdate, config.EnvironmentId, config.ProfileId, productVersion, report);
                        AdvanceNormalConnection();
                    }
                    break;
                case "normalSettings":
                    if (normalSettings == null)
                    {
                        normalSettings = new NormalSettingsProbe(sourceMod, sourceUpdate, config.EnvironmentId, productVersion, report);
                        AdvanceNormalSettings();
                    }
                    break;
                case "testCity":
                    if (testCity == null)
                    {
                        testCity = new TestCityLifecycleProbe(sourceUpdate, config.TestMapMetadataGuid, config.TestMapDataGuid);
                        AdvanceCity();
                    }
                    break;
                case "definitionContainer":
                    if (definitionContainer == null) { definitionContainer = new DefinitionContainerProbe(); definitionContainer.Run(); }
                    break;
                case "straightDefinition":
                    if (straightDefinition == null) { straightDefinition = new StraightDefinitionProbe(sourceWorld); straightDefinition.Run(); }
                    break;
                case "scopedClear":
                    if (scopedClear == null) { scopedClear = new ScopedClearProbe(sourceWorld); scopedClear.Run(); }
                    break;
                case "mapIdentity":
                    if (mapIdentity == null) { mapIdentity = new MapIdentityProbe(sourceWorld); mapIdentity.Run(); }
                    break;
            }
        }

        private void UpdateConnectionContext(bool menu, bool loading)
        {
            var owner = lifecycle?.Owner;
            if (owner == null || lifecycle!.StopRequested || owner.StopRequested) return;
            string state = menu ? "menu" : loading ? "loading" : "unavailable";
            if (owner.Context.Read().State == state) return;
            if (menu) owner.Context.EnterMenu();
            else if (loading) owner.Context.EnterLoading();
            else owner.Context.MarkUnavailable();
        }

        private void CaptureRequestedModules(bool menu)
        {
            var source = modules;
            if (!menu || source == null || !source.TryClaim(out var capture)) return;
            try { source.Complete(capture!, moduleCollector.Capture()); }
            catch (ModuleRegistryReadFailure error) { source.Fail(capture!, error.Code); }
        }

        private void ObserveHost()
        {
            if (configuration == null) return;
            try { HoldRendering(); HideOwnedWindows(); }
            catch (Exception error) { SetFailure("PROBE_NATIVE_OBSERVATION_FAILED", error.GetType().Name); }
        }

        private bool CityReleased => testCity == null || testCity.ResourcesReleased;
        private bool CityFinished => testCity == null || testCity.Finished;
        private bool NormalSettingsReleased => normalSettings == null || normalSettings.ResourcesReleased;
        private bool NormalConnectionReleased => normalConnection == null || normalConnection.ResourcesReleased;
        private bool NormalConnectionFinished => normalConnection == null || normalConnection.Finished;
        private bool PumpFinished => phase == HostPhase.Stopped || phase == HostPhase.QuitScheduled || phase == HostPhase.QuitRequested;
        private static bool IsReadyMenu(GameManager? manager) => manager != null && manager.state == GameManager.State.WorldReady
            && manager.gameMode == GameMode.MainMenu && !manager.isGameLoading;

        private void RequestHostStop(StopReason reason, bool denyQuit)
        {
            stopIntent = stopIntent.Merge(reason, denyQuit);
            lifecycle?.RequestStop();
            normalSettings?.RequestStop();
            normalConnection?.RequestStop();
        }
        private void HoldRendering()
        {
            if (!ownsRendering)
            {
                previousFrameRate = UnityApplication.targetFrameRate; previousVSync = UnityEngine.QualitySettings.vSyncCount;
                previousBackground = UnityApplication.runInBackground; ownsRendering = true;
            }
            else
            {
                // 游戏进入菜单会把帧率改为 120；保留最新外部值作为恢复目标，不能恢复成启动阶段旧值。
                if (UnityApplication.targetFrameRate != 30) previousFrameRate = UnityApplication.targetFrameRate;
                if (UnityEngine.QualitySettings.vSyncCount != 0) previousVSync = UnityEngine.QualitySettings.vSyncCount;
                if (!UnityApplication.runInBackground) previousBackground = false;
            }
            UnityApplication.runInBackground = true;
            UnityEngine.QualitySettings.vSyncCount = 0;
            UnityApplication.targetFrameRate = 30;
        }
        private void RestoreRendering()
        {
            if (!ownsRendering) return;
            // 只恢复仍等于本探针持有值的标量；不回写 settings，不覆盖已被外部改走的值。
            if (UnityApplication.targetFrameRate == 30) UnityApplication.targetFrameRate = previousFrameRate;
            if (UnityEngine.QualitySettings.vSyncCount == 0) UnityEngine.QualitySettings.vSyncCount = previousVSync;
            if (UnityApplication.runInBackground) UnityApplication.runInBackground = previousBackground;
            ownsRendering = false;
        }
        private void HideOwnedWindows()
        {
            foreach (var window in windows)
                if (OwnWindow(window.Handle) && IsWindowVisible(window.Handle)) ShowWindow(window.Handle, 0);
        }
        private bool ReadOwnedWindows(out int count)
        {
            count = 0; bool hidden = true;
            foreach (var window in windows)
            { if (!OwnWindow(window.Handle)) continue; count++; if (IsWindowVisible(window.Handle)) hidden = false; }
            return count > 0 && hidden;
        }

        // Every native/experiment value in this object is captured on this main
        // thread step. The background writer never refreshes or combines it.
        private NativeSnapshot CaptureSnapshot(bool discoverWindows)
        {
            RequireMainThread();
            var config = configuration ?? throw new ProbeFailure("PROBE_STATUS_NOT_READY");
            var manager = GameManager.instance;
            bool loading = manager != null && manager.isGameLoading;
            string state = IsReadyMenu(manager) ? "menu" : loading ? "loading"
                : manager != null && manager.state == GameManager.State.WorldReady && manager.gameMode == GameMode.Game ? "game" : "unavailable";
            bool hidden = ReadOwnedWindows(out int windowCount);
            int frameRate = UnityApplication.targetFrameRate, vSync = UnityEngine.QualitySettings.vSyncCount;
            bool background = UnityApplication.runInBackground;
            string sampledAt = Utc(DateTime.UtcNow);
            var city = testCity?.CaptureSnapshot();
            var life = lifecycle?.Read(); var owner = lifecycle?.Owner; var usage = owner?.Server.OwnedPayloadUsage;
            var ingress = owner?.Server.IngressPayloadUsage; var egress = owner?.Server.EgressPayloadUsage;
            var replies = owner?.Server.ReplyUsage; var moduleUsage = modules?.ReadUsage();
            var status = new JObject
            {
                ["schemaVersion"] = 1, ["productId"] = "skylineslink", ["runId"] = config.RunId,
                ["processId"] = processId, ["processStartTimeUtc"] = processStart, ["sampledAtUtc"] = sampledAt,
                ["connectionMode"] = config.ConnectionMode, ["ownerPresent"] = owner != null,
                ["experiment"] = config.Experiment,
                ["nativeGameState"] = state, ["userDataPathVerified"] = true,
                ["gameState"] = owner?.Context.Read().State ?? "unavailable", ["world"] = JValue.CreateNull(),
                ["targetFrameRate"] = frameRate, ["vSyncCount"] = vSync, ["runInBackground"] = background,
                ["windowHiddenVerified"] = hidden, ["windowCount"] = windowCount,
                ["contextInstanceId"] = owner == null ? JValue.CreateNull() : new JValue(owner.Context.Read().InstanceId),
                ["lifecyclePhase"] = life == null ? (CloseStarted ? "stopped" : "starting") : LowerFirst(life.Phase.ToString()),
                ["connectedCount"] = owner?.Server.ConnectedCount ?? 0, ["authenticatedCount"] = owner?.Server.AuthenticatedCount ?? 0,
                ["ownedPayloadCount"] = usage?.Count ?? 0, ["ownedPayloadCapacity"] = usage?.Capacity ?? 0,
                ["payloadUsageSnapshotKind"] = "directionalSamples",
                ["maximumOwnedPayloadCount"] = usage?.MaximumCount ?? 0, ["maximumOwnedPayloadCapacity"] = usage?.MaximumCapacity ?? 0,
                ["ingressPayloadCount"] = ingress?.Count ?? 0, ["ingressPayloadCapacity"] = ingress?.Capacity ?? 0,
                ["egressPayloadCount"] = egress?.Count ?? 0, ["egressPayloadCapacity"] = egress?.Capacity ?? 0,
                ["ordinaryReplyCount"] = replies?.OrdinaryCount ?? 0, ["controlReplyCount"] = replies?.ControlCount ?? 0,
                ["ownedReplyTextCharacters"] = replies?.OwnedTextCharacters ?? 0,
                ["fullyReleased"] = ResourcesReleased,
                ["definitionContainer"] = definitionContainer == null ? JValue.CreateNull() : definitionContainer.Read(),
                ["straightDefinition"] = straightDefinition == null ? JValue.CreateNull() : straightDefinition.Read(),
                ["scopedClear"] = scopedClear == null ? JValue.CreateNull() : scopedClear.Read(),
                ["mapIdentity"] = mapIdentity == null ? JValue.CreateNull() : mapIdentity.Read(),
                ["testCity"] = (JToken?)city ?? JValue.CreateNull(), ["nativeLeaseRetained"] = life?.NativeLeaseRetained ?? false,
                ["moduleSnapshotSlots"] = moduleUsage?.PublishedSnapshotSlots ?? 0,
                ["moduleCaptureCount"] = moduleUsage?.ActiveCaptureCount ?? 0,
                ["moduleConsumerSlots"] = moduleUsage?.ConsumerSlots ?? 0,
                // This is the requested quit policy, not proof of a call or exit.
                ["quitScheduled"] = QuitAllowedAfterClose,
                ["failureCode"] = failureCode == null ? JValue.CreateNull() : new JValue(failureCode),
                ["exceptionType"] = exceptionType == null ? JValue.CreateNull() : new JValue(exceptionType)
            };
            if (config.Experiment == "normalSettings" || config.Experiment == "normalConnection" || config.Experiment == "normalCityOverview")
            {
                if (config.Experiment == "normalSettings")
                    status["normalSettings"] = normalSettings == null ? JValue.CreateNull() : normalSettings.Read();
                else status["normalConnection"] = normalConnection == null ? JValue.CreateNull() : normalConnection.Read();
                // Existing zero counters describe only the legacy probe Owner.
                status["connectionMetricsScope"] = "legacyProbeOwner";
            }
            return new NativeSnapshot(config.RunId, status, discoverWindows);
        }

        private bool CloseStarted => phase == HostPhase.ClosingIo || phase == HostPhase.WritingFinalStatus || PumpFinished;
        private bool ResourcesReleased => connectionReleased && !ownsRendering && (modules?.WhenIdle.IsCompleted ?? true)
            && (definitionContainer?.ResourcesReleased ?? true) && (straightDefinition?.ResourcesReleased ?? true)
            && (scopedClear?.ResourcesReleased ?? true) && (mapIdentity?.ResourcesReleased ?? true) && CityReleased
            && NormalSettingsReleased && NormalConnectionReleased;
        private bool QuitAllowedAfterClose => stopIntent.Requested && !stopIntent.QuitDenied
            && configuration?.QuitAtEnd == true && failureCode == null && ResourcesReleased;

        private void StartClosing()
        {
            phase = HostPhase.ClosingIo;
            TryClosingAction(() => modules?.Stop());
            TryClosingAction(() => lifecycle?.RequestStop());
            TryClosingAction(() => normalSettings?.RequestStop());
            TryClosingAction(() => normalConnection?.RequestStop());
            if (definitionContainer != null) TryClosingAction(definitionContainer.Release);
            if (straightDefinition != null) TryClosingAction(straightDefinition.Release);
            if (scopedClear != null) TryClosingAction(scopedClear.Release);
            if (mapIdentity != null) TryClosingAction(mapIdentity.Release);
            TryClosingAction(RestoreRendering);
            closing = Task.Run(CloseCoreAsync);
        }

        private void TryClosingAction(Action action)
        {
            try { action(); }
            catch (Exception error) { SetFailure("PROBE_RESOURCE_CLOSE_FAILED", error.GetType().Name); }
        }

        // No SDK access, observations or quit scheduling occur on this worker.
        private async Task<bool> CloseCoreAsync()
        {
            await initialization.ConfigureAwait(false);
            GameConnectionLifecycle? life;
            lock (sync) life = lifecycle;
            bool released = life == null || await life.CloseAsync().ConfigureAwait(false);
            ModuleRuntimeObservationBroker? source;
            lock (sync) source = modules;
            if (source != null) await source.WhenIdle.ConfigureAwait(false);
            return released;
        }

        private void ObserveClosing()
        {
            var close = closing!;
            if (!close.IsCompleted) return;
            try
            {
                connectionReleased = close.GetAwaiter().GetResult();
                if (!connectionReleased)
                {
                    var state = lifecycle?.Read();
                    SetFailure(state?.FailureCode ?? "PROBE_CONNECTION_CLOSE_FAILED", state?.ExceptionType);
                }
            }
            catch (Exception error) { SetFailure("PROBE_CONNECTION_CLOSE_FAILED", error.GetType().Name); }
            try
            {
                // AdvanceCity already performed the child's final main-thread
                // audit this step. Freeze it with the actual native observation.
                phase = HostPhase.WritingFinalStatus;
                QueueSnapshot(CaptureSnapshot(discoverWindows: false));
            }
            catch (Exception error)
            {
                // Never relabel an older native frame as a fresh final sample.
                SetFailure("PROBE_FINAL_OBSERVATION_FAILED", error.GetType().Name);
                // Still wait for any already accepted evidence write before
                // removing the pump. The failure permanently denies quit.
            }
        }

        private bool FinishFinalWrite()
        {
            var write = statusWork;
            if (!write.IsCompleted) return false;
            try { write.GetAwaiter().GetResult(); }
            catch (Exception error) { SetFailure("PROBE_STATUS_IO_FAILED", error.GetType().Name); }
            ReportFailure();
            RemoveUpdater();
            if (QuitAllowedAfterClose)
            {
                phase = HostPhase.QuitScheduled;
                try { ScheduleQuitAfterFrame(0); }
                catch (Exception error)
                {
                    SetFailure("PROBE_QUIT_FAILED", error.GetType().Name); ReportFailure(); phase = HostPhase.Stopped;
                }
            }
            else phase = HostPhase.Stopped;
            return true;
        }

        private void RemoveUpdater()
        {
            if (updater == Guid.Empty) return;
            MainThreadDispatcher.UnregisterUpdater(updater); updater = Guid.Empty;
        }
        private void ScheduleQuitAfterFrame(int attempt)
        {
            RequireMainThread();
            lock (sync)
            {
                if (!CanQuit()) return;
                // WaitXFrames 的 List 只在主线程访问；完成发生于下一帧 UpdateWaitFrameCount、updater 枚举之前。
                MainThreadDispatcher.WaitXFrames(1).ContinueWith(_ =>
                {
                    try
                    {
                        lock (sync)
                        {
                            if (phase != HostPhase.QuitScheduled) return;
                            if (!MainThreadDispatcher.isMainThread)
                            {
                                if (attempt == 0) MainThreadDispatcher.RunOnMainThread(() => ScheduleQuitAfterFrame(1));
                                else { SetFailure("PROBE_QUIT_THREAD_UNAVAILABLE", null); ReportFailure(); phase = HostPhase.Stopped; }
                                return;
                            }
                            if (!CanQuit()) return;
                            phase = HostPhase.QuitRequested;
                            // 最后摘要已在 Close 内持久完成；关闭之后不再启动 writer。
                            // quitScheduled 只证明已安排，实际 Quit/进程退出由外部直接验收。
                            GameManager.QuitGame();
                        }
                    }
                    catch (Exception error)
                    { lock (sync) { SetFailure("PROBE_QUIT_FAILED", error.GetType().Name); ReportFailure(); phase = HostPhase.Stopped; } }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        private bool CanQuit()
        {
            if (phase != HostPhase.QuitScheduled || !QuitAllowedAfterClose)
            { if (phase == HostPhase.QuitScheduled) phase = HostPhase.Stopped; return false; }
            var manager = GameManager.instance;
            if (!IsReadyMenu(manager))
            { SetFailure("PROBE_QUIT_CONTEXT_CHANGED", null); ReportFailure(); phase = HostPhase.Stopped; return false; }
            return true;
        }
        private void SetFailure(string code, string? type)
        {
            if (failureCode == null) { failureCode = code; exceptionType = type; }
            RequestHostStop(StopReason.Failure, denyQuit: true);
        }
        private void ReportFailure()
        {
            if (reportedFailure || failureCode == null) return;
            reportedFailure = true;
            string detail = exceptionType == null ? "" : " (" + exceptionType + ")";
            if (statusIoHResult is int hresult) detail += " HRESULT=0x" + hresult.ToString("X8", CultureInfo.InvariantCulture);
            report?.Invoke("SkylinesLink menu probe: " + failureCode + detail);
            if (initializationFailureSite != null) report?.Invoke("SkylinesLink menu probe initialization site: " + initializationFailureSite);
        }

        private void SampleWhenDue(long now)
        {
            if (configuration == null || evidence == null || now < nextSample) return;
            // A native step may have taken time since input intake. Space the
            // next sample from this actual attempt, not the older deadline clock.
            nextSample = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            try { QueueSnapshot(CaptureSnapshot(discoverWindows: !CloseStarted)); }
            catch (Exception error) { SetFailure("PROBE_NATIVE_OBSERVATION_FAILED", error.GetType().Name); }
        }
        private void QueueSnapshot(NativeSnapshot snapshot)
        {
            if (evidence == null) throw new ProbeFailure("PROBE_STATUS_NOT_READY");
            pendingSnapshot = snapshot;
            if (writerActive) return;
            writerActive = true; statusWork = Task.Run(WriteSnapshots);
        }
        private void WriteSnapshots()
        {
            while (true)
            {
                NativeSnapshot snapshot; PrivateConnectionStorage storage;
                lock (sync)
                {
                    if (pendingSnapshot == null) { writerActive = false; return; }
                    snapshot = pendingSnapshot; pendingSnapshot = null; storage = evidence!;
                }
                try
                {
                    WriteStatus(storage, snapshot.RunId, snapshot.Status);
                    // Discovery only supplies handles for a later main-thread
                    // observation; it never changes this immutable status frame.
                    if (snapshot.DiscoverWindows)
                    {
                        var discovered = DiscoverWindows();
                        lock (sync) if (!CloseStarted) windows = discovered;
                    }
                }
                catch (Exception error)
                {
                    lock (sync)
                    {
                        var cause = error.InnerException as IOException ?? error;
                        if (failureCode == null && cause is IOException io) statusIoHResult = io.HResult;
                        SetFailure(error is ProbeFailure failure ? failure.Code : "PROBE_STATUS_IO_FAILED", cause.GetType().Name);
                        pendingSnapshot = null;
                        writerActive = false;
                    }
                    return;
                }
            }
        }
        private static void WriteStatus(PrivateConnectionStorage storage, string runId, JObject status)
        {
            var encoding = new UTF8Encoding(false, true);
            byte[] bytes = encoding.GetBytes(status.ToString(Formatting.None));
            bool cityObservation = (string?)status["experiment"] == "normalCityOverview";
            if (cityObservation)
            {
                // The closed geometry projection has at most 256 records, each
                // <=1817 ASCII bytes. Preserve the existing 32KiB budget for
                // everything else instead of enlarging all diagnostic fields.
                // JSON null is a JValue instance, not a C# null reference.
                var connection = status["normalConnection"] as JObject;
                var definition = connection?["objectDefinition"] as JObject;
                var commands = definition?["commands"] as JObject;
                var geometry = commands?["netCourseGeometry"] as JArray;
                int geometryBytes = geometry == null ? 2 : encoding.GetByteCount(geometry.ToString(Formatting.None));
                var generation = connection?["objectGeneration"] as JObject;
                JToken? rootReceipt = generation?["generatedEntity"];
                JToken? nodeReceipt = generation?["generatedNode"];
                var roundTrip = connection?["compositionRoundTrip"] as JObject;
                JToken? site = roundTrip?["site"];
                JToken? followups = (roundTrip?["reuse"] as JObject)?["followups"];
                JToken? commit = roundTrip?["commit"];
                JToken? relocation = roundTrip?["relocation"];
                JToken? relocationGeneration = roundTrip?["relocationGeneration"];
                JToken? relocationPreview = roundTrip?["relocationPreview"];
                JToken? relocationCommit = roundTrip?["relocationCommit"];
                JToken? rebuild = roundTrip?["rebuild"];
                int relocationCommitBytes = relocationCommit == null ? 0 : encoding.GetByteCount(relocationCommit.ToString(Formatting.None));
                int rebuildBytes = rebuild == null ? 0 : encoding.GetByteCount(rebuild.ToString(Formatting.None));
                var relocationSuccessors = (relocationPreview as JObject)?["successors"] as JObject;
                JToken? relocationEligibility = relocationSuccessors?["eligibility"];
                JToken? relocationCancellation = relocationSuccessors?["cancellation"];
                JToken? relocationApplyPreparation = relocationSuccessors?["applyPreparation"];
                int rootReceiptBytes = rootReceipt == null ? 0 : encoding.GetByteCount(rootReceipt.ToString(Formatting.None));
                int nodeReceiptBytes = nodeReceipt == null ? 0 : encoding.GetByteCount(nodeReceipt.ToString(Formatting.None));
                int siteBytes = site == null ? 0 : encoding.GetByteCount(site.ToString(Formatting.None));
                int followupBytes = followups == null ? 0 : encoding.GetByteCount(followups.ToString(Formatting.None));
                int commitBytes = commit == null ? 0 : encoding.GetByteCount(commit.ToString(Formatting.None));
                int relocationBytes = relocation == null ? 0 : encoding.GetByteCount(relocation.ToString(Formatting.None));
                int relocationGenerationBytes = relocationGeneration == null ? 0 : encoding.GetByteCount(relocationGeneration.ToString(Formatting.None));
                int relocationPreviewBytes = relocationPreview == null ? 0 : encoding.GetByteCount(relocationPreview.ToString(Formatting.None));
                int relocationEligibilityBytes = relocationEligibility == null ? 0 : encoding.GetByteCount(relocationEligibility.ToString(Formatting.None));
                // Cost and eligibility add nine cost rows and bounded private error,
                // cancellation and icon rows. The shared and file caps still
                // include the entire preview and therefore charge these bytes once.
                int relocationCancellationBytes = relocationCancellation == null ? 0 : encoding.GetByteCount(relocationCancellation.ToString(Formatting.None));
                int relocationApplyPreparationBytes = relocationApplyPreparation == null ? 0
                    : encoding.GetByteCount(relocationApplyPreparation.ToString(Formatting.None));
                int relocationGraphBytes = relocationPreviewBytes;
                if (relocationEligibility != null) relocationGraphBytes -= relocationEligibilityBytes - 4;
                if (relocationCancellation != null) relocationGraphBytes -= relocationCancellationBytes - 4;
                if (relocationApplyPreparation != null) relocationGraphBytes -= relocationApplyPreparationBytes - 4;
                JToken? postGeneration = generation?["postGeneration"];
                int postGenerationBytes = postGeneration == null ? 0 : encoding.GetByteCount(postGeneration.ToString(Formatting.None));
                JToken? subObjects = (postGeneration as JObject)?["subObjects"];
                int subObjectBytes = subObjects == null ? 0 : encoding.GetByteCount(subObjects.ToString(Formatting.None));
                JToken? subNets = (postGeneration as JObject)?["subNets"];
                int subNetBytes = subNets == null ? 0 : encoding.GetByteCount(subNets.ToString(Formatting.None));
                JToken? composition = (postGeneration as JObject)?["composition"];
                int compositionBytes = composition == null ? 0 : encoding.GetByteCount(composition.ToString(Formatting.None));
                JToken? subObjectCommands = (subObjects as JObject)?["commands"];
                int commandBytes = subObjectCommands == null ? 0 : encoding.GetByteCount(subObjectCommands.ToString(Formatting.None));
                int lifecycleBytes = subObjects == null ? postGenerationBytes : postGenerationBytes - subObjectBytes + 4;
                if (subNets != null) lifecycleBytes = lifecycleBytes - subNetBytes + 4;
                if (composition != null) lifecycleBytes = lifecycleBytes - compositionBytes + 4;
                JToken? subObjectSurvey = generation?["subObjectInputSurvey"];
                int surveyBytes = subObjectSurvey == null ? 0 : encoding.GetByteCount(subObjectSurvey.ToString(Formatting.None));
                // Detailed native object/network output and course geometry share the
                // existing output pool. The remaining lifecycle receipts keep
                // their smaller profile; no subtree is deducted twice.
                int otherBytes = bytes.Length - geometryBytes + 2;
                if (postGeneration != null) otherBytes = otherBytes - postGenerationBytes + 4;
                if (subObjectSurvey != null) otherBytes = otherBytes - surveyBytes + 4;
                if (rootReceipt != null) otherBytes = otherBytes - rootReceiptBytes + 4;
                if (nodeReceipt != null) otherBytes = otherBytes - nodeReceiptBytes + 4;
                if (site != null) otherBytes = otherBytes - siteBytes + 4;
                if (followups != null) otherBytes = otherBytes - followupBytes + 4;
                if (commit != null) otherBytes = otherBytes - commitBytes + 4;
                if (relocation != null) otherBytes = otherBytes - relocationBytes + 4;
                if (relocationGeneration != null) otherBytes = otherBytes - relocationGenerationBytes + 4;
                if (relocationPreview != null) otherBytes = otherBytes - relocationPreviewBytes + 4;
                if (relocationCommit != null) otherBytes = otherBytes - relocationCommitBytes + 4;
                if (rebuild != null) otherBytes = otherBytes - rebuildBytes + 4;
                // The first-layer survey has two exact owners and a finite row
                // budget. Its 16KiB profile keeps the full file below 512KiB
                // even with the maximum geometry and post-generation profiles.
                int sharedBytes = geometryBytes + subObjectBytes + subNetBytes + compositionBytes
                    + rootReceiptBytes + nodeReceiptBytes + siteBytes + followupBytes + commitBytes + relocationBytes
                    + relocationGenerationBytes + relocationPreviewBytes + relocationCommitBytes + rebuildBytes;
                if ((geometry?.Count ?? 0) > 256 || sharedBytes > 465409 ||
                    rootReceiptBytes > 4096 || nodeReceiptBytes > 8192 || siteBytes > 2048 || followupBytes > 12288 || commitBytes > 8192 ||
                    relocationCommitBytes > 4096 || relocationCommit != null && relocationCommit.Type != JTokenType.Null && !(relocationCommit is JObject) ||
                    rebuildBytes > 8192 || rebuild != null && rebuild.Type != JTokenType.Null && !(rebuild is JObject) ||
                    relocationBytes > 2048 || relocation != null && relocation.Type != JTokenType.Null && !(relocation is JObject) ||
                    relocationGenerationBytes > 2048 || relocationGeneration != null && relocationGeneration.Type != JTokenType.Null
                        && !(relocationGeneration is JObject) ||
                    relocationEligibilityBytes > 8192 || relocationEligibility != null && relocationEligibility.Type != JTokenType.Null
                        && !(relocationEligibility is JObject) ||
                    relocationCancellationBytes > 4096 || relocationCancellation != null && relocationCancellation.Type != JTokenType.Null
                        && !(relocationCancellation is JObject) ||
                    relocationApplyPreparationBytes > 2048 || relocationApplyPreparation != null
                        && relocationApplyPreparation.Type != JTokenType.Null && !(relocationApplyPreparation is JObject) ||
                    relocationGraphBytes > 12288 || relocationPreview != null && relocationPreview.Type != JTokenType.Null
                        && !(relocationPreview is JObject) ||
                    rootReceipt != null && rootReceipt.Type != JTokenType.Null && !(rootReceipt is JObject) ||
                    nodeReceipt != null && nodeReceipt.Type != JTokenType.Null && !(nodeReceipt is JObject) ||
                    site != null && site.Type != JTokenType.Null && !(site is JObject) ||
                    followups != null && followups.Type != JTokenType.Null && !(followups is JObject) ||
                    commit != null && commit.Type != JTokenType.Null && !(commit is JObject) ||
                    subObjectBytes > 65536 || subNetBytes > 8192 || compositionBytes > 65536 || commandBytes > 32768 || lifecycleBytes > 8192 || surveyBytes > 16384 || otherBytes > 32768 ||
                    postGeneration != null && postGeneration.Type != JTokenType.Null && !(postGeneration is JObject) ||
                    subObjects != null && subObjects.Type != JTokenType.Null && !(subObjects is JObject) ||
                    subNets != null && subNets.Type != JTokenType.Null && !(subNets is JObject) ||
                    composition != null && composition.Type != JTokenType.Null && !(composition is JObject) ||
                    subObjectCommands != null && subObjectCommands.Type != JTokenType.Null && !(subObjectCommands is JObject) ||
                    subObjectSurvey != null && subObjectSurvey.Type != JTokenType.Null && !(subObjectSurvey is JObject))
                    throw new ProbeFailure("PROBE_STATUS_BUDGET");
            }
            int maximum = cityObservation ? 524288 :
                status["normalSettings"] is JObject || status["normalConnection"] is JObject ? 16384 : 4096;
            if (bytes.Length > maximum) throw new ProbeFailure("PROBE_STATUS_BUDGET");
            string target = Path.Combine(storage.DirectoryPath, "menu-probe-" + runId + ".status.json");
            PublishStatus(storage, target, bytes);
        }

        private static void PublishStatus(PrivateConnectionStorage storage, string target, byte[] bytes)
        {
            // One writer and one staging file per run. A failed cleanup cannot
            // accumulate staging files while native work is still draining.
            string temporary = target + ".tmp";
            bool created = false;
            string stage = "VALIDATE";
            try
            {
                storage.ValidateFile(target, true);
                stage = "WRITE";
                using (var stream = storage.CreatePrivateFile(temporary))
                {
                    created = true;
                    stream.Write(bytes, 0, bytes.Length);
                    storage.FlushToDisk(stream);
                }
                stage = "PUBLISH";
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(target)) File.Replace(temporary, target, null);
                        else File.Move(temporary, target);
                        created = false;
                        return;
                    }
                    catch (IOException error) when (attempt < 3 && IsFileContention(error))
                    {
                        // Only the background writer waits. Never retry an unknown write or flush.
                        Thread.Sleep(15 * (attempt + 1));
                    }
                }
            }
            catch (IOException error) { throw new ProbeFailure("PROBE_STATUS_" + stage + "_FAILED", error); }
            finally
            {
                if (created)
                {
                    try { File.Delete(temporary); }
                    catch (IOException error) { throw new ProbeFailure("PROBE_STATUS_TEMP_CLEANUP_FAILED", error); }
                }
            }
        }

        private static bool IsFileContention(IOException error) =>
            error.HResult == unchecked((int)0x80070020) || error.HResult == unchecked((int)0x80070021);

        private WindowHandle[] DiscoverWindows()
        {
            var handles = new List<WindowHandle>(8); bool exceeded = false;
            bool completed = EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out uint owner);
                if (owner != processId) return true;
                if (handles.Count == 8) { exceeded = true; return false; }
                handles.Add(new WindowHandle(handle)); return true;
            }, IntPtr.Zero);
            if (exceeded) throw new ProbeFailure("PROBE_WINDOW_BUDGET");
            if (!completed) throw new ProbeFailure("PROBE_WINDOW_ENUMERATION_FAILED");
            return handles.ToArray();
        }
        private bool OwnWindow(IntPtr handle)
        { if (!IsWindow(handle)) return false; GetWindowThreadProcessId(handle, out uint owner); return owner == processId; }
        private static string LowerFirst(string value) => char.ToLowerInvariant(value[0]) + value.Substring(1);
        private static string Utc(DateTime value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        private static void RequireMainThread() { if (!MainThreadDispatcher.isMainThread) throw new InvalidOperationException("Menu probe requires the game main thread."); }
        private sealed class WindowHandle { internal WindowHandle(IntPtr handle) { Handle = handle; } internal IntPtr Handle { get; } }
        private enum HostPhase { Initializing, Running, Draining, ClosingIo, WritingFinalStatus, QuitScheduled, QuitRequested, Stopped }
        private enum StopReason { None, ExternalRequest, Deadline, LeftMenu, ExperimentFinished, Failure }
        private readonly struct StopIntent
        {
            internal StopIntent(StopReason reason, bool quitDenied) { Reason = reason; QuitDenied = quitDenied; }
            internal StopReason Reason { get; }
            internal bool QuitDenied { get; }
            internal bool Requested => Reason != StopReason.None;
            internal StopIntent Merge(StopReason reason, bool denyQuit) => new StopIntent(Requested ? Reason : reason, QuitDenied || denyQuit);
        }
        private sealed class NativeSnapshot
        {
            internal NativeSnapshot(string runId, JObject status, bool discoverWindows)
            { RunId = runId; Status = status; DiscoverWindows = discoverWindows; }
            internal string RunId { get; }
            internal JObject Status { get; }
            internal bool DiscoverWindows { get; }
        }
        private sealed class ProbeFailure : Exception
        {
            internal ProbeFailure(string code, Exception? cause = null) : base(code, cause) { Code = code; }
            internal string Code { get; }
        }
        private sealed class Configuration
        {
            internal string RunId = "", EnvironmentId = "", ProfileId = "", CoreHash = "";
            internal string ConnectionMode = "readOwner";
            internal string Experiment = "";
            internal string TestMapMetadataGuid = "", TestMapDataGuid = "";
            internal DateTime ExpiresAtUtc; internal int SessionSeconds; internal bool QuitAtEnd;
            internal static Configuration Read(PrivateConnectionStorage storage, string actualUserData, string launchRunId)
            {
                string path = Path.Combine(storage.DirectoryPath, "menu-probe.json"); storage.ValidateFile(path, false);
                byte[] bytes;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length < 1 || stream.Length > 4096) throw new ProbeFailure("PROBE_CONFIGURATION_BUDGET");
                    bytes = new byte[(int)stream.Length]; int offset = 0;
                    while (offset < bytes.Length) { int count = stream.Read(bytes, offset, bytes.Length - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
                }
                var value = ContractJson.Parse(new UTF8Encoding(false, true).GetString(bytes)) as JObject ?? throw new ProbeFailure("PROBE_CONFIGURATION_INVALID");
                string[] names = { "runId", "environmentId", "profileId", "coreManifestSha256", "expectedUserDataPath", "createdAtUtc", "expiresAtUtc", "sessionSeconds", "quitAtEnd" };
                if (value.Count < names.Length || value.Count > names.Length + 4
                    || names.Any(name => value[name] == null)
                    || value.Properties().Any(p => p.Name != "connectionMode" && p.Name != "experiment"
                        && p.Name != "testMapMetadataGuid" && p.Name != "testMapDataGuid"
                        && !names.Contains(p.Name, StringComparer.Ordinal))) throw new ProbeFailure("PROBE_CONFIGURATION_INVALID");
                string String(string key) => value[key]?.Type == JTokenType.String ? (string)value[key]! : throw new ProbeFailure("PROBE_CONFIGURATION_INVALID");
                var result = new Configuration { RunId = String("runId"), EnvironmentId = String("environmentId"), ProfileId = String("profileId"), CoreHash = String("coreManifestSha256") };
                if (value["connectionMode"] != null)
                {
                    result.ConnectionMode = String("connectionMode");
                    if (result.ConnectionMode != "readOwner" && result.ConnectionMode != "noOwner") throw new ProbeFailure("PROBE_CONFIGURATION_INVALID");
                }
                if (value["experiment"] != null)
                {
                    result.Experiment = String("experiment");
                    if ((result.Experiment != "definitionContainer" && result.Experiment != "straightDefinition" && result.Experiment != "scopedClear" && result.Experiment != "mapIdentity" && result.Experiment != "testCity" && result.Experiment != "normalSettings" && result.Experiment != "normalConnection" && result.Experiment != "normalCityOverview") || result.ConnectionMode != "noOwner") throw new ProbeFailure("PROBE_CONFIGURATION_INVALID");
                }
                if (result.Experiment == "testCity" || result.Experiment == "normalCityOverview")
                {
                    result.TestMapMetadataGuid = String("testMapMetadataGuid"); result.TestMapDataGuid = String("testMapDataGuid");
                    foreach (string id in new[] { result.TestMapMetadataGuid, result.TestMapDataGuid })
                        if (!Guid.TryParseExact(id, "N", out Guid parsed) || parsed == Guid.Empty || parsed.ToString("N") != id)
                            throw new ProbeFailure("PROBE_CONFIGURATION_INVALID");
                }
                else if (value["testMapMetadataGuid"] != null || value["testMapDataGuid"] != null) throw new ProbeFailure("PROBE_CONFIGURATION_INVALID");
                if (!string.Equals(result.RunId, launchRunId, StringComparison.Ordinal)) throw new ProbeFailure("PROBE_LAUNCH_MISMATCH");
                if (!Guid.TryParseExact(result.RunId, "N", out Guid run) || run.ToString("N") != result.RunId || AuthorizationState.NormalizeEnvironment(result.EnvironmentId) != result.EnvironmentId)
                    throw new ProbeFailure("PROBE_CONFIGURATION_INVALID");
                if ((result.Experiment == "normalSettings" || result.Experiment == "normalConnection" || result.Experiment == "normalCityOverview") && result.EnvironmentId != "probe-" + result.RunId)
                    throw new ProbeFailure("PROBE_CONFIGURATION_INVALID");
                AuthorizationState.ValidateIdentifier(result.ProfileId); AuthorizationState.ValidateHex(result.CoreHash);
                string expected = String("expectedUserDataPath");
                if (!Path.IsPathRooted(expected) || !string.Equals(Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(actualUserData).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new ProbeFailure("PROBE_USER_DATA_MISMATCH");
                DateTime ParseUtc(string key)
                {
                    if (!DateTime.TryParseExact(String(key), "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed)) throw new ProbeFailure("PROBE_CONFIGURATION_INVALID");
                    return parsed;
                }
                DateTime created = ParseUtc("createdAtUtc"), now = DateTime.UtcNow; result.ExpiresAtUtc = ParseUtc("expiresAtUtc");
                if (created > now || result.ExpiresAtUtc <= now || result.ExpiresAtUtc <= created || result.ExpiresAtUtc - created > TimeSpan.FromMinutes(30)) throw new ProbeFailure("PROBE_CONFIGURATION_EXPIRED");
                if (value["sessionSeconds"]?.Type != JTokenType.Integer || value["quitAtEnd"]?.Type != JTokenType.Boolean) throw new ProbeFailure("PROBE_CONFIGURATION_INVALID");
                long seconds = (long)value["sessionSeconds"]!; if (seconds < 60 || seconds > 900) throw new ProbeFailure("PROBE_CONFIGURATION_INVALID");
                result.SessionSeconds = (int)seconds; result.QuitAtEnd = (bool)value["quitAtEnd"]!; return result;
            }
        }
        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr window, int command);
    }
}
#endif
