using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SkylinesLink.Shared.Processes;

namespace SkylinesLink.Launcher;

internal static class OwnedProcess
{
    // Job 归属与允许继承的标准流在 CreateProcess 内原子确定，进程创建后没有补绑窗口。
    public static int Run(string executable, IReadOnlyList<string> arguments, string workingDirectory) => RunStdio(executable, arguments, workingDirectory).Code;

    internal sealed class RunResult
    {
        public RunResult(int code, string cause, bool workersJoined, int remainingBufferBytes, bool childExited)
        { Code = code; Cause = cause; WorkersJoined = workersJoined; RemainingBufferBytes = remainingBufferBytes; ChildExited = childExited; }
        public int Code { get; }
        public string Cause { get; }
        public bool WorkersJoined { get; }
        public int RemainingBufferBytes { get; }
        public bool ChildExited { get; }
    }

    public static RunResult RunStdio(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        using var externalInput = DuplicateStandardHandle(-10);
        using var externalOutput = DuplicateStandardHandle(-11);
        using var relay = new StdioRelay(externalInput, externalOutput);
        using var input = WindowsOwnedChild.OpenNull();
        using var output = WindowsOwnedChild.OpenNull();
        using var error = WindowsOwnedChild.OpenNull();
        var childArguments = new List<string>(arguments) { "--stdio-relay", relay.Nonce };
        using var child = WindowsOwnedChild.CreateSuspended(executable, childArguments, workingDirectory, input, output, error);
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; child.Terminate(130); };
        var cancelRegistered = false;
        try
        {
            Console.CancelKeyPress += cancel;
            cancelRegistered = true;
            child.Resume();
            try { relay.Connect(child.ProcessId); }
            catch
            {
                child.Terminate(76);
                var joined = relay.Close();
                var exited = child.WaitForExit(StdioRelay.CleanupMilliseconds);
                return new RunResult(joined && exited ? 76 : 75, joined && exited ? "RELAY_STARTUP_FAILED" : "RELAY_CLEANUP_INCOMPLETE", joined, relay.LiveBufferBytes, exited);
            }
            long closingStarted = 0;
            uint? childCode = null;
            string? failure = null;
            while (true)
            {
                var exited = child.WaitForExit(20);
                if (exited && childCode is null)
                {
                    childCode = child.ExitCode;
                    relay.StopInput();
                }
                if (relay.OutputExpired) { failure = "STDIO_OUTPUT_TIMEOUT"; break; }
                if (relay.Failed) { failure = "RELAY_IO_FAILED"; break; }
                if (childCode.HasValue && relay.OutputCompleted) break;
                if ((relay.InputCompleted || relay.OutputCompleted || childCode.HasValue) && closingStarted == 0)
                    closingStarted = Stopwatch.GetTimestamp();
                if (closingStarted != 0 && Stopwatch.GetElapsedTime(closingStarted).TotalMilliseconds >= StdioRelay.CleanupMilliseconds)
                { failure = "RELAY_DRAIN_TIMEOUT"; break; }
                if (childCode.HasValue) System.Threading.Thread.Sleep(10);
            }
            if (failure is not null) child.Terminate(74);
            var released = relay.Close();
            var childExited = childCode.HasValue || child.WaitForExit(StdioRelay.CleanupMilliseconds);
            if (!released || !childExited) return new RunResult(75, "RELAY_CLEANUP_INCOMPLETE", released, relay.LiveBufferBytes, childExited);
            if (failure is not null) return new RunResult(failure == "STDIO_OUTPUT_TIMEOUT" ? 74 : 77, failure, true, relay.LiveBufferBytes, true);
            return new RunResult(unchecked((int)childCode!.Value), "CHILD_EXITED", true, relay.LiveBufferBytes, true);
        }
        catch
        {
            // Resume/observation failures still require an actual wait before releasing ownership.
            child.Terminate(74);
            try { relay.Close(); }
            finally { child.WaitForExit(StdioRelay.CleanupMilliseconds); }
            throw;
        }
        finally { if (cancelRegistered) Console.CancelKeyPress -= cancel; }
    }

    internal static string Quote(string value) => WindowsOwnedChild.Quote(value);

    private static SafeFileHandle DuplicateStandardHandle(int number)
    {
        var source = GetStdHandle(number);
        if (source == IntPtr.Zero || source == new IntPtr(-1)) throw new InvalidOperationException("缺少标准流");
        if (GetFileType(source) != 3) throw new InvalidOperationException("STDIO_REQUIRES_PIPE");
        var current = GetCurrentProcess();
        if (!DuplicateHandle(current, source, current, out var copy, 0, false, 2)) throw new Win32Exception();
        return copy;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int number);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(IntPtr file);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out SafeFileHandle target, uint access, bool inherit, uint options);
}
