using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Operations;
using SkylinesLink.Contracts;

namespace SkylinesLink.Mod.Connection
{
    public interface IReadOnlyCapabilityDispatcher
    {
        JToken Execute(string capabilityId, string schemaRevision, JToken arguments, GrantSnapshot subject);
    }

    public sealed class CapabilityFailure : Exception
    {
        public CapabilityFailure(string code, string publicMessage) : base(publicMessage) { Code = code; }
        public string Code { get; }
    }

    /// <summary>固定接入槽；认证后每连接一个receiver及owned串行sender，回复责任全局有界。</summary>
    public sealed class BoundedPipeServer : IDisposable
    {
        private readonly string name;
        private readonly EnvironmentDiscovery identity;
        private readonly PipeLimits limits;
        private readonly GrantAuthority authentication;
        private readonly IReadOnlyCapabilityDispatcher dispatcher;
        private readonly Action<string> report;
        private readonly CapacityBudget ingressBudget;
        private readonly CapacityBudget egressBudget;
        private readonly PipeReplyBudget replyBudget = new PipeReplyBudget();
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        private readonly object sync = new object();
        private NamedPipeServerStream?[] streams;
        private Task[] workers = Array.Empty<Task>();
        private bool started;
        private bool disposed;
        private int connected;
        private int authenticated;

        public BoundedPipeServer(EnvironmentDiscovery identity,
            PipeLimits limits, GrantAuthority authentication, IReadOnlyCapabilityDispatcher dispatcher, Action<string>? report = null)
        {
            ValidatePipeName(identity.PipeName);
            AuthorizationState.ValidateHex(identity.CoreManifestSha256);
            if (identity.ContractVersion != "2" || identity.EnvironmentId != authentication.State.EnvironmentId || identity.AuthorityEpoch != authentication.State.AuthorityEpoch)
                throw new ArgumentException("Pipe authority identity mismatch.");
            this.name = identity.PipeName;
            this.identity = ContractCodec.DecodeEnvironmentDiscovery(ContractCodec.EncodeEnvironmentDiscovery(identity));
            this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
            this.authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.report = report ?? (_ => { });
            ingressBudget = new CapacityBudget(limits.MaximumDirectionalPayloadBuffers, limits.MaximumDirectionalPayloadCapacity);
            egressBudget = new CapacityBudget(limits.MaximumDirectionalPayloadBuffers, limits.MaximumDirectionalPayloadCapacity);
            streams = new NamedPipeServerStream?[limits.MaximumConnections];
        }

        public int ConnectedCount => Volatile.Read(ref connected);
        public int AuthenticatedCount => Volatile.Read(ref authenticated);
        public CapacityUsage IngressPayloadUsage => ingressBudget.Read();
        public CapacityUsage EgressPayloadUsage => egressBudget.Read();
        public PipePayloadUsage OwnedPayloadUsage => new PipePayloadUsage(ingressBudget.Read(), egressBudget.Read(), limits);
        public PipeReplyUsage ReplyUsage => replyBudget.Read();
        public Task Completion { get { lock (sync) return Task.WhenAll(workers); } }

        public void Start()
        {
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(BoundedPipeServer));
                if (started) throw new InvalidOperationException("Pipe listener has already started.");
                try
                {
                    for (int i = 0; i < streams.Length; i++) streams[i] = CreateStream();
                    workers = Enumerable.Range(0, streams.Length).Select(index => Task.Run(() => RunSlotAsync(index))).ToArray();
                    started = true;
                }
                catch
                {
                    foreach (var stream in streams) stream?.Dispose();
                    throw;
                }
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                shutdown.Cancel();
                foreach (var stream in streams) stream?.Dispose();
            }
            // 不在游戏线程同步等 IO；调用者可在独立测试/关闭协调中观察 Completion。
        }

        public static void ValidatePipeName(string value)
        {
            if (value == null || !value.StartsWith("skylineslink-", StringComparison.Ordinal) || value.Length > 120 || value.Length <= 13)
                throw new ArgumentException("Invalid local pipe name.", nameof(value));
            foreach (char c in value)
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-'))
                    throw new ArgumentException("Invalid local pipe name.", nameof(value));
        }

        private NamedPipeServerStream CreateStream() => new NamedPipeServerStream(name, PipeDirection.InOut, limits.MaximumConnections,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, LogonPipeSecurity.Create(), HandleInheritability.None, (PipeAccessRights)0);

        private async Task RunSlotAsync(int index)
        {
            while (!shutdown.IsCancellationRequested)
            {
                NamedPipeServerStream stream;
                lock (sync)
                {
                    if (disposed) return;
                    stream = streams[index] ?? throw new InvalidOperationException("Pipe slot is unavailable.");
                }
                bool hasConnection = false;
                try
                {
                    await stream.WaitForConnectionAsync(shutdown.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref connected);
                    hasConnection = true;
                    await ServeAsync(stream).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is OperationCanceledException ||
                    error is PipeProtocolException || error is ContractValidationException || error is Newtonsoft.Json.JsonException)
                {
                    if (!shutdown.IsCancellationRequested) report("CONNECTION_CLOSED");
                }
                catch
                {
                    // 不把任意异常内容、请求或令牌写到日志；隔离这条连接的处理失败。
                    report("CONNECTION_FAILED");
                }
                finally
                {
                    stream.Dispose();
                    if (hasConnection) Interlocked.Decrement(ref connected);
                }
                lock (sync)
                {
                    if (disposed) { streams[index] = null; return; }
                    try { streams[index] = CreateStream(); }
                    catch { report("LISTENER_FAILED"); Dispose(); return; }
                }
            }
        }

        private async Task ServeAsync(NamedPipeServerStream stream)
        {
            GrantSnapshot? subject = await AuthenticateAsync(stream).ConfigureAwait(false);
            if (subject == null) return;
            Interlocked.Increment(ref authenticated);
            try
            {
                using (var connection = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token))
                using (var replies = new ReplyQueue(report))
                {
                    Task receiver = ReceiveAsync(stream, subject, replies, connection.Token);
                    Task sender = SendAsync(stream, subject, replies, connection.Token);
                    try { await Task.WhenAny(receiver, sender).ConfigureAwait(false); }
                    finally
                    {
                        connection.Cancel(); stream.Dispose(); replies.Abort();
                        // slot不能在另一个loop或其真实IO尚未结束时被替换。
                        await Task.WhenAll(receiver, sender).ConfigureAwait(false);
                    }
                }
            }
            finally { Interlocked.Decrement(ref authenticated); }
        }

        private async Task ReceiveAsync(Stream stream, GrantSnapshot subject, ReplyQueue replies, CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                string? json = await FrameCodec.ReadAsync(stream, limits, limits.FrameTimeout, cancellation, ingressBudget).ConfigureAwait(false);
                if (json == null) return;
                bool keepReceiving = ReceiveFrame(json, subject, replies);
                json = null; // 不让父request DOM/原文越过下一次异步读取。
                if (!keepReceiving) { await replies.SenderEnded.ConfigureAwait(false); return; }
            }
        }

        private bool ReceiveFrame(string json, GrantSnapshot subject, ReplyQueue replies)
        {
            JToken request = ContractCatalog.ParseAndValidate(SchemaIds.WireRequest, json);
            string requestId = (string)request["requestId"]!;
            string capabilityId = (string)request["capabilityId"]!;
            string revision = (string)request["schemaRevision"]!;
            bool isStop = capabilityId == ControlTransportProfile.CapabilityId && revision == ControlTransportProfile.SchemaRevision;
            GrantScope required = isStop ? GrantScope.Control : GrantScope.Read;
            if (!authentication.Allows(subject, required))
            {
                QueueError(replies, requestId, "UNAUTHORIZED", "连接授权已失效。", required, closeAfterSend: true);
                return false;
            }
            var binding = new RegisteredBinding(capabilityId, revision);
            if (isStop)
            {
                if (StrictUtf8.GetByteCount(json) > ControlTransportProfile.MaximumRequestBytes) throw new PipeProtocolException("FRAME_LIMIT");
                var selector = ContractCodec.DecodeTaskStopQuery(request["arguments"]!);
                var stop = dispatcher as ITaskStopCapabilityDispatcher;
                try
                {
                    if (stop == null) throw new OperationRejectedException("TASKS_UNAVAILABLE");
                    stop.ValidateTaskStop(selector, subject);
                }
                catch (OperationRejectedException error)
                {
                    if (error.Code == "TASKS_UNAVAILABLE") QueueResult(replies, requestId, binding,
                        TaskStopQueryService.CompactUnavailable(selector, error.Code), required, ControlTransportProfile.MaximumResponseBytes);
                    else QueueError(replies, requestId, error.Code, "停止请求未获受理。", required);
                    return true;
                }
                var credit = replyBudget.TryControl(selector.TaskId, selector.ExpectedControlVersion);
                if (credit == null)
                {
                    QueueError(replies, requestId, "BUSY", "此停止身份仍有在途回复，或控制额度已满。", required);
                    return true;
                }
                Reply reply;
                try
                {
                    // 回复责任已拥有，Begin才可产生真实sticky决定；持久Work不归连接取消。
                    var accepted = stop!.BeginTaskStop(selector, subject);
                    reply = new Reply(requestId, binding, required, credit, selector, accepted.Key, stop);
                }
                catch (OperationRejectedException error)
                {
                    reply = ErrorReply(requestId, error.Code, "停止请求未获受理。", required, credit);
                }
                catch { credit.Dispose(); throw; }
                replies.Enqueue(reply);
                return true;
            }
            if (!IsReadCapability(capabilityId, revision))
            { QueueError(replies, requestId, "UNSUPPORTED_CAPABILITY", "当前能力尚未实现。", required); return true; }
            var ordinary = replyBudget.TryOrdinary() ?? throw new PipeProtocolException("REPLY_BUDGET");
            try
            {
                if (dispatcher is IDeferredReadCapabilityDispatcher deferred)
                {
                    // 先拥有回复责任；采样未就绪不能占住receiver或串行sender。
                    long deadline = Stopwatch.GetTimestamp() + (long)(limits.FrameTimeout.TotalSeconds * Stopwatch.Frequency);
                    var read = deferred.BeginRead(capabilityId, revision, request["arguments"]!, subject);
                    replies.Enqueue(new Reply(requestId, binding, required, ordinary, read: read, readDeadline: deadline));
                }
                else
                {
                    JToken result = dispatcher.Execute(capabilityId, revision, request["arguments"]!, subject);
                    replies.Enqueue(ResultReply(requestId, binding, result, required, ordinary));
                }
            }
            catch (CapabilityFailure error) { replies.Enqueue(ErrorReply(requestId, error.Code, error.Message, required, ordinary)); }
            catch (OperationRejectedException error) { replies.Enqueue(ErrorReply(requestId, error.Code, "当前请求未获受理。", required, ordinary)); }
            catch { ordinary.Dispose(); throw; }
            return true;
        }

        private async Task SendAsync(Stream stream, GrantSnapshot subject, ReplyQueue replies, CancellationToken cancellation)
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    using (var reply = await replies.TakeAsync(cancellation).ConfigureAwait(false))
                    {
                        if (reply == null) return;
                        bool close = reply.CloseAfterSend;
                        try
                        {
                            if (!authentication.Allows(subject, reply.RequiredScope)) throw new CapabilityFailure("UNAUTHORIZED", "连接授权已失效。");
                            if (reply.StopSelector != null)
                            {
                                var result = reply.StopDispatcher!.RefreshTaskStop(reply.StopSelector, reply.StopKey!, subject);
                                reply.OwnFrame(FreezeResult(reply.RequestId, reply.Binding!, result, ControlTransportProfile.MaximumResponseBytes));
                            }
                            else if (reply.Read != null)
                            {
                                // ReplyQueue只交付已终结读取；这里不能等待原生工作。
                                if (reply.ReadExpired)
                                {
                                    reply.Read.Dispose();
                                    throw new CapabilityFailure("READ_TIMEOUT", "当前读取未在等待期限内交付，请重新读取。");
                                }
                                JToken result = reply.Read.GetCurrentResult();
                                reply.OwnFrame(FreezeResult(reply.RequestId, reply.Binding!, result));
                            }
                            // 最后可撤销点在第一byte前；已交给真实IO的字节无法召回。
                            if (!authentication.Allows(subject, reply.RequiredScope)) throw new CapabilityFailure("UNAUTHORIZED", "连接授权已失效。");
                        }
                        catch (OperationRejectedException error)
                        {
                            reply.OwnFrame(FreezeError(reply.RequestId, error.Code, "当前停止回执不可用。"));
                            close |= error.Code == "UNAUTHORIZED";
                        }
                        catch (CapabilityFailure error)
                        {
                            reply.OwnFrame(FreezeError(reply.RequestId, error.Code, error.Message)); close |= error.Code == "UNAUTHORIZED";
                        }
                        catch (OperationCanceledException) when (reply.Read != null)
                        { reply.OwnFrame(FreezeError(reply.RequestId, "READ_CANCELLED", "当前读取已取消。")); }
                        await FrameCodec.WriteFrozenAsync(stream, reply.Frame!, limits, cancellation, egressBudget).ConfigureAwait(false);
                        if (close) return;
                    }
                }
            }
            finally { replies.NotifySenderEnded(); }
        }

        private void QueueResult(ReplyQueue replies, string requestId, RegisteredBinding binding, JToken result, GrantScope scope, int maximumBytes)
        {
            var credit = replyBudget.TryOrdinary() ?? throw new PipeProtocolException("REPLY_BUDGET");
            try { replies.Enqueue(ResultReply(requestId, binding, result, scope, credit, maximumBytes)); }
            catch { credit.Dispose(); throw; }
        }
        private Reply ResultReply(string requestId, RegisteredBinding binding, JToken result, GrantScope scope,
            PipeReplyBudget.ReplyCredit credit, int? maximumBytes = null)
        {
            var reply = new Reply(requestId, binding, scope, credit);
            try { reply.OwnFrame(FreezeResult(requestId, binding, result, maximumBytes)); return reply; }
            catch { reply.Dispose(); throw; }
        }
        private FrameCodec.FrozenFrame FreezeResult(string requestId, RegisteredBinding binding, JToken result, int? maximumBytes = null)
        {
            var response = new JObject { ["kind"] = "result", ["requestId"] = requestId, ["result"] = result };
            ValidateOutput(SchemaIds.WireResult, response, binding);
            return FrameCodec.Freeze(response, limits, maximumBytes);
        }
        private void QueueError(ReplyQueue replies, string requestId, string code, string message, GrantScope scope, bool closeAfterSend = false)
        {
            var credit = replyBudget.TryOrdinary() ?? throw new PipeProtocolException("REPLY_BUDGET");
            try { replies.Enqueue(ErrorReply(requestId, code, message, scope, credit, closeAfterSend)); }
            catch { credit.Dispose(); throw; }
        }
        private Reply ErrorReply(string requestId, string code, string message, GrantScope scope, PipeReplyBudget.ReplyCredit credit, bool closeAfterSend = false)
        {
            var reply = new Reply(requestId, null, scope, credit) { CloseAfterSend = closeAfterSend };
            try { reply.OwnFrame(FreezeError(requestId, code, message)); return reply; }
            catch { reply.Dispose(); throw; }
        }
        private FrameCodec.FrozenFrame FreezeError(string requestId, string code, string message)
        {
            var error = new JObject { ["kind"] = "error", ["requestId"] = requestId, ["code"] = code, ["message"] = message };
            ValidateOutput(SchemaIds.WireError, error); return FrameCodec.Freeze(error, limits);
        }

        private async Task<GrantSnapshot?> AuthenticateAsync(NamedPipeServerStream stream)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token))
            using (deadline.Token.Register(stream.Dispose))
            {
                deadline.CancelAfter(limits.HandshakeTimeout);
                string? json = await FrameCodec.ReadAsync(stream, limits, limits.HandshakeTimeout, deadline.Token, ingressBudget).ConfigureAwait(false);
                if (json == null) return null;
                var hello = ContractCodec.DecodeWireAuthenticateV2(ContractCatalog.ParseAndValidate(SchemaIds.WireAuthenticateV2, json));
                AuthorizationState.ValidateHex(hello.ClientNonce);
                if (hello.ProductVersion != identity.ProductVersion || hello.ContractVersion != identity.ContractVersion || hello.CoreManifestSha256 != identity.CoreManifestSha256)
                { await SendError(stream, hello.RequestId, "INCOMPATIBLE_COMPONENT", "连接组件版本不匹配。").ConfigureAwait(false); return null; }
                if (hello.EnvironmentId != identity.EnvironmentId || hello.ExpectedInstanceId != identity.InstanceId || hello.ExpectedAuthorityEpoch != identity.AuthorityEpoch)
                { await SendError(stream, hello.RequestId, "STALE_DISCOVERY", "连接实例身份已失效。").ConfigureAwait(false); return null; }
                var capture = authentication.CaptureAuthentication(hello.ProfileId, hello.ExpectedAuthorityEpoch, hello.ExpectedGrantEpoch);
                if (capture == null) { await SendError(stream, hello.RequestId, "UNAUTHORIZED", "连接授权无效。").ConfigureAwait(false); return null; }
                var challenge = new WireChallengeV2 { Kind = "challenge", RequestId = hello.RequestId, EnvironmentId = identity.EnvironmentId,
                    ProfileId = hello.ProfileId, InstanceId = identity.InstanceId, ProductVersion = identity.ProductVersion, ContractVersion = identity.ContractVersion,
                    CoreManifestSha256 = identity.CoreManifestSha256, AuthorityEpoch = capture.Subject.AuthorityEpoch, GrantEpoch = capture.Subject.Epoch,
                    ClientNonce = hello.ClientNonce, ServerNonce = HandshakeProof.RandomHex(), ServerProof = "" };
                challenge.ServerProof = HandshakeProof.Compute(capture.InternalToken, "server", challenge);
                await FrameCodec.WriteAsync(stream, ContractCodec.EncodeWireChallengeV2(challenge), limits, deadline.Token, egressBudget).ConfigureAwait(false);
                json = await FrameCodec.ReadAsync(stream, limits, limits.HandshakeTimeout, deadline.Token, ingressBudget).ConfigureAwait(false);
                if (json == null) return null;
                var proof = ContractCodec.DecodeWireProofV2(ContractCatalog.ParseAndValidate(SchemaIds.WireProofV2, json));
                if (proof.RequestId != hello.RequestId || !HandshakeProof.EqualsProof(HandshakeProof.Compute(capture.InternalToken, "client", challenge), proof.ClientProof) ||
                    !authentication.Allows(capture.Subject, capture.Subject.Scope))
                { await SendError(stream, hello.RequestId, "UNAUTHORIZED", "连接授权无效。").ConfigureAwait(false); return null; }
                var accepted = new WireAuthenticatedV2 { Kind = "authenticated", RequestId = hello.RequestId, EnvironmentId = identity.EnvironmentId,
                    ProfileId = hello.ProfileId, InstanceId = identity.InstanceId, ProductVersion = identity.ProductVersion, ContractVersion = identity.ContractVersion,
                    CoreManifestSha256 = identity.CoreManifestSha256, AuthorityEpoch = capture.Subject.AuthorityEpoch, GrantEpoch = capture.Subject.Epoch };
                await FrameCodec.WriteAsync(stream, ContractCodec.EncodeWireAuthenticatedV2(accepted), limits, deadline.Token, egressBudget).ConfigureAwait(false);
                return capture.Subject;
            }
        }

        private async Task SendError(Stream stream, string requestId, string code, string message)
        {
            var error = new JObject { ["kind"] = "error", ["requestId"] = requestId, ["code"] = code, ["message"] = message };
            ValidateOutput(SchemaIds.WireError, error);
            await FrameCodec.WriteAsync(stream, error, limits, shutdown.Token, egressBudget).ConfigureAwait(false);
        }

        private static bool IsReadCapability(string capabilityId, string revision)
        {
            foreach (var capability in ContractCatalog.Capabilities)
                if (capability.Id == capabilityId && capability.SchemaRevision == revision)
                    return capability.Effect == "read";
            return false;
        }

        private static void ValidateOutput(string schema, JToken value, RegisteredBinding? binding = null)
        {
            var result = ContractCatalog.Validate(schema, value, binding);
            if (!result.Valid) throw new ContractValidationException(result.Issues);
        }

        private sealed class Reply : IDisposable
        {
            internal Reply(string requestId, RegisteredBinding? binding, GrantScope scope, PipeReplyBudget.ReplyCredit credit,
                TaskStopSelector? selector = null, SubmissionKey? key = null, ITaskStopCapabilityDispatcher? stop = null,
                DeferredCapabilityRead? read = null, long readDeadline = 0)
            {
                RequestId = requestId; Binding = binding; RequiredScope = scope; Credit = credit;
                StopSelector = selector; StopKey = key; StopDispatcher = stop;
                Read = read;
                ReadDeadline = readDeadline;
            }
            internal readonly string RequestId;
            internal readonly RegisteredBinding? Binding;
            internal readonly GrantScope RequiredScope;
            internal readonly PipeReplyBudget.ReplyCredit Credit;
            internal readonly TaskStopSelector? StopSelector;
            internal readonly SubmissionKey? StopKey;
            internal readonly ITaskStopCapabilityDispatcher? StopDispatcher;
            internal DeferredCapabilityRead? Read;
            private readonly long ReadDeadline;
            private bool expirationClaimed;
            internal bool ReadExpired => Read != null && Stopwatch.GetTimestamp() >= ReadDeadline;
            internal bool IsReady => Read == null || Read.Completion.IsCompleted || ReadExpired;
            internal int RemainingWaitMilliseconds => Read == null ? Timeout.Infinite :
                (int)Math.Max(1, Math.Min(int.MaxValue, Math.Ceiling((ReadDeadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency)));
            internal bool ClaimExpiration()
            {
                if (expirationClaimed || !ReadExpired) return false;
                expirationClaimed = true; return true;
            }
            internal FrameCodec.FrozenFrame? Frame { get; private set; }
            internal bool CloseAfterSend;
            internal void OwnFrame(FrameCodec.FrozenFrame frame) { Credit.OwnText(frame); Frame = frame; }
            public void Dispose()
            {
                // 即使sender的async局部仍暂存已Dispose的Reply，也不继续保活结果Task/DOM。
                var read = Interlocked.Exchange(ref Read, null); Frame = null;
                try { read?.Dispose(); } finally { Credit.Dispose(); }
            }
        }

        private sealed class ReplyQueue : IDisposable
        {
            private readonly Action<string> report;
            private readonly object sync = new object();
            private readonly List<Reply> ordinary = new List<Reply>();
            private readonly Queue<Reply> control = new Queue<Reply>();
            private readonly SemaphoreSlim available = new SemaphoreSlim(0, 1);
            private readonly TaskCompletionSource<bool> senderEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private bool closed;
            internal ReplyQueue(Action<string> report) { this.report = report; }
            internal Task SenderEnded => senderEnded.Task;
            internal void NotifySenderEnded() => senderEnded.TrySetResult(true);
            internal void Enqueue(Reply reply)
            {
                lock (sync)
                {
                    if (!closed)
                    {
                        if (reply.Credit.IsControl) control.Enqueue(reply); else ordinary.Add(reply);
                        if (reply.Read != null && !reply.IsReady)
                            reply.Read.Completion.ContinueWith(completed =>
                            {
                                if (completed.IsFaulted) _ = completed.Exception;
                                Wake();
                            }, CancellationToken.None,
                                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        Signal(); return;
                    }
                }
                reply.Dispose(); throw new ObjectDisposedException(nameof(ReplyQueue));
            }
            internal async Task<Reply?> TakeAsync(CancellationToken cancellation)
            {
                while (true)
                {
                    int waitMilliseconds = Timeout.Infinite;
                    Reply? selected = null;
                    List<Reply>? expired = null;
                    lock (sync)
                    {
                        if (closed) return null;
                        // 即使控制回复持续就绪，也先认领所有已过期消费者的撤销责任。
                        foreach (var candidate in ordinary)
                            if (candidate.ClaimExpiration()) (expired ??= new List<Reply>()).Add(candidate);
                        if (control.Count != 0) selected = control.Dequeue();
                        // 全局普通责任最多8项；一个未就绪读取不阻挡其后的已就绪回复。
                        for (int index = 0; selected == null && index < ordinary.Count; index++)
                        {
                            var reply = ordinary[index];
                            if (reply.IsReady) { ordinary.RemoveAt(index); selected = reply; break; }
                            int remaining = reply.RemainingWaitMilliseconds;
                            waitMilliseconds = waitMilliseconds == Timeout.Infinite ? remaining : Math.Min(waitMilliseconds, remaining);
                        }
                    }
                    try { if (expired != null) foreach (var reply in expired) reply.Read?.Dispose(); }
                    catch
                    {
                        // selected已出队但尚未交给sender；清理失败不能遗失它的回复责任。
                        selected?.Dispose(); throw;
                    }
                    if (selected != null) return selected;
                    // 一个连接只有这一个wait；合法后续输入不能续延原读取的绝对期限。
                    await available.WaitAsync(waitMilliseconds, cancellation).ConfigureAwait(false);
                }
            }
            private void Wake() { lock (sync) if (!closed) Signal(); }
            // 通知仅表示可能有就绪工作。所有访问受同一锁保护，最多保留一个唤醒。
            private void Signal() { if (available.CurrentCount == 0) available.Release(); }
            internal void Abort()
            {
                Reply[] detached;
                lock (sync)
                {
                    if (closed) return;
                    closed = true;
                    detached = control.Concat(ordinary).ToArray();
                    control.Clear(); ordinary.Clear();
                }
                // 消费者撤销可能同步完成source，不能持queue锁调用provider。
                bool failed = false;
                foreach (var reply in detached)
                {
                    try { reply.Dispose(); }
                    catch { failed = true; }
                }
                if (failed) report("READ_CONSUMER_RELEASE_FAILED");
            }
            public void Dispose() { Abort(); available.Dispose(); }
        }
    }
}
