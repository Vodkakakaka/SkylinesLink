using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Operations;
using SkylinesLink.Application.Simulation;
using SkylinesLink.Infrastructure.Connection;
using SkylinesLink.Infrastructure.Operations;
using SkylinesLink.Mod.Connection;

namespace SkylinesLink.RuntimeProbe
{
    internal static class RecoverySealingTests
    {
        private static int checks;
        private static readonly LoadTarget Target = new LoadTarget("probe", "save-b", "version-1");
        private static void Check(bool condition, string label) { Program.Assert(condition, label); checks++; }
        private static void Reject(Action action, string code)
        {
            try { action(); }
            catch (OperationRejectedException error) { Check(error.Code == code, "recovery sealing rejects " + code); return; }
            throw new InvalidOperationException("Expected recovery sealing rejection.");
        }

        internal static async Task<int> RunAsync()
        {
            checks = 0;
            await StageFactsAndPureRestore();
            await RecoveredHostFence();
            await FullTerminalBudget();
            await UnusableRecovery();
            await CurrentGrantAndOldAudit();
            await LostFinalAcknowledgement();
            return checks;
        }

        private static async Task StageFactsAndPureRestore()
        {
            Program.Stage = "recovery sealing keeps six actual prefix facts";
            using (var f = new Fixture())
            {
                var kinds = new[] { Prefix.Accepted, Prefix.Commit, Prefix.ReturnedFalse, Prefix.Faulted, Prefix.Cancelled, Prefix.ReturnedTrue };
                var intents = new List<OperationIntent>();
                foreach (var kind in kinds) intents.Add(await f.SeedAsync(kind, kind.ToString()));
                await f.OpenRecovery();
                long before = f.Journal.CaptureConfirmedPrefix()!.ThroughSequence;
                Check(!f.Ops.IsRecoveryReady && f.Ops.ActiveCount == 0 && f.Journal.ReservedOperationCount == 0 &&
                    f.Journal.Recovery.Records.All(record => !(record.Entry is OperationFinished)),
                    "pure restore does not append finals or create new operation reservations");
                for (int i = 0; i < kinds.Length; i++) VerifySnapshot(f.Ops.Lookup(intents[i].OperationId, f.Subject), kinds[i], "volatile");
                Reject(() => f.Ops.IssueWriteContext(f.Subject), "JOURNAL_UNAVAILABLE");
                Check((await f.Ops.SubmitAsync(intents[0].Key, Target, f.Subject)).OperationId == intents[0].OperationId,
                    "known submission key returns original identity before recovery readiness");
                Reject(() => f.Ops.SubmitAsync(intents[0].Key, new LoadTarget("probe", "different", "version-1"), f.Subject), "IDEMPOTENCY_CONFLICT");

                Task sealing = f.Ops.SealRecoveryAsync();
                Check(ReferenceEquals(sealing, f.Ops.SealRecoveryAsync()), "concurrent recovery sealing shares one task");
                await Program.MustComplete(sealing);
                var folded = f.Fold();
                Check(f.Ops.IsRecoveryReady && folded.AllRecordsValid && folded.ValidRecordCount == before + kinds.Length,
                    "explicit seal appends exactly one valid final per incomplete operation");
                for (int i = 0; i < kinds.Length; i++)
                {
                    VerifySnapshot(f.Ops.Lookup(intents[i].OperationId, f.Subject), kinds[i], "durable");
                    VerifyFinal(folded.Operations.Single(item => item.Intent.OperationId == intents[i].OperationId), intents[i], kinds[i]);
                }
                Check(ReferenceEquals(sealing, f.Ops.SealRecoveryAsync()) && f.Fold().ValidRecordCount == folded.ValidRecordCount,
                    "completed seal is not replayed by a later caller");
                await f.OpenRecovery();
                Check(f.Ops.IsRecoveryReady && f.Fold().ValidRecordCount == folded.ValidRecordCount,
                    "restart sees persisted finals without manufacturing another final");
                await Program.MustComplete(f.Ops.SealRecoveryAsync());
                Check(f.Fold().ValidRecordCount == folded.ValidRecordCount, "already sealed restart has no terminal IO work");
            }
        }

        private static async Task RecoveredHostFence()
        {
            Program.Stage = "recovery sealing does not reinterpret native host identity";
            foreach (var state in new[] { NativeHostState.Alive, NativeHostState.Unknown, NativeHostState.Ended })
            using (var f = new Fixture())
            {
                var intent = await f.SeedAsync(Prefix.Commit, "host-state");
                await f.OpenRecovery(hostState: state);
                Check(f.Ops.HasNativeFence == (state != NativeHostState.Ended), "modeled host state controls only the recovered physical fence");
                await Program.MustComplete(f.Ops.SealRecoveryAsync());
                VerifySnapshot(f.Ops.Lookup(intent.OperationId, f.Subject), Prefix.Commit, "durable");
                var fact = f.Fold().Operations.Single();
                Check(fact.Final!.Submission == SubmissionKnowledge.Unknown && fact.Commit!.NativeHost.Equals(f.NativeHost) &&
                    f.Ops.HasNativeFence == (state != NativeHostState.Ended),
                    "durable unknown keeps original native host and never proves non-submission");
                await f.OpenRecovery(hostState: state);
                Check(f.Ops.HasNativeFence == (state != NativeHostState.Ended) && f.Fold().Operations.Single().Final!.Submission == SubmissionKnowledge.Unknown,
                    "sealed unknown final retains its fence and submission knowledge after another restore");
            }
        }

        private static async Task FullTerminalBudget()
        {
            Program.Stage = "recovery sealing uses exactly thirty-two terminal credits";
            using (var flush = new ControlledFlush { PauseAt = 2 })
            using (var f = new Fixture())
            {
                var intents = new List<OperationIntent>();
                for (int i = 0; i < OperationLimits.MaximumRetained; i++)
                    intents.Add(await f.SeedAsync(Prefix.ReturnedTrue, "full-" + i, contextId: "original-context-" + (i / OperationLimits.MaximumRequestsPerContext)));
                await f.OpenRecovery(durability: flush);
                Check(f.Fold().ValidRecordCount == 97, "thirty-two real three-stage chains occupy ninety-seven frames");
                Task sealing = f.Ops.SealRecoveryAsync();
                try
                {
                    await Program.MustComplete(flush.Entered.Task);
                    Check(!sealing.IsCompleted && f.Journal.RecoveryReservedTerminalCount == 32 && f.Journal.RecoveryQueuedOrWriting == 1 &&
                        f.Journal.ReservedOperationCount == 0 && f.Ops.ActiveCount == 0 && f.Journal.CaptureConfirmedPrefix()!.ThroughSequence == 97,
                        "paused actual flush holds one work item and all exact terminal credits without live operation slots");
                    Check(f.Journal.TryReserveOperation() == null, "normal reservations cannot consume recovery credits");
                    Reject(() => f.Ops.IssueWriteContext(f.Subject), "JOURNAL_UNAVAILABLE");
                    var duplicate = f.Ops.SubmitAsync(intents[0].Key, Target, f.Subject);
                    await Program.MustComplete(duplicate);
                    Check((await duplicate).OperationId == intents[0].OperationId && (await duplicate).Durability == "volatile",
                        "known key remains queryable while actual recovery flush is pending");
                    flush.Release.Set();
                    await Program.MustComplete(sealing);
                    var folded = f.Fold();
                    Check(folded.AllRecordsValid && folded.ValidRecordCount == 129 && folded.Operations.Count == 32 &&
                        folded.Operations.All(item => item.Final != null && item.Final.Submission == SubmissionKnowledge.Submitted &&
                            item.Final.State == "unknown" && item.Final.Load.LoadReturnValue == true),
                        "exact frame limit preserves every true return without inventing target-ready success");
                    Check(f.Journal.RecoveryReservedTerminalCount == 0 && f.Journal.RecoveryQueuedOrWriting == 0 &&
                        f.Journal.ReservedOperationCount == 0 && f.Ops.IsRecoveryReady,
                        "actual completion releases recovery credits without changing retained operation count");
                    await f.OpenRecovery();
                    await Program.MustComplete(f.Ops.SealRecoveryAsync());
                    Check(f.Fold().ValidRecordCount == 129 && f.Ops.IsRecoveryReady, "full sealed journal reopens without a 130th frame");
                }
                finally { flush.Release.Set(); }
            }
        }

        private static async Task UnusableRecovery()
        {
            Program.Stage = "incomplete or unconfirmed recovery cannot be sealed";
            foreach (string mode in new[] { "physical-tail", "semantic-tail", "unconfirmed" })
            using (var f = new Fixture())
            using (var flush = new ControlledFlush { FailAt = mode == "unconfirmed" ? 1 : 0 })
            {
                var kind = mode == "semantic-tail" ? Prefix.ReturnedFalse : Prefix.Accepted;
                var intent = await f.SeedAsync(kind, mode, invalidFinal: mode == "semantic-tail");
                await f.OpenRecovery(durability: flush, brokenTail: mode == "physical-tail");
                byte[] before = f.ReadBytes();
                var known = f.Ops.Lookup(intent.OperationId, f.Subject);
                Check(mode == "unconfirmed" ? f.Journal.Recovery.Complete && !f.Journal.Recovery.DurableConfirmed :
                    mode == "physical-tail" ? !f.Journal.Recovery.Complete : f.Journal.Recovery.Complete && !f.Fold().AllRecordsValid,
                    "fixture distinguishes physical EOF, semantic validity, and actual durability confirmation");
                await Program.MustComplete(f.Ops.SealRecoveryAsync());
                var after = f.Ops.Lookup(intent.OperationId, f.Subject);
                Check(!f.Ops.IsRecoveryReady && before.SequenceEqual(f.ReadBytes()) && f.Journal.RecoveryReservedTerminalCount == 0,
                    "unsupported recovery inference never appends or repairs the journal");
                Check(after.State == known.State && after.MayHaveApplied == known.MayHaveApplied && after.Load!.LoadReturnValue == known.Load!.LoadReturnValue,
                    "failed readiness retains all known execution facts");
                Reject(() => f.Ops.IssueWriteContext(f.Subject), "JOURNAL_UNAVAILABLE");
            }
        }

        private static async Task CurrentGrantAndOldAudit()
        {
            Program.Stage = "current grant changes do not erase accepted recovery audit";
            using (var f = new Fixture())
            {
                var intent = await f.SeedAsync(Prefix.ReturnedFalse, "grant-change");
                await f.OpenRecovery();
                var oldSubject = f.Subject;
                await f.Grants.UpdateAsync("probe", GrantScope.Read, new string('b', 64), null);
                Reject(() => f.Ops.Lookup(intent.OperationId, oldSubject), "UNAUTHORIZED");
                Reject(() => f.Ops.IssueWriteContext(f.Subject), "UNAUTHORIZED");
                await Program.MustComplete(f.Ops.SealRecoveryAsync());
                VerifySnapshot(f.Ops.Lookup(intent.OperationId, f.Subject), Prefix.ReturnedFalse, "durable");
                var audit = f.Fold().Operations.Single().Intent;
                Check(f.Ops.IsRecoveryReady && audit.ProfileId == intent.ProfileId && audit.AuthorityEpoch == intent.AuthorityEpoch &&
                    audit.GrantEpoch == intent.GrantEpoch && audit.GrantEpoch != f.Subject.Epoch,
                    "internal sealing preserves original audit epochs without granting the reader Control");
                Reject(() => f.Ops.IssueWriteContext(f.Subject), "UNAUTHORIZED");
            }
        }

        private static async Task LostFinalAcknowledgement()
        {
            Program.Stage = "recovery final real flush followed by lost acknowledgement";
            using (var flush = new ControlledFlush { FailAt = 2 })
            using (var f = new Fixture())
            {
                var already = await f.SeedAsync(Prefix.ReturnedFalse, "already-final", existingFinal: true);
                var negative = await f.SeedAsync(Prefix.ReturnedFalse, "negative");
                var positive = await f.SeedAsync(Prefix.ReturnedTrue, "positive");
                await f.OpenRecovery(durability: flush);
                long before = f.Journal.CaptureConfirmedPrefix()!.ThroughSequence;
                Task sealing = f.Ops.SealRecoveryAsync();
                await Program.MustComplete(sealing);
                Check(flush.Calls == 2 && !f.Ops.IsRecoveryReady && !f.Journal.IsHealthy &&
                    f.Journal.CaptureConfirmedPrefix()!.ThroughSequence == before,
                    "lost flush acknowledgement does not advance confirmed prefix or retry remaining finals");
                VerifySnapshot(f.Ops.Lookup(already.OperationId, f.Subject), Prefix.ReturnedFalse, "durable");
                VerifySnapshot(f.Ops.Lookup(negative.OperationId, f.Subject), Prefix.ReturnedFalse, "unavailable");
                VerifySnapshot(f.Ops.Lookup(positive.OperationId, f.Subject), Prefix.ReturnedTrue, "unavailable");
                byte[] once = f.ReadBytes();
                Check(ReferenceEquals(sealing, f.Ops.SealRecoveryAsync()) && flush.Calls == 2 && once.SequenceEqual(f.ReadBytes()) &&
                    f.Journal.RecoveryReservedTerminalCount == 0 && f.Journal.RecoveryQueuedOrWriting == 0,
                    "same instance never retries an uncertain final and settles only completed IO credits");
                Reject(() => f.Ops.IssueWriteContext(f.Subject), "JOURNAL_UNAVAILABLE");

                await f.OpenRecovery();
                var recovered = f.Fold();
                Check(recovered.AllRecordsValid && recovered.ValidRecordCount == before + 1 && recovered.Operations.Count(item => item.Final != null) == 2,
                    "restart reads the actual flushed negative final despite the old lost ACK");
                VerifySnapshot(f.Ops.Lookup(negative.OperationId, f.Subject), Prefix.ReturnedFalse, "durable");
                VerifySnapshot(f.Ops.Lookup(positive.OperationId, f.Subject), Prefix.ReturnedTrue, "volatile");
                await Program.MustComplete(f.Ops.SealRecoveryAsync());
                Check(f.Fold().ValidRecordCount == before + 2 && f.Fold().Operations.All(item => item.Final != null),
                    "new recovery appends only the remaining final, never a duplicate terminal");
            }
        }

        private static void VerifySnapshot(OperationLookupSnapshot value, Prefix kind, string durability)
        {
            string state = kind == Prefix.Accepted || kind == Prefix.Cancelled ? "cancelled" :
                kind == Prefix.ReturnedFalse || kind == Prefix.Faulted ? "failed" : "unknown";
            var physical = Physical(kind);
            bool? returned = ReturnValue(kind);
            Check(value.Kind == "found" && value.State == state && value.Durability == durability && value.MayHaveApplied == (kind != Prefix.Accepted) &&
                value.Load!.PhysicalTaskState == physical && value.Load.LoadReturnValue == returned && !value.IsObservationCurrent &&
                !value.Load.CorrelatedTargetReady && !value.Load.HistoricalCompletionProven && value.Load.ObservationContext == null &&
                value.Load.TargetMatch == LoadTargetMatch.Unknown && value.Load.ObservationEnd == LoadObservationEnd.OwnerStopped,
                "recovery preserves " + kind + " facts as " + durability);
        }

        private static void VerifyFinal(JournalOperationFacts fact, OperationIntent original, Prefix kind)
        {
            var submission = kind == Prefix.Accepted ? SubmissionKnowledge.NotSubmitted : kind == Prefix.Commit ? SubmissionKnowledge.Unknown : SubmissionKnowledge.Submitted;
            Check(fact.Final != null && fact.Final.Submission == submission && fact.Intent.OperationId == original.OperationId &&
                fact.Intent.Key.Equals(original.Key) && fact.Intent.CommandHash == original.CommandHash &&
                fact.Intent.Target.SameIdentity(original.Target) && fact.Intent.Target.ContentVersion == original.Target.ContentVersion &&
                fact.Intent.Source.InstanceId == original.Source.InstanceId && fact.Intent.Source.Generation == original.Source.Generation &&
                fact.Intent.Source.CityId == original.Source.CityId && fact.Intent.Source.State == original.Source.State &&
                (kind == Prefix.Accepted ? fact.Commit == null : fact.Commit != null) &&
                (kind == Prefix.Accepted || kind == Prefix.Commit ? fact.Native == null : fact.Native != null && fact.Native.LoadReturnValue == ReturnValue(kind) &&
                    fact.Native.BoundTarget != null && fact.Native.BoundTarget.SameIdentity(Target) && fact.Native.BoundTarget.ContentVersion == Target.ContentVersion),
                "persisted " + kind + " final has explicit submission knowledge and unchanged source chain");
        }

        private static NativeLoadTaskState Physical(Prefix kind) => kind == Prefix.Accepted ? NativeLoadTaskState.NotStarted :
            kind == Prefix.Commit ? NativeLoadTaskState.Unknown : kind == Prefix.Faulted ? NativeLoadTaskState.Faulted :
            kind == Prefix.Cancelled ? NativeLoadTaskState.Cancelled : NativeLoadTaskState.RanToCompletion;
        private static bool? ReturnValue(Prefix kind) => kind == Prefix.ReturnedFalse ? false : kind == Prefix.ReturnedTrue ? true : (bool?)null;
        private enum Prefix { Accepted, Commit, ReturnedFalse, Faulted, Cancelled, ReturnedTrue }

        private sealed class HostProof : INativeHostIdentitySource
        {
            private readonly NativeHostState state;
            internal HostProof(NativeHostIdentity identity, NativeHostState state) { Current = identity; this.state = state; }
            public NativeHostIdentity Current { get; }
            public NativeHostState Observe(NativeHostIdentity identity)
            { if (!Current.Equals(identity)) throw new InvalidOperationException("Unexpected fixture native identity."); return state; }
        }

        private sealed class ControlledFlush : IJournalDurability, IDisposable
        {
            internal int PauseAt, FailAt;
            private int calls;
            internal int Calls => Volatile.Read(ref calls);
            internal readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
            public void Flush(FileStream file)
            {
                int call = Interlocked.Increment(ref calls);
                if (call == PauseAt) { Entered.TrySetResult(true); if (!Release.Wait(5000)) throw new TimeoutException("Fixture flush deadline."); }
                file.Flush(true);
                if (call == FailAt) throw new IOException("Synthetic lost acknowledgement after actual recovery flush.");
            }
            public void Dispose() { Release.Set(); Release.Dispose(); }
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string root = Path.Combine(Path.GetFullPath("artifacts"), "recovery-sealing-" + Guid.NewGuid().ToString("N"));
            private readonly PrivateConnectionStorage boundary;
            private readonly EnvironmentLease lease;
            internal readonly NativeHostIdentity NativeHost = new NativeHostIdentity(101, 123456);
            internal OperationJournal Journal { get; private set; }
            internal OperationAuthority Ops { get; private set; } = null!;
            internal GrantAuthority Grants { get; }
            internal GrantSnapshot Subject => Grants.Read("probe")!;
            internal string JournalPath => Path.Combine(boundary.DirectoryPath, "operations.journal");
            internal Fixture()
            {
                Directory.CreateDirectory(root);
                boundary = new PrivateConnectionStorage(Path.Combine(root, "connection"));
                lease = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority()!;
                var store = new AtomicAuthorizationStore(boundary, lease, "probe");
                store.InitializeEmpty(); var activated = store.Load().Activate(); store.Save(1, activated);
                Grants = new GrantAuthority(activated, store);
                Grants.UpdateAsync("probe", GrantScope.Read | GrantScope.Control, new string('a', 64), null).GetAwaiter().GetResult();
                Journal = new OperationJournal(boundary, lease, "probe", true);
            }
            internal async Task<OperationIntent> SeedAsync(Prefix kind, string id, bool existingFinal = false, bool invalidFinal = false, string contextId = "original-context")
            {
                var source = new WorldContextSnapshot("original-instance", 7, "ready", "original-city");
                var intent = new OperationIntent(id, source.InstanceId, new SubmissionKey(contextId, id),
                    OperationCommandHash.Compute(source, Target), "probe", Subject.AuthorityEpoch, Subject.Epoch, source, Target);
                using (var reservation = Journal.TryReserveOperation() ?? throw new InvalidOperationException("Fixture seed capacity unavailable."))
                {
                    await reservation.AppendAsync(new OperationAccepted(intent), JournalSlot.Normal);
                    if (kind != Prefix.Accepted) await reservation.AppendAsync(new CommitIntended(id, Target, NativeHost), JournalSlot.Normal);
                    if (kind != Prefix.Accepted && kind != Prefix.Commit)
                        await reservation.AppendAsync(new NativeTerminated(id, Physical(kind), ReturnValue(kind), Target), JournalSlot.NativeTerminal);
                    if (existingFinal || invalidFinal)
                    {
                        var facts = new PersistedLoadFacts(invalidFinal ? NativeLoadTaskState.Unknown : Physical(kind),
                            invalidFinal ? null : ReturnValue(kind), LoadTargetMatch.Unknown, false, false, LoadObservationEnd.OwnerStopped, null);
                        await reservation.AppendAsync(new OperationFinished(id, invalidFinal ? "unknown" : "failed", true,
                            invalidFinal ? SubmissionKnowledge.Unknown : SubmissionKnowledge.Submitted, "FIXTURE_FINAL", facts), JournalSlot.Terminal);
                    }
                }
                return intent;
            }
            internal async Task OpenRecovery(IJournalDurability? durability = null, NativeHostState hostState = NativeHostState.Ended, bool brokenTail = false)
            {
                if (Ops == null) await Program.MustComplete(Journal.StopAsync());
                else await Program.MustComplete(Ops.CloseAsync());
                if (brokenTail) using (var file = new FileStream(JournalPath, FileMode.Append, FileAccess.Write)) { file.WriteByte(83); file.Flush(true); }
                Journal = new OperationJournal(boundary, lease, "probe", false, durability);
                var context = new WorldContextTracker(Guid.NewGuid().ToString("N"));
                context.ObserveReady(context.EnterLoading(), "save-b");
                Ops = new OperationAuthority(Journal, Grants, context, new HostProof(NativeHost, hostState));
            }
            internal JournalFactsFoldResult Fold() => JournalFactsFold.Fold(Journal.CaptureConfirmedPrefix()!.Records);
            internal byte[] ReadBytes()
            {
                // 只在 seal 已收束/尚未开始时取证；兼容仍持有写句柄的真实 journal。
                using (var file = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new BinaryReader(file))
                {
                    if (file.Length > OperationLimits.MaximumJournalBytes) throw new IOException("Fixture journal exceeds budget.");
                    int length = checked((int)file.Length);
                    byte[] result = reader.ReadBytes(length);
                    if (result.Length != length || file.ReadByte() != -1) throw new IOException("Fixture journal changed during capture.");
                    return result;
                }
            }
            public void Dispose()
            {
                if (Ops != null) Program.MustComplete(Ops.CloseAsync()).GetAwaiter().GetResult();
                Program.MustComplete(Journal.StopAsync()).GetAwaiter().GetResult();
                Grants.StopAsync().GetAwaiter().GetResult(); lease.Dispose();
                string absolute = Path.GetFullPath(root);
                if (Path.GetDirectoryName(absolute) != Path.GetFullPath("artifacts") ||
                    !Path.GetFileName(absolute).StartsWith("recovery-sealing-", StringComparison.Ordinal) ||
                    (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
                    (File.GetAttributes(boundary.DirectoryPath) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Fixture cleanup containment invalid.");
                foreach (string path in Directory.GetFiles(boundary.DirectoryPath))
                {
                    string name = Path.GetFileName(path);
                    if ((name != "authority.lock" && name != "authorization.json" && name != "operations.journal") ||
                        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unexpected recovery fixture file.");
                    File.Delete(path);
                }
                Directory.Delete(boundary.DirectoryPath); Directory.Delete(root);
            }
        }
    }
}
