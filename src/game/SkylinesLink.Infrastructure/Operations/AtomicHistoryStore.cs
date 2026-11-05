using System;
using System.IO;
using SkylinesLink.Application.Operations;
using SkylinesLink.Infrastructure.Connection;

namespace SkylinesLink.Infrastructure.Operations
{
    public interface IHistoryStore
    {
        HistoryProjection? Load();
        void Publish(HistoryProjection projection);
    }

    /// <summary>仅供唯一 publisher。组合 owner 必须 join 真实 IO 后才释放借用的环境 lease。</summary>
    public sealed class AtomicHistoryStore : IHistoryStore
    {
        private readonly IPrivateStorageBoundary boundary;
        private readonly EnvironmentLease lease;
        private readonly string environment;
        private readonly CapacityBudget frames = new CapacityBudget(1, HistoryFrameCodec.MaximumMixedFileBytes);
        public AtomicHistoryStore(IPrivateStorageBoundary boundary, EnvironmentLease lease, string environment)
        { this.boundary = boundary; this.lease = lease; this.environment = AuthorizationState.NormalizeEnvironment(environment); }
        public CapacityUsage FrameUsage => frames.Read();
        private string PathName => Path.Combine(boundary.DirectoryPath, "operations.history");
        private void RequireHeld() => lease.WhileHeld(() => true); // 只检查寿命，不在 monitor 内执行文件 IO。
        public HistoryProjection? Load()
        {
            RequireHeld(); boundary.ValidateDirectory(); boundary.ValidateFile(PathName, true);
            if (!File.Exists(PathName)) return null;
            CapacityReservation? reservation = null;
            byte[]? bytes = null;
            var header = new byte[12];
            try
            {
                int size;
                using (var file = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    if (file.Length < 45 || file.Length > HistoryFrameCodec.MaximumMixedFileBytes) throw new InvalidDataException("History file capacity invalid.");
                    ReadExactly(file, header, 0, header.Length);
                    int maximum = HistoryFrameCodec.MaximumForHeader(header, header.Length);
                    if (file.Length > maximum) throw new InvalidDataException("History file format capacity invalid.");
                    size = checked((int)file.Length);
                    reservation = frames.TryReserve(size) ?? throw new InvalidOperationException("History frame busy.");
                    bytes = new byte[size]; Buffer.BlockCopy(header, 0, bytes, 0, header.Length);
                    ReadExactly(file, bytes, header.Length, size - header.Length);
                    if (file.ReadByte() != -1) throw new InvalidDataException("History file length changed.");
                }
                var projection = HistoryFrameCodec.Decode(bytes, size);
                if (projection.Source.EnvironmentId != environment) throw new InvalidDataException("History environment mismatch.");
                RequireHeld(); return projection;
            }
            finally
            {
                Array.Clear(header, 0, header.Length);
                if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
                reservation?.Dispose();
            }
        }
        public void Publish(HistoryProjection projection)
        {
            if (projection.Source.EnvironmentId != environment) throw new ArgumentException("History environment mismatch.");
            RequireHeld(); boundary.ValidateDirectory(); boundary.ValidateFile(PathName, true);
            int capacity = HistoryFrameCodec.RequiredCapacity(projection);
            using (var reservation = frames.TryReserve(capacity) ?? throw new InvalidOperationException("History frame busy."))
            {
                var bytes = new byte[capacity];
                string temporary = Path.Combine(boundary.DirectoryPath, "history-" + Guid.NewGuid().ToString("N") + ".tmp");
                bool createdOwned = false;
                try
                {
                    int length = HistoryFrameCodec.Encode(projection, bytes);
                    using (var file = boundary.CreatePrivateFile(temporary)) { createdOwned = true; file.Write(bytes, 0, length); boundary.FlushToDisk(file); }
                    boundary.ValidateFile(temporary, false); boundary.ValidateDirectory(); boundary.ValidateFile(PathName, true);
                    RequireHeld();
                    if (File.Exists(PathName)) File.Replace(temporary, PathName, null, false); else File.Move(temporary, PathName);
                    createdOwned = false;
                    boundary.ValidateFile(PathName, false); RequireHeld();
                }
                finally
                {
                    Array.Clear(bytes, 0, bytes.Length);
                    if (createdOwned && File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }
        private static void ReadExactly(Stream source, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = source.Read(buffer, offset, count);
                if (read == 0) throw new EndOfStreamException();
                offset += read; count -= read;
            }
        }
    }
}
