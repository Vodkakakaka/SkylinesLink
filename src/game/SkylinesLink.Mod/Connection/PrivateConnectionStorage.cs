using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using SkylinesLink.Infrastructure.Connection;

namespace SkylinesLink.Mod.Connection
{
    /// <summary>持久资料按用户 SID 保留跨登录权限；管道另使用当前 logon SID。</summary>
    public sealed class PrivateConnectionStorage : IPrivateStorageBoundary
    {
        private readonly SecurityIdentifier user;
        private static readonly SecurityIdentifier SystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        public PrivateConnectionStorage(string path)
        {
            if (!Path.IsPathRooted(path)) throw new ArgumentException("Private directory must be absolute.");
            DirectoryPath = Path.GetFullPath(path);
            user = WindowsTokenIdentity.CurrentUser();
            RejectReparse(DirectoryPath);
            if (!Directory.Exists(DirectoryPath))
            {
                string parent = Path.GetDirectoryName(DirectoryPath)!;
                if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Private directory parent unavailable.");
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false); security.SetOwner(user);
                foreach (var sid in new[] { user, SystemSid }) security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                WindowsPrivateCreation.Directory(DirectoryPath, security);
            }
            ValidateDirectory();
        }
        public string DirectoryPath { get; }
        public FileStream CreatePrivateFile(string path)
        {
            ValidateDirectory(); ValidateFile(path, true);
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false); security.SetOwner(user);
            foreach (var sid in new[] { user, SystemSid }) security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            return WindowsPrivateCreation.File(path, security);
        }
        public void FlushToDisk(FileStream file) => WindowsPrivateCreation.FlushToDisk(file);
        public void ValidateDirectory()
        { RejectReparse(DirectoryPath); ValidateSecurity(new DirectoryInfo(DirectoryPath).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)); }
        public void ValidateFile(string path, bool mayBeMissing)
        {
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), DirectoryPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("Private file outside controlled directory.");
            RejectReparse(path);
            if (!File.Exists(path)) { if (mayBeMissing && !Directory.Exists(path)) return; throw new FileNotFoundException("Private record unavailable."); }
            ValidateSecurity(new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
        }
        private void ValidateSecurity(FileSystemSecurity security)
        {
            if (!user.Equals(security.GetOwner(typeof(SecurityIdentifier)))) throw new UnauthorizedAccessException("Private record owner mismatch.");
            bool userAllowed = false;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Deny) throw new UnauthorizedAccessException("Private record permissions unavailable.");
                if (!user.Equals(rule.IdentityReference) && !SystemSid.Equals(rule.IdentityReference)) throw new UnauthorizedAccessException("Private record has unrelated access.");
                if (user.Equals(rule.IdentityReference) && (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl) userAllowed = true;
            }
            if (!userAllowed) throw new UnauthorizedAccessException("Private record access missing.");
        }
        private static void RejectReparse(string path)
        {
            string? current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Private storage traverses a reparse point.");
                current = Path.GetDirectoryName(current);
            }
        }
    }
}
