using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using SkylinesLink.Application.Operations;
using SkylinesLink.Contracts;

namespace SkylinesLink.Infrastructure.Connection
{
    public interface IPrivateStorageBoundary
    {
        string DirectoryPath { get; }
        void ValidateDirectory();
        void ValidateFile(string path, bool mayBeMissing);
        FileStream CreatePrivateFile(string path);
        void FlushToDisk(FileStream file);
    }

    /// <summary>只写固定的私有记录。调用方必须持有真实进程租约；不从临时文件猜测恢复授权。</summary>
    public sealed class AtomicAuthorizationStore : IAuthorizationStore
    {
        private readonly IPrivateStorageBoundary boundary;
        private readonly EnvironmentLease lease;
        private readonly string environment;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public AtomicAuthorizationStore(IPrivateStorageBoundary boundary, EnvironmentLease lease, string environment)
        { this.boundary = boundary; this.lease = lease; this.environment = AuthorizationState.NormalizeEnvironment(environment); }
        public string AuthorizationPath => Path.Combine(boundary.DirectoryPath, "authorization.json");
        public string DiscoveryPath => Path.Combine(boundary.DirectoryPath, "discovery.json");
        public bool HasAuthorization => lease.WhileHeld(() => { boundary.ValidateFile(AuthorizationPath, true); return File.Exists(AuthorizationPath); });
        public AuthorizationState Load() => lease.WhileHeld(LoadCore);
        private AuthorizationState LoadCore()
        {
            var dto = ContractCodec.DecodeAuthorizationSnapshotV2(ContractCatalog.ParseAndValidate(SchemaIds.AuthorizationSnapshotV2, Read(AuthorizationPath, 65536)));
            if (dto.FormatVersion != 2 || dto.ProductId != "skylineslink" || dto.EnvironmentId != environment) throw new InvalidDataException("Authorization identity mismatch.");
            return new AuthorizationState(dto.EnvironmentId, dto.RecordRevision, dto.AuthorityEpoch,
                dto.Profiles.Select(p => new AuthorizationProfile(p.ProfileId, p.ProfileInstanceId, p.GrantEpoch, (GrantScope)p.ScopeMask, p.InternalToken, p.ExternalHttpToken)));
        }
        public void InitializeEmpty() => lease.WhileHeld(() =>
        {
            WriteAuthorization(new AuthorizationState(environment, 1, 0, Array.Empty<AuthorizationProfile>()), true);
            return true;
        });
        public void Save(long expectedRevision, AuthorizationState replacement) => lease.WhileHeld(() =>
        {
            var previous = LoadCore();
            if (previous.RecordRevision != expectedRevision || replacement.RecordRevision != AuthorizationState.Next(expectedRevision) || replacement.EnvironmentId != environment)
                throw new InvalidDataException("Authorization revision mismatch.");
            foreach (var profile in replacement.Profiles)
            {
                var sameName = previous.Find(profile.ProfileId);
                var sameIdentity = previous.Profiles.FirstOrDefault(old => old.ProfileInstanceId == profile.ProfileInstanceId);
                if ((sameName != null && sameName.ProfileInstanceId != profile.ProfileInstanceId) ||
                    (sameIdentity != null && sameIdentity.ProfileId != profile.ProfileId))
                    throw new InvalidDataException("Authorization profile identity changed without separate removal.");
            }
            WriteAuthorization(replacement, false);
            return true;
        });
        private void WriteAuthorization(AuthorizationState state, bool createOnly)
        {
            var dto = new AuthorizationSnapshotV2 { FormatVersion = 2, ProductId = "skylineslink", EnvironmentId = state.EnvironmentId, RecordRevision = state.RecordRevision,
                AuthorityEpoch = state.AuthorityEpoch, Profiles = state.Profiles.Select(p => new AuthorizationSnapshotV2ProfilesItem {
                    ProfileId = p.ProfileId, ProfileInstanceId = p.ProfileInstanceId, GrantEpoch = p.GrantEpoch, ScopeMask = (int)p.Scope,
                    InternalToken = p.InternalToken, ExternalHttpToken = p.ExternalHttpToken }).ToList() };
            Write(AuthorizationPath, ContractCodec.EncodeAuthorizationSnapshotV2(dto).ToString(Formatting.None), 65536, createOnly);
        }
        public void Publish(EnvironmentDiscovery discovery) => lease.WhileHeld(() =>
        {
            if (discovery.EnvironmentId != environment || discovery.AuthorityEpoch != LoadCore().AuthorityEpoch) throw new InvalidDataException("Discovery epoch mismatch.");
            Write(DiscoveryPath, ContractCodec.EncodeEnvironmentDiscovery(discovery).ToString(Formatting.None), 4096, false, true);
            return true;
        });
        public void Unpublish(string instance, long epoch) => lease.WhileHeld(() =>
        {
            boundary.ValidateFile(DiscoveryPath, true);
            if (!File.Exists(DiscoveryPath)) return true;
            var record = ContractCodec.DecodeEnvironmentDiscovery(ContractCatalog.ParseAndValidate(SchemaIds.EnvironmentDiscovery, Read(DiscoveryPath, 4096)));
            if (record.EnvironmentId != environment || record.InstanceId != instance || record.AuthorityEpoch != epoch) throw new InvalidDataException("Discovery ownership changed.");
            File.Delete(DiscoveryPath);
            return true;
        });
        private string Read(string path, int budget)
        {
            boundary.ValidateDirectory(); boundary.ValidateFile(path, false);
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (file.Length < 1 || file.Length > budget) throw new InvalidDataException("Private record exceeds budget.");
                var bytes = new byte[checked((int)file.Length)]; int offset = 0;
                while (offset < bytes.Length) { int read = file.Read(bytes, offset, bytes.Length - offset); if (read == 0) throw new EndOfStreamException(); offset += read; }
                if (file.ReadByte() != -1) throw new InvalidDataException("Private record changed length.");
                try { return Utf8.GetString(bytes); } finally { Array.Clear(bytes, 0, bytes.Length); }
            }
        }
        private void Write(string path, string json, int budget, bool createOnly, bool allowMissing = false)
        {
            boundary.ValidateDirectory(); boundary.ValidateFile(path, true);
            byte[] bytes = Utf8.GetBytes(json);
            if (bytes.Length > budget) throw new InvalidDataException("Private record exceeds budget.");
            string temporary = Path.Combine(boundary.DirectoryPath, "pending-" + Guid.NewGuid().ToString("N") + ".tmp");
            bool createdOwned = false;
            try
            {
                using (var file = boundary.CreatePrivateFile(temporary))
                { createdOwned = true; file.Write(bytes, 0, bytes.Length); boundary.FlushToDisk(file); }
                boundary.ValidateFile(temporary, false);
                boundary.ValidateDirectory(); boundary.ValidateFile(path, true);
                if (createOnly) File.Move(temporary, path);
                else if (File.Exists(path)) File.Replace(temporary, path, null, false);
                else if (allowMissing) File.Move(temporary, path);
                else throw new FileNotFoundException("Authorization record disappeared.");
                createdOwned = false;
                boundary.ValidateFile(path, false);
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
                // 失败不假定旧文件还存在，也不把临时内容自动提升为授权。
                if (createdOwned && File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
