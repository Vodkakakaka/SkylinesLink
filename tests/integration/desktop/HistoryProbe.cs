using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkylinesLink.Deployment;

internal static class HistoryProbe
{
    internal static int Run(string[] args)
    {
        if (args.Length < 2 || !Regex.IsMatch(args[1], "^probe-[a-f0-9]{24}$")) return 2;
        string root = CorePackage.EnvironmentRoot(args[1]);
        string connection = Path.Combine(root, "connection");
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false); security.SetOwner(user);
        foreach (var sid in new[] { user, system }) security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if ((args[0] == "history-fixture" || args[0] == "history-root") && args.Length == 2)
        {
            if (Directory.Exists(root) || !Directory.Exists(Path.GetDirectoryName(root))) throw new InvalidOperationException();
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, ".owned-probe"), args[1], new UTF8Encoding(false));
            if (args[0] == "history-root")
            {
                Console.WriteLine(JsonSerializer.Serialize(new { environment = args[1], directory = root }));
                return 0;
            }
            new DirectoryInfo(connection).Create(security);
            using (new FileStream(Path.Combine(connection, "authority.lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.WriteAllText(Path.Combine(connection, "authorization.json"), JsonSerializer.Serialize(new {
                formatVersion = 2, productId = "skylineslink", environmentId = args[1], recordRevision = 8, authorityEpoch = 3,
                profiles = new[] { new { profileId = "Reader.With_Id", profileInstanceId = Guid.NewGuid().ToString("N"), grantEpoch = 7, scopeMask = 1,
                    internalToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), externalHttpToken = (string?)null } }
            }), new UTF8Encoding(false));
            using (new FileStream(Path.Combine(connection, "operations.history"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            // Elevated creators otherwise inherit Administrators as owner; mirror the production private-file boundary.
            foreach (string name in new[] { "authority.lock", "authorization.json", "operations.history" })
            {
                var fileSecurity = new FileSecurity();
                fileSecurity.SetAccessRuleProtection(true, false); fileSecurity.SetOwner(user);
                foreach (var sid in new[] { user, system }) fileSecurity.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(Path.Combine(connection, name)).SetAccessControl(fileSecurity);
            }
            Console.WriteLine(JsonSerializer.Serialize(new { environment = args[1], directory = root }));
            return 0;
        }
        if (args[0] == "history-acl" && args.Length == 4 && File.ReadAllText(Path.Combine(root, ".owned-probe")) == args[1] &&
            (args[2] == "connection" || args[2] == "authorization.json") && (args[3] == "private" || args[3] == "broad"))
        {
            if (args[3] == "broad") security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            if (args[2] == "connection") new DirectoryInfo(connection).SetAccessControl(security);
            else
            {
                var fileSecurity = new FileSecurity();
                fileSecurity.SetAccessRuleProtection(true, false); fileSecurity.SetOwner(user);
                foreach (var sid in new[] { user, system }) fileSecurity.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
                if (args[3] == "broad") fileSecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
                new FileInfo(Path.Combine(connection, args[2])).SetAccessControl(fileSecurity);
            }
            return 0;
        }
        return 2;
    }
}
