using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using SkylinesLink.Mod.Connection;

namespace SkylinesLink.RuntimeProbe
{
    internal static class WindowsBoundaryTests
    {
        internal static int Run()
        {
            Program.Stage = "atomic Windows private creation and actual handle lifetime";
            int checks = 0;
            void Check(bool value, string name) { Program.Assert(value, name); checks++; }
            var actualUser = WindowsTokenIdentity.CurrentUser();
            using (var identity = WindowsIdentity.GetCurrent()) Check(actualUser.Equals(identity.User), "native token user matches independent Framework identity");
            var actualLogon = WindowsTokenIdentity.CurrentLogon();
            Check(actualLogon.IsWellKnown(WellKnownSidType.LogonIdsSid), "actual token group is a logon SID");
            for (int i = 0; i < 16; i++) { WindowsTokenIdentity.CurrentUser(); WindowsTokenIdentity.CurrentLogon(); }
            using (var process = Process.GetCurrentProcess())
            {
                process.Refresh(); _ = process.HandleCount;
                process.Refresh(); int before = process.HandleCount;
                bool sameIdentity = true;
                for (int i = 0; i < 256; i++)
                    sameIdentity &= actualUser.Equals(WindowsTokenIdentity.CurrentUser()) && actualLogon.Equals(WindowsTokenIdentity.CurrentLogon());
                process.Refresh(); int after = process.HandleCount;
                Check(sameIdentity, "repeated native token queries preserve the current user and logon identities");
                Check(after <= before + 2, "prewarmed native token queries release their process handles without waiting for GC");
            }
            string root = Path.Combine(Path.GetTempPath(), "SkylinesLink-WindowsBoundary-" + Guid.NewGuid().ToString("N"));
            string connection = Path.Combine(root, "connection"), path = Path.Combine(connection, "owned.bin");
            Directory.CreateDirectory(root);
            try
            {
                var storage = new PrivateConnectionStorage(connection);
                void CheckSecurity(FileSystemSecurity security, string kind)
                {
                    var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                    var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
                    Check(security.AreAccessRulesProtected && actualUser.Equals(security.GetOwner(typeof(SecurityIdentifier))), kind + " is protected and user-owned");
                    Check(rules.Length == 2 && rules.All(rule => rule.AccessControlType == AccessControlType.Allow &&
                        (actualUser.Equals(rule.IdentityReference) || system.Equals(rule.IdentityReference)) &&
                        (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl), kind + " only grants user and SYSTEM");
                }
                CheckSecurity(new DirectoryInfo(connection).GetAccessControl(), "created directory");
                var stream = storage.CreatePrivateFile(path);
                var handle = stream.SafeFileHandle;
                try
                {
                    CheckSecurity(stream.GetAccessControl(), "created file");
                    stream.WriteByte(73); storage.FlushToDisk(stream);
                    Program.Expect<IOException>(() => { using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { } }); checks++;
                }
                finally { stream.Dispose(); }
                Check(handle.IsClosed, "disposing private FileStream closes its original OS handle");
                stream.Dispose(); Check(handle.IsClosed, "repeated dispose preserves closed handle");
                Program.Expect<IOException>(() => { using (storage.CreatePrivateFile(path)) { } }); checks++;
                Check(File.ReadAllBytes(path).SequenceEqual(new byte[] { 73 }), "CreateNew collision preserves original bytes");
                using (var reopened = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Check(reopened.ReadByte() == 73, "same file reopens exclusively after real release");
                using (var readOnly = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    IOException? failure = null;
                    try { storage.FlushToDisk(readOnly); }
                    catch (IOException error) { failure = error; }
                    Check(failure != null && (failure.HResult & 0xffff) == 5,
                        "checked FlushFileBuffers reports the actual access-denied failure of a read-only OS handle");
                    Check(readOnly.CanRead && !readOnly.SafeFileHandle.IsClosed && readOnly.ReadByte() == 73,
                        "failed disk flush neither closes the caller handle nor changes the original bytes");
                }
                Check(File.ReadAllBytes(path).SequenceEqual(new byte[] { 73 }), "failed read-only disk flush preserves the file after handle release");
                storage.ValidateDirectory(); storage.ValidateFile(path, false); checks++;
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (Directory.Exists(connection)) Directory.Delete(connection);
                Directory.Delete(root);
            }
            return checks;
        }

        internal static int CheckActualPipeHandles(BoundedPipeServer server)
        {
            Program.Stage = "actual listening pipe handle security";
            var slots = (NamedPipeServerStream?[]?)typeof(BoundedPipeServer)
                .GetField("streams", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(server)
                ?? throw new InvalidOperationException("Actual listening pipe handles unavailable.");
            Program.Assert(slots.Length > 0 && slots.All(stream => stream != null), "actual production listener created every bounded pipe slot");
            int checks = 1;
            var logon = WindowsTokenIdentity.CurrentLogon();
            foreach (var stream in slots)
            {
                // Read the kernel object's descriptor through its real production handle.
                // Inspecting LogonPipeSecurity.Create() alone misses Mono overloads that drop it.
                var security = stream!.GetAccessControl();
                var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
                Program.Assert(security.AreAccessRulesProtected && rules.Length == 1 &&
                    rules[0].AccessControlType == AccessControlType.Allow && !rules[0].IsInherited &&
                    logon.Equals(rules[0].IdentityReference) &&
                    (rules[0].PipeAccessRights & PipeAccessRights.FullControl) == PipeAccessRights.FullControl,
                    "actual pipe handle grants protected full control to only this interactive logon");
                checks++;
            }
            return checks;
        }
    }
}
