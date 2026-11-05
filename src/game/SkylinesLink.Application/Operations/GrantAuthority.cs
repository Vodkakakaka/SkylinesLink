using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SkylinesLink.Application.Operations
{
    [Flags]
    public enum GrantScope { None = 0, Read = 1, Write = 2, Control = 4 }

    public sealed class GrantSnapshot
    {
        internal GrantSnapshot(string profileId, string profileInstanceId, long authorityEpoch, long epoch, GrantScope scope)
        { ProfileId = profileId; ProfileInstanceId = profileInstanceId; AuthorityEpoch = authorityEpoch; Epoch = epoch; Scope = scope; }
        public string ProfileId { get; }
        public string ProfileInstanceId { get; }
        public long AuthorityEpoch { get; }
        public long Epoch { get; }
        public GrantScope Scope { get; }
    }

    public sealed class AuthorizationProfile
    {
        public AuthorizationProfile(string profileId, long grantEpoch, GrantScope scope, string internalToken, string? externalHttpToken)
            : this(profileId, Guid.NewGuid().ToString("N"), grantEpoch, scope, internalToken, externalHttpToken) { }
        // 只供可信持久值重建；对外profile更新入口不接收或选择此身份。
        public AuthorizationProfile(string profileId, string profileInstanceId, long grantEpoch, GrantScope scope, string internalToken, string? externalHttpToken)
        {
            AuthorizationState.ValidateIdentifier(profileId);
            AuthorizationState.ValidateProfileInstanceId(profileInstanceId);
            AuthorizationState.ValidateCounter(grantEpoch);
            if ((scope & ~(GrantScope.Read | GrantScope.Write | GrantScope.Control)) != 0) throw new ArgumentException("Invalid scope.");
            AuthorizationState.ValidateHex(internalToken);
            if (externalHttpToken != null) { AuthorizationState.ValidateHex(externalHttpToken); if (externalHttpToken == internalToken) throw new ArgumentException("Credentials must be independent."); }
            ProfileId = profileId; ProfileInstanceId = profileInstanceId; GrantEpoch = grantEpoch; Scope = scope; InternalToken = internalToken; ExternalHttpToken = externalHttpToken;
        }
        public string ProfileId { get; }
        public string ProfileInstanceId { get; }
        public long GrantEpoch { get; }
        public GrantScope Scope { get; }
        public string InternalToken { get; }
        public string? ExternalHttpToken { get; }
    }

    public sealed class AuthenticationGrant
    {
        internal AuthenticationGrant(GrantSnapshot subject, string token) { Subject = subject; InternalToken = token; }
        public GrantSnapshot Subject { get; }
        public string InternalToken { get; }
    }

    /// <summary>不可变完整授权值；序列和凭据不会在两个字典之间分步发布。</summary>
    public sealed class AuthorizationState
    {
        public const long MaximumCounter = 9007199254740991;
        public AuthorizationState(string environmentId, long recordRevision, long authorityEpoch, IEnumerable<AuthorizationProfile> profiles)
        {
            if (NormalizeEnvironment(environmentId) != environmentId) throw new ArgumentException("Environment must be canonical.");
            ValidateCounter(recordRevision);
            if (authorityEpoch < 0 || authorityEpoch > recordRevision) throw new ArgumentException("Invalid authority epoch.");
            var entries = profiles.ToArray();
            if ((authorityEpoch == 0 && entries.Length != 0) || entries.Length > 64 || entries.Select(p => p.ProfileId).Distinct(StringComparer.Ordinal).Count() != entries.Length ||
                entries.Select(p => p.ProfileInstanceId).Distinct(StringComparer.Ordinal).Count() != entries.Length || entries.Any(p => p.GrantEpoch > recordRevision))
                throw new ArgumentException("Invalid authorization profiles.");
            EnvironmentId = environmentId; RecordRevision = recordRevision; AuthorityEpoch = authorityEpoch;
            Profiles = Array.AsReadOnly(entries);
        }
        public string EnvironmentId { get; }
        public long RecordRevision { get; }
        public long AuthorityEpoch { get; }
        public IReadOnlyList<AuthorizationProfile> Profiles { get; }
        public AuthorizationProfile? Find(string profile) => Profiles.FirstOrDefault(p => p.ProfileId == profile);
        public AuthorizationState Activate() => new AuthorizationState(EnvironmentId, Next(RecordRevision), Next(AuthorityEpoch), Profiles);
        public static long Next(long value) { if (value < 0 || value >= MaximumCounter) throw new InvalidOperationException("Authorization epoch exhausted."); return value + 1; }
        public static void ValidateCounter(long value) { if (value < 1 || value > MaximumCounter) throw new ArgumentException("Invalid authorization epoch."); }
        public static void ValidateHex(string value)
        {
            if (value == null || value.Length != 64 || value.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))) throw new ArgumentException("Invalid credential encoding.");
        }
        public static void ValidateProfileInstanceId(string value)
        {
            if (value == null || value.Length != 32 || value.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
                throw new ArgumentException("Invalid profile instance identity.");
        }
        public static void ValidateIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64 || value == "." || value == ".." || value.EndsWith(".", StringComparison.Ordinal) ||
                value.Any(c => !((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-')))
                throw new ArgumentException("Invalid identifier.");
        }
        public static string NormalizeEnvironment(string value)
        {
            ValidateIdentifier(value);
            string stem = value.Split('.')[0].ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '1' && stem[3] <= '9'))
                throw new ArgumentException("Reserved identifier.");
            return value.ToLowerInvariant();
        }
    }

    public interface IAuthorizationStore
    {
        AuthorizationState Load();
        void Save(long expectedRevision, AuthorizationState replacement);
    }

    /// <summary>唯一可信写者。磁盘 IO 在后台执行；连接与提交只读短锁内的当前快照。</summary>
    public sealed class GrantAuthority
    {
        private readonly object sync = new object();
        private readonly IAuthorizationStore store;
        private AuthorizationState current;
        private string? blockedProfile;
        private bool updating, stopping, persistenceFailed;
        private Task pending = Task.CompletedTask;

        public GrantAuthority(AuthorizationState activated, IAuthorizationStore store)
        {
            if (activated.AuthorityEpoch < 1) throw new ArgumentException("Authority must be durably activated.");
            current = activated; this.store = store ?? throw new ArgumentNullException(nameof(store));
        }
        public AuthorizationState State { get { lock (sync) return current; } }
        public GrantSnapshot? Read(string profileId)
        {
            lock (sync)
            {
                var profile = Available(profileId);
                return profile == null ? null : new GrantSnapshot(profileId, profile.ProfileInstanceId, current.AuthorityEpoch, profile.GrantEpoch, profile.Scope);
            }
        }
        public AuthenticationGrant? CaptureAuthentication(string profileId, long authorityEpoch, long grantEpoch)
        {
            lock (sync)
            {
                var profile = Available(profileId);
                return current.AuthorityEpoch == authorityEpoch && profile?.GrantEpoch == grantEpoch
                    ? new AuthenticationGrant(new GrantSnapshot(profileId, profile.ProfileInstanceId, authorityEpoch, grantEpoch, profile.Scope), profile.InternalToken) : null;
            }
        }
        public bool Allows(GrantSnapshot subject, GrantScope required)
        { lock (sync) return AllowsLocked(subject, required); }
        // 仅冻结已存在的普通值；回调不可执行 IO 或等待，捕获和当前 Read 判定共享线性点。
        public bool TryRead<T>(GrantSnapshot subject, Func<T> capture, out T value)
        {
            if (capture == null) throw new ArgumentNullException(nameof(capture));
            lock (sync)
            {
                if (!AllowsLocked(subject, GrantScope.Read)) { value = default!; return false; }
                value = capture(); return true;
            }
        }
        // 校验与同步原生提交的进入点有同一线性化边界；回调不可 await 或做持久化 IO。
        public bool TryCommit(GrantSnapshot subject, GrantScope required, Action synchronousCommit)
        {
            if (synchronousCommit == null) throw new ArgumentNullException(nameof(synchronousCommit));
            lock (sync)
            {
                if (!AllowsLocked(subject, required)) return false;
                synchronousCommit();
                return true;
            }
        }
        public Task UpdateAsync(string profileId, GrantScope scope, string internalToken, string? externalHttpToken) => ChangeAsync(profileId, scope, internalToken, externalHttpToken, false);
        public Task RemoveAsync(string profileId) => ChangeAsync(profileId, GrantScope.None, "", null, true);
        // Only a trusted, explicit connection-configuration action calls this.
        // The credential factory is short synchronous CSPRNG work, never client IO.
        // Matching configuration preserves the actual tokens, identity and epoch.
        public Task EnsureProfileAsync(string profileId, GrantScope scope, bool? enableHttp, Func<string> createCredential)
        {
            if (createCredential == null) throw new ArgumentNullException(nameof(createCredential));
            lock (sync)
            {
                RequireWriter();
                AuthorizationState.ValidateIdentifier(profileId);
                if (scope == GrantScope.None || (scope & ~(GrantScope.Read | GrantScope.Write | GrantScope.Control)) != 0)
                    throw new ArgumentException("A nonempty connection scope is required.", nameof(scope));
                var existing = current.Find(profileId);
                // Null means the caller did not edit transport settings. Select
                // the current value here, never from a stale caller-side snapshot.
                bool httpEnabled = enableHttp ?? (existing?.ExternalHttpToken != null);
                if (existing != null && existing.Scope == scope && (existing.ExternalHttpToken != null) == httpEnabled)
                    return Task.CompletedTask;
                string token = existing?.InternalToken ?? createCredential();
                string? http = httpEnabled ? existing?.ExternalHttpToken ?? createCredential() : null;
                return ChangeLocked(profileId, scope, token, http, false);
            }
        }
        private Task ChangeAsync(string profileId, GrantScope scope, string internalToken, string? externalHttpToken, bool remove)
        {
            lock (sync) return ChangeLocked(profileId, scope, internalToken, externalHttpToken, remove);
        }
        private void RequireWriter()
        {
            if (stopping || updating || persistenceFailed) throw new InvalidOperationException("Authorization writer unavailable.");
        }
        private Task ChangeLocked(string profileId, GrantScope scope, string internalToken, string? externalHttpToken, bool remove)
        {
            RequireWriter();
            long revision = AuthorizationState.Next(current.RecordRevision);
            AuthorizationState.ValidateIdentifier(profileId);
            if (remove && current.Find(profileId) == null) throw new InvalidOperationException("Profile unavailable.");
            var remaining = current.Profiles.Where(p => p.ProfileId != profileId);
            if (!remove)
            {
                string identity = current.Find(profileId)?.ProfileInstanceId ?? Guid.NewGuid().ToString("N");
                remaining = remaining.Concat(new[] { new AuthorizationProfile(profileId, identity, revision, scope, internalToken, externalHttpToken) });
            }
            var replacement = new AuthorizationState(current.EnvironmentId, revision, current.AuthorityEpoch, remaining);
            long expected = current.RecordRevision;
            blockedProfile = profileId; updating = true;
            pending = Task.Run(() =>
            {
                try
                {
                    store.Save(expected, replacement);
                    lock (sync) { current = replacement; blockedProfile = null; }
                }
                catch { lock (sync) persistenceFailed = true; throw; }
                finally { lock (sync) updating = false; }
            });
            return pending;
        }
        public Task StopAsync() { lock (sync) { stopping = true; return pending; } }
        private AuthorizationProfile? Available(string profileId)
        {
            if (stopping || blockedProfile == profileId) return null;
            var profile = current.Find(profileId);
            return profile?.Scope == GrantScope.None ? null : profile;
        }
        private bool AllowsLocked(GrantSnapshot subject, GrantScope required)
        {
            if (subject == null || required == GrantScope.None || (required & ~(GrantScope.Read | GrantScope.Write | GrantScope.Control)) != 0) return false;
            var profile = Available(subject.ProfileId);
            return subject.AuthorityEpoch == current.AuthorityEpoch && profile != null && profile.ProfileInstanceId == subject.ProfileInstanceId && profile.GrantEpoch == subject.Epoch &&
                (subject.Scope & required) == required && (profile.Scope & required) == required;
        }
    }
}
