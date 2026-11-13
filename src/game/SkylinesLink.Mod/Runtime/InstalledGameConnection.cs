using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Modules;
using SkylinesLink.Application.Observation;
using SkylinesLink.Application.Buildings;
using SkylinesLink.Application.Operations;
using SkylinesLink.Infrastructure.Installation;
using SkylinesLink.Infrastructure.Connection;
using SkylinesLink.Mod.Connection;

namespace SkylinesLink.Mod.Runtime
{
    /// <summary>一个已安装接入寿命的装配与资源归属；原生观察器由主线程根持有。</summary>
    public sealed class InstalledGameConnection
    {
        private readonly object sync = new object();
        private readonly string environment;
        private readonly LoadedManagedComponent[] components;
        private readonly string? initialProfile;
        private readonly bool initialCityChanges;
        private readonly ModBundleOrigin? bundledOrigin;
        private readonly GameConnectionLifecycle lifecycle;
        private InstalledCoreBinding? binding;
        private Task<bool>? closing;
        private Task? configuration;
        private string? version;
        private SetupInitializationRun? preparation;
        private SetupInitializationResult? preparationResult;

        public InstalledGameConnection(string environment, IReadOnlyCollection<LoadedManagedComponent> components,
            string? initialProfile = null, bool allowCityChanges = false, ModBundleOrigin? bundledOrigin = null)
        {
            this.environment = AuthorizationState.NormalizeEnvironment(environment);
            if (components == null) throw new ArgumentNullException(nameof(components));
            this.components = components.ToArray();
            if (initialProfile != null) AuthorizationState.ValidateIdentifier(initialProfile);
            this.initialProfile = initialProfile;
            initialCityChanges = allowCityChanges;
            this.bundledOrigin = bundledOrigin;
            Context = new WorldContextTracker(Guid.NewGuid().ToString("N"));
            Context.MarkUnavailable();
            Modules = new ModuleRuntimeObservationBroker(Context.Read().InstanceId);
            CityOverview = new CityOverviewReadBroker(Context);
            Buildings = new BuildingInspectionReadBroker(Context);
            lifecycle = new GameConnectionLifecycle(CreateAsync);
        }

        public WorldContextTracker Context { get; }
        public ModuleRuntimeObservationBroker Modules { get; }
        public CityOverviewReadBroker CityOverview { get; }
        public BuildingInspectionReadBroker Buildings { get; }
        public GameConnectionLifecycleSnapshot Read() => lifecycle.Read();
        public string? ProductVersion { get { lock (sync) return version; } }
        internal SetupInitializationSnapshot? ReadPreparation()
        { lock (sync) return preparation?.Read(); }
        internal SetupInitializationResult? PreparationResult { get { lock (sync) return preparationResult; } }

        public async Task<bool> StartAsync()
        {
            bool active = await lifecycle.StartAsync().ConfigureAwait(false);
            // ENVIRONMENT_BUSY is retryable with this same instance. All other
            // failures consume the lifetime, including failures before Owner exists.
            if (!active && lifecycle.Read().FailureCode != "ENVIRONMENT_BUSY") RequestStop();
            return active;
        }

        private async Task<ConnectionAuthorityOwner?> CreateAsync(CancellationToken cancellation)
        {
            // Lifecycle invokes the factory on its worker, including its first IO.
            InstalledCoreBinding opened;
            try { opened = InstalledCoreBinding.Open(environment, components, cancellation); }
            catch (MissingCoreActivationException) when (bundledOrigin != null)
            {
                // Only this precise record-absence boundary may initiate bundled
                // preparation. Authorization, payload or cohort failures never do.
                cancellation.ThrowIfCancellationRequested();
                var run = SetupInitializationRun.Start(bundledOrigin, components, environment);
                lock (sync) preparation = run;
                SetupInitializationResult prepared;
                using (cancellation.Register(run.RequestStop))
                    prepared = await run.Completion.ConfigureAwait(false);
                lock (sync) preparationResult = prepared;
                // Stop may have arrived after the child published successfully.
                // Preserve that fact but do not create a connection or grant.
                cancellation.ThrowIfCancellationRequested();
                if (!prepared.Ready) throw new InvalidOperationException("BUNDLED_CORE_PREPARATION_FAILED");
                // Revalidate the actual current selection. A completed readback
                // is evidence, never a lease handed to the connection authority.
                opened = InstalledCoreBinding.Open(environment, components, cancellation);
            }
            lock (sync) { binding = opened; version = opened.Version; }
            var owner = await ConnectionAuthorityOwner.CreateAsync(opened.ConnectionDirectory, opened.EnvironmentId,
                opened.CoreManifestSha256, opened.Version,
                new PipeLimits(4, 65536, 32, 8192, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)),
                Context, (operations, context, productVersion) => new SnapshotCapabilityDispatcher(context, productVersion, operations, Modules, CityOverview, Buildings),
                initializeEmpty: initialProfile != null, cancellationToken: cancellation).ConfigureAwait(false);
            if (owner == null)
            {
                // A rejected lease attempt must not pin another cohort across retry.
                opened.Dispose();
                lock (sync) binding = null;
                return null;
            }
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (initialProfile != null)
                    await EnsureAsync(owner, initialProfile, initialCityChanges).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                return owner;
            }
            catch (Exception failure)
            {
                // The lifecycle has not received this Owner yet. Preserve its
                // actual close responsibility if explicit configuration fails.
                try { await owner.CloseAsync().ConfigureAwait(false); }
                catch { throw new ConnectionInitializationException(failure, false, owner.NativeLeaseRetained); }
                if (!owner.FullyReleased)
                    throw new ConnectionInitializationException(failure, false, owner.NativeLeaseRetained);
                throw;
            }
        }

        public Task ConfigureAsync(string profile, bool allowCityChanges)
        {
            AuthorizationState.ValidateIdentifier(profile);
            // Reuse an active authority: repeated equal configuration must not
            // disconnect clients or create another authority epoch.
            lock (sync)
            {
                if (closing != null || lifecycle.StopRequested || (configuration != null && !configuration.IsCompleted))
                    throw new InvalidOperationException("Connection configuration is unavailable.");
                return configuration = Task.Run(async () =>
                {
                    var owner = lifecycle.Owner;
                    if (owner == null || !owner.IsActive || lifecycle.StopRequested)
                        throw new InvalidOperationException("The connection is not active.");
                    await EnsureAsync(owner, profile, allowCityChanges).ConfigureAwait(false);
                });
            }
        }

        private static Task EnsureAsync(ConnectionAuthorityOwner owner, string profile, bool cityChanges) =>
            owner.Authority.EnsureProfileAsync(profile,
                cityChanges ? GrantScope.Read | GrantScope.Write | GrantScope.Control : GrantScope.Read,
                null, HandshakeProof.RandomHex);

        public void RequestStop()
        {
            Context.Stop();
            Modules.Stop();
            CityOverview.Stop();
            Buildings.Stop();
            lifecycle.RequestStop();
            lock (sync) if (closing == null) closing = Task.Run(CloseCoreAsync);
        }

        public Task<bool> CloseAsync()
        { RequestStop(); lock (sync) return closing!; }

        private async Task<bool> CloseCoreAsync()
        {
            Task? configured;
            lock (sync) configured = configuration;
            if (configured != null)
                try { await configured.ConfigureAwait(false); }
                catch { /* The configuration caller owns its result; still join the real work. */ }
            bool released = await lifecycle.CloseAsync().ConfigureAwait(false);
            // Stop precedes WhenIdle. A claimed native capture must still be
            // completed/failed by its main-thread owner before this task can end.
            await Modules.WhenIdle.ConfigureAwait(false);
            await CityOverview.WhenIdle.ConfigureAwait(false);
            await Buildings.WhenIdle.ConfigureAwait(false);
            if (lifecycle.Read().FailureCode == "CLOSE_FAILED") return false;
            InstalledCoreBinding? owned;
            lock (sync) owned = binding;
            try { owned?.Dispose(); }
            catch { return false; }
            lock (sync) binding = null;
            // A retained native fence keeps its minimal environment lease;
            // current producers no longer consume Core files after this drain.
            return released;
        }
    }
}
