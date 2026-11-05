using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Operations;
using SkylinesLink.Application.Simulation;
using SkylinesLink.Contracts;
using SkylinesLink.Infrastructure.Connection;
using SkylinesLink.Infrastructure.Operations;
using SkylinesLink.Mod.Connection;

namespace SkylinesLink.RuntimeProbe
{
    /// <summary>真实受控文件上的owned maintenance；业务退休资格另由Operations验证。</summary>
    internal static class JournalRotationTests
    {
        private static int checks;
        private static void Check(bool condition, string label) { Program.Assert(condition, label); checks++; }
        internal static async Task<int> RunAsync()
        {
            checks = 0;
            await SameWriterAndSuccessiveGenerations();
            await LegacyReaderDoesNotInterpretRetiredBank();
            await BoundedAdmissionAndLegacy();
            await RealWorkAndRecoveryReservation();
            await StopBeforeAndAfterSeal();
            await FailedFlushAndPartialActivation();
            await ForwardPreparedRequiresActualRecoveryConfirmation();
            await CorruptLegacyKeepsFactsAndCloses();
            return checks;
        }

        private static async Task LegacyReaderDoesNotInterpretRetiredBank()
        {
            Program.Stage = "legacy composition cannot interpret bank retention without coverage support";
            using (var f = new Fixture())
            {
                await f.AddFull("keep"); await f.AddFull("retire"); var source = f.Position();
                var rotation = f.Journal.TryStartRotation(new JournalRotationPlan(source.StoreId, source.ThroughSequence, new[] { "keep" }))!;
                await Program.MustComplete(rotation.Completion);
                string store = Header((await rotation.Completion).Prefix);
                await f.Journal.StopAsync(); byte[] before = File.ReadAllBytes(f.JournalPath);
                await f.OpenLegacyReader();
                Check(f.Journal.Recovery.Incompatible && !f.Journal.IsHealthy && f.Journal.Recovery.Records.Count == 0 &&
                    f.Journal.TryReserveOperation() == null,
                    "public legacy-only constructor refuses a retired bank rather than exposing unqualified absent results");
                await f.Journal.StopAsync();
                Check(before.SequenceEqual(File.ReadAllBytes(f.JournalPath)), "legacy-only bank refusal preserves the exact source file");
                await f.Reopen();
                Check(f.Journal.Recovery.Complete && f.Journal.Recovery.DurableConfirmed && f.Journal.Recovery.RetentionApplied && f.Position().StoreId == store,
                    "explicit coverage-capable bank owner can reopen after the legacy-only reader actually stops");
            }
        }

        private static async Task SameWriterAndSuccessiveGenerations()
        {
            Program.Stage = "journal owned rotation preserves facade, complete facts and coverage";
            using (var f = new Fixture())
            {
                OperationJournal facade = f.Journal; Task lifetime = facade.Completion;
                int notifications = 0;
                using (facade.ObserveConfirmed(() => Interlocked.Increment(ref notifications)))
                {
                    await f.AddFull("a"); await f.AddFull("b"); await f.AddFull("c");
                    await Until(() => Volatile.Read(ref notifications) == 12);
                    var first = f.Position();
                    long? originalBytes = facade.CaptureConfirmedPrefix()!.ThroughBytes;
                    var rotation = facade.TryStartRotation(new JournalRotationPlan(first.StoreId, first.ThroughSequence, new[] { "b", "c" }))!;
                    await Program.MustComplete(rotation.Completion);
                    JournalRotationResult result = await rotation.Completion;
                    await Until(() => Volatile.Read(ref notifications) == 13);
                    var facts = JournalFactsFold.Fold(result.Prefix.Records);
                    Check(result.Outcome == JournalRotationOutcome.Installed && result.RetentionApplied &&
                        facts.AllRecordsValid && facts.StoreId != first.StoreId && result.Prefix.ThroughSequence == 9,
                        "rotation changes store with a smaller consecutive prefix and durable retention coverage");
                    Check(result.Prefix.ThroughBytes == f.ReadBodyBytes() && facade.CaptureConfirmedPrefix()!.ThroughBytes == result.Prefix.ThroughBytes &&
                        result.Prefix.ThroughBytes.GetValueOrDefault() < originalBytes.GetValueOrDefault(),
                        "installed retirement publishes the actual smaller new-store body bytes rather than the prior source or container EOF");
                    Check(facts.Operations.Select(item => item.Intent.OperationId).SequenceEqual(new[] { "b", "c" }) &&
                        facts.Operations.All(item => item.Commit != null && item.Native!.LoadReturnValue == false &&
                            item.Final!.Load.LoadReturnValue == false && item.Final.MayHaveApplied && item.Final.State == "failed"),
                        "retained operations preserve the entire accepted/commit/native/final chain and actual false result");
                    Check(ReferenceEquals(facade, f.Journal) && ReferenceEquals(lifetime, facade.Completion) &&
                        !lifetime.IsCompleted && Volatile.Read(ref notifications) == 13,
                        "same facade, worker Completion and original observer remain active after installation");
                    Check(facade.TryStartRotation(new JournalRotationPlan(first.StoreId, first.ThroughSequence, new[] { "b" })) == null,
                        "the exact old source watermark cannot start a second maintenance attempt");
                    await f.AddFull("d");
                    var second = f.Position();
                    rotation = facade.TryStartRotation(new JournalRotationPlan(second.StoreId, second.ThroughSequence, new[] { "c" }))!;
                    await Program.MustComplete(rotation.Completion);
                    var next = await rotation.Completion;
                    Check(next.Outcome == JournalRotationOutcome.Installed && next.RetentionApplied && next.Prefix.ThroughSequence == 5 &&
                        Header(next.Prefix) != second.StoreId && JournalFactsFold.Fold(next.Prefix.Records).Operations.Single().Intent.OperationId == "c",
                        "second bank reuse retires another chain without resetting inherited coverage");
                    Check(next.Prefix.ThroughBytes == f.ReadBodyBytes() && facade.CaptureConfirmedPrefix()!.ThroughBytes == next.Prefix.ThroughBytes,
                        "a second bank reuse also binds the retained prefix to its exact actual frame extent");
                    await f.AddFull("e");
                    string installed = f.Position().StoreId;
                    using (var reopenFlush = new ControlledFlush())
                    {
                        await f.Reopen(reopenFlush);
                        Check(reopenFlush.ActualFlushes == 1 && f.Journal.Recovery.Complete && f.Journal.Recovery.DurableConfirmed &&
                            f.Journal.Recovery.RetentionApplied && f.Position().StoreId == installed &&
                            JournalFactsFold.Fold(f.Journal.Recovery.Records).Operations.Select(item => item.Intent.OperationId).SequenceEqual(new[] { "c", "e" }),
                            "reopen confirms the selected bank with real Flush before exposing durable retained facts");
                        Check(f.Journal.CaptureConfirmedPrefix()!.ThroughBytes == f.ReadBodyBytes(),
                            "new-owner bank recovery preserves the actual selected body byte watermark");
                    }
                    Check(new FileInfo(f.JournalPath).Length == JournalBankFormat.FileBytes,
                        "multiple generations keep the one fixed physical container length");
                }
            }
        }

        private static async Task BoundedAdmissionAndLegacy()
        {
            Program.Stage = "journal rotation finite capacity and no-IO plan rejections";
            using (var f = new Fixture())
            {
                for (int i = 0; i < OperationLimits.MaximumRetained; i++) await f.AddFull("full-" + i);
                var position = f.Position(); int writes = f.Stream.WriteCalls;
                Check(position.ThroughSequence == 129 && f.Journal.TryReserveOperation() == null,
                    "all 32 complete chains consume exactly the original 129-frame business bound");
                bool mixedRejected = false;
                try { f.Journal.TryStartRotation(new JournalRotationPlan(position.StoreId, position.ThroughSequence,
                    new[] { "full-31" }, new[] { "task-not-supported" })); }
                catch (ArgumentException) { mixedRejected = true; }
                Check(mixedRejected && f.Stream.WriteCalls == writes && f.Journal.IsHealthy,
                    "legacy public owner cannot silently ignore task retention identities or activate mixed maintenance");
                bool rejected = false;
                try { f.Journal.TryStartRotation(new JournalRotationPlan(position.StoreId, position.ThroughSequence, new[] { "not-recorded" })); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected && f.Stream.WriteCalls == writes && f.Journal.IsHealthy,
                    "unknown retained identity is rejected without file IO or poisoning the writer");
                var all = Enumerable.Range(0, 32).Select(i => "full-" + i).ToArray();
                Check(f.Journal.TryStartRotation(new JournalRotationPlan(position.StoreId, position.ThroughSequence, all)) == null && f.Stream.WriteCalls == writes,
                    "zero-yield maintenance does not rewrite a full healthy store");
                var work = f.Journal.TryStartRotation(new JournalRotationPlan(position.StoreId, position.ThroughSequence, new[] { "full-31" }))!;
                await Program.MustComplete(work.Completion);
                using (var restored = f.Journal.TryReserveOperation())
                    Check((await work.Completion).Prefix.ThroughSequence == 5 && restored != null,
                        "one approved retained chain restores bounded reservation capacity");
            }
            using (var f = new Fixture(banked: false))
            {
                await f.AddFull("legacy-a"); await f.AddFull("legacy-b");
                var position = f.Position(); int writes = f.Stream.WriteCalls;
                Check(!position.SupportsRotation && !position.RetentionApplied &&
                    f.Journal.TryStartRotation(new JournalRotationPlan(position.StoreId, position.ThroughSequence, new[] { "legacy-b" })) == null &&
                    writes == f.Stream.WriteCalls && new FileInfo(f.JournalPath).Length < OperationLimits.MaximumJournalBytes,
                    "default legacy journal stays strict SLJO2 and never pretends to support bank rotation");
            }
        }

        private static async Task RealWorkAndRecoveryReservation()
        {
            Program.Stage = "journal rotation rejects owned reservations, active IO and recovery sealing";
            using (var flush = new ControlledFlush())
            using (var f = new Fixture(flush))
            {
                await f.AddFull("keep"); await f.AddFull("retire");
                var position = f.Position();
                var plan = new JournalRotationPlan(position.StoreId, position.ThroughSequence, new[] { "keep" });
                using (var held = f.Journal.TryReserveOperation()!)
                    Check(f.Journal.TryStartRotation(plan) == null && f.Journal.ReservedOperationCount == 1,
                        "an unused reservation is still an owned obligation and prevents maintenance");
                flush.Arm(1);
                var reservation = f.Journal.TryReserveOperation()!;
                Task<long> append = reservation.AppendAsync(new OperationAccepted(Intent("pending")), JournalSlot.Normal);
                reservation.Dispose();
                try
                {
                    await Program.MustComplete(flush.Entered.Task);
                    Check(!append.IsCompleted && f.Journal.QueuedOrWriting == 1 && f.Journal.TryStartRotation(plan) == null,
                        "queue ownership persists until the actual paused Flush ends, not just reservation Dispose");
                }
                finally { flush.Release.Set(); await Program.MustComplete(append); }
                await f.Reopen();
                var recovered = f.Position();
                using (var sealing = f.Journal.TryReserveRecoveryTerminals(recovered.ThroughSequence, 1)!)
                    Check(sealing != null && f.Journal.TryStartRotation(new JournalRotationPlan(recovered.StoreId, recovered.ThroughSequence, new[] { "keep" })) == null,
                        "a recovery-terminal reservation blocks rotation before its final work is queued");
            }
        }

        private static async Task StopBeforeAndAfterSeal()
        {
            Program.Stage = "journal maintenance Stop joins actual preparation and admitted seal handoff";
            foreach (bool afterSeal in new[] { false, true })
            using (var flush = new ControlledFlush())
            using (var f = new Fixture(flush))
            {
                await f.AddFull("keep"); await f.AddFull("retire");
                var source = f.Position(); Task lifetime = f.Journal.Completion;
                flush.Arm(afterSeal ? 4 : 1);
                IJournalRotation work = f.Journal.TryStartRotation(new JournalRotationPlan(source.StoreId, source.ThroughSequence, new[] { "keep" }))!;
                Task? close = null;
                try
                {
                    await Program.MustComplete(flush.Entered.Task);
                    Task<bool> leasePulse = f.PulseLease();
                    await Program.MustComplete(leasePulse, 1000);
                    Check(await leasePulse, "slow bank Flush does not monopolize the environment lease monitor");
                    work.RequestStop(); close = f.Journal.StopAsync();
                    Check(ReferenceEquals(close, lifetime) && !work.Completion.IsCompleted && !close.IsCompleted &&
                        f.Journal.TryReserveOperation() == null && f.Journal.TryStartRotation(new JournalRotationPlan(source.StoreId, source.ThroughSequence, new[] { "keep" })) == null,
                        "Stop closes new admission but keeps maintenance and actual worker ownership until IO ends");
                }
                finally { flush.Release.Set(); await Program.MustComplete(work.Completion); if (close != null) await Program.MustComplete(close); }
                var result = await work.Completion;
                Check(result.Outcome == (afterSeal ? JournalRotationOutcome.Installed : JournalRotationOutcome.CurrentPreserved) &&
                    (Header(result.Prefix) != source.StoreId) == afterSeal,
                    "Stop before seal preserves the proved source; after seal completes that single finite handoff");
                await f.Reopen();
                Check(f.Journal.Recovery.DurableConfirmed && f.Position().StoreId == Header(result.Prefix) &&
                    f.Journal.Recovery.RetentionApplied == afterSeal,
                    "reopen selects exactly the stopped maintenance outcome after a new recovery Flush");
            }
        }

        private static async Task FailedFlushAndPartialActivation()
        {
            Program.Stage = "journal maintenance unknown IO never revives its owner or repairs a partial receipt";
            using (var flush = new ControlledFlush())
            using (var f = new Fixture(flush))
            {
                await f.AddFull("keep"); await f.AddFull("retire"); var source = f.Position();
                flush.Arm(1, failAfterFlush: true);
                var work = f.Journal.TryStartRotation(new JournalRotationPlan(source.StoreId, source.ThroughSequence, new[] { "keep" }))!;
                flush.Release.Set(); await ExpectPersistence(work.Completion);
                int writes = f.Stream.WriteCalls;
                Check(!f.Journal.IsHealthy && f.Journal.TryReserveOperation() == null &&
                    f.Journal.TryStartRotation(new JournalRotationPlan(source.StoreId, source.ThroughSequence, new[] { "keep" })) == null && f.Stream.WriteCalls == writes,
                    "a real Flush followed by lost acknowledgement remains sticky and does not retry or restore the same owner");
                await f.Reopen();
                Check(f.Journal.Recovery.DurableConfirmed && f.Position().StoreId == source.StoreId && !f.Journal.Recovery.RetentionApplied,
                    "only a new owner with actual recovery Flush may select the intact unsealed source");
            }
            using (var f = new Fixture())
            {
                await f.AddFull("keep"); await f.AddFull("retire"); var source = f.Position();
                f.Stream.PartialActivation = true;
                var work = f.Journal.TryStartRotation(new JournalRotationPlan(source.StoreId, source.ThroughSequence, new[] { "keep" }))!;
                await ExpectPersistence(work.Completion);
                Check(f.Stream.PartialBytes == 71 && !f.Journal.IsHealthy, "actual activation prefix write failure stops the original writer");
                await f.Journal.StopAsync(); byte[] before = File.ReadAllBytes(f.JournalPath);
                await f.Reopen();
                Check(!f.Journal.Recovery.Complete && !f.Journal.IsHealthy && f.Journal.TryReserveOperation() == null,
                    "reopen never repairs a nonempty partial activation or rolls back to the sealed source");
                await f.Journal.StopAsync();
                Check(before.SequenceEqual(File.ReadAllBytes(f.JournalPath)), "failed bank recovery leaves the exact partial receipt bytes unchanged");
            }
        }

        private static async Task ForwardPreparedRequiresActualRecoveryConfirmation()
        {
            Program.Stage = "forward prepared recovery confirms the existing prefix before any activation";
            using (var sealFlush = new ControlledFlush())
            using (var f = new Fixture(sealFlush))
            {
                await f.AddFull("keep"); await f.AddFull("retire"); var source = f.Position();
                sealFlush.Arm(4, failAfterFlush: true); sealFlush.Release.Set();
                var rotation = f.Journal.TryStartRotation(new JournalRotationPlan(source.StoreId, source.ThroughSequence, new[] { "keep" }))!;
                await ExpectPersistence(rotation.Completion);
                int writes = f.Stream.WriteCalls;
                Check(!f.Journal.IsHealthy && f.Journal.TryReserveOperation() == null &&
                    f.Journal.TryStartRotation(new JournalRotationPlan(source.StoreId, source.ThroughSequence, new[] { "keep" })) == null && f.Stream.WriteCalls == writes,
                    "lost acknowledgement after real seal Flush never retries activation or revives that owner");
                await f.Journal.StopAsync();
                byte[] sealedBytes = File.ReadAllBytes(f.JournalPath);
                string successor;
                using (var file = new FileStream(f.JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var selected = JournalBankReader.Read(file, "probe");
                    Check(selected.Kind == JournalBankSelectionKind.ForwardPrepared && selected.Current.Descriptor.StoreId == source.StoreId &&
                        selected.Current.Seal != null && selected.Candidate!.Activation == null && selected.Candidate.Records.Count == 5,
                        "actual failed handoff leaves a sealed full source and exact prepared successor without activation");
                    successor = selected.Candidate!.Descriptor.StoreId;
                }
                using (var failRecovery = new ControlledFlush())
                {
                    failRecovery.Arm(1, failAfterFlush: true); failRecovery.Release.Set();
                    await f.Reopen(failRecovery);
                    var facts = JournalFactsFold.Fold(f.Journal.Recovery.Records);
                    Check(failRecovery.ActualFlushes == 1 && f.Journal.Recovery.Complete && !f.Journal.Recovery.DurableConfirmed &&
                        !f.Journal.IsHealthy && f.Journal.TryReserveOperation() == null && f.Journal.CaptureConfirmedPrefix() == null,
                        "failed recovery Flush keeps readable complete facts without confirming durability or reopening admission");
                    Check(facts.StoreId == source.StoreId && facts.AllRecordsValid && facts.Operations.Count == 2 &&
                        facts.Operations.All(item => item.Native!.LoadReturnValue == false && item.Final!.Load.LoadReturnValue == false),
                        "recovery acknowledgement loss preserves full original native false facts rather than installing the successor");
                    await f.Journal.StopAsync();
                    Check(sealedBytes.SequenceEqual(File.ReadAllBytes(f.JournalPath)),
                        "failed pre-activation recovery confirmation leaves the exact sealed/prepared bytes unchanged");
                }
                using (var confirmedRecovery = new ControlledFlush())
                {
                    await f.Reopen(confirmedRecovery);
                    var prefix = f.Journal.CaptureConfirmedPrefix()!;
                    var facts = JournalFactsFold.Fold(prefix.Records);
                    Check(confirmedRecovery.ActualFlushes == 2 && f.Journal.Recovery.Complete && f.Journal.Recovery.DurableConfirmed &&
                        f.Journal.Recovery.RetentionApplied && f.Journal.IsHealthy && Header(prefix) == successor && prefix.ThroughSequence == 5,
                        "forward recovery needs both actual existing-prefix Flush and new activation Flush before installing the retained store");
                    Check(facts.AllRecordsValid && facts.Operations.Single().Intent.OperationId == "keep" &&
                        facts.Operations[0].Native!.LoadReturnValue == false && facts.Operations[0].Final!.Load.LoadReturnValue == false,
                        "forward recovery copies the retained complete chain without changing its native result");
                }
                using (var activeRecovery = new ControlledFlush())
                {
                    await f.Reopen(activeRecovery);
                    Check(activeRecovery.ActualFlushes == 1 && f.Journal.Recovery.DurableConfirmed && f.Position().StoreId == successor &&
                        f.Journal.Recovery.RetentionApplied && f.Journal.Recovery.Records.Count == 5,
                        "later active reopen uses one recovery Flush and never creates another successor or activation");
                }
            }
        }

        private static async Task CorruptLegacyKeepsFactsAndCloses()
        {
            Program.Stage = "legacy corrupt frame keeps proven prefix and closes the real file";
            foreach (bool corruptMagic in new[] { false, true })
            using (var f = new Fixture(banked: false))
            {
                await f.AddFull("complete"); await f.AddFull("last");
                await f.Journal.StopAsync();
                long completeFrameBytes;
                using (var file = new FileStream(f.JournalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    file.Position = 0;
                    for (int i = 0; i < 8; i++) JournalFrameCodec.Read(file);
                    completeFrameBytes = file.Position;
                    long position = corruptMagic ? completeFrameBytes : file.Length - 1;
                    file.Position = position; int value = file.ReadByte(); file.Position = position; file.WriteByte((byte)(value ^ 0x41)); file.Flush(true);
                }
                byte[] before = File.ReadAllBytes(f.JournalPath);
                await f.Reopen();
                Check(!f.Journal.Recovery.Complete && !f.Journal.IsHealthy && f.Journal.Recovery.Records.Count == 8 &&
                    JournalFactsFold.Fold(f.Journal.Recovery.Records).Operations[0].Final?.State == "failed",
                    "bad trailing magic or SHA preserves the valid earlier chain without accepting new writes");
                Check(f.Journal.CaptureConfirmedPrefix()!.ThroughBytes == completeFrameBytes && completeFrameBytes < before.Length,
                    "a fully consumed bad-SHA frame and a bad magic both stop confirmed bytes at the previous complete frame");
                await Program.MustComplete(f.Journal.StopAsync());
                using (var exclusive = new FileStream(f.JournalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Check(exclusive.Length == before.Length, "unavailable legacy recovery releases its actual file after Stop");
                Check(before.SequenceEqual(File.ReadAllBytes(f.JournalPath)), "legacy rejection never truncates or repairs the rejected suffix");
            }
        }

        private static string Header(ConfirmedJournalPrefix prefix) => ((StoreOpened)prefix.Records[0].Entry).StoreId;
        private static async Task Until(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition()) { if (watch.ElapsedMilliseconds > 3000) throw new InvalidOperationException("Rotation observation deadline."); await Task.Delay(1); }
        }
        private static async Task ExpectPersistence(Task work)
        {
            try { await Program.MustComplete(work); }
            catch (JournalPersistenceException) { Check(true, "maintenance reports the domain persistence failure"); return; }
            throw new InvalidOperationException("Expected maintenance persistence failure.");
        }
        private static OperationIntent Intent(string id)
        {
            var source = new WorldContextSnapshot("rotation-instance", 3, "menu", null);
            var target = new LoadTarget("probe", "save-" + id + "城市", "v1");
            return new OperationIntent(id, source.InstanceId, new SubmissionKey("retired-source-context", id),
                OperationCommandHash.Compute(source, target), "probe", 1, 1, source, target);
        }
        private sealed class ControlledFlush : IJournalDurability, IDisposable
        {
            private int calls, actualFlushes, pauseAt;
            private bool failAfterFlush;
            internal int ActualFlushes => Volatile.Read(ref actualFlushes);
            internal readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
            internal void Arm(int relativeCall, bool failAfterFlush = false)
            { this.failAfterFlush = failAfterFlush; Volatile.Write(ref pauseAt, Volatile.Read(ref calls) + relativeCall); }
            public void Flush(FileStream file)
            {
                int call = Interlocked.Increment(ref calls);
                bool selected = call == Volatile.Read(ref pauseAt);
                if (selected) { Entered.TrySetResult(true); if (!Release.Wait(5000)) throw new TimeoutException("Rotation fixture Flush deadline."); }
                file.Flush(true); Interlocked.Increment(ref actualFlushes);
                if (selected && failAfterFlush) throw new IOException("Synthetic lost acknowledgement after actual maintenance Flush.");
            }
            public void Dispose() { Release.Set(); Release.Dispose(); }
        }
        private sealed class Fixture : IDisposable
        {
            private readonly string root = Path.Combine(Path.GetFullPath("artifacts"), "journal-rotation-" + Guid.NewGuid().ToString("N"));
            private readonly GuardedBoundary boundary;
            private readonly EnvironmentLease lease;
            private readonly bool banked;
            internal OperationJournal Journal { get; private set; }
            internal GuardedStream Stream => boundary.Stream!;
            internal string JournalPath => Path.Combine(boundary.DirectoryPath, "operations.journal");
            internal Fixture(IJournalDurability? flush = null, bool banked = true)
            {
                this.banked = banked;
                Directory.CreateDirectory(root);
                boundary = new GuardedBoundary(new PrivateConnectionStorage(Path.Combine(root, "connection")));
                lease = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority()!;
                Journal = new OperationJournal(boundary, lease, "probe", true, flush, banked);
            }
            internal JournalRotationPosition Position() => Journal.CaptureRotationPosition() ?? throw new InvalidOperationException("Rotation position unavailable.");
            internal long ReadBodyBytes()
            {
                using (var stream = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    return JournalBankReader.Read(stream, "probe").Current.ThroughBytes;
            }
            internal async Task AddFull(string id)
            {
                var intent = Intent(id);
                using (var reservation = Journal.TryReserveOperation() ?? throw new InvalidOperationException("Rotation fixture capacity unavailable."))
                {
                    await reservation.AppendAsync(new OperationAccepted(intent), JournalSlot.Normal);
                    await reservation.AppendAsync(new CommitIntended(id, intent.Target, new NativeHostIdentity(101, 123456)), JournalSlot.Normal);
                    await reservation.AppendAsync(new NativeTerminated(id, NativeLoadTaskState.RanToCompletion, false, null), JournalSlot.NativeTerminal);
                    var facts = new PersistedLoadFacts(NativeLoadTaskState.RanToCompletion, false, LoadTargetMatch.Unknown, false, false, LoadObservationEnd.OwnerStopped, null);
                    await reservation.AppendAsync(new OperationFinished(id, "failed", true, SubmissionKnowledge.Submitted, "LOAD_RETURNED_FALSE", facts), JournalSlot.Terminal);
                }
            }
            internal async Task Reopen(IJournalDurability? flush = null)
            { await Program.MustComplete(Journal.StopAsync()); Journal = new OperationJournal(boundary, lease, "probe", false, flush, banked); }
            internal async Task OpenLegacyReader()
            { await Program.MustComplete(Journal.StopAsync()); Journal = new OperationJournal(boundary, lease, "probe", false); }
            internal Task<bool> PulseLease() => Task.Run(() => lease.WhileHeld(() => true));
            public void Dispose()
            {
                Journal.StopAsync().GetAwaiter().GetResult(); lease.Dispose();
                if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath("artifacts") ||
                    !Path.GetFileName(root).StartsWith("journal-rotation-", StringComparison.Ordinal)) throw new IOException("Rotation fixture containment invalid.");
                foreach (string directory in new[] { boundary.DirectoryPath, root })
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Rotation fixture refuses directory links.");
                foreach (string path in Directory.GetFiles(boundary.DirectoryPath))
                { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Rotation fixture refuses file links."); File.Delete(path); }
                Directory.Delete(boundary.DirectoryPath); Directory.Delete(root);
            }
        }
        private sealed class GuardedBoundary : IPrivateStorageBoundary
        {
            private readonly IPrivateStorageBoundary inner;
            internal GuardedBoundary(IPrivateStorageBoundary inner) { this.inner = inner; }
            public string DirectoryPath => inner.DirectoryPath;
            public void FlushToDisk(FileStream file) => inner.FlushToDisk(file);
            internal GuardedStream? Stream { get; private set; }
            public void ValidateDirectory() => inner.ValidateDirectory();
            public void ValidateFile(string path, bool mayBeMissing) => inner.ValidateFile(path, mayBeMissing);
            public FileStream CreatePrivateFile(string path)
            {
                if (Path.GetFileName(path) != "operations.journal") return inner.CreatePrivateFile(path);
                using (inner.CreatePrivateFile(path)) { }
                return Stream = new GuardedStream(path);
            }
        }
        private sealed class GuardedStream : FileStream
        {
            private int writeCalls;
            internal bool PartialActivation;
            internal int PartialBytes { get; private set; }
            internal int WriteCalls => Volatile.Read(ref writeCalls);
            internal GuardedStream(string path) : base(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.None) { }
            public override void Write(byte[] buffer, int offset, int count)
            {
                Interlocked.Increment(ref writeCalls);
                if (PartialActivation && count == JournalBankFormat.SlotBytes && buffer[offset] == 'S' && buffer[offset + 3] == 'C')
                {
                    using (var source = new MemoryStream(buffer, offset, count, false))
                    {
                        var control = JournalControlCodec.Read(source, 0);
                        if (control?.Value.Entry is JournalControlEntryActivation activation && activation.Generation > 0)
                        {
                            PartialActivation = false; base.Write(buffer, offset, 71); PartialBytes = 71;
                            throw new IOException("Synthetic partial activation receipt after real prefix write.");
                        }
                    }
                }
                base.Write(buffer, offset, count);
            }
        }
    }
}
