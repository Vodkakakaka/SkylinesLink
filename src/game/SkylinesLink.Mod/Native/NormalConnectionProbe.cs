#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Diagnostics;
using System.Globalization;
using Colossal.Core;
using Game;
using Game.City;
using Game.Common;
using Game.Modding;
using Game.Prefabs;
using Game.SceneFlow;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // A bounded native qualification of the existing settings action and root.
    // The serving timer does not establish that an external MCP client passed.
    internal sealed class NormalConnectionProbe
    {
        private const double ServingSeconds = 45;
        private const double SelectionPublishSeconds = 5;
        private double RequiredServingSeconds => parallelPreview ? 0 : ServingSeconds;
        private readonly object sync = new object();
        private readonly IMod mod;
        private readonly UpdateSystem updateSystem;
        private readonly string environment, profile, expectedVersion;
        private readonly Action<string>? report;
        private readonly bool cityOverview;
        private readonly bool parallelPreview;
        private NormalModSession? root;
        private TestCityLifecycleProbe? city;
        private JObject? before, active, afterStop;
        private JObject? configuredMenu, returnedMenu;
        private JObject? objectInputSurvey;
        private CompositionRoundTrip? compositionRoundTrip;
        private FixtureSelectionQualification? selectionFixture;
        private ParallelPreviewProbe? externalPreview;
        private Action? definitionScopeGuard;
        private bool startAttempted, applyAttempted, applyObservedBusy, servingCompleted, stopping, resourcesReleased;
        private bool staleGuardRejected;
        private long guardChecks, fullUICaptures;
        private long servingStarted;
        private long selectionStarted;
        private long menuObservationStarted;
        private long nextScopeSampleTimestamp;
        private string? failureCode, exceptionType;

        internal NormalConnectionProbe(IMod mod, UpdateSystem updateSystem, string environment,
            string profile, string expectedVersion, Action<string>? report, bool cityOverview = false, bool parallelPreview = false)
        {
            this.mod = mod ?? throw new ArgumentNullException(nameof(mod));
            this.updateSystem = updateSystem ?? throw new ArgumentNullException(nameof(updateSystem));
            if (!environment.StartsWith("probe-", StringComparison.Ordinal)) throw new ArgumentException("A probe environment is required.");
            this.environment = environment;
            this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
            this.expectedVersion = expectedVersion ?? throw new ArgumentNullException(nameof(expectedVersion));
            this.report = report;
            this.cityOverview = cityOverview;
            if (parallelPreview && !cityOverview) throw new ArgumentException("The external preview requires the owned test city.");
            this.parallelPreview = parallelPreview;
        }

        internal bool ResourcesReleased { get { lock (sync) return resourcesReleased && (compositionRoundTrip?.ResourcesReleased ?? true)
            && (externalPreview?.ResourcesReleased ?? true) && (selectionFixture?.ResourcesReleased ?? true); } }
        internal bool Finished { get { lock (sync) return resourcesReleased && (compositionRoundTrip?.WorkersFinished ?? true)
            && (externalPreview?.WorkersFinished ?? true) && (selectionFixture?.IntentEnded ?? true); } }
        internal bool SuppressAutomaticReturn { get { lock (sync) return compositionRoundTrip?.NativeStateUncertain == true
            || externalPreview?.NativeStateUncertain == true; } }
        internal bool HoldingCityForPreviewCleanup { get { lock (sync) return externalPreview?.CleanupPending == true; } }
        internal bool ChecksPassed { get { lock (sync) return Passed; } }
        internal string? FailureCode { get { lock (sync) return failureCode; } }
        internal string? ExceptionType { get { lock (sync) return exceptionType; } }
        private bool Passed => resourcesReleased && before != null && active != null && afterStop != null
            && applyObservedBusy && servingCompleted && failureCode == null
            && (!cityOverview || configuredMenu != null && returnedMenu != null && city?.ChecksPassed == true
                && staleGuardRejected && (parallelPreview ? externalPreview?.Passed == true
                    : compositionRoundTrip?.Passed == true && compositionRoundTrip.ResourcesReleased && selectionFixture?.Passed == true));

        internal bool ReadyToLoadCity
        { get { lock (sync) return cityOverview && configuredMenu != null && city == null && !stopping && failureCode == null; } }

        internal void AttachCity(TestCityLifecycleProbe ownedCity)
        {
            RequireMainThread();
            lock (sync)
            {
                Check(ReadyToLoadCity && ownedCity != null, "NORMAL_CITY_ATTACH_REJECTED");
                var sample = Capture();
                CheckActiveScope(sample, "menu", configuredMenu!);
                Check(Generation(sample) == Generation(configuredMenu!), "NORMAL_CITY_MENU_SCOPE_CHANGED_BEFORE_LOAD");
                city = ownedCity;
            }
        }

        // Only main-thread Step invokes the root's SDK-facing stop path.
        internal void RequestStop() { lock (sync) stopping = true; }

        internal void Step(bool hostStopping)
        {
            RequireMainThread();
            lock (sync)
            {
                if (resourcesReleased) { ReleaseOwnedSelection(); return; }
                if (hostStopping)
                {
                    stopping = true;
                    if (!servingCompleted || cityOverview && returnedMenu == null)
                        RecordFailure("NORMAL_CONNECTION_WINDOW_INCOMPLETE", null);
                }
                try
                {
                    if (!stopping)
                    {
                        if (!cityOverview || configuredMenu == null)
                        {
                            var manager = GameManager.instance;
                            Check(manager != null && manager.state == GameManager.State.WorldReady
                                && manager.gameMode == GameMode.MainMenu && !manager.isGameLoading, "NORMAL_CONNECTION_MENU_REQUIRED");
                        }
                        if (!startAttempted)
                        {
                            startAttempted = true;
                            root = NormalModSession.ForSettingsProbe(mod, updateSystem, environment, report);
                            root.Start();
                        }
                        if (!applyAttempted && !root!.IsBusy()) ObserveUnconfiguredAndApply();
                        else if (applyAttempted && (cityOverview ? configuredMenu == null : servingStarted == 0)
                            && !root!.IsBusy()) ObserveActive();
                        if (cityOverview && configuredMenu != null) AdvanceCityRead();
                        else if (servingStarted != 0 && ElapsedSeconds(servingStarted) >= ServingSeconds)
                        {
                            var sample = Capture();
                            CheckCommon(sample, 1);
                            Check(Text(sample, "connectionPhase") == "Active" && Text(sample, "contextState") == "menu"
                                && Bool(sample, "contextStopped", false) && Bool(sample, "nativeLeaseRetained", false)
                                && Instance(sample) == Instance(active!) && Text(sample, "profileDraft") == profile
                                && Bool(sample, "cityChangesDraft", false), "NORMAL_CONNECTION_WINDOW_LOST_AUTHORITY");
                            servingCompleted = true;
                            stopping = true;
                        }
                    }
                }
                catch (CheckFailure error) { RecordFailure(error.Code, error.GetType().Name); stopping = true; }
                catch (CompositionRoundTrip.Rejection error) { RecordFailure(error.Code, error.GetType().Name); stopping = true; }
                catch (CompositionInitializationExpectation.Rejection error) { RecordFailure(error.Code, error.GetType().Name); stopping = true; }
                catch (PreviewCommitExecution.Rejection error) { RecordFailure(error.Code, error.GetType().Name); stopping = true; }
                catch (Exception error) { RecordFailure("NORMAL_CONNECTION_OBSERVATION_FAILED", error.GetType().Name); stopping = true; }
                if (stopping) AdvanceStop();
            }
        }

        private void ObserveUnconfiguredAndApply()
        {
            var sample = Capture();
            before = sample;
            CheckCommon(sample, 0);
            Check(Text(sample, "connectionPhase") != "Active", "NORMAL_CONNECTION_UNEXPECTED_INITIAL_AUTHORITY");
            Check(Bool(sample, "nativeLeaseRetained", false), "NORMAL_CONNECTION_INITIAL_LEASE_RETAINED");
            // StartAsync can finish before its failed initial lifetime's close.
            // Wait for that actual close rather than replacing a live owner.
            if (Text(sample, "connectionPhase") != "Stopped" || !Bool(sample, "connectionFullyReleased", true)) return;
            Check(Text(sample, "contextState") == "unavailable" && Bool(sample, "contextStopped", true) && Instance(sample) != null,
                "NORMAL_CONNECTION_INITIAL_CONTEXT_NOT_STOPPED");
            Check(Bool(sample, "settingsRegistryObserved", true) && Bool(sample, "settingsRegistered", true),
                "NORMAL_CONNECTION_SETTINGS_NOT_REGISTERED");
            // Mark before invoking: a failed observation must never replay an
            // action which may already have enqueued its single trusted request.
            applyAttempted = true;
            root!.ApplySettingsProbe(profile);
            applyObservedBusy = root.IsBusy();
            Check(applyObservedBusy, "NORMAL_CONNECTION_APPLY_NOT_BUSY");
        }

        private void ObserveActive()
        {
            var sample = Capture();
            CheckCommon(sample, 1);
            Check(Text(sample, "connectionPhase") == "Active" && Text(sample, "contextState") == "menu",
                "NORMAL_CONNECTION_NOT_ACTIVE_MENU");
            Check(Text(sample, "profileDraft") == profile && Bool(sample, "cityChangesDraft", false),
                "NORMAL_CONNECTION_SETTING_SELECTION_MISMATCH");
            // NativeLeaseRetained reports the emergency fence root left by an
            // incomplete close, not the ordinary active environment lease.
            Check(Bool(sample, "nativeLeaseRetained", false) && Bool(sample, "contextStopped", false),
                "NORMAL_CONNECTION_ACTIVE_LIFETIME_UNAVAILABLE");
            Check(Bool(sample, "settingsRegistryObserved", true) && Bool(sample, "settingsRegistered", true),
                "NORMAL_CONNECTION_ACTIVE_SETTINGS_UNAVAILABLE");
            string? instance = Instance(sample);
            Check(instance != null && instance != Instance(before!), "NORMAL_CONNECTION_CONTEXT_REUSED");
            Check(sample["contextGeneration"]?.Type == JTokenType.Integer && (long)sample["contextGeneration"]! >= 0,
                "NORMAL_CONNECTION_GENERATION_UNAVAILABLE");
            if (cityOverview) configuredMenu = sample;
            else { active = sample; servingStarted = Stopwatch.GetTimestamp(); }
        }

        private void AdvanceCityRead()
        {
            if (city == null) return;
            Check(city.FailureCode == null, "NORMAL_CITY_NATIVE_QUALIFICATION_FAILED");
            if (servingStarted == 0)
            {
                if (!city.QualifiedForRead || root!.IsBusy()) return;
                // This is the original protection deadline, not a renewed lease.
                // Leave ten seconds for the owned return-to-menu prefix.
                Check(city.CanHoldReadWindow(RequiredServingSeconds + 10), "NORMAL_CITY_READ_WINDOW_INSUFFICIENT");
                if (!ScopeSampleDue()) return;
                var sample = Capture();
                CheckConnectionIdentity(sample, configuredMenu!);
                // The observer is another updater. A qualified city may become
                // visible here on a later frame; never manufacture its context.
                if (Text(sample, "contextState") != "ready") return;
                CheckActiveScope(sample, "ready", configuredMenu!);
                Check(Generation(sample) > Generation(configuredMenu!), "NORMAL_CITY_LOADING_GENERATION_MISSING");
                if (parallelPreview)
                {
                    if (externalPreview == null)
                    {
                        var source = updateSystem.World;
                        Guid session = Game.PSI.Telemetry.GetCurrentSession();
                        Check(session != Guid.Empty && definitionScopeGuard == null, "EXTERNAL_PREVIEW_SCOPE_UNAVAILABLE");
                        definitionScopeGuard = BindDefinitionScopeGuard(sample, source, GameManager.instance, session);
                        externalPreview = new ParallelPreviewProbe(source, updateSystem, definitionScopeGuard);
                    }
                    externalPreview.Advance();
                    Check(externalPreview.FailureCode == null, externalPreview.FailureCode ?? "EXTERNAL_PREVIEW_FAILED");
                    if (!externalPreview.Passed) return;
                }
                else
                {
                    if (compositionRoundTrip == null)
                    {
                        objectInputSurvey = CaptureObjectInputSurvey(sample);
                        Check(city.CanHoldReadWindow(ServingSeconds + 10), "NORMAL_CITY_READ_WINDOW_INSUFFICIENT_AFTER_SURVEY");
                        compositionRoundTrip = BindCompositionRoundTrip(sample);
                    }
                    compositionRoundTrip.Advance();
                    if (!compositionRoundTrip.Passed) return;
                    Check(compositionRoundTrip.ResourcesReleased && compositionRoundTrip.WorkersFinished
                        && !compositionRoundTrip.NativeStateUncertain, "SELECTION_FIXTURE_CONSTRUCTION_NOT_RELEASED");
                    compositionRoundTrip.ReleaseInputSnapshot();
                    if (selectionFixture == null)
                    {
                        Check(city.CanHoldReadWindow(ServingSeconds + SelectionPublishSeconds + 10),
                            "NORMAL_CITY_READ_WINDOW_INSUFFICIENT_BEFORE_SELECTION");
                        var fixture = compositionRoundTrip.RequireOwnedRebuiltBuilding();
                        selectionFixture = new FixtureSelectionQualification(updateSystem.World, updateSystem,
                            definitionScopeGuard!, fixture.Root, fixture.Prefab);
                        selectionStarted = Stopwatch.GetTimestamp();
                        selectionFixture.Start();
                    }
                    selectionFixture.Observe();
                    Check(selectionFixture.FailureCode == null, selectionFixture.FailureCode ?? "SELECTION_FIXTURE_FAILED");
                    if (!selectionFixture.Published)
                    {
                        Check(ElapsedSeconds(selectionStarted) < SelectionPublishSeconds, "SELECTION_FIXTURE_PUBLICATION_TIMEOUT");
                        return;
                    }
                }
                Check(city.CanHoldReadWindow(RequiredServingSeconds + 10), "NORMAL_CITY_READ_WINDOW_INSUFFICIENT_AFTER_DEFINITION");
                active = sample;
                servingStarted = Stopwatch.GetTimestamp();
                return;
            }
            if (!servingCompleted)
            {
                Check(city.QualifiedForRead, "NORMAL_CITY_READ_QUALIFICATION_LOST");
                if (ElapsedSeconds(servingStarted) < RequiredServingSeconds)
                {
                    selectionFixture?.Observe();
                    Check(selectionFixture?.FailureCode == null, selectionFixture?.FailureCode ?? "SELECTION_FIXTURE_FAILED");
                    return;
                }
                var sample = Capture();
                CheckActiveScope(sample, "ready", active!);
                Check(Generation(sample) == Generation(active!), "NORMAL_CITY_READ_SCOPE_CHANGED");
                ReleaseOwnedSelection();
                Check(selectionFixture?.FailureCode == null, selectionFixture?.FailureCode ?? "SELECTION_FIXTURE_FAILED");
                servingCompleted = true;
                city.RequestReturnFromRead();
                return;
            }
            if (!city.ResourcesReleased) return;
            Check(city.ChecksPassed, "NORMAL_CITY_RETURN_NOT_QUALIFIED");
            if (menuObservationStarted == 0) menuObservationStarted = Stopwatch.GetTimestamp();
            if (!ScopeSampleDue()) return;
            var menuSample = Capture();
            CheckConnectionIdentity(menuSample, active!);
            if (Text(menuSample, "contextState") != "menu")
            {
                // No native responsibility remains, so a missing observer
                // result becomes a bounded failure rather than a permanent hold.
                Check(ElapsedSeconds(menuObservationStarted) < 30, "NORMAL_CITY_MENU_OBSERVATION_TIMEOUT");
                return;
            }
            CheckActiveScope(menuSample, "menu", active!);
            Check(Generation(menuSample) > Generation(active!), "NORMAL_CITY_RETURN_GENERATION_MISSING");
            Check(definitionScopeGuard != null, "OBJECT_EXECUTION_SCOPE_GUARD_UNAVAILABLE");
            try { definitionScopeGuard!(); }
            catch (CheckFailure error) when (error.Code == "OBJECT_EXECUTION_SCOPE_CHANGED") { staleGuardRejected = true; }
            Check(staleGuardRejected, "OBJECT_EXECUTION_STALE_SCOPE_ACCEPTED");
            definitionScopeGuard = null;
            returnedMenu = menuSample;
            stopping = true;
        }

        private JObject CaptureObjectInputSurvey(JObject scope)
        {
            var source = updateSystem.World;
            var manager = GameManager.instance;
            Check(source != null && source.IsCreated && manager != null &&
                manager.state == GameManager.State.WorldReady && manager.gameMode == GameMode.Game && !manager.isGameLoading,
                "OBJECT_INPUT_SURVEY_SOURCE_UNAVAILABLE");
            var prefabs = source!.GetExistingSystemManaged<PrefabSystem>();
            var configuration = source.GetExistingSystemManaged<CityConfigurationSystem>();
            Check(prefabs != null && configuration != null, "OBJECT_INPUT_SURVEY_DEPENDENCY_UNAVAILABLE");
            var timer = Stopwatch.StartNew();
            var patch = ObjectDefinitionPatchProbe.Observe();
            var surveys = new JArray();
            foreach (var purpose in new[] { ObjectDefinitionInputProbe.Purpose.Medical, ObjectDefinitionInputProbe.Purpose.Transformer,
                ObjectDefinitionInputProbe.Purpose.WaterTower, ObjectDefinitionInputProbe.Purpose.WindPower })
            {
                var reading = Stopwatch.StartNew();
                var input = ObjectDefinitionInputProbe.Select(source, prefabs!, configuration!, purpose);
                reading.Stop();
                Check(ReferenceEquals(input.Source, source), "OBJECT_INPUT_SURVEY_SOURCE_CHANGED");
                surveys.Add(InputSurveyReport(input, reading.Elapsed.TotalMilliseconds));
            }
            timer.Stop();
            var after = Capture();
            CheckActiveScope(after, "ready", scope);
            Check(Generation(after) == Generation(scope) && ReferenceEquals(updateSystem.World, source) &&
                ReferenceEquals(manager, GameManager.instance) && manager!.state == GameManager.State.WorldReady &&
                manager.gameMode == GameMode.Game && !manager.isGameLoading, "OBJECT_INPUT_SURVEY_SCOPE_CHANGED");
            return new JObject {
                ["purposes"] = surveys, ["readAndPatchObservationMilliseconds"] = timer.Elapsed.TotalMilliseconds,
                ["worldSequence"] = source.SequenceNumber.ToString(CultureInfo.InvariantCulture),
                ["scopeInstanceId"] = Instance(scope), ["scopeGeneration"] = Generation(scope),
                ["definitionKernelPatchObservation"] = new JObject {
                    ["qualified"] = patch.Qualified, ["reason"] = patch.Reason, ["methods"] = patch.MethodsChecked,
                    ["patchedMethods"] = patch.PatchedMethods, ["methodSetSha256"] = patch.MethodSetSha256,
                    ["harmonyMvid"] = patch.HarmonyMvid, ["fullCallGraphOrNativeDetourCoverage"] = false
                },
                ["producerCalled"] = false, ["playbackCalled"] = false,
                ["scope"] = "Development purpose-specific input survey; no unlock, construction or native definition execution."
            };
        }

        private static JObject InputSurveyReport(ObjectDefinitionInputProbe.Result input, double milliseconds)
        {
            var reasons = new JObject();
            int eligible = 0, closedGraphs = 0;
            ObjectDefinitionInputProbe.CandidateStatistics? selected = null;
            foreach (var candidate in input.Candidates)
            {
                if (candidate.Eligible) eligible++;
                if (candidate.InspectedReferencesComplete) closedGraphs++;
                if (candidate.FailureCode != null) reasons[candidate.FailureCode] = (int?)reasons[candidate.FailureCode] + 1 ?? 1;
                if (candidate.RootIndex == input.Root.Index && candidate.RootVersion == input.Root.Version) selected = candidate;
            }
            return new JObject {
                ["purpose"] = input.Purpose.ToString(), ["complete"] = input.Complete,
                ["failureCode"] = input.FailureCode == null ? JValue.CreateNull() : new JValue(input.FailureCode),
                ["rootCount"] = input.RootCount, ["reportedCandidates"] = input.Candidates.Count,
                ["eligibleForDefinitionProbe"] = eligible, ["closedInspectedReferenceGraphs"] = closedGraphs,
                ["readMilliseconds"] = milliseconds, ["rejections"] = reasons,
                ["selected"] = selected == null ? JValue.CreateNull() : (JToken)new JObject {
                    ["prefabIndex"] = selected.RootIndex, ["prefabVersion"] = selected.RootVersion,
                    ["patientCapacity"] = Value(selected.PatientCapacity), ["rootConstructionCost"] = selected.RootConstructionCost,
                    ["waterSourceTypesRaw"] = Value(selected.WaterSourceTypesRaw), ["waterCapacityRaw"] = Value(selected.WaterCapacityRaw),
                    ["waterPurificationRaw"] = Value(selected.WaterPurificationRaw),
                    ["windNominalProductionRaw"] = Value(selected.WindNominalProductionRaw),
                    ["windMaximumWindRaw"] = Value(selected.WindMaximumWindRaw),
                    ["basePowerPlantProductionRaw"] = Value(selected.BasePowerPlantProductionRaw),
                    ["capacityUnitConversionVerified"] = false,
                    ["lotWidth"] = selected.LotWidth, ["lotDepth"] = selected.LotDepth,
                    ["uniquePrefabs"] = selected.UniquePrefabs, ["bufferEntries"] = selected.BufferEntries,
                    ["rootSubNets"] = selected.RootSubNetCount, ["rootSubAreas"] = selected.RootSubAreaCount,
                    ["commandUpperBound"] = selected.CommandUpperBound,
                    ["areaOutputNodeUpperBound"] = selected.AreaOutputNodeUpperBound,
                    ["deferredContentReferences"] = selected.DeferredContentReferences
                }
            };
        }

        private static JToken Value(object? value) => value == null ? JValue.CreateNull() : new JValue(value);

        private CompositionRoundTrip BindCompositionRoundTrip(JObject scope)
        {
            var source = updateSystem.World;
            var manager = GameManager.instance;
            Check(source != null && source.IsCreated && manager != null && manager.state == GameManager.State.WorldReady
                && manager.gameMode == GameMode.Game && !manager.isGameLoading, "OBJECT_EXECUTION_SCOPE_UNAVAILABLE");
            Guid session = Game.PSI.Telemetry.GetCurrentSession();
            Check(session != Guid.Empty, "OBJECT_EXECUTION_SESSION_UNAVAILABLE");
            Check(definitionScopeGuard == null, "OBJECT_EXECUTION_SCOPE_GUARD_ALREADY_BOUND");
            var requireScope = BindDefinitionScopeGuard(scope, source!, manager!, session);
            definitionScopeGuard = requireScope;
            return new CompositionRoundTrip(source!, requireScope);
        }

        private Action BindDefinitionScopeGuard(JObject scope, World source, GameManager manager, Guid session)
        {
            string? instance = Instance(scope);
            Check(instance != null, "OBJECT_EXECUTION_SCOPE_UNAVAILABLE");
            var requireRuntimeScope = root!.BindRuntimeScopeProbe(environment, expectedVersion, profile, instance!, Generation(scope));
            return () =>
            {
                guardChecks++;
                try { requireRuntimeScope(); }
                catch (InvalidOperationException error) when (error.Message == NormalModSession.RuntimeScopeProbeChanged)
                { throw new CheckFailure("OBJECT_EXECUTION_SCOPE_CHANGED"); }
                Check(ReferenceEquals(updateSystem.World, source) && source.IsCreated
                    && ReferenceEquals(manager, GameManager.instance) && manager.state == GameManager.State.WorldReady
                    && manager.gameMode == GameMode.Game && !manager.isGameLoading
                    && Game.PSI.Telemetry.GetCurrentSession() == session, "OBJECT_EXECUTION_SCOPE_CHANGED");
            };
        }
        private void CheckActiveScope(JObject sample, string state, JObject previous)
        {
            CheckConnectionIdentity(sample, previous);
            Check(Text(sample, "contextState") == state, "NORMAL_CITY_CONNECTION_SCOPE_UNAVAILABLE");
        }

        private bool ScopeSampleDue()
        {
            long now = Stopwatch.GetTimestamp();
            if (now < nextScopeSampleTimestamp) return false;
            nextScopeSampleTimestamp = now + Stopwatch.Frequency / 4;
            return true;
        }

        private void CheckConnectionIdentity(JObject sample, JObject previous)
        {
            CheckCommon(sample, 1);
            Check(Text(sample, "connectionPhase") == "Active"
                && Bool(sample, "contextStopped", false) && Bool(sample, "nativeLeaseRetained", false)
                && Instance(sample) == Instance(previous) && Text(sample, "profileDraft") == profile
                && Bool(sample, "cityChangesDraft", false), "NORMAL_CITY_CONNECTION_SCOPE_UNAVAILABLE");
        }

        private static long Generation(JObject sample)
        {
            Check(sample["contextGeneration"]?.Type == JTokenType.Integer && (long)sample["contextGeneration"]! >= 0,
                "NORMAL_CONNECTION_GENERATION_UNAVAILABLE");
            return (long)sample["contextGeneration"]!;
        }

        private void AdvanceStop()
        {
            ReleaseOwnedSelection();
            compositionRoundTrip?.ReleaseInputSnapshot();
            externalPreview?.RequestStop();
            externalPreview?.Advance();
            if (SuppressAutomaticReturn) city?.SuspendAutomaticReturnForNativeUnknown();
            if (HoldingCityForPreviewCleanup) return;
            // Stop intent does not revoke the city's actual load/return owner.
            // Keep the ordinary connection alive until that owner truly releases.
            if (city != null && !city.ResourcesReleased && !SuppressAutomaticReturn)
            {
                if (city.QualifiedForRead) city.RequestReturnFromRead();
                return;
            }
            if (root == null) { resourcesReleased = true; return; }
            try
            {
                root.RequestStop();
                if (!root.IsFullyReleased) return;
                var sample = Capture();
                afterStop = sample;
                Check(Bool(sample, "settingsRegistryObserved", true) && Bool(sample, "settingsRegistered", false),
                    "NORMAL_CONNECTION_SETTINGS_REMOVAL_NOT_OBSERVED");
                Check(Bool(sample, "connectionFullyReleased", true) && Bool(sample, "nativeLeaseRetained", false),
                    "NORMAL_CONNECTION_RELEASE_NOT_OBSERVED");
                Check(Bool(sample, "contextStopped", true) && Text(sample, "contextState") == "unavailable",
                    "NORMAL_CONNECTION_CONTEXT_NOT_STOPPED");
                Check(sample["multilineWidgets"] is JObject widgets && widgets.Count == 0,
                    "NORMAL_CONNECTION_WIDGET_REMOVAL_NOT_OBSERVED");
                resourcesReleased = true;
                CheckCommon(sample, applyAttempted ? 1 : 0);
                if (applyAttempted)
                    Check(Text(sample, "profileDraft") == profile && Bool(sample, "cityChangesDraft", false),
                        "NORMAL_CONNECTION_STOPPED_SELECTION_MISMATCH");
            }
            catch (CheckFailure error) { RecordFailure(error.Code, error.GetType().Name); }
            catch (Exception error) { RecordFailure("NORMAL_CONNECTION_CLOSE_OBSERVATION_FAILED", error.GetType().Name); }
        }

        // Runs before AdvanceCity so its own deadline/stop cannot start the
        // menu transition ahead of this one main-thread selection cleanup.
        // It never extends the city's window or delays its safe return.
        internal void ReleaseSelectionBeforeCity(bool hostStopping)
        {
            RequireMainThread();
            lock (sync)
            {
                if (selectionFixture == null || selectionFixture.IntentEnded) return;
                selectionFixture.Observe();
                bool serviceDone = servingStarted != 0 && ElapsedSeconds(servingStarted) >= RequiredServingSeconds;
                bool publicationExpired = !selectionFixture.Published && ElapsedSeconds(selectionStarted) >= SelectionPublishSeconds;
                bool windowEnding = city == null || !city.CanHoldReadWindow(1);
                if (!hostStopping && !stopping && !serviceDone && !publicationExpired && !windowEnding
                    && selectionFixture.FailureCode == null) return;
                if (publicationExpired) RecordFailure("SELECTION_FIXTURE_PUBLICATION_TIMEOUT", null);
                if (windowEnding && !hostStopping && !stopping && !serviceDone)
                    RecordFailure("SELECTION_FIXTURE_CITY_WINDOW_ENDING", null);
                ReleaseOwnedSelection();
                if (selectionFixture.FailureCode != null) RecordFailure(selectionFixture.FailureCode, null);
                if (failureCode != null) stopping = true;
            }
        }

        private void ReleaseOwnedSelection()
        {
            selectionFixture?.ReleaseOwnedSelection();
            if (selectionFixture?.FailureCode is string code) RecordFailure(code, null);
        }

        private JObject Capture()
        {
            fullUICaptures++;
            var sample = root!.ReadSettingsProbe();
            sample["sampleFrame"] = UnityEngine.Time.frameCount;
            sample["sampledAtUtc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
            return sample;
        }

        private void CheckCommon(JObject sample, int accepted)
        {
            Check(Text(sample, "environmentId") == environment && Bool(sample, "preferencesRead", false),
                "NORMAL_CONNECTION_ISOLATION_MISMATCH");
            Check(Text(sample, "loadedVersion") == expectedVersion && Text(sample, "boundProductVersion") == expectedVersion,
                "NORMAL_CONNECTION_BOUND_VERSION_MISMATCH");
            Check(sample["acceptedConfigurationRequests"]?.Type == JTokenType.Integer
                && (long)sample["acceptedConfigurationRequests"]! == accepted, "NORMAL_CONNECTION_REQUEST_COUNT_MISMATCH");
        }

        internal JObject Read()
        {
            lock (sync)
            {
                var result = new JObject {
                    ["phase"] = PhaseName(),
                    ["checksPassed"] = Passed, ["resourcesReleased"] = ResourcesReleased, ["finished"] = Finished,
                    ["applySource"] = "ConnectionSettings.ApplyConnection", ["applyAttempted"] = applyAttempted,
                    ["applyObservedBusy"] = applyObservedBusy, ["servingWindowSeconds"] = RequiredServingSeconds,
                    ["servingWindowCompleted"] = servingCompleted, ["externalClientResultRequired"] = !parallelPreview,
                    ["guardChecks"] = guardChecks, ["fullUICaptures"] = fullUICaptures,
                    ["staleGuardRejected"] = staleGuardRejected,
                    ["before"] = ProjectSample(before),
                    ["active"] = ProjectSample(active),
                    ["configuredMenu"] = ProjectSample(configuredMenu),
                    ["returnedMenu"] = ProjectSample(returnedMenu),
                    ["objectInputSurvey"] = objectInputSurvey == null ? JValue.CreateNull() : objectInputSurvey.DeepClone(),
                    ["objectDefinition"] = (JToken?)compositionRoundTrip?.DefinitionSnapshot() ?? JValue.CreateNull(),
                    ["objectGeneration"] = (JToken?)compositionRoundTrip?.GenerationSnapshot() ?? JValue.CreateNull(),
                    ["compositionRoundTrip"] = compositionRoundTrip == null ? JValue.CreateNull() : compositionRoundTrip.Snapshot(),
                    ["selectionFixture"] = selectionFixture == null ? JValue.CreateNull() : selectionFixture.Snapshot(),
                    ["afterStop"] = ProjectSample(afterStop),
                    ["failureCode"] = failureCode == null ? JValue.CreateNull() : new JValue(failureCode),
                    ["exceptionType"] = exceptionType == null ? JValue.CreateNull() : new JValue(exceptionType)
                };
                if (parallelPreview) result["parallelPreview"] = externalPreview == null ? JValue.CreateNull() : externalPreview.Snapshot();
                return result;
            }
        }

        private JToken ProjectSample(JObject? sample)
        {
            if (sample == null) return JValue.CreateNull();
            if (!cityOverview) return sample.DeepClone();
            // Settings qualification retains the complete settings observation.
            // City runs project the lifecycle fields actually consumed here;
            // full UI observations remain at stage boundaries, not native guards.
            var result = new JObject();
            foreach (string field in new[] { "environmentId", "loadedVersion", "preferencesRead", "settingsRegistryObserved",
                "settingsRegistered", "acceptedConfigurationRequests", "connectionPhase", "connectionFailure", "connectionFullyReleased",
                "nativeLeaseRetained", "contextInstanceId", "contextGeneration", "contextState", "contextStopped",
                "boundProductVersion", "sampleFrame", "sampledAtUtc" })
                result[field] = sample[field]?.DeepClone() ?? JValue.CreateNull();
            result["projection"] = "connection-lifecycle";
            return result;
        }

        private string PhaseName()
        {
            if (resourcesReleased) return ResourcesReleased ? "Stopped" : "Unresolved";
            if (stopping) return "Stopping";
            if (cityOverview && configuredMenu != null)
                return servingCompleted ? "ReturningCity" : servingStarted != 0 ? "Serving" : city == null ? "AwaitingCity" : "LoadingCity";
            return servingStarted != 0 ? "Serving" : applyAttempted ? "Applying" : startAttempted ? "WaitingForUnconfigured" : "NotStarted";
        }

        private void RecordFailure(string code, string? type)
        {
            if (failureCode != null) return;
            failureCode = code;
            exceptionType = type == null || type.Length <= 160 ? type : type.Substring(0, 160);
        }

        private static string? Instance(JObject sample)
        {
            string? text = Text(sample, "contextInstanceId");
            return Guid.TryParseExact(text, "N", out var id) && id != Guid.Empty && id.ToString("N") == text ? text : null;
        }
        private static bool Bool(JObject sample, string name, bool value)
            => sample[name]?.Type == JTokenType.Boolean && (bool)sample[name]! == value;
        private static string? Text(JObject sample, string name)
            => sample[name]?.Type == JTokenType.String ? (string?)sample[name] : null;
        private static double ElapsedSeconds(long started) => (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
        private static void Check(bool condition, string code) { if (!condition) throw new CheckFailure(code); }
        private static void RequireMainThread()
        { if (!MainThreadDispatcher.isMainThread) throw new InvalidOperationException("The connection probe requires the main thread."); }
        private sealed class CheckFailure : Exception, INativeProbeFailure
        {
            string INativeProbeFailure.Code => Code;
            internal string Code { get; }
            internal CheckFailure(string code)
            { Code = code; }
        }
    }
}
#endif
