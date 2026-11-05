using System;
using System.IO;
using SkylinesLink.Shared.Processes;

namespace SkylinesLink.RuntimeProbe
{
    internal static class OwnedChildProcessTests
    {
        internal static int Run()
        {
            Program.Stage = "shared owned child on the actual Framework runtime";
            int checks = 0;
            string executable = typeof(Program).Assembly.Location;
            using (var input = WindowsOwnedChild.OpenNull())
            using (var output = WindowsOwnedChild.OpenNull())
            using (var error = WindowsOwnedChild.OpenNull())
            {
                using (var child = WindowsOwnedChild.CreateSuspended(executable, new[] { "--owned-child-exit" },
                    Path.GetDirectoryName(executable)!, input, output, error))
                {
                    try
                    {
                        Program.Assert(!child.WaitForExit(0), "Framework child starts suspended under its owned Job"); checks++;
                        child.Resume();
                        Program.Assert(child.WaitForExit(5000) && child.ExitCode == 23,
                            "Framework shared interop resumes and observes the real child exit code"); checks++;
                    }
                    finally { JoinOwned(child); }
                }
                using (var child = WindowsOwnedChild.CreateSuspended(executable, new[] { "--owned-child-exit" },
                    Path.GetDirectoryName(executable)!, input, output, error))
                {
                    try
                    {
                        Program.Assert(!child.WaitForExit(0), "the second Framework child has not run user code"); checks++;
                        Program.Assert(child.Terminate(73), "Job ownership already exists before Resume on Framework"); checks++;
                        Program.Assert(child.WaitForExit(5000) && child.ExitCode == 73,
                            "termination of the suspended Framework child reaches actual process exit"); checks++;
                    }
                    finally { JoinOwned(child); }
                }
            }
            return checks;
        }

        private static void JoinOwned(WindowsOwnedChild child)
        {
            if (child.WaitForExit(0)) return;
            child.Terminate(74);
            if (!child.WaitForExit(5000)) throw new InvalidOperationException("The exact owned fixture child did not exit.");
        }
    }
}
