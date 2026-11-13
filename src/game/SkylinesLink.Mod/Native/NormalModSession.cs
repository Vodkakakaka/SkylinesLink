using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Colossal.Core;
using Colossal.IO.AssetDatabase;
using Game;
using Game.Modding;
using Game.SceneFlow;
using SkylinesLink.Application.Modules;
using SkylinesLink.Application.Observation;
using SkylinesLink.Application.Buildings;
using SkylinesLink.Application.Operations;
using SkylinesLink.Application.Simulation;
using SkylinesLink.Contracts;
using SkylinesLink.Infrastructure.Installation;
using SkylinesLink.Infrastructure.Operations;
using SkylinesLink.Mod.Runtime;
using SkylinesLink.Mod.Settings;

namespace SkylinesLink.Mod.Native
{
    /// <summary>普通模组装配根。SDK资源与重建只在主线程，IO归现有连接寿命。</summary>
    public sealed partial class NormalModSession
    {
        private readonly object sync = new object();
        private readonly IMod mod;
        private readonly UpdateSystem updateSystem;
        private readonly Action<string>? report;
        private readonly string environment;
        private readonly bool readPreferences;
        private readonly Func<bool> updater;
        private readonly LoadedManagedComponent[] components;
        private readonly NativeModuleRuntimeCollector collector = new NativeModuleRuntimeCollector();
        private NativeCityOverviewCollector? cityOverviewCollector;
        private NativeBuildingInspectionCollector? buildingCollector;
        private readonly List<ConnectionLocaleSource> sources = new List<ConnectionLocaleSource>();
        private InstalledGameConnection? connection;
        private NativeWorldContextObserver? observer;
        private ConnectionSettings? settings;
        private Task? action;
        private Task<bool>? drain;
        private ApplyRequest? pending, replacement;
        private bool stopping, busy = true, updaterMayBeHeld, settingsMayBeHeld, configuring, cleanupFailed;
        private bool preferencesUnavailable;
        private ConnectionNotice status = ConnectionNotice.Starting;
        private string localeId = "en-US";
        private ModBundleOrigin? bundledOrigin;
#if SKYLINESLINK_INTEGRATION_PROBE
        private int acceptedConfigurationRequests;
#endif

        public NormalModSession(IMod mod, UpdateSystem updateSystem, Action<string>? report = null)
            : this(mod, updateSystem, report, "default", true) { }

        private NormalModSession(IMod mod, UpdateSystem updateSystem, Action<string>? report, string environment, bool readPreferences)
        {
            this.mod = mod ?? throw new ArgumentNullException(nameof(mod));
            this.updateSystem = updateSystem ?? throw new ArgumentNullException(nameof(updateSystem));
            this.report = report;
            this.environment = AuthorizationState.NormalizeEnvironment(environment);
            this.readPreferences = readPreferences;
            updater = Tick;
            // These four actual type anchors describe the loaded cohort, regardless
            // of its market/cache path. No AppDomain-first or path fallback.
            components = new[] {
                LoadedManagedComponent.Capture(typeof(global::SkylinesLink.Mod.Mod).Assembly),
                LoadedManagedComponent.Capture(typeof(OperationJournal).Assembly),
                LoadedManagedComponent.Capture(typeof(LoadTransition).Assembly),
                LoadedManagedComponent.Capture(typeof(ContractCodec).Assembly)
            };
        }



        public void Start()
        {
            RequireMainThread();
            try
            {
                ThrowIfStopping();
                RefreshLocale();
                updaterMayBeHeld = true;
                if (MainThreadDispatcher.RegisterUpdater(updater) == Guid.Empty)
                    throw new InvalidOperationException("The connection updater was not registered.");
                updaterMayBeHeld = true;
                string information = Array.Find(components, item => item.Name == "SkylinesLink.Mod")!.InformationalVersion;
                int sourceSuffix = information.IndexOf('+');
                var page = new ConnectionSettings(mod, RequestApply, ReadStatus, IsBusy, ReadLocale,
                    sourceSuffix < 0 ? information : information.Substring(0, sourceSuffix), environment);
                lock (sync)
                {
                    settings = page;
                    if (stopping) page.Detach();
                }
                ThrowIfStopping();
                page.SetDefaults();
                try { if (readPreferences) AssetDatabase.global.LoadSettings(nameof(ConnectionSettings), page); }
                catch (Exception)
                {
                    // Preferences are drafts, not authorization. Keep the settings
                    // page usable without rewriting a corrupt preference file.
                    page.SetDefaults(); preferencesUnavailable = true;
                }
                foreach (var source in ConnectionLocaleSource.Create(page))
                {
                    lock (sync)
                    {
                        if (stopping) { source.Deactivate(); throw new OperationCanceledException(); }
                        sources.Add(source); // Own partial SDK registration before the call.
                    }
                    GameManager.instance.localizationManager.AddSource(source.LocaleId, source);
                }
                ThrowIfStopping();
                settingsMayBeHeld = true;
                page.RegisterInOptionsUI();
                settingsMayBeHeld = true;
                ThrowIfStopping();
                // SDK identity is captured on the main thread only. An existing
                // healthy installation remains usable if bundle capture fails.
                try { bundledOrigin = NativeOwnBundleLocator.Capture(mod); }
                catch (Exception) { bundledOrigin = null; }
                ThrowIfStopping();
                StartLifetime(null);
            }
            finally
            {
                bool stopped;
                lock (sync) stopped = stopping;
                // Stop may have arrived before settings/source publication. Do
                // not depend on a later frame to detach that partial startup.
                if (stopped) ReleaseNative();
            }
        }

        private void ThrowIfStopping()
        { lock (sync) if (stopping) throw new OperationCanceledException(); }

        public string ReadStatus()
        {
            lock (sync)
            {
                ConnectionNotice current = status;
                var preparation = connection?.ReadPreparation();
                if (!stopping && action != null && preparation != null && preparation.Phase != "Completed")
                    current = preparation.CleanupPending ? ConnectionNotice.StoppingPreparation :
                        preparation.Phase == "InspectingSource" ? ConnectionNotice.CheckingFiles :
                        preparation.Phase == "VerifyingInstallation" ? ConnectionNotice.CheckingConnectionFiles :
                        preparation.Phase == "Closing" ? ConnectionNotice.FinishingPreparation : ConnectionNotice.Preparing;
                string text = ConnectionSettingsText.Format(current, localeId);
                return preferencesUnavailable ?
                    text + "\n" + ConnectionSettingsText.Format(ConnectionNotice.PreferencesUnavailable, localeId) : text;
            }
        }
        private string ReadLocale() { lock (sync) return localeId; }
        private void RefreshLocale()
        {
            RequireMainThread();
            string? current;
            try { current = GameManager.instance?.localizationManager?.activeLocaleId; }
            catch (Exception) { return; }
            if (!string.IsNullOrWhiteSpace(current)) lock (sync) localeId = current!;
        }
        public bool IsBusy() { lock (sync) return stopping || busy; }
        private void SetStatus(ConnectionNotice value, bool isBusy = false)
        {
            lock (sync)
            {
                status = value;
                // A finishing status update cannot reopen the button over a
                // concurrently accepted request or another owned work item.
                busy = isBusy || pending != null || replacement != null || action != null || drain != null;
            }
        }

        private void RequestApply(string profile, bool cityChanges)
        {
            // Settings callbacks may run off-thread. Only a bounded ordinary-value
            // request crosses here; loading preferences never invokes this action.
            lock (sync)
            {
                if (stopping || busy || pending != null) return;
                try { AuthorizationState.ValidateIdentifier(profile); }
                catch (ArgumentException)
                {
                    status = ConnectionNotice.InvalidProfile;
                    return;
                }
                pending = new ApplyRequest(profile, cityChanges);
#if SKYLINESLINK_INTEGRATION_PROBE
                acceptedConfigurationRequests++;
#endif
                busy = true;
                status = ConnectionNotice.Applying;
            }
        }

        private void StartLifetime(ApplyRequest? request)
        {
            RequireMainThread();
            lock (sync) if (stopping) return;
            var created = new InstalledGameConnection(environment, components, request?.Profile,
                request?.CityChanges ?? false, bundledOrigin);
            connection = created;
            cityOverviewCollector = new NativeCityOverviewCollector(updateSystem, created.Context);
            buildingCollector = new NativeBuildingInspectionCollector(updateSystem, created.Context);
            buildingCollector.ObserveSelectionScope(created.Context.Read());
            lock (sync) if (stopping) { created.RequestStop(); return; }
            observer = new NativeWorldContextObserver(updateSystem, created.Context, report, buildingCollector.ObserveSelectionScope);
            try { observer.Start(); }
            catch { created.RequestStop(); throw; }
            configuring = false;
            action = created.StartAsync();
            SetStatus(request == null ? ConnectionNotice.CheckingInstalled : ConnectionNotice.Applying, true);
        }

        private bool Tick()
        {
            try
            {
                RequireMainThread();
                bool stopped;
                lock (sync) stopped = stopping;
                if (stopped)
                {
                    if (!cleanupFailed) ReleaseNative();
                    // Preserve the old failure policy for other native cleanup,
                    // but keep retrying this newly owned selection detach.
                    else ReleaseBuildingSelection();
                    // ReleaseNative already confirms removal of our exact Guid.
                    // Returning true would request a redundant native removal.
                    return false;
                }
                RefreshLocale();
                FinishAction();
                FinishReplacement();
                ApplyRequest? request;
                lock (sync) { request = pending; pending = null; }
                if (request != null) ApplyOnMainThread(request);
                CaptureModules();
                CaptureCityOverview();
                CaptureBuilding();
            }
            catch (Exception error)
            {
                // Preserve the diagnostic category in the existing private log,
                // while the settings page uses a localized player-facing message.
                try { report?.Invoke("SkylinesLink connection update unavailable: " + error.GetType().Name); }
                catch (Exception) { /* Logging cannot replace the original cleanup responsibility. */ }
                connection?.RequestStop();
                bool stopped;
                lock (sync) stopped = stopping;
                SetStatus(stopped ? ConnectionNotice.CleanupIncomplete : ConnectionNotice.Unavailable, stopped);
            }
            return false;
        }

        private void FinishAction()
        {
            if (action == null || !action.IsCompleted) return;
            bool failed = action.IsFaulted || action.IsCanceled;
            if (action.IsFaulted) _ = action.Exception;
            action = null;
            if (failed)
            {
                connection?.RequestStop();
                SetStatus(ConnectionNotice.ConfigurationFailed);
            }
            else if (connection?.Read().Phase == GameConnectionPhase.Active)
                SetStatus(configuring ? ConnectionNotice.ProfileApplied : ConnectionNotice.ServiceRunning);
            else if (connection?.Read().FailureCode == "ENVIRONMENT_BUSY")
                SetStatus(ConnectionNotice.EnvironmentBusy);
            else if (connection?.PreparationResult is SetupInitializationResult prepared && !prepared.Ready)
                SetStatus(ConnectionSettingsText.PreparationFailure(prepared));
            else if (connection?.ProductVersion == null)
                SetStatus(ConnectionNotice.InstallationUnavailable);
            else SetStatus(ConnectionNotice.NotConfigured);
        }

        private void ApplyOnMainThread(ApplyRequest request)
        {
            lock (sync) if (stopping) return;
            if (action != null || drain != null) { SetStatus(ConnectionNotice.WorkFinishing, true); return; }
            if (connection?.Read().Phase == GameConnectionPhase.Active)
            {
                configuring = true;
                action = connection.ConfigureAsync(request.Profile, request.CityChanges);
                return;
            }
            // A terminal context is never reused. Native callbacks leave first;
            // only a confirmed complete close permits a fresh instance below.
            observer?.Dispose(); observer = null;
            replacement = request;
            drain = connection?.CloseAsync() ?? Task.FromResult(true);
        }

        private void FinishReplacement()
        {
            if (drain == null || !drain.IsCompleted) return;
            bool released = drain.Status == TaskStatus.RanToCompletion && drain.Result;
            if (drain.IsFaulted) _ = drain.Exception;
            if (!released)
            {
                // Existing close-failure behavior is unchanged. Only a
                // successful drain can wait for the new selection obligation.
                drain = null; replacement = null;
                SetStatus(ConnectionNotice.CleanupIncomplete); return;
            }
            if (!ReleaseBuildingSelection()) { SetStatus(ConnectionNotice.CleanupIncomplete, true); return; }
            // Consume the accepted replacement only after its old listener is
            // physically detached. A failed detach keeps this completed drain.
            drain = null;
            var request = replacement; replacement = null;
            connection = null;
            if (request != null) StartLifetime(request);
        }

        private void CaptureModules()
        {
            var source = connection;
            if (source == null || source.Context.IsStopped) return;
            string state = source.Context.Read().State;
            if (state != "menu" && state != "ready") return;
            if (!source.Modules.TryClaim(out var work)) return;
            ModuleRegistryCapture capture;
            try { capture = collector.Capture(); }
            catch (ModuleRegistryReadFailure error) { source.Modules.Fail(work!, error.Code); return; }
            catch (Exception) { source.Modules.Fail(work!, "CAPTURE_FAILED"); return; }
            source.Modules.Complete(work!, capture);
        }

        private void CaptureCityOverview()
        {
            var source = connection;
            if (source == null || source.Context.IsStopped) return;
            if (!source.CityOverview.TryClaim(out var work)) return;
            CityOverviewCapture capture;
            try { capture = cityOverviewCollector!.Capture(work!.Scope); }
            catch (CityOverviewReadFailure error) { source.CityOverview.Fail(work!, error.Code); return; }
            catch (Exception) { source.CityOverview.Fail(work!, "CAPTURE_FAILED"); return; }
            source.CityOverview.Complete(work!, capture);
        }

        private void CaptureBuilding()
        {
            var source = connection;
            if (source == null || source.Context.IsStopped || !source.Buildings.TryClaim(out var work)) return;
            BuildingInspectionCapture capture;
            try { capture = buildingCollector!.Capture(work!.Scope, work.Query); }
            catch (BuildingInspectionReadFailure error) { source.Buildings.Fail(work!, error.Code); return; }
            catch (Exception) { source.Buildings.Fail(work!, "CAPTURE_FAILED"); return; }
            source.Buildings.Complete(work!, capture);
        }

        public void RequestStop()
        {
            lock (sync)
            {
                stopping = true; pending = null; replacement = null; busy = true; status = ConnectionNotice.Stopping;
                settings?.Detach();
                foreach (var source in sources) source.Deactivate();
            }
            connection?.RequestStop();
            buildingCollector?.StopSelectionSource();
            // Off-thread disposal only invalidates ordinary state. The owned
            // updater performs SDK cleanup on its next main-thread invocation.
            if (MainThreadDispatcher.isMainThread) { cleanupFailed = false; ReleaseNative(); }
        }

        private void ReleaseNative()
        {
            RequireMainThread();
            var failures = new List<Exception>();
            // Always continue releasing the root's other native resources. A
            // failed optional detach stays visible as its own retained owner.
            bool selectionReleased = ReleaseBuildingSelection();
            try { observer?.Dispose(); observer = null; } catch (Exception error) { failures.Add(error); }
            settings?.Detach();
            if (settingsMayBeHeld)
                try { settings!.UnregisterInOptionsUI(); settingsMayBeHeld = false; } catch (Exception error) { failures.Add(error); }
            for (int i = sources.Count - 1; i >= 0; i--)
            {
                var source = sources[i]; source.Deactivate();
                try
                {
                    GameManager.instance.localizationManager.RemoveSource(source.LocaleId, source);
                    lock (sync) sources.RemoveAt(i);
                }
                catch (Exception error) { failures.Add(error); }
            }
            if (updaterMayBeHeld && failures.Count == 0 && selectionReleased)
                try
                {
                    Guid owned = MainThreadDispatcher.GetUpdaterGuid(updater);
                    if (owned != Guid.Empty)
                    {
                        try { MainThreadDispatcher.UnregisterUpdater(owned); }
                        catch { if (MainThreadDispatcher.GetUpdaterGuid(updater) != Guid.Empty) throw; }
                    }
                    if (MainThreadDispatcher.GetUpdaterGuid(updater) != Guid.Empty)
                        throw new InvalidOperationException("The connection updater remains registered.");
                    updaterMayBeHeld = false;
                }
                catch (Exception error) { failures.Add(error); }
            cleanupFailed = failures.Count != 0;
            if (cleanupFailed) throw new AggregateException("Native connection cleanup was not confirmed.", failures);
        }

        private bool ReleaseBuildingSelection()
        {
            if (buildingCollector == null) return true;
            if (!buildingCollector.ReleaseSelectionSource()) return false;
            buildingCollector = null;
            return true;
        }

        public bool IsFullyReleased
        {
            get
            {
                lock (sync) if (!stopping) return false;
                if (updaterMayBeHeld || settingsMayBeHeld || observer != null || sources.Count != 0 ||
                    buildingCollector != null && !buildingCollector.IsSelectionSourceReleased) return false;
                if (connection == null) return true;
                var closed = connection.CloseAsync();
                return closed.Status == TaskStatus.RanToCompletion && closed.Result;
            }
        }

        private static void RequireMainThread()
        { if (!MainThreadDispatcher.isMainThread) throw new InvalidOperationException("The connection root requires the main thread."); }
        private sealed class ApplyRequest
        {
            internal ApplyRequest(string profile, bool cityChanges) { Profile = profile; CityChanges = cityChanges; }
            internal string Profile { get; }
            internal bool CityChanges { get; }
        }
    }
}
