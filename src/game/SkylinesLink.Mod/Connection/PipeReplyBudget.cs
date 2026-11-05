using System;
using System.Collections.Generic;
using System.Diagnostics;
using SkylinesLink.Contracts;

namespace SkylinesLink.Mod.Connection
{
    /// <summary>同一server的回复责任。排队和在途都持额，不把Task数当作RPC数。</summary>
    internal sealed class PipeReplyBudget
    {
        private readonly object sync = new object();
        private readonly HashSet<ControlIdentity> controls = new HashSet<ControlIdentity>();
        private int ordinary, peakOrdinary, peakControl, ordinaryMessages, controlMessages;
        private long windowStarted = Stopwatch.GetTimestamp();
        private long textCharacters, textBytes, peakTextCharacters, peakTextBytes;

        internal ReplyCredit? TryOrdinary()
        {
            lock (sync)
            {
                RefreshWindow();
                if (ordinary == ControlTransportProfile.OrdinaryActiveRequests ||
                    ordinaryMessages == ControlTransportProfile.OrdinaryMessagesPerWindow) return null;
                ordinary++; ordinaryMessages++; peakOrdinary = Math.Max(peakOrdinary, ordinary);
                return new ReplyCredit(this, null);
            }
        }

        internal ReplyCredit? TryControl(string taskId, long expectedControlVersion)
        {
            lock (sync)
            {
                RefreshWindow();
                var identity = new ControlIdentity(taskId, expectedControlVersion);
                if (controls.Contains(identity) || controls.Count == ControlTransportProfile.MaximumControlRequests ||
                    controlMessages == ControlTransportProfile.MaximumControlMessagesPerWindow) return null;
                controls.Add(identity); controlMessages++; peakControl = Math.Max(peakControl, controls.Count);
                return new ReplyCredit(this, identity);
            }
        }

        internal PipeReplyUsage Read()
        {
            lock (sync) return new PipeReplyUsage(ordinary, controls.Count, peakOrdinary, peakControl,
                textCharacters, textBytes, peakTextCharacters, peakTextBytes);
        }

        private void RefreshWindow()
        {
            long now = Stopwatch.GetTimestamp();
            if (now - windowStarted < (long)Stopwatch.Frequency * ControlTransportProfile.WindowMilliseconds / 1000) return;
            windowStarted = now; ordinaryMessages = controlMessages = 0;
        }

        internal sealed class ReplyCredit : IDisposable
        {
            private PipeReplyBudget? owner;
            private readonly ControlIdentity? identity;
            private int characters, bytes;
            internal ReplyCredit(PipeReplyBudget owner, ControlIdentity? identity) { this.owner = owner; this.identity = identity; }
            internal bool IsControl => identity.HasValue;
            internal void OwnText(FrameCodec.FrozenFrame frame)
            {
                var current = owner ?? throw new ObjectDisposedException(nameof(ReplyCredit));
                lock (current.sync)
                {
                    if (owner == null) throw new ObjectDisposedException(nameof(ReplyCredit));
                    current.textCharacters += frame.Json.Length - characters; current.textBytes += frame.PayloadBytes - bytes;
                    characters = frame.Json.Length; bytes = frame.PayloadBytes;
                    current.peakTextCharacters = Math.Max(current.peakTextCharacters, current.textCharacters);
                    current.peakTextBytes = Math.Max(current.peakTextBytes, current.textBytes);
                }
            }
            public void Dispose()
            {
                var current = owner;
                if (current == null) return;
                lock (current.sync)
                {
                    if (owner == null) return;
                    if (identity.HasValue) current.controls.Remove(identity.Value); else current.ordinary--;
                    current.textCharacters -= characters; current.textBytes -= bytes; owner = null;
                }
            }
        }

        internal readonly struct ControlIdentity : IEquatable<ControlIdentity>
        {
            private readonly string taskId;
            private readonly long version;
            internal ControlIdentity(string taskId, long version) { this.taskId = taskId; this.version = version; }
            public bool Equals(ControlIdentity other) => version == other.version && string.Equals(taskId, other.taskId, StringComparison.Ordinal);
            public override bool Equals(object? value) => value is ControlIdentity other && Equals(other);
            public override int GetHashCode() => unchecked(StringComparer.Ordinal.GetHashCode(taskId) * 397 ^ version.GetHashCode());
        }
    }

    public sealed class PipeReplyUsage
    {
        internal PipeReplyUsage(int ordinary, int control, int peakOrdinary, int peakControl,
            long textCharacters, long textBytes, long peakTextCharacters, long peakTextBytes)
        {
            OrdinaryCount = ordinary; ControlCount = control; PeakOrdinaryCount = peakOrdinary; PeakControlCount = peakControl;
            OwnedTextCharacters = textCharacters; OwnedTextUtf8Bytes = textBytes;
            PeakOwnedTextCharacters = peakTextCharacters; PeakOwnedTextUtf8Bytes = peakTextBytes;
        }
        public int OrdinaryCount { get; }
        public int ControlCount { get; }
        public int PeakOrdinaryCount { get; }
        public int PeakControlCount { get; }
        public long OwnedTextCharacters { get; }
        public long OwnedTextUtf8Bytes { get; }
        public long PeakOwnedTextCharacters { get; }
        public long PeakOwnedTextUtf8Bytes { get; }
    }
}
