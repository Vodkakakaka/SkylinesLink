using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SkylinesLink.Shared.Processes
{
    // Owns only the Job and process/thread handles. Standard streams remain borrowed.
    // Callers must observe exit and finish their IO before releasing their own resources.
    internal sealed class WindowsOwnedChild : IDisposable
    {
        private const uint CreateSuspendedFlag = 0x00000004;
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateNoWindow = 0x08000000;
        private const uint KillOnJobClose = 0x00002000;
        private const uint WaitTimeout = 258;
        private readonly KernelHandle job = new KernelHandle();
        private readonly KernelHandle process = new KernelHandle();
        private readonly KernelHandle thread = new KernelHandle();

        private WindowsOwnedChild() { }

        internal uint ProcessId { get; private set; }

        internal static WindowsOwnedChild CreateSuspended(string executable, IReadOnlyList<string> arguments,
            string workingDirectory, SafeFileHandle input, SafeFileHandle output, SafeFileHandle error)
        {
            if (string.IsNullOrWhiteSpace(executable)) throw new ArgumentException("Missing executable.", nameof(executable));
            if (arguments == null) throw new ArgumentNullException(nameof(arguments));
            if (workingDirectory == null) throw new ArgumentNullException(nameof(workingDirectory));
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (error == null) throw new ArgumentNullException(nameof(error));

            var command = new StringBuilder(Quote(executable));
            foreach (var argument in arguments) command.Append(' ').Append(Quote(argument));
            // Allocate the owner and all three handle wrappers before creating a child.
            var child = new WindowsOwnedChild();
            var inputReference = false;
            var outputReference = false;
            var errorReference = false;
            try
            {
                var jobHandle = CreateJobObjectW(IntPtr.Zero, null);
                if (jobHandle == IntPtr.Zero) throw new Win32Exception();
                child.job.Initialize(jobHandle);
                var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
                if (!SetInformationJobObject(child.job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) throw new Win32Exception();

                input.DangerousAddRef(ref inputReference);
                output.DangerousAddRef(ref outputReference);
                error.DangerousAddRef(ref errorReference);
                if (input.IsInvalid || output.IsInvalid || error.IsInvalid) throw new ArgumentException("Invalid standard stream handle.");
                using (var attributes = new CreationAttributes(child.job.DangerousGetHandle(),
                    input.DangerousGetHandle(), output.DangerousGetHandle(), error.DangerousGetHandle()))
                {
                    var startup = new StartupInfoEx
                    {
                        Info = new StartupInfo
                        {
                            Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                            StandardInput = input.DangerousGetHandle(), StandardOutput = output.DangerousGetHandle(),
                            StandardError = error.DangerousGetHandle()
                        },
                        Attributes = attributes.Pointer
                    };
                    // Both Job membership and the exact inheritance list apply inside CreateProcess.
                    if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true,
                        CreateSuspendedFlag | ExtendedStartupInfoPresent | CreateNoWindow,
                        IntPtr.Zero, workingDirectory, ref startup, out var created)) throw new Win32Exception();
                    child.process.Initialize(created.Process);
                    child.thread.Initialize(created.Thread);
                    child.ProcessId = created.ProcessId;
                }
                return child;
            }
            catch { child.Dispose(); throw; }
            finally
            {
                if (errorReference) error.DangerousRelease();
                if (outputReference) output.DangerousRelease();
                if (inputReference) input.DangerousRelease();
            }
        }

        // A failed resume leaves the suspended child owned, so the caller can terminate and wait.
        internal void Resume()
        {
            if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception();
        }

        internal bool WaitForExit(int milliseconds)
        {
            if (milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
            var result = WaitForSingleObject(process, (uint)milliseconds);
            if (result == 0) return true;
            if (result == WaitTimeout) return false;
            throw new Win32Exception();
        }

        internal uint ExitCode
        {
            get
            {
                if (!WaitForExit(0)) throw new InvalidOperationException("Child has not exited.");
                if (!GetExitCodeProcess(process, out var code)) throw new Win32Exception();
                return code;
            }
        }

        internal bool Terminate(uint exitCode)
        {
            try { return TerminateJobObject(job, exitCode); }
            catch (ObjectDisposedException) { return false; }
        }

        // Closing the Job requests termination; disposing is not evidence of actual exit.
        public void Dispose()
        {
            job.Dispose();
            thread.Dispose();
            process.Dispose();
        }

        internal static SafeFileHandle OpenNull()
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
            var handle = CreateFileW("NUL", 0xC0000000, 3, ref security, 3, 0, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            var code = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(code);
        }

        internal static string Quote(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') result.Append('\\', slashes * 2 + 1).Append('"');
                else result.Append('\\', slashes).Append(character);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        private sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            internal KernelHandle() : base(true) { }
            internal void Initialize(IntPtr value) { SetHandle(value); }
            protected override bool ReleaseHandle() { return CloseHandle(handle); }
        }

        private sealed class CreationAttributes : IDisposable
        {
            internal IntPtr Pointer { get; private set; }
            private IntPtr jobs, handles;
            private bool initialized;

            internal CreationAttributes(IntPtr job, IntPtr input, IntPtr output, IntPtr error)
            {
                try
                {
                    var size = UIntPtr.Zero;
                    InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
                    if (size == UIntPtr.Zero) throw new Win32Exception();
                    Pointer = Marshal.AllocHGlobal(new IntPtr(checked((long)size.ToUInt64())));
                    if (!InitializeProcThreadAttributeList(Pointer, 2, 0, ref size)) throw new Win32Exception();
                    initialized = true;
                    jobs = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(jobs, job);
                    handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
                    Marshal.WriteIntPtr(handles, 0, input);
                    Marshal.WriteIntPtr(handles, IntPtr.Size, output);
                    Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, error);
                    if (!UpdateProcThreadAttribute(Pointer, 0, new UIntPtr(0x2000D), jobs, new UIntPtr((uint)IntPtr.Size), IntPtr.Zero, IntPtr.Zero)
                        || !UpdateProcThreadAttribute(Pointer, 0, new UIntPtr(0x20002), handles, new UIntPtr((uint)(3 * IntPtr.Size)), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
                }
                catch { Dispose(); throw; }
            }

            public void Dispose()
            {
                if (initialized) { DeleteProcThreadAttributeList(Pointer); initialized = false; }
                Marshal.FreeHGlobal(Pointer); Pointer = IntPtr.Zero;
                Marshal.FreeHGlobal(jobs); jobs = IntPtr.Zero;
                Marshal.FreeHGlobal(handles); handles = IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string? Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public ushort ShowWindow, ReservedBytes;
            public IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(KernelHandle job, int informationClass, ref ExtendedLimits information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(KernelHandle job, uint exitCode);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, uint count, uint flags, ref UIntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, UIntPtr attribute, IntPtr value, UIntPtr size, IntPtr previous, IntPtr returnedSize);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(KernelHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(KernelHandle handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(KernelHandle process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, ref SecurityAttributes security, uint creation, uint flags, IntPtr template);
    }
}
