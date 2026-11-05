using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using SkylinesLink.Launcher;
using SkylinesLink.Setup;

internal static class ProcessProbe
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "task-rules") return TaskRuleProbe.Run(args);
        if (args.Length > 0 && (args[0] == "history-fixture" || args[0] == "history-root" || args[0] == "history-acl")) return HistoryProbe.Run(args);
        if (args.Length == 8 && args[0] == "relay-run")
        {
            var result = OwnedProcess.RunStdio(args[1], new[] { args[2], "--transport", "stdio", "--profile", args[4], "--environment", args[5], "--core-manifest-sha256", args[6] }, args[3]);
            File.WriteAllText(args[7], JsonSerializer.Serialize(new { code = result.Code, cause = result.Cause, workersJoined = result.WorkersJoined, remainingBufferBytes = result.RemainingBufferBytes, childExited = result.ChildExited }), new UTF8Encoding(false));
            return result.Code;
        }
        if (args.Length == 2 && args[0] == "preparation-boundary")
        {
            using var paths = new PreparationFiles();
            paths.HoldDirectory(args[1], false);
            using var manifest = paths.OpenWritable(Path.Combine(args[1], "preparation-manifest.json"), createNew: true, canRename: true);
            manifest.Write(System.Text.Encoding.UTF8.GetBytes("manifest-probe"));
            manifest.Flush(true);
            using var payload = new FileStream(Path.Combine(args[1], "payload.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
            Console.WriteLine("held");
            if (Console.ReadLine() != "publish") return 2;
            paths.PublishFile(manifest, Path.Combine(args[1], "core-manifest.json"));
            Console.WriteLine("published");
            Console.ReadLine();
            return 0;
        }
        if (args.Length == 2 && args[0] == "hold-read")
        {
            using var file = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.Read);
            Console.WriteLine("held");
            Console.ReadLine();
            return 0;
        }
        if (args.Length == 2 && args[0] == "hold-file")
        {
            using var file = new FileStream(args[1], FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Console.WriteLine("held");
            Console.ReadLine();
            return 0;
        }
        if (args.Length == 4 && args[0] == "child" && args[2] == "--stdio-relay")
        {
            var unrelated = new IntPtr(long.Parse(args[1]));
            // 查询原事件的状态，不能仅检查数值句柄是否已被运行时重用。
            var inherited = WaitForSingleObject(unrelated, 0) == 0;
            if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var inJob)) throw new Win32Exception();
            using var input = new NamedPipeClientStream(".", "skylineslink-stdio-" + args[3] + "-in", PipeDirection.InOut, PipeOptions.Asynchronous);
            using var output = new NamedPipeClientStream(".", "skylineslink-stdio-" + args[3] + "-out", PipeDirection.InOut, PipeOptions.Asynchronous);
            Task.WhenAll(input.ConnectAsync(5000), output.ConnectAsync(5000)).GetAwaiter().GetResult();
            var bytes = Encoding.UTF8.GetBytes($"{{\"inJob\":{inJob.ToString().ToLowerInvariant()},\"inheritedUnrelatedHandle\":{inherited.ToString().ToLowerInvariant()}}}\n");
            output.WriteAsync(bytes).AsTask().GetAwaiter().GetResult();
            return inJob && !inherited ? 0 : 1;
        }
        var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
        using var unrelatedEvent = CreateEventW(ref attributes, true, true, null);
        if (unrelatedEvent.IsInvalid) throw new Win32Exception();
        return OwnedProcess.Run(Environment.ProcessPath!, new[] { "child", unrelatedEvent.DangerousGetHandle().ToInt64().ToString() }, AppContext.BaseDirectory);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateEventW(ref SecurityAttributes attributes, bool manual, bool initial, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
}
