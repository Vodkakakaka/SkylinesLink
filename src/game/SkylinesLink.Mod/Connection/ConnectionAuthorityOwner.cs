using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Runtime.InteropServices;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Operations;
using SkylinesLink.Contracts;
using SkylinesLink.Infrastructure.Connection;
using SkylinesLink.Infrastructure.Operations;
using SkylinesLink.Mod.Runtime;

namespace SkylinesLink.Mod.Connection
{
    public interface IConnectionPublication
    {
        void Publish(EnvironmentDiscovery discovery);
        void Unpublish(string instanceId, long authorityEpoch);
    }

    /// <summary>接入实例的唯一生命周期所有者；借用装配根的同一次上下文寿命。真实 Mod 尚未装配此服务。</summary>
    public sealed class ConnectionAuthorityOwner
    {
        private readonly object sync = new object();
        private readonly EnvironmentLease lease;
        private readonly IConnectionPublication publication;
        private readonly EnvironmentDiscovery discovery;
        private Task? closing;
        private Task? activating;
        private bool publicationAttempted, stopRequested, active, fullyReleased;
        private GCHandle nativeLifetimeRoot;
        private ConnectionAuthorityOwner(EnvironmentLease lease, IConnectionPublication publication, GrantAuthority authority, EnvironmentDiscovery discovery, BoundedPipeServer server, OperationAuthority operations, WorldContextTracker context, HistoryPublisher history)
        { this.lease = lease; this.publication = publication; Authority = authority; this.discovery = discovery; Server = server; Operations = operations; Context = context; History = history; }
        public GrantAuthority Authority { get; }
        public OperationAuthority Operations { get; }
        public WorldContextTracker Context { get; }
        public HistoryPublisher History { get; }
        public bool FullyReleased { get { lock (sync) return fullyReleased; } }
        public bool NativeLeaseRetained { get { lock (sync) return nativeLifetimeRoot.IsAllocated; } }
        public bool IsActive { get { lock (sync) return active && !stopRequested; } }
        public bool StopRequested { get { lock (sync) return stopRequested; } }
        public BoundedPipeServer Server { get; }
        public EnvironmentDiscovery Discovery => ContractCodec.DecodeEnvironmentDiscovery(ContractCodec.EncodeEnvironmentDiscovery(discovery));

        public static async Task<ConnectionAuthorityOwner?> OpenAsync(string directory, string environment, string coreHash, string productVersion, PipeLimits limits,
            WorldContextTracker context, Func<OperationAuthority, WorldContextTracker, string, IReadOnlyCapabilityDispatcher> dispatcher,
            bool initializeEmpty = false,
            Func<IAuthorizationStore, IAuthorizationStore>? persistencePort = null,
            Func<IConnectionPublication, IConnectionPublication>? publicationPort = null,
            Func<IHistoryStore, IHistoryStore>? historyStorePort = null, CancellationToken cancellationToken = default, int leaseWaitMilliseconds = 1000,
            IJournalDurability? journalDurability = null)
        {
            var owner = await CreateAsync(directory, environment, coreHash, productVersion, limits, context, dispatcher, initializeEmpty,
                persistencePort, publicationPort, historyStorePort, cancellationToken, leaseWaitMilliseconds, journalDurability).ConfigureAwait(false);
            if (owner == null) return null;
            try { await owner.ActivateAsync().ConfigureAwait(false); return owner; }
            catch { await owner.CloseAsync().ConfigureAwait(false); throw; }
        }

        // 整个构造包括首次目录/ACL/授权读取均在后台；返回时没有监听或发现发布。
        public static Task<ConnectionAuthorityOwner?> CreateAsync(string directory, string environment, string coreHash, string productVersion, PipeLimits limits,
            WorldContextTracker context, Func<OperationAuthority, WorldContextTracker, string, IReadOnlyCapabilityDispatcher> dispatcher,
            bool initializeEmpty = false,
            Func<IAuthorizationStore, IAuthorizationStore>? persistencePort = null,
            Func<IConnectionPublication, IConnectionPublication>? publicationPort = null,
            Func<IHistoryStore, IHistoryStore>? historyStorePort = null, CancellationToken cancellationToken = default, int leaseWaitMilliseconds = 1000,
            IJournalDurability? journalDurability = null) =>
            Task.Run(() => CreateCoreAsync(directory, environment, coreHash, productVersion, limits, context, dispatcher, initializeEmpty,
                persistencePort, publicationPort, historyStorePort, cancellationToken, leaseWaitMilliseconds, journalDurability));

        private static async Task<ConnectionAuthorityOwner?> CreateCoreAsync(string directory, string environment, string coreHash, string productVersion, PipeLimits limits,
            WorldContextTracker context, Func<OperationAuthority, WorldContextTracker, string, IReadOnlyCapabilityDispatcher> dispatcher,
            bool initializeEmpty,
            Func<IAuthorizationStore, IAuthorizationStore>? persistencePort,
            Func<IConnectionPublication, IConnectionPublication>? publicationPort,
            Func<IHistoryStore, IHistoryStore>? historyStorePort, CancellationToken cancellationToken, int leaseWaitMilliseconds,
            IJournalDurability? journalDurability)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
            if (limits == null) throw new ArgumentNullException(nameof(limits));
            string instance = context.Read().InstanceId;
            if (!Guid.TryParseExact(instance, "N", out var identity) || identity == Guid.Empty || identity.ToString("N") != instance)
                throw new ArgumentException("A canonical connection instance identity is required.", nameof(context));
            if (context.IsStopped) throw new InvalidOperationException("The world context lifetime has stopped.");
            if (leaseWaitMilliseconds < 1 || leaseWaitMilliseconds > 5000) throw new ArgumentOutOfRangeException(nameof(leaseWaitMilliseconds));
            cancellationToken.ThrowIfCancellationRequested();
            environment = AuthorizationState.NormalizeEnvironment(environment);
            AuthorizationState.ValidateHex(coreHash);
            AuthorizationState.ValidateIdentifier(productVersion);
            bool newDirectory = !Directory.Exists(directory);
            if (newDirectory && !initializeEmpty) throw new InvalidOperationException("Connection authorization is not configured.");
            var boundary = new PrivateConnectionStorage(directory);
            var provider = new EnvironmentLeaseProvider(boundary);
            // 这里只计真实锁争用；目录/ACL 准备不等同于已尝试获取租约。
            var acquiring = Stopwatch.StartNew();
            EnvironmentLease? lease;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (acquiring.ElapsedMilliseconds >= leaseWaitMilliseconds) return null;
                lease = provider.TryAcquireAuthority();
                if (lease != null) break;
                int remaining = leaseWaitMilliseconds - checked((int)acquiring.ElapsedMilliseconds);
                if (remaining <= 0) return null;
                await Task.Delay(Math.Min(25, remaining), cancellationToken).ConfigureAwait(false);
            }
            ConnectionAuthorityOwner? owner = null;
            GrantAuthority? authority = null;
            OperationJournal? journal = null;
            HistoryPublisher? history = null;
            OperationAuthority? operations = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var store = new AtomicAuthorizationStore(boundary, lease, environment);
                bool created = !store.HasAuthorization;
                if (created)
                {
                    if (!initializeEmpty || !newDirectory) throw new InvalidOperationException("Connection authorization is not configured.");
                    store.InitializeEmpty();
                }
                var old = store.Load();
                bool baseline = old.RecordRevision == 1 && old.AuthorityEpoch == 0 && old.Profiles.Count == 0;
                if (old.AuthorityEpoch == 0 && (!baseline || !initializeEmpty))
                    throw new InvalidOperationException("Connection storage initialization requires an explicit request and an intact baseline.");
                if (baseline)
                {
                    // This exact durable value precedes any active authority.
                    // Complete its journal before advancing the epoch or granting a profile.
                    string journalPath = Path.Combine(boundary.DirectoryPath, "operations.journal");
                    boundary.ValidateFile(journalPath, true);
                    bool createJournal = !File.Exists(journalPath);
                    journal = await Task.Run(() => OperationJournal.OpenForAuthority(boundary, lease, environment, createJournal, journalDurability)).ConfigureAwait(false);
                    var recovered = journal.Recovery;
                    if (!journal.IsHealthy || !recovered.Complete || !recovered.DurableConfirmed || recovered.RetentionApplied ||
                        recovered.Records.Count != 1 || recovered.Records[0].Sequence != 1 || !(recovered.Records[0].Entry is StoreOpened))
                        throw new InvalidDataException("Unactivated connection storage contains an incomplete or conflicting journal.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                var activated = old.Activate();
                await Task.Run(() => store.Save(old.RecordRevision, activated)).ConfigureAwait(false);
                authority = new GrantAuthority(activated, persistencePort == null ? store : persistencePort(store));
                if (journal == null)
                    journal = await Task.Run(() => OperationJournal.OpenForAuthority(boundary, lease, environment, false, journalDurability)).ConfigureAwait(false);
                IHistoryStore historyStore = new AtomicHistoryStore(boundary, lease, environment);
                if (historyStorePort != null) historyStore = historyStorePort(historyStore);
                history = new HistoryPublisher(journal, historyStore, environment, productVersion, instance, coreHash);
                operations = new OperationAuthority(journal, authority, context, new ProcessNativeHostIdentity(), history: history);
                string environmentHash;
                using (var sha = SHA256.Create()) environmentHash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(environment))).Replace("-", "").ToLowerInvariant().Substring(0, 16);
                var record = new EnvironmentDiscovery { ProductId = "skylineslink", EnvironmentId = environment, InstanceId = instance,
                    AuthorityEpoch = authority.State.AuthorityEpoch, PipeName = "skylineslink-" + environmentHash + "-" + instance,
                    ProductVersion = productVersion, ContractVersion = "2", CoreManifestSha256 = coreHash, ProcessId = Process.GetCurrentProcess().Id };
                if (context.IsStopped) throw new InvalidOperationException("The world context lifetime has stopped.");
                var server = new BoundedPipeServer(record, limits, authority, dispatcher(operations, context, productVersion));
                IConnectionPublication publisher = new StorePublication(store);
                if (publicationPort != null) publisher = publicationPort(publisher);
                owner = new ConnectionAuthorityOwner(lease, publisher, authority, record, server, operations, context, history);
                return owner;
            }
            catch (Exception error)
            {
                if (owner != null) await owner.CloseAsync().ConfigureAwait(false);
                else
                {
                    // Once authority construction has begun, this instance must
                    // not be reused after failure. Busy-before-acquisition is different.
                    context.Stop();
                    bool cleanupFailed = false;
                    try
                    {
                        if (operations != null) await operations.CloseAsync().ConfigureAwait(false);
                        else if (journal != null) await journal.StopAsync().ConfigureAwait(false);
                    }
                    catch { cleanupFailed = true; }
                    try { if (history != null) await history.StopAsync().ConfigureAwait(false); }
                    catch { cleanupFailed = true; }
                    try { if (authority != null) await authority.StopAsync().ConfigureAwait(false); }
                    catch { cleanupFailed = true; }
                    bool retained = operations?.HasNativeFence == true;
                    // owner 尚不存在也不能丢掉 Restore 得到的屏障。保留的只有最小 lease，不是整个操作图。
                    CompleteLeaseOwnership(lease, retained);
                    if (retained || cleanupFailed) throw new ConnectionInitializationException(error, !retained && !cleanupFailed, retained);
                }
                throw;
            }
        }

        public Task ActivateAsync()
        {
            lock (sync)
            {
                if (stopRequested) return Task.FromException(new InvalidOperationException("Connection owner has stopped."));
                return activating ?? (activating = Task.Run(ActivateCoreAsync));
            }
        }
        private async Task ActivateCoreAsync()
        {
            try
            {
                lock (sync)
                {
                    if (stopRequested) return;
                    if (Context.IsStopped) throw new InvalidOperationException("The world context lifetime has stopped.");
                }
                await Operations.SealRecoveryAsync().ConfigureAwait(false);
                lock (sync)
                {
                    if (stopRequested) return;
                    if (Context.IsStopped) throw new InvalidOperationException("The world context lifetime has stopped.");
                }
                Server.Start();
                lock (sync)
                {
                    if (stopRequested) return;
                    publicationAttempted = true;
                }
                // 实际发布 IO 不可取消；停止期间允许它结束，但 Close 必须随后撤回，不能先释放 lease。
                publication.Publish(discovery);
                lock (sync) { if (!stopRequested) active = true; }
            }
            catch { RequestStop(); throw; }
        }
        public void RequestStop()
        {
            lock (sync)
            {
                if (!stopRequested)
                {
                    stopRequested = true; active = false;
                    Authority.StopAsync();
                    Operations.RequestStop();
                    Context.Stop();
                }
                if (closing == null) closing = Task.Run(CloseCoreAsync);
            }
        }
        public Task CloseAsync()
        { RequestStop(); lock (sync) return closing!; }
        private async Task CloseCoreAsync()
        {
            Task operations = Operations.CloseAsync();
            Task? activation;
            lock (sync) activation = activating;
            if (activation != null) { try { await activation.ConfigureAwait(false); } catch { /* 激活调用方保原错误；这里仍完成同一 owner 的资源关闭。 */ } }
            Task writer = Authority.StopAsync();
            Server.Dispose();
            Exception? withdrawalFailure = null;
            Task withdraw = Task.Run(async () =>
            {
                try { await writer.ConfigureAwait(false); } catch { }
                if (publicationAttempted)
                    try { publication.Unpublish(discovery.InstanceId, discovery.AuthorityEpoch); }
                    catch (Exception error) { withdrawalFailure = error; }
            });
            try { await Task.WhenAll(Server.Completion, withdraw, operations).ConfigureAwait(false); }
            finally
            {
                try { await History.StopAsync().ConfigureAwait(false); }
                finally
                {
                    bool retained = Operations.HasNativeFence;
                    var root = CompleteLeaseOwnership(lease, retained);
                    lock (sync) { nativeLifetimeRoot = root; fullyReleased = !retained; }
                }
            }
            if (withdrawalFailure != null) throw new InvalidOperationException("Discovery withdrawal failed.");
        }
        private static GCHandle CompleteLeaseOwnership(EnvironmentLease lease, bool retained)
        {
            if (retained) return GCHandle.Alloc(lease);
            lease.Dispose(); return default;
        }
        private sealed class StorePublication : IConnectionPublication
        {
            private readonly AtomicAuthorizationStore store;
            internal StorePublication(AtomicAuthorizationStore store) { this.store = store; }
            public void Publish(EnvironmentDiscovery discovery) => store.Publish(discovery);
            public void Unpublish(string instanceId, long authorityEpoch) => store.Unpublish(instanceId, authorityEpoch);
        }
    }
}
