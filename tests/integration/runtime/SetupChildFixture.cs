using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace SkylinesLink.RuntimeProbe
{
    /// <summary>Only the explicitly copied, marked Setup IO fixture; never an installation implementation.</summary>
    internal static class SetupChildFixture
    {
        private const string Prefix = "SkylinesLink owned Setup IO fixture\n";
        private const int Refused = 64;

        internal static int Run(string[] args)
        {
            try
            {
                if (args.Length != 3 || args[0] != "initialize-core" || !FixtureEnvironment(args[2])) return Refused;
                string source = args[1];
                if (!Path.IsPathRooted(source) || Path.GetPathRoot(source)!.Length <= 2 ||
                    source.StartsWith(@"\\.\", StringComparison.Ordinal)) return Refused;
                source = Path.GetFullPath(source);
                string expected = Path.GetFullPath(Path.Combine(source, "desktop", "setup", "SkylinesLink.Setup.exe"));
                string actual = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) return Refused;

                string mode;
                using (var marker = new FileStream(Path.Combine(source, "SOURCE.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (marker.Length > 128) return Refused;
                    var bytes = new byte[128];
                    int length = 0;
                    while (length < bytes.Length)
                    {
                        int read = marker.Read(bytes, length, bytes.Length - length);
                        if (read == 0) break;
                        length += read;
                    }
                    if (marker.ReadByte() != -1) return Refused;
                    string text = new UTF8Encoding(false, true).GetString(bytes, 0, length);
                    if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return Refused;
                    mode = text.Substring(Prefix.Length).Trim();
                }

                if (mode == "empty") return 0;
                using (var output = Console.OpenStandardOutput())
                using (var error = Console.OpenStandardError())
                {
                    if (mode == "fail")
                    {
                        byte[] message = Encoding.ASCII.GetBytes("fixture-failure\n");
                        error.Write(message, 0, message.Length); error.Flush();
                        return 37;
                    }
                    if (mode == "wait" || mode == "release")
                    {
                        byte[] ready = Encoding.ASCII.GetBytes("RDY\n");
                        output.Write(ready, 0, ready.Length); output.Flush();
                        error.Write(ready, 0, ready.Length); error.Flush();
                        if (mode == "release")
                            using (var released = EventWaitHandle.OpenExisting(ReleaseEventName(args[2])))
                                return released.WaitOne(5000) ? 0 : Refused;
                        Thread.Sleep(Timeout.Infinite);
                        return Refused;
                    }
                    if (mode != "normal" && mode != "flood") return Refused;
                    if (mode == "normal")
                        using (var input = Console.OpenStandardInput())
                            if (input.ReadByte() != -1) return Refused;
                    var outBlock = new byte[1024];
                    var errorBlock = new byte[1024];
                    for (int i = 0; i < 1024; i++) { outBlock[i] = (byte)'O'; errorBlock[i] = (byte)'E'; }
                    if (mode == "normal")
                    {
                        for (int i = 0; i < 12; i++) output.Write(outBlock, 0, outBlock.Length);
                        for (int i = 0; i < 12; i++) error.Write(errorBlock, 0, errorBlock.Length);
                    }
                    else
                        for (int i = 0; i < 128; i++)
                        {
                            output.Write(outBlock, 0, outBlock.Length);
                            error.Write(errorBlock, 0, errorBlock.Length);
                        }
                    output.Flush(); error.Flush();
                    return 0;
                }
            }
            catch { return Refused; }
        }

        internal static string ReleaseEventName(string environment) => "Local\\SkylinesLink.SetupProbe." + environment;

        private static bool FixtureEnvironment(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= 6 || value.Length > 64 ||
                !value.StartsWith("probe-", StringComparison.Ordinal) || value.EndsWith(".", StringComparison.Ordinal)) return false;
            foreach (char item in value)
                if (!((item >= 'a' && item <= 'z') || (item >= 'A' && item <= 'Z') ||
                    (item >= '0' && item <= '9') || item == '.' || item == '_' || item == '-')) return false;
            return true;
        }
    }
}
