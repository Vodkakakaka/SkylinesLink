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
        if (args.Length == 3 && args[0] == "inherited-control")
            return RefersToEvent(new IntPtr(long.Parse(args[1])), args[2]) ? 0 : 1;
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
        if (args.Length == 6 && args[0] == "child" && args[4] == "--stdio-relay")
        {
            var unrelated = new IntPtr(long.Parse(args[1]));
            var inherited = RefersToEvent(unrelated, args[2]);
            var inheritedHandlePositiveControl = bool.Parse(args[3]);
            var collisionAttributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
            using var collision = CreateEventW(ref collisionAttributes, true, true, null);
            if (collision.IsInvalid) throw new Win32Exception();
            // A valid, signaled child-local object reproduces the old numeric
            // lookup's false positive without depending on handle-slot reuse.
            var signaledCollisionRejected = WaitForSingleObject(collision, 0) == 0
                && !RefersToEvent(collision.DangerousGetHandle(), args[2]);
            if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var inJob)) throw new Win32Exception();
            using var input = new NamedPipeClientStream(".", "skylineslink-stdio-" + args[5] + "-in", PipeDirection.InOut, PipeOptions.Asynchronous);
            using var output = new NamedPipeClientStream(".", "skylineslink-stdio-" + args[5] + "-out", PipeDirection.InOut, PipeOptions.Asynchronous);
            Task.WhenAll(input.ConnectAsync(5000), output.ConnectAsync(5000)).GetAwaiter().GetResult();
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { inJob, inheritedUnrelatedHandle = inherited,
                inheritedHandlePositiveControl, signaledCollisionRejected }) + "\n");
            output.WriteAsync(bytes).AsTask().GetAwaiter().GetResult();
            return inJob && !inherited && inheritedHandlePositiveControl && signaledCollisionRejected ? 0 : 1;
        }
        var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
        var eventName = "Local\\SkylinesLink.ProcessProbe." + Guid.NewGuid().ToString("N");
        using var unrelatedEvent = CreateEventW(ref attributes, true, true, eventName);
        if (unrelatedEvent.IsInvalid) throw new Win32Exception();
        if (Marshal.GetLastWin32Error() == 183) throw new InvalidOperationException("The probe event already exists.");
        var numericHandle = unrelatedEvent.DangerousGetHandle().ToInt64().ToString();
        var positiveControl = RunInheritedControl(numericHandle, eventName);
        if (!positiveControl) throw new InvalidOperationException("Actual inherited-event positive control failed.");
        return OwnedProcess.Run(Environment.ProcessPath!, new[] { "child", numericHandle, eventName, positiveControl.ToString() }, AppContext.BaseDirectory);
    }

    private static bool RefersToEvent(IntPtr candidate, string eventName)
    {
        // Pin the candidate before OpenEvent can allocate a handle at that same
        // number. Never close or borrow ownership of the original numeric slot.
        var duplicated = DuplicateHandle(GetCurrentProcess(), candidate, GetCurrentProcess(), out var snapshot, 0, false, 2);
        var duplicateError = Marshal.GetLastWin32Error();
        using (snapshot)
        {
            if (!duplicated)
            {
                if (duplicateError == 6) return false; // ERROR_INVALID_HANDLE: no object at this number.
                throw new Win32Exception(duplicateError);
            }
            using var expected = OpenEventW(0x00100000, false, eventName); // SYNCHRONIZE, not inheritable.
            if (expected.IsInvalid) throw new Win32Exception();
            if (CompareObjectHandles(snapshot, expected)) return true;
            var comparisonError = Marshal.GetLastWin32Error();
            if (comparisonError == 1656) return false; // ERROR_NOT_SAME_OBJECT.
            throw new Win32Exception(comparisonError);
        }
    }

    private static bool RunInheritedControl(string numericHandle, string eventName)
    {
        // Deliberately allow inheritance only in this short-lived positive
        // control. The production OwnedProcess inheritance list is untouched.
        var executable = Environment.ProcessPath!;
        var command = new StringBuilder(OwnedProcess.Quote(executable))
            .Append(" inherited-control ").Append(OwnedProcess.Quote(numericHandle)).Append(' ').Append(OwnedProcess.Quote(eventName));
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08000000,
            IntPtr.Zero, AppContext.BaseDirectory, ref startup, out var created)) throw new Win32Exception();
        using var process = new SafeFileHandle(created.Process, true);
        using var thread = new SafeFileHandle(created.Thread, true);
        var exited = false;
        try
        {
            var wait = WaitForSingleObject(process, 5000);
            if (wait == uint.MaxValue) throw new Win32Exception();
            if (wait != 0) throw new TimeoutException("Inherited-event control did not exit.");
            exited = true;
            if (!GetExitCodeProcess(process, out var code)) throw new Win32Exception();
            return code == 0;
        }
        finally
        {
            if (!exited)
            {
                if (!TerminateProcess(process, 1)) throw new Win32Exception();
                if (WaitForSingleObject(process, 5000) != 0) throw new InvalidOperationException("Inherited-event control exit was not observed.");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedBytes;
        public IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateEventW(ref SecurityAttributes attributes, bool manual, bool initial, string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle OpenEventW(uint access, bool inherit, string name);
    [DllImport("kernelbase.dll", SetLastError = true)] private static extern bool CompareObjectHandles(SafeFileHandle first, SafeFileHandle second);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out SafeFileHandle target, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeFileHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
}
