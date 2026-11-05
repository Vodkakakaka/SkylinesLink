#if SKYLINESLINK_INTEGRATION_PROBE
using System;
using System.Threading.Tasks;
using Colossal.Core;
using Colossal.IO.AssetDatabase;
using Colossal.Serialization.Entities;
using Game;
using Game.Assets;
using Game.City;
using Game.Common;
using Game.Prefabs.Modes;
using Game.SceneFlow;
using Game.Serialization;
using Game.Settings;
using Game.Simulation;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace SkylinesLink.Mod.Native
{
    // One public native new-game/return-menu cycle. No process start, city save,
    // persistent-setting write, private SDK entry point or native cancellation.
    internal sealed class TestCityLifecycleProbe
    {
        private const string GameMvid = "e15551e93dea48558dde1da3cea7d7ef";
        private const string EntitiesMvid = "3d6b23aeb28144a6a5f4cbd5fd3d75e6";
        private const string NormalMode = "NormalMode";

        private enum Phase { Created, QueueWaiting, Preparing, Loading, AwaitingCity, Qualifying, Returning, Restoring, Released, Unresolved }
        private enum HoldReason { None, InvocationUnknown, OwnershipUnproven, OwnershipLost, ReturnUncorrelated, RestoreBlocked, NativeOperationUnknown }
        private enum Outcome { Running, Passed, Stopped, Failed }
        private enum InvocationState { NotCalled, Invoking, Captured, Unknown }
        private enum AuditResult { Missing, Unchanged, Changed, ReadFailed }

        private readonly object sync = new object();
        private readonly UpdateSystem update;
        private readonly string metadataGuid, dataGuid;
        private readonly bool holdAfterQualification;
        private readonly LastSaveAudit lastSave = new LastSaveAudit();
        private OwnedValues? values;
        private Phase phase;
        private HoldReason holdReason;
        private Outcome outcome;
        private bool stopIntent;
        private string? failureCode, exceptionType;
        private QueueAdmission admission;
        private NativeInvocation load, menu;
        private StepTasks observed;
        private CallbackEvidence callback;
        private OwnedCity city;
        private MapMetadata? metadata;
        private AsyncReadDescriptor expectedDescriptor;
        private int visited, matches;
        private bool scanComplete, rawPlayable;
        private ReadyObservation? first, second;
        private ReadyObservation? latestHeldReady;
        private bool qualifiedForRead, returnFromReadRequested;
        private bool automaticReturnSuppressed;
        private int heldReadySamples;

        internal TestCityLifecycleProbe(UpdateSystem sourceUpdate, string metadataGuid, string dataGuid, bool holdAfterQualification = false)
        {
            update = sourceUpdate ?? throw new ArgumentNullException(nameof(sourceUpdate));
            this.metadataGuid = NormalizeGuid(metadataGuid);
            this.dataGuid = NormalizeGuid(dataGuid);
            this.holdAfterQualification = holdAfterQualification;
        }

        // Only ReleaseAfterRestore changes phase to Released. Audit outcome may
        // still change afterwards without resurrecting native responsibility.
        internal bool ResourcesReleased { get { lock (sync) return phase == Phase.Released; } }
        internal bool Finished { get { lock (sync) return phase == Phase.Released || automaticReturnSuppressed; } }
        internal bool QualifiedForRead { get { lock (sync) return qualifiedForRead; } }
        internal bool ChecksPassed { get { lock (sync) return phase == Phase.Released && outcome == Outcome.Passed && failureCode == null; } }
        internal string? FailureCode { get { lock (sync) return failureCode; } }
        internal string? ExceptionType { get { lock (sync) return exceptionType; } }

        // A query of the existing protection window, never a new reservation or
        // an implicit failure. The coordinator supplies its return allowance.
        internal bool CanHoldReadWindow(double seconds)
        {
            RequireMainThread();
            lock (sync)
                return !double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds > 0
                    && holdAfterQualification && qualifiedForRead && !returnFromReadRequested && !stopIntent
                    && outcome == Outcome.Running && phase == Phase.Qualifying && values != null
                    && values.CanHoldReadWindow(Now(), seconds);
        }

        // A normal end of the read hold is distinct from host cancellation. The
        // next Step still proves ownership before issuing the one menu return.
        internal void RequestReturnFromRead()
        {
            lock (sync)
            {
                Check(holdAfterQualification, "TEST_CITY_READ_HOLD_NOT_ENABLED");
                if (automaticReturnSuppressed || returnFromReadRequested || stopIntent || outcome == Outcome.Failed || phase == Phase.Unresolved
                    || phase == Phase.Returning || phase == Phase.Restoring || phase == Phase.Released) return;
                Check(phase == Phase.Qualifying && second != null, "TEST_CITY_READ_HOLD_NOT_READY");
                returnFromReadRequested = true;
                qualifiedForRead = false;
            }
        }

        // End this diagnostic's automatic load/return work without claiming that
        // an unjoined native producer or the city's runtime values were released.
        // This is not a hook or veto on player Quit/Preload.
        internal void SuspendAutomaticReturnForNativeUnknown()
        {
            RequireMainThread();
            lock (sync)
            {
                if (automaticReturnSuppressed) return;
                automaticReturnSuppressed = true;
                qualifiedForRead = false;
                Hold(HoldReason.NativeOperationUnknown, "TEST_CITY_NATIVE_OPERATION_UNKNOWN");
                try { values?.DetachDiagnosticObserver(); }
                catch (Exception error) { Fail("TEST_CITY_OBSERVER_REMOVAL_UNKNOWN", error); }
            }
        }

        internal void Step(bool stopRequested)
        {
            RequireMainThread();
            lock (sync)
            {
                if (automaticReturnSuppressed) return;
                qualifiedForRead = false;
                stopIntent |= stopRequested;
                // One immutable conclusion per handle for this entire step.
                // A handle issued by this step is first observed next step.
                observed = new StepTasks(ProbeTaskObservation.Capture(admission.Handle),
                    ProbeTaskObservation.Capture(load.Handle), ProbeTaskObservation.Capture(menu.Handle));
                if (values != null && phase != Phase.Released) ObserveRuntimeBoundary();
                switch (phase)
                {
                    case Phase.Created: Start(); break;
                    case Phase.QueueWaiting: ObserveAdmission(); break;
                    case Phase.Preparing: break; // Synchronous queue delegate owns this prefix.
                    case Phase.Loading: ObserveLoad(observed); break;
                    case Phase.AwaitingCity: EstablishCity(observed); break;
                    case Phase.Qualifying: Qualify(observed); break;
                    case Phase.Returning: ObserveReturn(observed); break;
                    case Phase.Restoring: Restore(observed); break;
                    case Phase.Released: break; // The trailing audit remains live until host closure.
                    case Phase.Unresolved: break; // Keep observations; never replay or drain a hold.
                }
                // The published audit includes synchronous effects of this
                // step. Effect/qualification boundaries also sample it locally.
                if (qualifiedForRead) ObserveRuntimeBoundary();
                if (values != null) ObserveAudit();
                if (stopIntent || outcome != Outcome.Running || phase != Phase.Qualifying || returnFromReadRequested)
                    qualifiedForRead = false;
            }
        }

        private void Start()
        {
            if (stopIntent) { ReleaseAfterRestore(observed); return; }
            try
            {
                Check(typeof(GameManager).Assembly.ManifestModule.ModuleVersionId.ToString("N") == GameMvid
                    && typeof(World).Assembly.ManifestModule.ModuleVersionId.ToString("N") == EntitiesMvid, "NATIVE_BUILD_UNQUALIFIED");
                values = OwnedValues.Capture(update, OnLoaded);
                Check(values.ReadyMenu, "TEST_CITY_MENU_REQUIRED");
                ObserveAudit(capture: true);
                Check(lastSave.Result == AuditResult.Unchanged, "TEST_CITY_LAST_SAVE_BASELINE_UNAVAILABLE");
                admission.Attempted = true;
                phase = Phase.QueueWaiting;
                // Enqueue may enter synchronously. Its delegate alone advances
                // Preparing/Loading; returning the queue handle must not undo it.
                admission.Handle = Colossal.TaskManager.instance.EnqueueTask(SaveHelpers.kSaveLoadTaskName, EnterLoad, cap: 1);
                Check(admission.Handle != null, "TEST_CITY_QUEUE_TASK_MISSING");
            }
            catch (Exception error)
            {
                Fail(Code(error, "TEST_CITY_QUEUE_ENTRY_FAILED"), error);
                if (load.State == InvocationState.Unknown || load.State == InvocationState.Invoking)
                    Hold(HoldReason.InvocationUnknown, "TEST_CITY_LOAD_INVOCATION_UNKNOWN");
                else if (load.Attempted) phase = Phase.Loading;
                else if (admission.Attempted && admission.Handle == null)
                    Hold(HoldReason.InvocationUnknown, "TEST_CITY_QUEUE_RECEIPT_UNKNOWN");
                else phase = Phase.Restoring;
            }
        }

        private Task EnterLoad()
        {
            lock (sync)
            {
                bool duplicate = admission.Entered;
                admission.Entered = true;
                bool onMain = MainThreadDispatcher.isMainThread;
                if (!duplicate) admission.OnMain = onMain;
                try
                {
                    Check(!duplicate, "TEST_CITY_DELEGATE_REENTERED");
                    Check(onMain, "TEST_CITY_DELEGATE_THREAD_REJECTED");
                    phase = Phase.Preparing;
                    OwnedValues owned = values!;
                    owned.RequireSource(); Check(owned.ReadyMenu, "TEST_CITY_DELEGATE_MENU_CHANGED");
                    Check(!stopIntent, "TEST_CITY_STOPPED_BEFORE_LOAD");
                    ResolveMap(owned.Manager);
                    ObserveAudit();
                    Check(lastSave.Result == AuditResult.Unchanged, "TEST_CITY_LAST_SAVE_BASELINE_UNAVAILABLE");
                    city.PreviousSession = Game.PSI.Telemetry.GetCurrentSession();
                    owned.Prepare(metadata!.target.startingYear);
                    owned.ArmWindow(Now());
                }
                catch (Exception error)
                {
                    Fail(Code(error, "TEST_CITY_PREPARATION_FAILED"), error);
                    if (!load.Attempted) phase = Phase.Restoring;
                    return Task.CompletedTask;
                }

                load.Begin(); phase = Phase.Loading;
                try
                {
                    load.Receive(values!.Manager.Load(GameMode.Game, Purpose.NewGame, metadata!));
                    if (load.State == InvocationState.Unknown)
                        Hold(HoldReason.InvocationUnknown, "TEST_CITY_NATIVE_TASK_MISSING");
                }
                catch (Exception error)
                {
                    load.MissingReceipt();
                    Hold(HoldReason.InvocationUnknown, "TEST_CITY_LOAD_INVOCATION_UNKNOWN", error);
                }
                return (Task?)load.Handle ?? Task.CompletedTask;
            }
        }

        private void ObserveAdmission()
        {
            if (!observed.Queue.IsTerminal) return;
            if (!admission.Entered) Fail("TEST_CITY_QUEUE_NOT_ENTERED");
            if (observed.Queue.State != ProbeTaskState.Completed) Fail("TEST_CITY_QUEUE_FAILED");
            // A delegate which did enter has already chosen its next phase.
            if (!load.Attempted) phase = Phase.Restoring;
        }

        private void ObserveLoad(StepTasks tasks)
        {
            if (load.State == InvocationState.Unknown || load.State == InvocationState.Invoking || tasks.Load.State == ProbeTaskState.Missing)
            { Hold(HoldReason.InvocationUnknown, "TEST_CITY_LOAD_INVOCATION_UNKNOWN"); return; }
            // An unknown queue receipt cannot be replaced by native completion.
            // Still observe a known pending native handle before holding it.
            if (!tasks.Load.IsTerminal) return;
            if (tasks.Queue.State == ProbeTaskState.Missing)
            { Hold(HoldReason.InvocationUnknown, "TEST_CITY_QUEUE_RECEIPT_UNKNOWN"); return; }
            if (!tasks.Queue.IsTerminal) return;
            if (tasks.Queue.State != ProbeTaskState.Completed) Fail("TEST_CITY_QUEUE_FAILED");
            if (!tasks.Load.HasTrueResult) Fail(LoadFailure(tasks.Load));
            phase = Phase.AwaitingCity;
        }

        private void EstablishCity(StepTasks tasks)
        {
            if (!ConfirmOwnedCity(tasks)) return;
            phase = Phase.Qualifying;
        }

        private void Qualify(StepTasks tasks)
        {
            if (!ConfirmOwnedCity(tasks)) return;
            if (stopIntent || outcome == Outcome.Failed || (holdAfterQualification && returnFromReadRequested))
            { BeginReturn(tasks); return; }
            try
            {
                Check(callback.DescriptorValid && callback.DescriptorMatch, "TEST_CITY_MAP_DESCRIPTOR_UNQUALIFIED");
                OwnedValues owned = values!;
                // This pending mode is consumed only by ordinary native frames.
                if (second == null && owned.Modes.overrideMode == NormalMode) return;
                double now = Now();
                if (first != null && second == null && now - first.At < 0.5) return;
                ReadyObservation sample = CaptureReady(owned, now);
                if (first == null) { first = sample; return; }
                Check(sample.Session == first.Session, "TEST_CITY_SESSION_CHANGED");
                if (second == null) second = sample;
                if (holdAfterQualification)
                {
                    // Keep the initial qualification evidence unchanged. Only
                    // one latest sample is retained while checking every Step.
                    latestHeldReady = sample;
                    if (heldReadySamples < int.MaxValue) heldReadySamples++;
                    qualifiedForRead = true;
                    return;
                }
            }
            catch (Exception error) { Fail(Code(error, "TEST_CITY_QUALIFICATION_FAILED"), error); }
            // Qualification failure is not lost ownership. The effect point
            // independently rechecks that this is still our actual city.
            BeginReturn(tasks);
        }

        private bool ConfirmOwnedCity(StepTasks tasks)
        {
            if (city.Lost) { Hold(HoldReason.OwnershipLost, "TEST_CITY_OWNERSHIP_LOST"); return false; }
            if (!tasks.Queue.IsTerminal || !tasks.Load.IsTerminal)
            { phase = Phase.Loading; return false; }
            if (!tasks.Load.HasTrueResult)
            {
                // False/fault/cancel does not prove no native effects. The
                // callback has no session token to claim a matching foreign city.
                Hold(HoldReason.OwnershipUnproven, "TEST_CITY_OWNERSHIP_UNPROVEN"); return false;
            }
            try
            {
                OwnedValues owned = values!;
                owned.RequireSource();
                var context = owned.Loader.context;
                Guid session = Game.PSI.Telemetry.GetCurrentSession();
                Check(callback.Count == 1 && callback.OnMain && callback.Epoch && callback.ContextMatch
                    && context.purpose == Purpose.NewGame
                    && string.Equals(context.instigatorGuid.ToString(), metadataGuid, StringComparison.OrdinalIgnoreCase)
                    && session != Guid.Empty && session != city.PreviousSession
                    && owned.Manager.gameMode == GameMode.Game && owned.Manager.state == GameManager.State.WorldReady
                    && !owned.Manager.isGameLoading, "TEST_CITY_OWNERSHIP_LOST");
                Check(city.Session == Guid.Empty || city.Session == session, "TEST_CITY_SESSION_NOT_OWNED");
                if (city.Session == Guid.Empty) city.Session = session;
                return true;
            }
            catch (Exception error)
            {
                city.Lost = true;
                Hold(HoldReason.OwnershipLost, Code(error, "TEST_CITY_OWNERSHIP_LOST"), error); return false;
            }
        }

        private void BeginReturn(StepTasks tasks)
        {
            if (!ConfirmOwnedCity(tasks)) return;
            try
            {
                Check(tasks.Queue.IsTerminal && tasks.Load.IsTerminal && !menu.Attempted, "TEST_CITY_RETURN_NOT_QUIESCENT");
                OwnedValues owned = values!;
                // isGameLoading is false while quitting/terminated too.
                owned.RequireSource();
                Check(owned.Manager.state == GameManager.State.WorldReady && !owned.Manager.isGameLoading,
                    "TEST_CITY_RETURN_NOT_WORLD_READY");
                ObserveAudit(); // Audit failure stops qualification, never this safe drain.
                owned.PrepareReturn();
            }
            catch (Exception error)
            { Hold(HoldReason.RestoreBlocked, Code(error, "TEST_CITY_RETURN_PREPARATION_BLOCKED"), error); return; }
            menu.Begin(); phase = Phase.Returning;
            try
            {
                menu.Receive(values!.Manager.MainMenu());
                if (menu.State == InvocationState.Unknown)
                    Hold(HoldReason.InvocationUnknown, "TEST_CITY_MENU_TASK_MISSING");
            }
            catch (Exception error)
            {
                menu.MissingReceipt();
                Hold(HoldReason.InvocationUnknown, "TEST_CITY_RETURN_INVOCATION_UNKNOWN", error);
            }
            // This observation is separate from acquiring the native receipt.
            // Its failure must never relabel a captured task as unknown work.
            if (menu.State == InvocationState.Captured) ObserveNativeEnvironment(values!);
        }

        private void ObserveReturn(StepTasks tasks)
        {
            if (menu.State == InvocationState.Unknown || menu.State == InvocationState.Invoking || tasks.Menu.State == ProbeTaskState.Missing)
            { Hold(HoldReason.InvocationUnknown, "TEST_CITY_RETURN_INVOCATION_UNKNOWN"); return; }
            if (!tasks.Menu.IsTerminal) return;
            if (!tasks.Menu.HasTrueResult)
            {
                // Menu load may have completed before audio failed, but all
                // Cleanup instigators are Empty. There is no unique public epoch
                // which excludes a later external Cleanup. Do not drain it.
                Hold(HoldReason.ReturnUncorrelated, "TEST_CITY_RETURN_UNCORRELATED"); return;
            }
            if (city.Lost) { Hold(HoldReason.OwnershipLost, "TEST_CITY_RETURN_OWNERSHIP_LOST"); return; }
            try
            {
                values!.RequireSource(); Check(values.ReadyMenu, "TEST_CITY_RETURN_NOT_MENU");
                phase = Phase.Restoring;
            }
            catch (Exception error)
            {
                city.Lost = true;
                Hold(HoldReason.OwnershipLost, Code(error, "TEST_CITY_RETURN_OWNERSHIP_LOST"), error);
            }
        }

        private void Restore(StepTasks tasks)
        {
            if (admission.Attempted && !tasks.Queue.IsTerminal)
            {
                if (tasks.Queue.State == ProbeTaskState.Missing) Hold(HoldReason.InvocationUnknown, "TEST_CITY_QUEUE_RECEIPT_UNKNOWN");
                return;
            }
            if (load.Attempted && (!menu.Attempted || !tasks.Menu.HasTrueResult || city.Lost))
            { Hold(HoldReason.OwnershipLost, "TEST_CITY_RESTORE_NOT_OWNED"); return; }
            try
            {
                if (values != null && values.HoldsResources)
                {
                    values.RequireSource(); Check(values.ReadyMenu, "TEST_CITY_RESTORE_NOT_MENU");
                    ObserveAudit(); // Never write back the audited UserState.
                    values.RestoreInMenu();
                }
                ReleaseAfterRestore(tasks);
            }
            catch (Exception error)
            { Hold(HoldReason.RestoreBlocked, Code(error, "TEST_CITY_RESTORE_BLOCKED"), error); }
        }

        private void ReleaseAfterRestore(StepTasks tasks)
        {
            Check(values == null || !values.HoldsResources, "TEST_CITY_OWNED_VALUES_RETAINED");
            Check(!load.Attempted || (tasks.Queue.IsTerminal && tasks.Load.IsTerminal && tasks.Menu.HasTrueResult && !city.Lost),
                "TEST_CITY_NATIVE_RESPONSIBILITY_RETAINED");
            phase = Phase.Released; holdReason = HoldReason.None;
            if (outcome != Outcome.Failed)
            {
                bool qualified = second != null && tasks.Menu.HasTrueResult;
                if (holdAfterQualification) qualified &= returnFromReadRequested && !stopIntent;
                outcome = qualified ? Outcome.Passed : Outcome.Stopped;
            }
        }

        private void ResolveMap(GameManager manager)
        {
            using (var entries = AssetDatabase.global.GetAssets<Metadata<MapInfo>>().GetEnumerator())
            {
                while (visited < 128)
                {
                    if (!entries.MoveNext()) { scanComplete = true; break; }
                    visited++;
                    var entry = entries.Current;
                    if (entry == null || !string.Equals(entry.id.guid.ToString(), metadataGuid, StringComparison.OrdinalIgnoreCase)) continue;
                    matches++;
                    Check(matches == 1 && entry.GetType() == typeof(MapMetadata) && entry.isBuiltin && entry.isValid
                        && entry.state == LoadState.Full, "TEST_CITY_MAP_AMBIGUOUS_OR_UNQUALIFIED");
                    metadata = (MapMetadata)entry;
                    // Full target may still PostLoad/hydrate/cache and do metadata IO.
                    var info = metadata.target;
                    Check(info != null && ReferenceEquals(info.metaData, metadata), "TEST_CITY_MAP_NOT_HYDRATED");
                    var data = info!.mapData;
                    Check(data != null && data.isBuiltin && data.isValid
                        && string.Equals(data.id.guid.ToString(), dataGuid, StringComparison.OrdinalIgnoreCase), "TEST_CITY_MAP_DATA_MISMATCH");
                    string[]? prerequisites = info.contentPrerequisites;
                    Check(BoundedPrerequisites(prerequisites) && manager.ArePrerequisitesMet(prerequisites!), "TEST_CITY_MAP_PREREQUISITES");
                    rawPlayable = info.isPlayable; // Only the native custom branch vetoes this flag.
                    expectedDescriptor = data!.GetAsyncReadDescriptor();
                    Check(DescriptorShape(expectedDescriptor), "TEST_CITY_MAP_DESCRIPTOR_INVALID");
                }
            }
            Check(scanComplete && matches == 1 && metadata != null, "TEST_CITY_MAP_SCAN_INCOMPLETE_OR_MISSING");
        }

        private void OnLoaded(Context context)
        {
            // This event must never throw: native completion follows it. Context
            // is a borrowed struct; retain no Context, setter, Dispose or mapping.
            try
            {
                lock (sync)
                {
                    if (automaticReturnSuppressed) return;
                    ProbeTaskObservation atEvent = ProbeTaskObservation.Capture(load.Handle);
                    callback.Count = Math.Min(3, callback.Count + 1);
                    callback.OnMain = MainThreadDispatcher.isMainThread;
                    callback.Epoch = !menu.Attempted && (load.State == InvocationState.Invoking
                        || (load.State == InvocationState.Captured && atEvent.State == ProbeTaskState.Pending));
                    callback.ContextMatch = callback.Epoch && context.purpose == Purpose.NewGame
                        && string.Equals(context.instigatorGuid.ToString(), metadataGuid, StringComparison.OrdinalIgnoreCase);
                    if (!callback.OnMain || values == null) return;
                    AsyncReadDescriptor descriptor = values.Loader.dataDescriptor;
                    callback.DescriptorValid = DescriptorShape(descriptor);
                    callback.DescriptorMatch = descriptor.Equals(expectedDescriptor) && descriptor.packaged == expectedDescriptor.packaged;
                }
            }
            catch (Exception error)
            {
                try { lock (sync) Fail("TEST_CITY_OBSERVER_FAILED", error); }
                catch { /* Reporting cannot become a native completion failure. */ }
            }
        }

        private ReadyObservation CaptureReady(OwnedValues owned, double now)
        {
            owned.RequireSource(); ObserveAudit();
            Check(lastSave.Result == AuditResult.Unchanged, "TEST_CITY_LAST_SAVE_NOT_UNCHANGED");
            var context = owned.Loader.context;
            Check(context.purpose == Purpose.NewGame && string.Equals(context.instigatorGuid.ToString(), metadataGuid, StringComparison.OrdinalIgnoreCase),
                "TEST_CITY_CURRENT_CONTEXT_MISMATCH");
            Guid session = Game.PSI.Telemetry.GetCurrentSession();
            Check(session != Guid.Empty && session == city.Session && session != city.PreviousSession, "TEST_CITY_SESSION_NOT_OWNED");
            Check(owned.Manager.state == GameManager.State.WorldReady && owned.Manager.gameMode == GameMode.Game && !owned.Manager.isGameLoading,
                "TEST_CITY_NOT_READY");
            Check(owned.Modes.currentModeName == NormalMode && owned.Modes.overrideMode == null && !owned.Config.overrideLoadedOptions,
                "TEST_CITY_NORMAL_MODE_NOT_CONSUMED");
            Check(!owned.Config.unlockAll && !owned.Config.unlimitedMoney && !owned.Config.unlockMapTiles, "TEST_CITY_EFFECTIVE_CHEATS_ENABLED");
            Check(owned.Config.leftHandTraffic == owned.Traffic && owned.Config.naturalDisasters == owned.Disasters, "TEST_CITY_EFFECTIVE_OPTIONS_MISMATCH");
            int? observedStartingYear = owned.ObserveTimeDataStartingYear();
            var sample = new ReadyObservation(session.ToString("N"), now,
                (int)Math.Min(int.MaxValue, Math.Max(0, (now - owned.LoadStarted) * 1000)), owned.Config.leftHandTraffic,
                owned.Config.naturalDisasters, owned.ExpectedStartingYear, observedStartingYear);
            Check(sample.StartingYearMatches, "TEST_CITY_TIME_DATA_STARTING_YEAR_MISMATCH");
            return sample;
        }

        private void ObserveRuntimeBoundary()
        {
            OwnedValues owned = values!;
            try { owned.RequireSource(); }
            catch (Exception error)
            { if (load.Attempted) city.Lost = true; Fail(Code(error, "TEST_CITY_SOURCE_CHANGED"), error); return; }
            if (load.Attempted) ObserveNativeEnvironment(owned);
            if (!owned.HoldsResources) return;
            try
            {
                owned.ObserveConsumedIntent();
                if (!owned.ObserveStartingYearOwnership()) Fail("TEST_CITY_STARTING_YEAR_INTENT_CHANGED");
                if (!owned.ObserveGeneral()) Fail("TEST_CITY_GENERAL_CHANGED");
                if (owned.Autosave.Enabled) Fail("TEST_CITY_AUTOSAVE_GATE_LOST");
                if (owned.ObserveWindow(Now())) Fail("TEST_CITY_WINDOW_EXPIRED");
            }
            catch (Exception error) { Fail("TEST_CITY_RUNTIME_OBSERVATION_FAILED", error); }
        }

        private void ObserveNativeEnvironment(OwnedValues owned)
        {
            if (city.Lost) return;
            try
            {
                owned.RequireSource();
                GameManager.State state = owned.Manager.state;
                Check(state != GameManager.State.Quitting && state != GameManager.State.Terminated,
                    "TEST_CITY_OWNERSHIP_LOST");
                if (!menu.Attempted) return;
                GameMode mode = owned.Manager.gameMode;
                if (mode == GameMode.MainMenu)
                {
                    // Normal Cleanup/CloseSession may change context/session.
                    // Seeing a menu is not a unique return receipt or release.
                    city.ReturnMenuObserved = true; return;
                }
                if (mode != GameMode.Game) return;
                Check(!city.ReturnMenuObserved, "TEST_CITY_RETURN_FOREIGN_GAME");
                // Do not guess the identity of a loading intermediate state.
                // A stable Game before the menu prefix must still be our city.
                if (state != GameManager.State.WorldReady || owned.Manager.isGameLoading) return;
                var context = owned.Loader.context;
                Check(context.purpose == Purpose.NewGame
                    && string.Equals(context.instigatorGuid.ToString(), metadataGuid, StringComparison.OrdinalIgnoreCase)
                    && city.Session != Guid.Empty && Game.PSI.Telemetry.GetCurrentSession() == city.Session,
                    "TEST_CITY_RETURN_FOREIGN_GAME");
            }
            catch (Exception error)
            {
                city.Lost = true;
                Fail(Code(error, "TEST_CITY_NATIVE_ENVIRONMENT_UNPROVEN"), error);
            }
        }

        private void ObserveAudit(bool capture = false)
        {
            lastSave.Observe(values?.Manager, capture);
            switch (lastSave.Result)
            {
                case AuditResult.Changed: Fail("TEST_CITY_LAST_SAVE_CHANGED"); break;
                case AuditResult.ReadFailed: Fail("TEST_CITY_LAST_SAVE_OBSERVATION_FAILED", lastSave.ExceptionType); break;
            }
        }

        private void Hold(HoldReason reason, string code, Exception? error = null)
        { Fail(code, error); holdReason = reason; phase = Phase.Unresolved; }
        private void Fail(string code, Exception? error = null) => Fail(code, error == null ? null : ExceptionName(error));
        private void Fail(string code, string? errorType)
        {
            outcome = Outcome.Failed; stopIntent = true; qualifiedForRead = false;
            if (failureCode != null) return;
            failureCode = code; exceptionType = errorType;
        }

        // Called on the main thread after Step. Every returned tree is new and
        // contains only ordinary values. The producer never mutates it later.
        internal JObject CaptureSnapshot()
        {
            RequireMainThread();
            lock (sync)
            {
                var samples = new JArray(); if (first != null) samples.Add(first.Project()); if (second != null) samples.Add(second.Project());
                string ownership = city.Lost ? "lost" : city.Session == Guid.Empty ? "unestablished"
                    : phase == Phase.Released ? "released" : menu.Attempted ? "returning" : "owned";
                var snapshot = new JObject { ["kind"] = "testCity", ["phase"] = Label(phase), ["outcome"] = Label(outcome),
                    ["holdReason"] = Label(holdReason), ["passed"] = outcome == Outcome.Passed,
                    ["resourcesReleased"] = phase == Phase.Released, ["finished"] = Finished,
                    ["automaticReturnSuppressed"] = automaticReturnSuppressed, ["stopRequested"] = stopIntent,
                    ["metadataGuid"] = metadataGuid, ["dataGuid"] = dataGuid, ["ownership"] = ownership,
                    ["ownSession"] = city.Session == Guid.Empty ? JValue.CreateNull() : new JValue(city.Session.ToString("N")),
                    ["scan"] = new JObject { ["visited"] = visited, ["complete"] = scanComplete, ["matches"] = matches, ["rawPlayable"] = rawPlayable },
                    ["taskSample"] = "stepStart", ["queue"] = observed.Queue.StatusText, ["entered"] = admission.Entered, ["enteredOnMain"] = admission.OnMain,
                    ["loadInvocation"] = Label(load.State), ["load"] = observed.Load.StatusText,
                    ["callbackCount"] = callback.Count, ["callbackMatch"] = callback.OnMain && callback.Epoch && callback.ContextMatch,
                    ["descriptorValid"] = callback.DescriptorValid, ["descriptorMatch"] = callback.DescriptorMatch,
                    ["sourceStable"] = values?.SourceStable ?? false, ["ready"] = samples,
                    ["menuInvocation"] = Label(menu.State), ["menu"] = observed.Menu.StatusText,
                    ["runtimeValuesHeld"] = values?.RuntimeHeld ?? false, ["observerHeld"] = values?.ObserverHeld ?? false,
                    ["generalStable"] = values?.GeneralStable ?? false, ["windowExpired"] = values?.WindowExpired ?? false,
                    ["startingYearInitialization"] = values == null ? JValue.CreateNull() : values.StartingYearSnapshot(),
                    ["lastSave"] = lastSave.Project(), ["failureCode"] = Value(failureCode), ["exceptionType"] = Value(exceptionType),
                    ["scope"] = "metadata IO/cache; no permanent city ID/content/finance/collision proof; timer gate excludes queued/explicit saves and General-event pruning" };
                if (holdAfterQualification)
                    snapshot["readHold"] = new JObject { ["qualifiedThisStep"] = qualifiedForRead,
                        ["returnRequested"] = returnFromReadRequested, ["sampleCount"] = heldReadySamples,
                        ["latest"] = latestHeldReady == null ? JValue.CreateNull() : latestHeldReady.Project() };
                return snapshot;
            }
        }

        private readonly struct StepTasks
        {
            internal StepTasks(ProbeTaskObservation queue, ProbeTaskObservation load, ProbeTaskObservation menu)
            { Queue = queue; Load = load; Menu = menu; }
            internal ProbeTaskObservation Queue { get; }
            internal ProbeTaskObservation Load { get; }
            internal ProbeTaskObservation Menu { get; }
        }
        private struct QueueAdmission { internal Task? Handle; internal bool Attempted, Entered, OnMain; }
        private struct NativeInvocation
        {
            internal InvocationState State;
            internal Task<bool>? Handle;
            internal bool Attempted => State != InvocationState.NotCalled;
            internal void Begin() { Check(!Attempted, "TEST_CITY_NATIVE_CALL_REENTERED"); State = InvocationState.Invoking; }
            internal void Receive(Task<bool>? handle) { Handle = handle; State = handle == null ? InvocationState.Unknown : InvocationState.Captured; }
            internal void MissingReceipt() { if (State == InvocationState.Invoking) State = InvocationState.Unknown; }
        }
        private struct OwnedCity { internal Guid PreviousSession, Session; internal bool Lost, ReturnMenuObserved; }
        private struct CallbackEvidence { internal int Count; internal bool OnMain, Epoch, ContextMatch, DescriptorValid, DescriptorMatch; }

        // Concrete ownership of this experiment's existing systems and scalar
        // changes. There is no automatic Dispose, generic lease or reflection.
        private sealed class OwnedValues
        {
            private readonly UpdateSystem update;
            private readonly World source;
            private readonly LoadGameSystem.EventGameLoaded observer;
            // Borrow the existing TimeSystem's exact read-only query. This
            // probe never changes its filter or disposes this system-owned handle.
            private EntityQuery timeDataQuery;
            private bool timeDataQueryBound, startingYearOwned, startingYearSet, startingYearRestored, startingYearChanged;
            private int previousStartingYear;
            private int? mapStartingYear, expectedStartingYear, observedTimeDataStartingYear;
            private bool timeDataSingletonObserved;
            private GeneralSettings? general;
            private bool generalAutosave, previousAutosave, previousTraffic, previousDisasters, previousUnlockAll, previousMoney, previousTiles;
            private int generalInterval, generalCount;
            private bool loadedOptionsOwned, modeOwned, windowArmed;
            private double deadline;
            private OwnedValues(UpdateSystem update, World source, GameManager manager, LoadGameSystem loader,
                CityConfigurationSystem config, GameModeSystem modes, AutoSaveSystem autosave, TimeSystem time,
                LoadGameSystem.EventGameLoaded observer)
            { this.update = update; this.source = source; Manager = manager; Loader = loader; Config = config; Modes = modes; Autosave = autosave; Time = time; this.observer = observer; }
            internal GameManager Manager { get; }
            internal LoadGameSystem Loader { get; }
            internal CityConfigurationSystem Config { get; }
            internal GameModeSystem Modes { get; }
            internal AutoSaveSystem Autosave { get; }
            private TimeSystem Time { get; }
            internal int ExpectedStartingYear => expectedStartingYear ?? throw new CheckFailure("TEST_CITY_STARTING_YEAR_NOT_PREPARED");
            internal bool SourceStable { get; private set; }
            internal bool GeneralStable { get; private set; }
            internal bool RuntimeHeld { get; private set; }
            internal bool ObserverHeld { get; private set; }
            internal bool WindowExpired { get; private set; }
            internal bool Traffic { get; private set; }
            internal bool Disasters { get; private set; }
            internal double LoadStarted { get; private set; }
            internal bool HoldsResources => RuntimeHeld || ObserverHeld || loadedOptionsOwned || modeOwned || startingYearOwned;
            internal bool ReadyMenu => Manager.state == GameManager.State.WorldReady && Manager.gameMode == GameMode.MainMenu && !Manager.isGameLoading;

            internal static OwnedValues Capture(UpdateSystem update, LoadGameSystem.EventGameLoaded observer)
            {
                World source = update.World;
                Check(source != null && source.IsCreated, "TEST_CITY_SOURCE_UNAVAILABLE");
                GameManager manager = GameManager.instance;
                var loader = source!.GetExistingSystemManaged<LoadGameSystem>();
                var config = source.GetExistingSystemManaged<CityConfigurationSystem>();
                var modes = source.GetExistingSystemManaged<GameModeSystem>();
                var autosave = source.GetExistingSystemManaged<AutoSaveSystem>();
                var time = source.GetExistingSystemManaged<TimeSystem>();
                Check(manager != null && loader != null && config != null && modes != null && autosave != null && time != null, "TEST_CITY_EXISTING_SYSTEMS_UNAVAILABLE");
                var result = new OwnedValues(update, source, manager!, loader!, config!, modes!, autosave!, time!, observer);
                result.RequireSource(); result.BindTimeDataQuery(); return result;
            }
            internal void RequireSource()
            {
                SourceStable = false;
                SourceStable = source.IsCreated && ReferenceEquals(Manager, GameManager.instance) && ReferenceEquals(update.World, source)
                    && ReferenceEquals(source.GetExistingSystemManaged<UpdateSystem>(), update)
                    && ReferenceEquals(Loader.World, source) && ReferenceEquals(source.GetExistingSystemManaged<LoadGameSystem>(), Loader)
                    && ReferenceEquals(Config.World, source) && ReferenceEquals(source.GetExistingSystemManaged<CityConfigurationSystem>(), Config)
                    && ReferenceEquals(Modes.World, source) && ReferenceEquals(source.GetExistingSystemManaged<GameModeSystem>(), Modes)
                    && ReferenceEquals(Autosave.World, source) && ReferenceEquals(source.GetExistingSystemManaged<AutoSaveSystem>(), Autosave)
                    && ReferenceEquals(Time.World, source) && ReferenceEquals(source.GetExistingSystemManaged<TimeSystem>(), Time);
                Check(SourceStable, "TEST_CITY_SOURCE_CHANGED");
            }
            private void BindTimeDataQuery()
            {
                EntityQuery[] queries = Time.EntityQueries;
                Check(queries.Length <= 16, "TEST_CITY_TIME_QUERY_SET_UNQUALIFIED");
                int matches = 0;
                foreach (EntityQuery query in queries)
                {
                    EntityQueryDesc[] descriptions = query.GetEntityQueryDescs();
                    if (descriptions.Length != 1) continue;
                    EntityQueryDesc description = descriptions[0];
                    if (description.Options != EntityQueryOptions.Default || description.All.Length != 1
                        || description.Any.Length != 0 || description.None.Length != 0
                        || !description.All[0].Equals(ComponentType.ReadOnly<TimeData>())) continue;
                    Check(!query.HasFilter(), "TEST_CITY_TIME_QUERY_FILTERED");
                    matches++; timeDataQuery = query;
                }
                Check(matches == 1, "TEST_CITY_TIME_QUERY_AMBIGUOUS_OR_MISSING");
                timeDataQueryBound = true;
            }
            internal void Prepare(int declaredStartingYear)
            {
                RequireSource(); Check(ReadyMenu, "TEST_CITY_PREPARATION_NOT_MENU");
                mapStartingYear = declaredStartingYear;
                int selectedStartingYear = declaredStartingYear == -1 ? DateTime.Now.Year : declaredStartingYear;
                Check(selectedStartingYear >= 1 && selectedStartingYear <= 9999, "TEST_CITY_MAP_STARTING_YEAR_INVALID");
                expectedStartingYear = selectedStartingYear;
                Check(!Config.overrideLoadedOptions && Config.overrideCityName == null && Config.overrideThemeName == null
                    && Modes.overrideMode == null, "TEST_CITY_EXISTING_PENDING_INTENT");
                Check(Modes.GetModeSetting(NormalMode) != null, "TEST_CITY_NORMAL_MODE_UNAVAILABLE");
                general = Manager.settings.general;
                Check(general != null, "TEST_CITY_GENERAL_UNAVAILABLE");
                generalAutosave = general!.autoSave; generalInterval = (int)general.autoSaveInterval; generalCount = (int)general.autoSaveCount;
                Check(generalInterval > 0, "TEST_CITY_AUTOSAVE_INTERVAL_INVALID"); GeneralStable = true;
                previousAutosave = Autosave.Enabled; previousTraffic = Config.overrideLeftHandTraffic; previousDisasters = Config.overrideNaturalDisasters;
                previousUnlockAll = Config.overrideUnlockAll; previousMoney = Config.overrideUnlimitedMoney; previousTiles = Config.overrideUnlockMapTiles;
                previousStartingYear = Time.startingYear;
                Traffic = Config.leftHandTraffic; Disasters = Config.naturalDisasters;
                RuntimeHeld = true; // Before the first setter, including partial setup.
                Autosave.Enabled = false;
                // MenuUISystem.NewGame and GameManager.AutoLoad set this input
                // before Load. NewGame PostDeserialize copies it into TimeData.
                startingYearOwned = true;
                Time.startingYear = selectedStartingYear;
                startingYearSet = true;
                Config.overrideLeftHandTraffic = Traffic; Config.overrideNaturalDisasters = Disasters;
                Config.overrideUnlockAll = false; Config.overrideUnlimitedMoney = false; Config.overrideUnlockMapTiles = false;
                loadedOptionsOwned = true; Config.overrideLoadedOptions = true;
                modeOwned = true; Modes.overrideMode = NormalMode;
                ObserverHeld = true; Loader.onOnSaveGameLoaded += observer;
            }
            internal bool ObserveStartingYearOwnership()
            {
                if (startingYearOwned && Time.startingYear != expectedStartingYear) startingYearChanged = true;
                return !startingYearChanged;
            }
            internal int? ObserveTimeDataStartingYear()
            {
                RequireSource();
                observedTimeDataStartingYear = null; timeDataSingletonObserved = false;
                Check(timeDataQueryBound && !timeDataQuery.HasFilter(), "TEST_CITY_TIME_QUERY_UNAVAILABLE");
                if (!timeDataQuery.TryGetSingletonEntity<TimeData>(out Entity entity)) return null;
                observedTimeDataStartingYear = source.EntityManager.GetComponentData<TimeData>(entity).m_StartingYear;
                timeDataSingletonObserved = true;
                return observedTimeDataStartingYear;
            }
            internal JObject StartingYearSnapshot() => new JObject {
                ["mapDeclared"] = Number(mapStartingYear), ["expected"] = Number(expectedStartingYear),
                ["setBeforeLoad"] = startingYearSet, ["owned"] = startingYearOwned,
                ["changedExternally"] = startingYearChanged, ["restored"] = startingYearRestored,
                ["timeDataSingletonObserved"] = timeDataSingletonObserved,
                ["timeDataStartingYear"] = Number(observedTimeDataStartingYear),
                ["timeDataMatchesExpected"] = timeDataSingletonObserved && expectedStartingYear.HasValue
                    ? new JValue(observedTimeDataStartingYear == expectedStartingYear) : JValue.CreateNull()
            };
            private static JToken Number(int? value) => value.HasValue ? new JValue(value.Value) : JValue.CreateNull();
            internal void ArmWindow(double now)
            {
                double interval = (float)general!.autoSaveInterval;
                Check(!double.IsNaN(interval) && !double.IsInfinity(interval) && interval > 0, "TEST_CITY_AUTOSAVE_INTERVAL_INVALID");
                LoadStarted = now; deadline = now + interval - Math.Min(5, interval * 0.1); windowArmed = true;
            }
            internal bool ObserveGeneral()
            {
                bool previouslyStable = GeneralStable; GeneralStable = false;
                bool same = ReferenceEquals(Manager.settings.general, general) && general != null && general.autoSave == generalAutosave
                    && (int)general.autoSaveInterval == generalInterval && (int)general.autoSaveCount == generalCount;
                GeneralStable = previouslyStable && same; return GeneralStable;
            }
            internal bool ObserveWindow(double now)
            { if (windowArmed && now >= deadline) WindowExpired = true; return WindowExpired; }
            internal bool CanHoldReadWindow(double now, double seconds) =>
                windowArmed && RuntimeHeld && !WindowExpired && now < deadline && seconds < deadline - now;
            internal void ObserveConsumedIntent()
            {
                if (loadedOptionsOwned && !Config.overrideLoadedOptions) loadedOptionsOwned = false;
                if (modeOwned && Modes.overrideMode == null) modeOwned = false;
            }
            private void RequireOwnedIntent()
            {
                ObserveConsumedIntent();
                Check(Config.overrideCityName == null && Config.overrideThemeName == null
                    && (!Config.overrideLoadedOptions || loadedOptionsOwned)
                    && (Modes.overrideMode == null || (modeOwned && Modes.overrideMode == NormalMode)), "TEST_CITY_FOREIGN_PENDING_INTENT");
            }
            private void ClearOwnedIntent()
            {
                if (loadedOptionsOwned && Config.overrideLoadedOptions) Config.overrideLoadedOptions = false;
                if (modeOwned && Modes.overrideMode == NormalMode) Modes.overrideMode = null;
                loadedOptionsOwned = false; modeOwned = false;
            }
            private void RemoveObserver()
            { if (ObserverHeld) { Loader.onOnSaveGameLoaded -= observer; ObserverHeld = false; } }
            internal void DetachDiagnosticObserver() { RemoveObserver(); }
            internal void PrepareReturn()
            { RequireSource(); RequireOwnedIntent(); RemoveObserver(); ClearOwnedIntent(); }
            internal void RestoreInMenu()
            {
                RequireSource(); Check(ReadyMenu, "TEST_CITY_RESTORE_NOT_MENU"); RequireOwnedIntent();
                RemoveObserver(); ClearOwnedIntent();
                if (!RuntimeHeld) return;
                // Restore only after the existing native load/return receipts
                // have physically completed. Never overwrite a later owner's input.
                if (startingYearOwned)
                {
                    if (!startingYearChanged && Time.startingYear == expectedStartingYear)
                    { Time.startingYear = previousStartingYear; startingYearRestored = true; }
                    else startingYearChanged = true;
                    startingYearOwned = false;
                }
                if (Config.overrideLeftHandTraffic == Traffic) Config.overrideLeftHandTraffic = previousTraffic;
                if (Config.overrideNaturalDisasters == Disasters) Config.overrideNaturalDisasters = previousDisasters;
                if (!Config.overrideUnlockAll) Config.overrideUnlockAll = previousUnlockAll;
                if (!Config.overrideUnlimitedMoney) Config.overrideUnlimitedMoney = previousMoney;
                if (!Config.overrideUnlockMapTiles) Config.overrideUnlockMapTiles = previousTiles;
                // This pauses timed updates only; never restore while city or
                // unknown native work may still be active, and never edit General.
                if (!Autosave.Enabled) Autosave.Enabled = previousAutosave;
                RuntimeHeld = false;
            }
        }

        private sealed class LastSaveAudit
        {
            private sealed class FirstDifference { internal FirstDifference(string? value) { Value = value; } internal string? Value { get; } }
            private string? baseline;
            private FirstDifference? firstDifference;
            private bool captured, readFailed;
            private int checks;
            internal string? ExceptionType { get; private set; }
            internal AuditResult Result => readFailed ? AuditResult.ReadFailed : firstDifference != null ? AuditResult.Changed
                : captured ? AuditResult.Unchanged : AuditResult.Missing;
            internal void Observe(GameManager? manager, bool capture)
            {
                // Reporting failure is a result, not an exception which can
                // repeatedly preempt pending native task/ownership processing.
                try
                {
                    if (!captured && !capture) return;
                    Check(manager != null && ReferenceEquals(manager, GameManager.instance), "TEST_CITY_LAST_SAVE_SOURCE_CHANGED");
                    var reference = manager!.settings.userState.lastSaveGameMetadata;
                    string? current = reference == null ? null : reference.id.guid.ToString();
                    if (checks < int.MaxValue) checks++;
                    if (!captured) { baseline = current; captured = true; return; }
                    if (firstDifference == null && !string.Equals(baseline, current, StringComparison.Ordinal))
                        firstDifference = new FirstDifference(current);
                }
                catch (Exception error) { readFailed = true; if (ExceptionType == null) ExceptionType = ExceptionName(error); }
            }
            internal JObject Project() => new JObject { ["captured"] = captured, ["unchanged"] = Result == AuditResult.Unchanged,
                ["checks"] = checks, ["difference"] = firstDifference != null, ["readFailed"] = readFailed };
        }

        private sealed class ReadyObservation
        {
            internal ReadyObservation(string session, double at, int elapsed, bool traffic, bool disasters,
                int expectedStartingYear, int? observedStartingYear)
            { Session = session; At = at; Elapsed = elapsed; Traffic = traffic; Disasters = disasters;
                ExpectedStartingYear = expectedStartingYear; ObservedStartingYear = observedStartingYear; }
            internal string Session { get; }
            internal double At { get; }
            private int Elapsed { get; }
            private bool Traffic { get; }
            private bool Disasters { get; }
            private int ExpectedStartingYear { get; }
            private int? ObservedStartingYear { get; }
            internal bool StartingYearMatches => ObservedStartingYear.HasValue && ObservedStartingYear.Value == ExpectedStartingYear;
            internal JObject Project() => new JObject { ["session"] = Session, ["elapsedMs"] = Elapsed, ["normalMode"] = true,
                ["worldReadyGame"] = true, ["loading"] = false, ["contextMatched"] = true, ["overridesConsumed"] = true, ["newSession"] = true,
                ["unlockAll"] = false, ["unlimitedMoney"] = false, ["unlockMapTiles"] = false,
                ["leftHandTraffic"] = Traffic, ["naturalDisasters"] = Disasters,
                ["expectedStartingYear"] = ExpectedStartingYear,
                ["timeDataStartingYear"] = ObservedStartingYear.HasValue ? new JValue(ObservedStartingYear.Value) : JValue.CreateNull(),
                ["startingYearMatches"] = ObservedStartingYear.HasValue ? new JValue(StartingYearMatches) : JValue.CreateNull() };
        }

        private static string LoadFailure(ProbeTaskObservation task)
        {
            switch (task.State)
            {
                case ProbeTaskState.Cancelled: return "TEST_CITY_LOAD_CANCELLED";
                case ProbeTaskState.Faulted: return "TEST_CITY_LOAD_FAULTED";
                case ProbeTaskState.Completed: return "TEST_CITY_LOAD_FALSE";
                default: return "TEST_CITY_LOAD_RECEIPT_MISSING";
            }
        }
        private static string Code(Exception error, string fallback) => error is CheckFailure failure ? failure.Code : fallback;
        private static double Now()
        {
            double value = UnityEngine.Time.realtimeSinceStartup;
            Check(!double.IsNaN(value) && !double.IsInfinity(value) && value >= 0, "TEST_CITY_REALTIME_INVALID"); return value;
        }
        private static bool DescriptorShape(AsyncReadDescriptor value) => value != AsyncReadDescriptor.Invalid
            && !string.IsNullOrEmpty(value.name) && !string.IsNullOrEmpty(value.path) && value.offset >= 0
            && (value.size > 0 || (!value.packaged && value.size == -1));
        private static bool BoundedPrerequisites(string[]? values)
        {
            if (values == null) return true;
            if (values.Length > 16) return false;
            foreach (string value in values) if (value == null || value.Length == 0 || value.Length > 128) return false;
            return true;
        }
        private static string NormalizeGuid(string value)
        {
            bool nonzero = false;
            if (value == null || value.Length != 32) throw new ArgumentException("A nonzero 32-hex identity is required.");
            foreach (char character in value)
            {
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f') || (character >= 'A' && character <= 'F')))
                    throw new ArgumentException("A nonzero 32-hex identity is required.");
                nonzero |= character != '0';
            }
            if (!nonzero) throw new ArgumentException("A nonzero 32-hex identity is required.");
            return value.ToLowerInvariant();
        }
        private static string ExceptionName(Exception error)
        {
            string name = error.GetType().Name;
            if (name.Length > 64) return "Exception";
            foreach (char character in name)
                if (!((character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z')
                    || (character >= '0' && character <= '9') || character == '_')) return "Exception";
            return name;
        }
        private static string Label(Enum value) { string text = value.ToString(); return char.ToLowerInvariant(text[0]) + text.Substring(1); }
        private static JToken Value(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static void RequireMainThread() { if (!MainThreadDispatcher.isMainThread) throw new InvalidOperationException("Main thread required."); }
        private static void Check(bool condition, string code) { if (!condition) throw new CheckFailure(code); }
        private sealed class CheckFailure : Exception { internal CheckFailure(string code) { Code = code; } internal string Code { get; } }
    }
}
#endif
