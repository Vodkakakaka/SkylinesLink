using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Operations;
using SkylinesLink.Application.Simulation;
using SkylinesLink.Infrastructure.Connection;
using SkylinesLink.Infrastructure.Operations;
using SkylinesLink.Mod.Connection;
using SkylinesLink.Mod.Runtime;
using Newtonsoft.Json.Linq;
using SkylinesLink.Contracts;

namespace SkylinesLink.RuntimeProbe
{
    internal static class DurableOperationTests
    {
        private static int checks;
        private static readonly LoadTarget Target = new LoadTarget("probe", "save-b", "content-1");
        private static void Check(bool result, string label) { Program.Assert(result, label); checks++; }
        private static TaskCompletionSource<bool> Completion() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private static void Reject(Action work, string code)
        { try { work(); } catch (OperationRejectedException error) { Check(error.Code == code, "operation rejects " + code); return; } throw new InvalidOperationException("Missing operation rejection."); }

        internal static async Task<int> RunAsync()
        {
            checks = 0;
            await GroupCommit();
            await PendingAndDedupe();
            await ConfirmedPrefixAndFold();
            await GuardAndSuccess();
            await NegativeNativeReturn();
            await DeadlineAndClose();
            await FlushFailure();
            await RecoveryAndFence();
            await LimitsAndEncoding();
            await ContextRotation();
            await SemanticRecovery();
            await IncompatibleJournal();
            await ScalarIdentityRecovery();
            await UnknownOwnerProcess();
            return checks;
        }
        private static async Task GroupCommit()
        {
            Program.Stage = "bounded FIFO group durability";
            foreach (bool loseAck in new[] { false, true })
            {
                var flush = new ControlledFlush { PauseAt = 2, PauseAgainAt = 3, FailAt = loseAck ? 3 : 0 };
                using (var f = new Fixture(flush))
                {
                    var tasks = new Task<OperationLookupSnapshot>[4];
                    string write = f.Ops.IssueWriteContext(f.Subject);
                    tasks[0] = f.Ops.SubmitAsync(new SubmissionKey(write, "group-0"), Target, f.Subject);
                    try
                    {
                        await Program.MustComplete(flush.Entered.Task);
                        for (int i = 1; i < tasks.Length; i++)
                            tasks[i] = f.Ops.SubmitAsync(new SubmissionKey(write, "group-" + i), Target, f.Subject);
                        Check(f.Journal.CaptureConfirmedPrefix()!.ThroughSequence == 1 && tasks.All(task => !task.IsCompleted),
                            "no acknowledgement or confirmed record precedes the first real flush");
                        flush.Release.Set();
                        await Program.MustComplete(flush.EnteredAgain.Task);
                        await Program.MustComplete(tasks[0]);
                        Check(tasks.Skip(1).All(task => !task.IsCompleted) && f.Journal.QueuedOrWriting == 3 &&
                            f.Journal.CaptureConfirmedPrefix()!.ThroughSequence == 2,
                            "waiting batch keeps every stage credit and exposes no unflushed prefix");
                        Task? stop = null;
                        if (!loseAck)
                        {
                            stop = f.Journal.StopAsync();
                            Check(!stop.IsCompleted && f.Journal.QueuedOrWriting == 3 && f.Journal.ReservedOperationCount == 4,
                                "journal stop waits for the actual paused batch flush without retiring stage ownership");
                        }
                        flush.ReleaseAgain.Set();
                        var accepted = await Task.WhenAll(tasks);
                        Check(flush.Calls == 3, "three waiting requests share exactly one additional flush without a fill timer");
                        var prefix = f.Journal.CaptureConfirmedPrefix()!;
                        Check(prefix.ThroughSequence == (loseAck ? 2 : 5) && prefix.Records.Select(record => record.Sequence)
                            .SequenceEqual(Enumerable.Range(1, prefix.Records.Count).Select(i => (long)i)),
                            "whole batch registers a consecutive confirmed prefix only after successful flush");
                        Check(accepted.Skip(1).All(value => loseAck ? value.Kind == "pending" && value.Durability == "unavailable"
                            : value.Kind == "found" && value.Durability == "durable"),
                            "lost batch flush acknowledgement never becomes partial durable acceptance");
                        if (stop != null)
                        {
                            await Program.MustComplete(stop);
                            Check(f.Journal.Completion.IsCompleted && f.Journal.QueuedOrWriting == 0 &&
                                ReferenceEquals(stop, f.Journal.StopAsync()),
                                "journal stop drains the accepted batch and repeated stop does not reuse a disposed signal");
                        }
                        if (loseAck)
                        {
                            Check(!f.Journal.IsHealthy && f.Journal.ReservedOperationCount == 1,
                                "failed batch refuses new work and returns all failed stage reservations");
                            await f.Reopen();
                            Check(f.Journal.Recovery.Complete && accepted.All(value =>
                                f.Ops.Lookup(value.OperationId!, f.Subject).State == "cancelled"),
                                "restart reads the real flushed batch and proves only complete unsubmitted EOF cancellation");
                        }
                        await f.Close();
                        if (stop != null)
                            Check(f.Journal.ReservedOperationCount == 0 && f.Ops.ActiveCount == 0,
                                "operation close returns remaining reservations after the journal has really stopped");
                    }
                    finally { flush.Release.Set(); flush.ReleaseAgain.Set(); }
                }
            }

            Program.Stage = "partial write within a durability batch";
            var partialFlush = new ControlledFlush { PauseAt = 2 };
            using (var f = new Fixture(partialFlush, guardJournalLength: true))
            {
                var tasks = new Task<OperationLookupSnapshot>[4];
                string write = f.Ops.IssueWriteContext(f.Subject);
                tasks[0] = f.Ops.SubmitAsync(new SubmissionKey(write, "partial-0"), Target, f.Subject);
                try
                {
                    await Program.MustComplete(partialFlush.Entered.Task);
                    for (int i = 1; i < tasks.Length; i++)
                        tasks[i] = f.Ops.SubmitAsync(new SubmissionKey(write, "partial-" + i), Target, f.Subject);
                    var stream = f.GuardedJournal!;
                    stream.FailWriteAt = stream.WriteCalls + 2;
                    partialFlush.Release.Set();
                    var all = Task.WhenAll(tasks);
                    await Program.MustComplete(all);
                    var accepted = await all;
                    Check(stream.PartialWriteBytes == 7 && stream.WriteCalls == stream.FailWriteAt && partialFlush.Calls == 2,
                        "the second batch write leaves a real partial frame and never reaches batch flush or the third write");
                    Check(accepted[0].Kind == "found" && accepted[0].Durability == "durable" &&
                        accepted.Skip(1).All(value => value.Kind == "pending" && value.Durability == "unavailable") &&
                        f.Journal.CaptureConfirmedPrefix()!.ThroughSequence == 2 &&
                        f.Journal.CaptureConfirmedPrefix()!.ThroughBytes == partialFlush.LastActualPosition,
                        "a partial batch write confirms none of the three batch operations");
                    Check(!f.Journal.IsHealthy && f.Journal.TryReserveOperation() == null && f.Journal.QueuedOrWriting == 0 &&
                        f.Journal.ReservedOperationCount == 1 && f.Ops.ActiveCount == 1,
                        "failed batch returns all three operation stage reservations and refuses new work");
                    await Program.MustComplete(f.Journal.StopAsync());
                    await Program.MustComplete(f.Close());
                    Check(f.Journal.Completion.IsCompleted && f.Journal.QueuedOrWriting == 0 &&
                        f.Journal.ReservedOperationCount == 0 && f.Ops.ActiveCount == 0,
                        "partial write failure still stops the actual writer and returns every remaining reservation");
                    long completeFrameBytes = new FileInfo(f.JournalPath).Length - stream.PartialWriteBytes;
                    await f.Reopen(raw: true);
                    var prefix = f.Journal.CaptureConfirmedPrefix()!;
                    Check(!f.Journal.Recovery.Complete && !f.Journal.IsHealthy && f.Journal.Recovery.Records.Count == 3 &&
                        prefix.ThroughSequence == 3 && prefix.ThroughBytes == completeFrameBytes && accepted.Take(2).All(value => {
                            var recovered = f.Ops.Lookup(value.OperationId!, f.Subject);
                            return recovered.Kind == "found" && recovered.State == "unknown" && recovered.MayHaveApplied;
                        }), "real recovery preserves both complete accepted frames without inferring no submission across a partial tail");
                    Check(accepted.Skip(2).All(value => f.Ops.Lookup(value.OperationId!, f.Subject).Reason == "RECOVERY_INCOMPLETE") &&
                        f.Ops.Lookup("missing-after-partial-write", f.Subject).Kind == "unavailable",
                        "a truncated batch cannot turn missing operations into absent or authorize replay");
                    await f.Close();
                }
                finally { partialFlush.Release.Set(); }
            }
        }
        private static async Task ContextRotation()
        {
            Program.Stage = "bounded live write-context rotation";
            using (var f = new Fixture())
            {
                Reject(() => OperationQueryService.ExecuteV3(f.Ops, new JObject { ["source"] = "history", ["selector"] = new JObject {
                    ["kind"] = "operationId", ["operationId"] = "missing" } }, f.Subject, f.Context.Read().InstanceId, 1), "HISTORY_UNAVAILABLE");
                var issued = Enumerable.Range(0, OperationLimits.MaximumContexts).Select(_ => f.Ops.IssueWriteContext(f.Subject, 1000)).ToArray();
                Reject(() => f.Ops.IssueWriteContext(f.Subject), "BUSY");
                var key = new SubmissionKey(issued[0], "before-expiry");
                string id = (await f.Ops.SubmitAsync(key, Target, f.Subject)).OperationId!;
                await f.Ops.PersistCommitIntentAsync(id);
                var native = Completion();
                Check(f.Ops.TryCommitAtSafePoint(id, new NativeLoadGate(f.Context.Read().InstanceId, _ => NativeLoadInvocation.Started(native.Task, Target))),
                    "native operation starts before its admission context expires");
                f.Now = 1000;
                Reject(() => f.Ops.SubmitAsync(new SubmissionKey(issued[0], "after-expiry"), Target, f.Subject), "CONTEXT_EXPIRED");
                string replacement = f.Ops.IssueWriteContext(f.Subject, 1000);
                Check(!issued.Contains(replacement) && (await f.Ops.SubmitAsync(key, Target, f.Subject)).OperationId == id,
                    "expired context slots rotate while retained keys still deduplicate");
                Reject(() => f.Ops.SubmitAsync(new SubmissionKey(issued[0], "after-expiry"), Target, f.Subject), "CONTEXT_UNKNOWN");
                Check(f.Ops.Lookup(key, f.Subject).OperationId == id &&
                    f.Ops.Lookup(new SubmissionKey(issued[0], "unseen"), f.Subject).Kind == "expired" &&
                    f.Ops.Lookup(new SubmissionKey(issued[1], "unseen"), f.Subject).Reason == "CONTEXT_UNKNOWN" &&
                    f.Ops.ActiveCount == 1 && f.Journal.ReservedOperationCount == 1 && !native.Task.IsCompleted,
                    "context collection preserves original operation, running native work and terminal credits");
                native.SetResult(false); await f.Ops.DrainAsync(id);
                Check(f.Ops.Lookup(id, f.Subject).Load!.LoadReturnValue == false && f.Ops.ActiveCount == 0 && f.Journal.ReservedOperationCount == 0,
                    "original physical result completes independently of context rotation");
                var identities = new System.Collections.Generic.HashSet<string>(issued) { replacement };
                for (int i = 0; i < OperationLimits.MaximumContexts * 2; i++)
                {
                    f.Now += 1000;
                    Check(identities.Add(f.Ops.IssueWriteContext(f.Subject, 1000)), "repeated expiry never reuses an issued context identity");
                }
                await f.Close();
            }
            using (var f = new Fixture())
            {
                var previous = Enumerable.Range(0, OperationLimits.MaximumContexts).Select(_ => f.Ops.IssueWriteContext(f.Subject)).ToArray();
                f.Context.EnterMenu();
                string replacement = f.Ops.IssueWriteContext(f.Subject);
                Check(!previous.Contains(replacement), "a new loading generation releases stale context slots without rebinding their identities");
                Reject(() => f.Ops.SubmitAsync(new SubmissionKey(previous[0], "old-generation"), Target, f.Subject), "CONTEXT_UNKNOWN");
                await f.Close();
            }
            using (var f = new Fixture())
            {
                var previous = Enumerable.Range(0, OperationLimits.MaximumContexts).Select(_ => f.Ops.IssueWriteContext(f.Subject)).ToArray();
                await f.Grants.UpdateAsync("probe", GrantScope.Read | GrantScope.Control, f.Token, null);
                string replacement = f.Ops.IssueWriteContext(f.Subject);
                Check(!previous.Contains(replacement), "a new grant epoch releases contexts bound to the revoked subject");
                Reject(() => f.Ops.SubmitAsync(new SubmissionKey(previous[0], "old-grant"), Target, f.Subject), "CONTEXT_UNKNOWN");
                await f.Close();
            }
        }
        private static async Task NegativeNativeReturn()
        {
            Program.Stage = "typed native false durability";
            foreach (int failedFlush in new[] { 0, 4, 5 })
            using (var f = new Fixture(new ControlledFlush { FailAt = failedFlush }))
            {
                string id = (await f.Accept("false-" + failedFlush)).OperationId!;
                await f.Ops.PersistCommitIntentAsync(id);
                var gate = new NativeLoadGate(f.Context.Read().InstanceId, _ => NativeLoadInvocation.Started(Task.FromResult(false), null));
                Check(f.Ops.TryCommitAtSafePoint(id, gate), "false-returning native call was actually submitted");
                await f.Ops.DrainAsync(id);
                var live = f.Ops.Lookup(id, f.Subject);
                Check(!gate.HasInFlight && !f.Ops.HasNativeFence && live.State == "failed" && live.MayHaveApplied &&
                    live.Load!.PhysicalTaskState == NativeLoadTaskState.RanToCompletion && live.Load.LoadReturnValue == false &&
                    !live.Load.HistoricalCompletionProven && live.Durability == (failedFlush == 0 ? "durable" : "unavailable"),
                    "known false remains separate from actual Task completion and projection durability");
                var encoded = QueryCurrent(f, id);
                Check((bool?)encoded["result"]?["lookup"]?["load"]?["loadReturnValue"] == false &&
                    (string?)encoded["result"]?["lookup"]?["load"]?["physicalTaskState"] == "ranToCompletion",
                    "active query revision 5 preserves known false in the generated live envelope");
                await f.Reopen();
                var recovered = f.Ops.Lookup(id, f.Subject);
                Check(recovered.State == "failed" && recovered.MayHaveApplied && recovered.Load!.LoadReturnValue == false && !f.Ops.HasNativeFence,
                    "durable native false prefix remains known across final or native acknowledgement loss");
                await f.Close();
            }
        }

        private static async Task IncompatibleJournal()
        {
            Program.Stage = "legacy and mixed journal rejection";
            foreach (bool mixed in new[] { false, true })
            using (var f = new Fixture())
            {
                string id = (await f.Accept("legacy" )).OperationId!;
                await f.Journal.StopAsync();
                byte[] bytes = File.ReadAllBytes(f.JournalPath);
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int size = checked((int)BitConverter.ToUInt32(bytes, offset + 8));
                    if (!mixed || offset > 0)
                    {
                        // Header 与 accepted 的 JSON 在旧 schema 中也合法；只把完整帧改回真实 v1 并重新计算摘要。
                        bytes[offset + 4] = 1;
                        using (var sha = SHA256.Create()) Buffer.BlockCopy(sha.ComputeHash(bytes, offset, size + 12), 0, bytes, offset + size + 12, 32);
                    }
                    offset += size + 44;
                }
                File.WriteAllBytes(f.JournalPath, bytes);
                await f.Reopen(raw: true);
                Check(f.Journal.Recovery.Incompatible && !f.Journal.Recovery.Complete && !f.Journal.Recovery.DurableConfirmed &&
                    f.Journal.Recovery.Records.Count == 0 && f.Journal.CaptureConfirmedPrefix() == null && !f.Journal.IsHealthy && f.Journal.TryReserveOperation() == null,
                    "old or mixed frame version cannot activate a new writer or partial new index");
                Check(f.Ops.Lookup(id, f.Subject).Reason == "JOURNAL_INCOMPATIBLE" && f.Ops.Lookup("missing", f.Subject).Kind == "unavailable",
                    "incompatible storage is explicit and never healthy absent");
                Check(ContractCatalog.Validate(SchemaIds.OperationLookupV3, QueryCurrent(f, id)).Valid,
                    "incompatible lookup uses the active generated envelope");
                Check(ContractCatalog.Validate(SchemaIds.OperationLookupV2, OperationQueryService.Encode(f.Ops.Lookup(id, f.Subject))).Valid,
                    "immutable prior lookup schema remains an independent codec regression");
                Reject(() => f.Ops.IssueWriteContext(f.Subject), "JOURNAL_UNAVAILABLE");
                await f.Close();
                Check(File.ReadAllBytes(f.JournalPath).SequenceEqual(bytes), "incompatible journal bytes are retained without migration or append");
            }
        }
        private static async Task PendingAndDedupe()
        {
            Program.Stage = "durable pending and dedupe";
            var flush = new ControlledFlush { PauseAt = 2 };
            using (var f = new Fixture(flush, guardJournalLength: true))
            {
                var initialPrefix = f.Journal.CaptureConfirmedPrefix()!;
                Check(initialPrefix.ThroughBytes == flush.LastActualPosition && initialPrefix.ThroughBytes.GetValueOrDefault() > 0 &&
                    new ConfirmedJournalPrefix(initialPrefix.Records, initialPrefix.Records.Count).ThroughBytes == null,
                    "fresh legacy confirmed bytes come from the actual header Flush position");
                string write = f.Ops.IssueWriteContext(f.Subject);
                string unicodeRequest = string.Concat(Enumerable.Repeat("\U0001F680", 100));
                var key = new SubmissionKey(write, unicodeRequest);
                Task<OperationLookupSnapshot> accepted = f.Ops.SubmitAsync(key, Target, f.Subject);
                await Program.MustComplete(flush.Entered.Task);
                var pending = f.Ops.Lookup(key, f.Subject);
                Check(pending.Kind == "pending" && pending.OperationId != null && pending.Durability == "pending", "unconfirmed accepted retains allocated identity");
                var duringFlush = f.Journal.CaptureConfirmedPrefix()!;
                Check(duringFlush.ThroughSequence == 1 && duringFlush.Records.Count == 1 &&
                    duringFlush.ThroughBytes == initialPrefix.ThroughBytes &&
                    ReferenceEquals(initialPrefix.Records[0], duringFlush.Records[0]), "pending real flush cannot enter the confirmed record prefix");
                Reject(() => f.Ops.SubmitAsync(key, new LoadTarget("probe", "different", "1"), f.Subject), "IDEMPOTENCY_CONFLICT");
                var duplicate = f.Ops.SubmitAsync(key, Target, f.Subject);
                Check(f.Journal.ReservedOperationCount == 1 && f.Journal.QueuedOrWriting == 1 && !duplicate.IsCompleted, "same key shares one live append and reservation");
                var stream = f.GuardedJournal!;
                int lengthReads = stream.LengthReads;
                stream.RejectLength = true;
                try
                {
                    using (var extra = f.Journal.TryReserveOperation())
                        Check(extra != null && stream.LengthReads == lengthReads && !accepted.IsCompleted,
                            "admission during slow flush uses only memory and never reads shared stream length");
                }
                finally { stream.RejectLength = false; }
                Check(f.Ops.Lookup("missing", f.Subject).Kind == "absent" && f.Ops.Lookup(new SubmissionKey("never-issued", "r"), f.Subject).Reason == "CONTEXT_UNKNOWN", "healthy absent differs from unknown context");
                flush.Release.Set(); await Program.MustComplete(accepted); await Program.MustComplete(duplicate);
                var found = await accepted;
                Check(found.OperationId == pending.OperationId && found.Durability == "durable", "ack loss is recoverable through original key");
                var afterFlush = f.Journal.CaptureConfirmedPrefix()!;
                Check(afterFlush.ThroughSequence == 2 && afterFlush.Records.Count == 2 &&
                    afterFlush.ThroughBytes == flush.LastActualPosition && afterFlush.ThroughBytes.GetValueOrDefault() > initialPrefix.ThroughBytes.GetValueOrDefault() &&
                    ((OperationAccepted)afterFlush.Records[1].Entry).Intent.OperationId == found.OperationId &&
                    initialPrefix.ThroughSequence == 1 && initialPrefix.Records.Count == 1 &&
                    ReferenceEquals(initialPrefix.Records[0], afterFlush.Records[0]), "new confirmed prefix shares immutable records without changing an older capture");
                f.Now = 70000;
                Check((await f.Ops.SubmitAsync(key, Target, f.Subject)).OperationId == found.OperationId, "known key dedupes before context expiry");
                Check(f.Ops.Lookup(new SubmissionKey(write, "unused"), f.Subject).Kind == "expired", "known expired context has separate lookup");
                Check(ContractCatalog.Validate(SchemaIds.OperationLookupV3, QueryCurrent(f, found.OperationId!)).Valid,
                    "real operation query validates active generated envelope");
                await f.Grants.UpdateAsync("reader", GrantScope.Read, HandshakeProof.RandomHex(), null);
                var reader = f.Grants.Read("reader")!;
                Check(f.Ops.Lookup(key, reader).OperationId == found.OperationId && f.Ops.Lookup(found.OperationId!, reader).OperationId == found.OperationId,
                    "same-environment different profile Read sees identical sanitized operation facts");
                await f.Grants.UpdateAsync("probe", GrantScope.Read, f.Token, null);
                Check(f.Ops.Lookup(key, reader).OperationId == found.OperationId && f.Ops.Lookup(key, f.Subject).OperationId == found.OperationId,
                    "original Control rotation does not erase historical Read access");
                await f.Close();
            }
            flush = new ControlledFlush { FailAt = 7 };
            using (var f = new Fixture(flush))
            {
                string previous = (await f.Accept("durable-before-other-failure")).OperationId!;
                await f.Ops.PersistCommitIntentAsync(previous);
                f.Ops.TryCommitAtSafePoint(previous, new NativeLoadGate(f.Context.Read().InstanceId, _ => NativeLoadInvocation.NotStarted()));
                await f.Ops.DrainAsync(previous);
                string id = (await f.Accept("native-terminal-failure")).OperationId!;
                await f.Ops.PersistCommitIntentAsync(id);
                f.Ops.TryCommitAtSafePoint(id, new NativeLoadGate(f.Context.Read().InstanceId, _ => NativeLoadInvocation.Started(Task.FromResult(true), Target)));
                await f.Ops.DrainAsync(id);
                long generation = f.Context.EnterLoading(); f.Context.ObserveReady(generation, "city-c");
                f.Ops.ObserveReady(id, new LoadReadyObservation(f.Context.Read(), new LoadTarget("probe", "different", "1"), null));
                Check(f.Ops.Lookup(id, f.Subject).Durability == "unavailable", "unrelated observation cannot erase known per-operation persistence failure");
                Check(f.Ops.Lookup(previous, f.Subject).Durability == "durable", "another operation failure does not downgrade a durable final");
                f.Ops.EndObservation(id, LoadObservationEnd.Expired); await f.Ops.DrainAsync(id); await f.Close();
            }
        }
        private static async Task ConfirmedPrefixAndFold()
        {
            Program.Stage = "confirmed journal prefix and pure facts fold";
            using (var f = new Fixture())
            {
                using (var reservation = f.Journal.TryReserveOperation()!)
                {
                    var source = f.Context.Read();
                    var intent = new OperationIntent("delayed-consumer", source.InstanceId, new SubmissionKey("prefix-context", "request"),
                        OperationCommandHash.Compute(source, Target), "probe", f.Subject.AuthorityEpoch, f.Subject.Epoch, source, Target);
                    var accepted = new OperationAccepted(intent);
                    var acknowledged = reservation.AppendAsync(accepted, JournalSlot.Normal);
                    var entered = Completion(); var resume = Completion(); long appliedByConsumer = 0;
                    async Task DelayedConsumer()
                    {
                        long sequence = await acknowledged;
                        entered.TrySetResult(true);
                        await resume.Task;
                        appliedByConsumer = sequence;
                    }
                    Task consumer = DelayedConsumer();
                    try
                    {
                        await Program.MustComplete(entered.Task);
                        var prefix = f.Journal.CaptureConfirmedPrefix()!;
                        var folded = JournalFactsFold.Fold(prefix.Records);
                        Check(!consumer.IsCompleted && appliedByConsumer == 0 && prefix.ThroughSequence == 2 &&
                            ReferenceEquals(prefix.Records[1].Entry, accepted), "confirmed record and sequence are available independently of a delayed ACK consumer");
                        Check(folded.AllRecordsValid && folded.ValidRecordCount == 2 && folded.ThroughSequence == 2 &&
                            folded.EnvironmentId == "probe" && folded.StoreId != null && folded.Operations.Count == 1 &&
                            ReferenceEquals(folded.Operations[0].Intent, intent) && folded.Operations[0].Final == null && folded.Operations[0].FinalSequence == null,
                            "pure accepted-prefix fold preserves source identity without manufacturing an EOF terminal");
                        var changed = prefix.Records.ToArray();
                        changed[1] = new JournalRecord(3, "probe", accepted);
                        var broken = JournalFactsFold.Fold(changed);
                        Check(!broken.AllRecordsValid && broken.ValidRecordCount == 1 && broken.Operations.Count == 0 &&
                            JournalFactsFold.Fold(prefix.Records).AllRecordsValid, "invalid sequence stops at valid prefix and caller copies cannot alter captured records");
                    }
                    finally { resume.TrySetResult(true); await Program.MustComplete(consumer); }
                    Check(appliedByConsumer == 2, "delayed ACK consumer eventually observes the same confirmed sequence");
                }
                await f.Reopen(raw: true);
                Check(f.Ops.Lookup("delayed-consumer", f.Subject).State == "cancelled" &&
                    !f.Ops.Lookup("delayed-consumer", f.Subject).MayHaveApplied &&
                    JournalFactsFold.Fold(f.Journal.CaptureConfirmedPrefix()!.Records).Operations[0].Final == null,
                    "authority EOF recovery retains its own inference while the identical pure prefix has no terminal record");
                await f.Close();
            }
        }
        private static async Task GuardAndSuccess()
        {
            Program.Stage = "durable commit guard and successful evidence";
            using (var f = new Fixture())
            {
                var receipt = await f.Accept("guard");
                await f.Ops.PersistCommitIntentAsync(receipt.OperationId!);
                await f.Grants.UpdateAsync("probe", GrantScope.Read, f.Token, null);
                int native = 0;
                var gate = new NativeLoadGate(f.Context.Read().InstanceId, _ => { native++; return NativeLoadInvocation.Started(Task.FromResult(true), Target); });
                Check(!f.Ops.TryCommitAtSafePoint(receipt.OperationId!, gate) && native == 0, "durable ack then revoke prevents actual native entry");
                await f.Ops.DrainAsync(receipt.OperationId!);
                var failed = f.Ops.Lookup(receipt.OperationId!, f.Subject);
                Check(failed.State == "cancelled" && !failed.MayHaveApplied && failed.Durability == "durable", "guard failure durable final proves never submitted");
                await f.Close();
            }
            using (var f = new Fixture())
            {
                f.Context.ObserveReady(f.Context.EnterLoading());
                string frozenContext = f.Ops.IssueWriteContext(f.Subject);
                f.Context.ObserveCityIdentity(f.Context.Read().Generation, "source-city");
                var receipt = await f.Ops.SubmitAsync(new SubmissionKey(frozenContext, "success"), Target, f.Subject); string id = receipt.OperationId!;
                await f.Ops.PersistCommitIntentAsync(id);
                var actual = Completion(); long generation = 0;
                var gate = new NativeLoadGate(f.Context.Read().InstanceId, _ => { generation = f.Context.EnterLoading(); return NativeLoadInvocation.Started(actual.Task, Target); });
                Check(f.Ops.TryCommitAtSafePoint(id, gate) && f.Ops.HasNativeFence, "native starts only after intent flush");
                f.Context.ObserveReady(generation);
                f.Ops.ObserveReady(id, new LoadReadyObservation(f.Context.Read(), Target, id));
                Check(f.Ops.Lookup(id, f.Subject).Load!.CorrelatedTargetReady && !f.Ops.Lookup(id, f.Subject).Load!.HistoricalCompletionProven,
                    "ready event does not substitute native completion");
                actual.SetResult(true); await f.Ops.DrainAsync(id);
                var complete = f.Ops.Lookup(id, f.Subject);
                Check(complete.State == "succeeded" && complete.Durability == "durable" && complete.Load!.HistoricalCompletionProven && complete.IsObservationCurrent && !f.Ops.HasNativeFence,
                    "native and causal ready produce durable success");
                Check(f.Context.ObserveCityIdentity(generation, "city-b") && f.Ops.Lookup(id, f.Subject).IsObservationCurrent,
                    "learning persistent identity does not stale the actual load observation");
                long c = f.Context.EnterLoading(); f.Context.ObserveReady(c, "city-c");
                Check(!f.Ops.Lookup(id, f.Subject).IsObservationCurrent && f.Ops.Lookup(id, f.Subject).Load!.HistoricalCompletionProven,
                    "new world does not rewrite historical completion");
                await f.Reopen();
                var recovered = f.Ops.Lookup(id, f.Subject);
                Check(recovered.State == "succeeded" && recovered.Durability == "durable" && !recovered.IsObservationCurrent && !f.Ops.HasNativeFence,
                    "final history recovers without stale current context or fence");
                var persisted = f.Journal.Recovery.Records.Select(record => record.Entry).OfType<OperationAccepted>().Single().Intent.Source;
                Check(persisted.State == "ready" && persisted.CityId == null && recovered.Load!.ObservationContext!.CityId == null,
                    "actual journal recovery preserves unknown source and ready observation identity without fabricating a city ID");
                await f.Close();
            }
        }
        private static async Task DeadlineAndClose()
        {
            Program.Stage = "native observation expiry and close";
            using (var f = new Fixture())
            {
                string id = (await f.Accept("slow")).OperationId!;
                await f.Ops.PersistCommitIntentAsync(id);
                var native = Completion();
                var gate = new NativeLoadGate(f.Context.Read().InstanceId, _ => NativeLoadInvocation.Started(native.Task, Target));
                f.Ops.TryCommitAtSafePoint(id, gate);
                f.Ops.EndObservation(id, LoadObservationEnd.Expired);
                Check(f.Ops.ActiveCount == 1 && f.Journal.ReservedOperationCount == 1 && f.Ops.HasNativeFence, "observation expiry retains real task and final credit");
                Task close = f.Ops.CloseAsync();
                Check(!close.IsCompleted && gate.HasInFlight, "close remains asynchronous until known native task ends");
                native.SetResult(true); await Program.MustComplete(close);
                Check(!f.Ops.HasNativeFence && f.Ops.ActiveCount == 0 && f.Journal.ReservedOperationCount == 0, "actual native termination retires physical and budget ownership");
                var final = f.Ops.Lookup(id, f.Subject);
                Check(final.State == "unknown" && final.Load!.PhysicalTaskState == NativeLoadTaskState.RanToCompletion && final.Load.ObservationEnd == LoadObservationEnd.Expired,
                    "native success without causal ready remains semantic unknown");
                await f.Close();
            }
        }
        private static async Task FlushFailure()
        {
            Program.Stage = "unknown flush and live execution facts";
            var flush = new ControlledFlush { FailAt = 2 };
            using (var f = new Fixture(flush))
            {
                long? initialBytes = f.Journal.CaptureConfirmedPrefix()!.ThroughBytes;
                var receipt = await f.Accept("lost-flush");
                Check(receipt.Kind == "pending" && receipt.Durability == "unavailable" && !f.Journal.IsHealthy, "failed initial flush preserves pending identity and poisons writer");
                Check(f.Journal.CaptureConfirmedPrefix()!.ThroughSequence == 1, "failed flush acknowledgement never promotes its uncertain bytes into confirmed prefix");
                Check(f.Journal.CaptureConfirmedPrefix()!.ThroughBytes == initialBytes && flush.LastActualPosition > initialBytes.GetValueOrDefault(),
                    "actual bytes written before lost ACK never advance the old confirmed byte watermark");
                Reject(() => f.Ops.PersistCommitIntentAsync(receipt.OperationId!), "NOT_COMMITTABLE");
                await f.Reopen();
                Check(f.Ops.Lookup(receipt.OperationId!, f.Subject).State == "cancelled", "durable accepted prefix is not replayed after restart");
                await f.Close();
            }
            flush = new ControlledFlush { FailAt = 4 };
            using (var f = new Fixture(flush))
            {
                string id = (await f.Accept("guard-final-failure")).OperationId!;
                await f.Ops.PersistCommitIntentAsync(id);
                await f.Grants.UpdateAsync("probe", GrantScope.Read, f.Token, null);
                f.Ops.TryCommitAtSafePoint(id, new NativeLoadGate(f.Context.Read().InstanceId, _ => throw new InvalidOperationException()));
                await f.Ops.DrainAsync(id);
                var known = f.Ops.Lookup(id, f.Subject);
                Check(known.State == "cancelled" && !known.MayHaveApplied && known.Durability == "unavailable", "failed final flush does not erase proven no-native fact");
                await f.Close();
            }
            flush = new ControlledFlush { FailAt = 5 };
            using (var f = new Fixture(flush))
            {
                string id = (await f.Accept("success-final-failure")).OperationId!;
                await f.Ops.PersistCommitIntentAsync(id);
                var native = Completion(); long generation = 0;
                f.Ops.TryCommitAtSafePoint(id, new NativeLoadGate(f.Context.Read().InstanceId, _ =>
                { generation = f.Context.EnterLoading(); return NativeLoadInvocation.Started(native.Task, Target); }));
                f.Context.ObserveReady(generation, "city-b"); f.Ops.ObserveReady(id, new LoadReadyObservation(f.Context.Read(), Target, id));
                native.SetResult(true); await f.Ops.DrainAsync(id);
                var known = f.Ops.Lookup(id, f.Subject);
                Check(known.State == "succeeded" && known.MayHaveApplied && known.Load!.HistoricalCompletionProven && known.Durability == "unavailable",
                    "failed final flush preserves live native and ready proof");
                Check(!f.Ops.HasNativeFence, "known physical terminal frees load slot even when final flush failed");
                await f.Close();
            }
        }
        private static async Task RecoveryAndFence()
        {
            Program.Stage = "durable recovery and same-host fence";
            using (var f = new Fixture())
            {
                string id = (await f.Accept("unknown-native")).OperationId!;
                await f.Ops.PersistCommitIntentAsync(id);
                f.Ops.TryCommitAtSafePoint(id, new NativeLoadGate(f.Context.Read().InstanceId, _ => throw new InvalidOperationException("lost native handle")));
                await f.Ops.DrainAsync(id);
                Check(f.Ops.HasNativeFence && f.Ops.Lookup(id, f.Subject).State == "unknown", "lost native handle final is not a physical completion");
                await f.Reopen();
                Check(f.Ops.HasNativeFence && f.Ops.Lookup(id, f.Subject).State == "unknown", "same process new instance cannot bypass journal-derived fence");
                string next = (await f.Accept("new-after-unknown")).OperationId!;
                Reject(() => f.Ops.PersistCommitIntentAsync(next), "NATIVE_LOAD_FENCED");
                await f.Reopen(host: new IdentityProof(NativeHostState.Ended));
                Check(!f.Ops.HasNativeFence && f.Ops.Lookup(id, f.Subject).State == "unknown", "proven previous host exit removes physical fence but preserves unknown history");
                await f.Close();
            }
            using (var f = new Fixture())
            {
                string id = (await f.Accept("cut-tail")).OperationId!;
                await f.Journal.StopAsync();
                long completeFrameBytes = new FileInfo(f.JournalPath).Length;
                using (var file = new FileStream(f.JournalPath, FileMode.Append, FileAccess.Write)) { file.WriteByte(83); file.Flush(true); }
                await f.Reopen(raw: true);
                var known = f.Ops.Lookup(id, f.Subject);
                Check(known.State == "unknown" && known.MayHaveApplied, "damaged tail may hide commit intent after accepted prefix");
                var prefix = f.Journal.CaptureConfirmedPrefix()!;
                var folded = JournalFactsFold.Fold(prefix.Records);
                Check(prefix.ThroughSequence == 2 && folded.AllRecordsValid && folded.Operations[0].Final == null && !f.Journal.Recovery.Complete,
                    "confirmed parseable prefix never claims complete file EOF after a damaged tail");
                Check(prefix.ThroughBytes == completeFrameBytes && new FileInfo(f.JournalPath).Length == completeFrameBytes + 1,
                    "recovered confirmed bytes end at the actual last complete frame, excluding the rejected one-byte tail");
                Check(f.Ops.Lookup("missing", f.Subject).Reason == "RECOVERY_INCOMPLETE", "corrupt tail makes missing lookup unavailable");
                Reject(() => f.Ops.IssueWriteContext(f.Subject), "JOURNAL_UNAVAILABLE");
                await f.Close();
            }
            using (var f = new Fixture())
            {
                await f.Close(); File.Delete(f.JournalPath);
                await f.Reopen(raw: true);
                Check(f.Ops.Lookup("missing", f.Subject).Kind == "unavailable" && !File.Exists(f.JournalPath), "missing prior journal is never rebuilt as empty");
                await f.Close();
            }
            using (var f = new Fixture())
            {
                string id = (await f.Accept("recovery-confirmation" )).OperationId!;
                await f.Close();
                await f.Reopen(durability: new ControlledFlush { FailAt = 1 });
                var known = f.Ops.Lookup(id, f.Subject);
                Check(f.Journal.Recovery.Complete && !f.Journal.Recovery.DurableConfirmed && known.State == "cancelled" && !known.MayHaveApplied && known.Durability == "unavailable",
                    "recovery flush failure retains readable proven facts but withholds durable claim");
                Check(f.Journal.CaptureConfirmedPrefix() == null, "failed recovery durability confirmation exposes no confirmed history prefix");
                Check(f.Ops.Lookup("missing", f.Subject).Reason == "JOURNAL_UNAVAILABLE" &&
                    (string?)new SnapshotCapabilityDispatcher(f.Context, Program.ProductVersion, f.Ops).Execute("system.context", "2", new JObject(), f.Subject)["gameState"] == "menu",
                    "recovery flush failure blocks journal missing lookup while context reads remain available");
                Reject(() => f.Ops.IssueWriteContext(f.Subject), "JOURNAL_UNAVAILABLE");
                await f.Reopen();
                Check(f.Journal.IsHealthy && f.Journal.Recovery.DurableConfirmed && f.Ops.Lookup(id, f.Subject).Durability == "durable",
                    "later successful recovery confirms same bytes without replay or replacement");
                await f.Close();
            }
        }
        private static async Task LimitsAndEncoding()
        {
            Program.Stage = "durable reservation limits and final representation";
            using (var f = new Fixture())
            {
                string write = f.Ops.IssueWriteContext(f.Subject);
                var keys = Enumerable.Range(0, 4).Select(i => new SubmissionKey(write, "request-" + i)).ToArray();
                var accepted = new OperationLookupSnapshot[4];
                for (int i = 0; i < 4; i++) accepted[i] = await f.Ops.SubmitAsync(keys[i], Target, f.Subject);
                Reject(() => f.Ops.SubmitAsync(new SubmissionKey(write, "overflow"), Target, f.Subject), "BUSY");
                Check((await f.Ops.SubmitAsync(keys[0], Target, f.Subject)).OperationId == accepted[0].OperationId && f.Journal.ReservedOperationCount == 4,
                    "dedupe precedes capacity and does not consume a fifth credit");
                for (int i = 0; i < 4; i++)
                {
                    await f.Ops.PersistCommitIntentAsync(accepted[i].OperationId!);
                    f.Ops.TryCommitAtSafePoint(accepted[i].OperationId!, new NativeLoadGate(f.Context.Read().InstanceId, _ => NativeLoadInvocation.NotStarted()));
                    await f.Ops.DrainAsync(accepted[i].OperationId!);
                }
                Check(f.Journal.ReservedOperationCount == 0 && f.Ops.ActiveCount == 0, "reserved final slots remain usable at full admission");
                await f.Close();
            }
            using (var f = new Fixture())
            {
                var credit = f.Journal.TryReserveOperation()!;
                string worst = new string('\u0001', 128);
                credit.ValidateEntry(new OperationFinished(worst, "unknown", true, SubmissionKnowledge.Unknown, new string('x', 64),
                    new PersistedLoadFacts(NativeLoadTaskState.Unknown, null, LoadTargetMatch.Unknown, false, false, LoadObservationEnd.OwnerStopped,
                        new WorldContextSnapshot(worst, long.MaxValue, "ready", worst))));
                Check(true, "worst escaped terminal identifiers fit reserved 4096 payload");
                credit.ValidateEntry(new NativeTerminated(worst, NativeLoadTaskState.RanToCompletion, false, new LoadTarget(worst, worst, worst)));
                credit.ValidateEntry(new OperationFinished(worst, "failed", true, SubmissionKnowledge.Submitted, new string('x', 64),
                    new PersistedLoadFacts(NativeLoadTaskState.RanToCompletion, false, LoadTargetMatch.Unknown, false, false, LoadObservationEnd.OwnerStopped,
                        new WorldContextSnapshot(worst, long.MaxValue, "ready", worst))));
                Check(true, "new physical state and false return fit worst escaped native and final frame reservations");
                credit.Dispose(); await f.Close();
            }
            using (var f = new Fixture())
            {
                string? write = null; SubmissionKey? first = null; string? firstId = null;
                for (int i = 0; i < 32; i++)
                {
                    if (i % 8 == 0) write = f.Ops.IssueWriteContext(f.Subject);
                    var key = new SubmissionKey(write!, "retained-" + i);
                    string id = (await f.Ops.SubmitAsync(key, Target, f.Subject)).OperationId!;
                    if (i == 0) { first = key; firstId = id; }
                    await f.Ops.PersistCommitIntentAsync(id);
                    f.Ops.TryCommitAtSafePoint(id, new NativeLoadGate(f.Context.Read().InstanceId, _ =>
                        NativeLoadInvocation.Started(Task.FromException<bool>(new InvalidOperationException("Synthetic native failure.")), Target)));
                    await f.Ops.DrainAsync(id);
                }
                Reject(() => f.Ops.SubmitAsync(new SubmissionKey(f.Ops.IssueWriteContext(f.Subject), "over-retention"), Target, f.Subject), "BUSY");
                Check((await f.Ops.SubmitAsync(first!, Target, f.Subject)).OperationId == firstId && f.Journal.ReservedOperationCount == 0,
                    "full retained journal keeps same-key identity with no extra writer");
                await f.Reopen();
                Check(f.Journal.Recovery.Complete && f.Journal.Recovery.Records.Count == 129 && new FileInfo(f.JournalPath).Length < OperationLimits.MaximumJournalBytes,
                    "32 four-record operations fill exact 129-frame bounded journal");
                var fullPrefix = f.Journal.CaptureConfirmedPrefix()!;
                var fullFacts = JournalFactsFold.Fold(fullPrefix.Records);
                Check(fullPrefix.ThroughSequence == OperationLimits.MaximumFrames && fullPrefix.Records.Count == OperationLimits.MaximumFrames &&
                    f.Journal.TryReserveOperation() == null && fullFacts.AllRecordsValid && fullFacts.Operations.Count == OperationLimits.MaximumRetained &&
                    fullFacts.Operations.All(value => value.Final != null && value.FinalSequence > 0 && value.FinalSequence <= fullFacts.ThroughSequence),
                    "fixed confirmed storage covers exact full capacity and every final retains its own confirmed sequence");
                await f.Close();
            }
        }

        private static async Task SemanticRecovery()
        {
            Program.Stage = "journal semantic cross-record rejection";
            using (var f = new Fixture())
            {
                var reservation = f.Journal.TryReserveOperation()!; var source = f.Context.Read();
                var intent = new OperationIntent("contradictory-bool", source.InstanceId, new SubmissionKey("context", "bool"), OperationCommandHash.Compute(source, Target),
                    "probe", f.Subject.AuthorityEpoch, f.Subject.Epoch, source, Target);
                await reservation.AppendAsync(new OperationAccepted(intent), JournalSlot.Normal);
                await reservation.AppendAsync(new CommitIntended(intent.OperationId, Target, new ProcessNativeHostIdentity().Current), JournalSlot.Normal);
                await reservation.AppendAsync(new NativeTerminated(intent.OperationId, NativeLoadTaskState.RanToCompletion, false, Target), JournalSlot.NativeTerminal);
                await reservation.AppendAsync(new OperationFinished(intent.OperationId, "succeeded", true, SubmissionKnowledge.Submitted, "SYNTHETIC",
                    new PersistedLoadFacts(NativeLoadTaskState.RanToCompletion, true, LoadTargetMatch.Exact, true, true, LoadObservationEnd.Open,
                        new WorldContextSnapshot(source.InstanceId, 1, "ready", "city"))), JournalSlot.Terminal);
                reservation.Dispose(); await f.Reopen(raw: true);
                Check(f.Ops.Lookup(intent.OperationId, f.Subject).State == "failed" && f.Ops.Lookup(intent.OperationId, f.Subject).Load!.LoadReturnValue == false &&
                    f.Ops.Lookup("missing", f.Subject).Reason == "RECOVERY_INCOMPLETE", "a later fabricated true result cannot replace durable native false");
                var folded = JournalFactsFold.Fold(f.Journal.CaptureConfirmedPrefix()!.Records);
                Check(!folded.AllRecordsValid && folded.ValidRecordCount == 4 && folded.ThroughSequence == 4 &&
                    folded.Operations[0].Native!.LoadReturnValue == false && folded.Operations[0].Final == null,
                    "shared fold returns the valid native-false prefix and excludes a contradictory durable final");
                await f.Close();
            }
            foreach (LoadTarget? bound in new LoadTarget?[] { null, new LoadTarget("probe", "different", "content-1"), new LoadTarget("probe", "save-b", "different-content") })
            using (var f = new Fixture())
            {
                var reservation = f.Journal.TryReserveOperation()!;
                var source = f.Context.Read();
                var intent = new OperationIntent("synthetic-op", source.InstanceId, new SubmissionKey("synthetic-context", "request"),
                    OperationCommandHash.Compute(source, Target), "probe", f.Subject.AuthorityEpoch, f.Subject.Epoch, source, Target);
                await reservation.AppendAsync(new OperationAccepted(intent), JournalSlot.Normal);
                await reservation.AppendAsync(new CommitIntended(intent.OperationId, Target, new ProcessNativeHostIdentity().Current), JournalSlot.Normal);
                await reservation.AppendAsync(new NativeTerminated(intent.OperationId, NativeLoadTaskState.RanToCompletion, true, bound), JournalSlot.NativeTerminal);
                await reservation.AppendAsync(new OperationFinished(intent.OperationId, "succeeded", true, SubmissionKnowledge.Submitted, "SYNTHETIC",
                    new PersistedLoadFacts(NativeLoadTaskState.RanToCompletion, true, LoadTargetMatch.Exact, true, true, LoadObservationEnd.Open,
                        new WorldContextSnapshot(source.InstanceId, 1, "ready", "city-b"))), JournalSlot.Terminal);
                reservation.Dispose(); await f.Reopen(raw: true);
                Check(f.Ops.Lookup("synthetic-op", f.Subject).State == "unknown" && f.Ops.Lookup("absent", f.Subject).Reason == "RECOVERY_INCOMPLETE",
                    "schema-valid forged success fails actual bound-target recovery");
                await f.Close();
            }
            using (var f = new Fixture())
            {
                var reservation = f.Journal.TryReserveOperation()!; var source = f.Context.Read();
                var intent = new OperationIntent("bad-submission", source.InstanceId, new SubmissionKey("context", "r"), OperationCommandHash.Compute(source, Target),
                    "probe", f.Subject.AuthorityEpoch, f.Subject.Epoch, source, Target);
                await reservation.AppendAsync(new OperationAccepted(intent), JournalSlot.Normal);
                await reservation.AppendAsync(new CommitIntended(intent.OperationId, Target, new ProcessNativeHostIdentity().Current), JournalSlot.Normal);
                await reservation.AppendAsync(new OperationFinished(intent.OperationId, "failed", false, SubmissionKnowledge.Submitted, "SYNTHETIC",
                    new PersistedLoadFacts(NativeLoadTaskState.NotStarted, null, LoadTargetMatch.Unknown, false, false, LoadObservationEnd.Interrupted, null)), JournalSlot.Terminal);
                reservation.Dispose(); await f.Reopen(raw: true);
                Check(f.Ops.Lookup(intent.OperationId, f.Subject).State == "unknown" && f.Ops.HasNativeFence, "contradictory submitted and notStarted final does not clear fence");
                await f.Close();
            }
            using (var f = new Fixture())
            {
                string context = f.Ops.IssueWriteContext(f.Subject);
                f.Context.EnterLoading();
                Check(f.Ops.Lookup(new SubmissionKey(context, "not-seen"), f.Subject).Kind == "expired", "source transition expires known write context");
                await f.Close();
            }
            using (var f = new Fixture())
            {
                string context = f.Ops.IssueWriteContext(f.Subject);
                await f.Grants.UpdateAsync("probe", GrantScope.Read, f.Token, null);
                Check(f.Ops.Lookup(new SubmissionKey(context, "not-seen"), f.Subject).Kind == "expired", "grant rotation expires unused context without adding reader-owner ACL");
                await f.Close();
            }
        }

        private static async Task UnknownOwnerProcess()
        {
            Program.Stage = "unknown native close process lifetime";
            string root = Path.Combine(Path.GetFullPath("artifacts"), "durable-process-" + Guid.NewGuid().ToString("N"));
            string directory = Path.Combine(root, "connection"); Directory.CreateDirectory(root);
            string token = HandshakeProof.RandomHex();
            try
            {
                var start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--unknown-close-probe") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, WorkingDirectory = Environment.CurrentDirectory, StandardOutputEncoding = new UTF8Encoding(false, true) };
                string id;
                using (var process = Process.Start(start) ?? throw new InvalidOperationException())
                {
                    Task<string> stderr = process.StandardError.ReadToEndAsync();
                    try
                    {
                        process.StandardInput.WriteLine(new JObject { ["directory"] = directory, ["token"] = token }.ToString(Newtonsoft.Json.Formatting.None));
                        process.StandardInput.Close();
                        var output = process.StandardOutput.ReadLineAsync(); await Program.MustComplete(output);
                        string? line = await output; if (line == null) throw new InvalidOperationException("Child exited without close evidence.");
                        var value = (JObject)ContractJson.Parse(line); id = (string)value["operationId"]!;
                        Check((string?)value["kind"] == "unknown-closed" && !(bool)value["fullyReleased"]! && (bool)value["nativeLeaseRetained"]! &&
                            (bool)value["sameCloseTask"]! && (int)value["connected"]! == 0, "unknown close releases IO, retains one lease and is idempotent");
                        await Program.MustComplete(Task.Run(() => process.WaitForExit()));
                        Check(process.ExitCode == 0 && (await stderr).Length == 0, "owned hidden unknown-close child terminates cleanly");
                    }
                    finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
                }
                var owner = await ConnectionAuthorityOwner.OpenAsync(directory, "probe", new string('c', 64), Program.ProductVersion, Program.Limits(),
                    new WorldContextTracker(Guid.NewGuid().ToString("N")), (operations, context, version) => new SnapshotCapabilityDispatcher(context, version, operations));
                Check(owner != null && !owner.Operations.HasNativeFence, "actual old process exit permits new owner without restoring physical fence");
                var lookup = owner!.Operations.Lookup(id, owner.Authority.Read("probe")!);
                Check(lookup.State == "unknown" && lookup.MayHaveApplied, "actual process exit never converts historical unknown into successful or replayable");
                await owner.CloseAsync(); Check(owner.FullyReleased && !owner.NativeLeaseRetained, "recovered ended-host owner fully releases environment");
            }
            finally
            {
                if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture containment failed.");
                if (Directory.Exists(directory))
                {
                    foreach (string file in Directory.GetFiles(directory))
                    { if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture cleanup refuses links."); File.Delete(file); }
                    Directory.Delete(directory);
                }
                Directory.Delete(root);
            }
        }

        private static async Task ScalarIdentityRecovery()
        {
            Program.Stage = "scalar identity journal recovery";
            string wide65 = string.Concat(Enumerable.Repeat("\U0001F680", 65));
            string wide128 = string.Concat(Enumerable.Repeat("\U0001F680", 128));
            string wide129 = wide128 + "\U0001F680";
            using (var f = new Fixture())
            {
                string write = f.Ops.IssueWriteContext(f.Subject);
                var targets = new[] { new LoadTarget("provider", wide65, wide65), new LoadTarget("provider", wide128, wide128), new LoadTarget(" \t", " ", null) };
                var ids = new string[targets.Length];
                for (int i = 0; i < targets.Length; i++)
                    ids[i] = (await f.Ops.SubmitAsync(new SubmissionKey(write, "scalar-" + i), targets[i], f.Subject)).OperationId!;
                await f.Reopen();
                var restored = f.Journal.Recovery.Records.Select(record => record.Entry).OfType<OperationAccepted>().Select(accepted => accepted.Intent.Target).ToArray();
                Check(f.Journal.Recovery.Complete && restored.Length == targets.Length &&
                    restored.Zip(targets, (actual, expected) => actual.SameIdentity(expected) && actual.ContentVersion == expected.ContentVersion).All(equal => equal),
                    "65 and 128 non-BMP scalar targets and whitespace identities survive exact durable roundtrip");
                Check(ids.All(id => f.Ops.Lookup(id, f.Subject).State == "cancelled"), "valid scalar journal does not become recovery incomplete");
                Program.Expect<ArgumentException>(() => new LoadTarget("provider", wide129, null)); checks++;
                Program.Expect<ArgumentException>(() => new LoadTarget("provider", "\uD800", null)); checks++;
                Program.Expect<ArgumentException>(() => new LoadTarget("provider", "\uDC00", null)); checks++;
                var transition = new LoadIntent(wide128, targets[0], f.Context.Read());
                var observation = new LoadReadyObservation(new WorldContextSnapshot(f.Context.Read().InstanceId, 1, "ready", "city"), targets[0], wide128);
                Check(transition.TransitionId == wide128 && observation.CorrelatedTransitionId == wide128, "shared transition and correlation identities use scalar bounds");
                await f.Close();
            }
        }

        private static JToken QueryCurrent(Fixture fixture, string operationId) => new SnapshotCapabilityDispatcher(fixture.Context, Program.ProductVersion, fixture.Ops).Execute(
            "operations.get", "5", new JObject { ["selector"] = new JObject { ["kind"] = "operationId", ["operationId"] = operationId } }, fixture.Subject);
        private sealed class IdentityProof : INativeHostIdentitySource
        {
            private readonly NativeHostState state;
            internal IdentityProof(NativeHostState state) { this.state = state; Current = new ProcessNativeHostIdentity().Current; }
            public NativeHostIdentity Current { get; }
            public NativeHostState Observe(NativeHostIdentity identity) => state;
        }
        private sealed class ControlledFlush : IJournalDurability
        {
            internal int PauseAt, PauseAgainAt, FailAt;
            private int count;
            private long lastActualPosition;
            internal int Calls => Volatile.Read(ref count);
            internal long LastActualPosition => Interlocked.Read(ref lastActualPosition);
            internal readonly TaskCompletionSource<bool> Entered = Completion();
            internal readonly TaskCompletionSource<bool> EnteredAgain = Completion();
            internal readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
            internal readonly ManualResetEventSlim ReleaseAgain = new ManualResetEventSlim(false);
            public void Flush(FileStream file)
            {
                int call = Interlocked.Increment(ref count);
                if (call == PauseAt) { Entered.TrySetResult(true); if (!Release.Wait(5000)) throw new TimeoutException("Fixture flush deadline."); }
                if (call == PauseAgainAt) { EnteredAgain.TrySetResult(true); if (!ReleaseAgain.Wait(5000)) throw new TimeoutException("Fixture batch flush deadline."); }
                file.Flush(true);
                Interlocked.Exchange(ref lastActualPosition, file.Position);
                if (call == FailAt) throw new IOException("Synthetic acknowledgement loss after real flush.");
            }
        }
        private sealed class Fixture : IDisposable
        {
            private readonly string root = Path.Combine(Path.GetFullPath("artifacts"), "durable-runtime-" + Guid.NewGuid().ToString("N"));
            private readonly IPrivateStorageBoundary boundary;
            private readonly EnvironmentLease lease;
            internal readonly string Token = HandshakeProof.RandomHex();
            internal long Now;
            internal OperationJournal Journal { get; private set; }
            internal OperationAuthority Ops { get; private set; }
            internal WorldContextTracker Context { get; private set; }
            internal GrantAuthority Grants { get; }
            internal GrantSnapshot Subject => Grants.Read("probe")!;
            internal string JournalPath => Path.Combine(boundary.DirectoryPath, "operations.journal");
            internal GuardedFileStream? GuardedJournal => (boundary as GuardedJournalStorage)?.Journal;
            internal Fixture(IJournalDurability? durability = null, bool guardJournalLength = false)
            {
                Directory.CreateDirectory(root);
                var storage = new PrivateConnectionStorage(Path.Combine(root, "connection"));
                boundary = guardJournalLength ? new GuardedJournalStorage(storage) : storage;
                lease = new EnvironmentLeaseProvider(boundary).TryAcquireAuthority()!;
                var store = new AtomicAuthorizationStore(boundary, lease, "probe"); store.InitializeEmpty();
                var activated = store.Load().Activate(); store.Save(1, activated); Grants = new GrantAuthority(activated, store);
                Grants.UpdateAsync("probe", GrantScope.Read | GrantScope.Control, Token, null).GetAwaiter().GetResult();
                Journal = new OperationJournal(boundary, lease, "probe", true, durability);
                Context = new WorldContextTracker(Guid.NewGuid().ToString("N"));
                Ops = new OperationAuthority(Journal, Grants, Context, new ProcessNativeHostIdentity(), () => Now);
            }
            internal Task<OperationLookupSnapshot> Accept(string request) => Ops.SubmitAsync(new SubmissionKey(Ops.IssueWriteContext(Subject), request), Target, Subject);
            internal Task Close() => Ops.CloseAsync();
            internal async Task Reopen(bool raw = false, INativeHostIdentitySource? host = null, IJournalDurability? durability = null)
            {
                if (!raw) await Close(); else await Journal.StopAsync();
                Journal = new OperationJournal(boundary, lease, "probe", false, durability);
                Context = new WorldContextTracker(Guid.NewGuid().ToString("N"));
                Ops = new OperationAuthority(Journal, Grants, Context, host ?? new ProcessNativeHostIdentity(), () => Now);
            }
            public void Dispose()
            {
                Journal.StopAsync().GetAwaiter().GetResult(); lease.Dispose();
                string absolute = Path.GetFullPath(root);
                if (!absolute.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture containment failed.");
                foreach (string path in Directory.GetFiles(boundary.DirectoryPath))
                { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture cleanup refuses links."); File.Delete(path); }
                Directory.Delete(boundary.DirectoryPath); Directory.Delete(root);
            }
        }
        private sealed class GuardedJournalStorage : IPrivateStorageBoundary
        {
            private readonly IPrivateStorageBoundary inner;
            internal GuardedJournalStorage(IPrivateStorageBoundary inner) { this.inner = inner; }
            internal GuardedFileStream? Journal { get; private set; }
            public string DirectoryPath => inner.DirectoryPath;
            public void FlushToDisk(FileStream file) => inner.FlushToDisk(file);
            public void ValidateDirectory() => inner.ValidateDirectory();
            public void ValidateFile(string path, bool mayBeMissing) => inner.ValidateFile(path, mayBeMissing);
            public FileStream CreatePrivateFile(string path)
            {
                if (Path.GetFileName(path) != "operations.journal") return inner.CreatePrivateFile(path);
                // 保留真实安全文件创建，只在本夹具观察 Length 并注入有限 Write 故障。
                using (inner.CreatePrivateFile(path)) { }
                return Journal = new GuardedFileStream(path);
            }
        }
        private sealed class GuardedFileStream : FileStream
        {
            private int lengthReads, writeCalls, partialWriteBytes;
            internal volatile bool RejectLength;
            internal volatile int FailWriteAt;
            internal int LengthReads => Volatile.Read(ref lengthReads);
            internal int WriteCalls => Volatile.Read(ref writeCalls);
            internal int PartialWriteBytes => Volatile.Read(ref partialWriteBytes);
            internal GuardedFileStream(string path) : base(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.None) { }
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Interlocked.Increment(ref writeCalls) == FailWriteAt)
                {
                    int prefix = Math.Min(7, count);
                    base.Write(buffer, offset, prefix);
                    Volatile.Write(ref partialWriteBytes, prefix);
                    throw new IOException("Synthetic partial journal frame write.");
                }
                base.Write(buffer, offset, count);
            }
            public override long Length
            {
                get
                {
                    Interlocked.Increment(ref lengthReads);
                    if (RejectLength) throw new IOException("Fixture forbids concurrent file length reads.");
                    return base.Length;
                }
            }
        }
    }
}
