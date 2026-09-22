using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Sage50Connector.Helpers
{
    internal static class SageNativeRuntime
    {
        // Keep the reference for the process lifetime: Sage's sessions share this
        // native runtime. Do not unload it while SDK objects may still use it.
        private static IntPtr runtimeHandle;
        private static readonly object Gate = new object();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

        public static bool TryRecover(Exception error)
        {
            var missing = error as DllNotFoundException;
            if (missing == null || missing.Message.IndexOf("w3dbav90.dll", StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            lock (Gate)
            {
                // At most one successful preload per process; do not loop when
                // the subsequent SDK attempt fails for another reason.
                if (runtimeHandle != IntPtr.Zero) return false;
                string x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var roots = new[] { x86, programFiles }.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (string root in roots)
                {
                    foreach (string relative in new[] { @"Actian\Zen\bin", @"Actian\PSQL\bin", @"Pervasive Software\PSQL\bin" })
                    {
                        string path = Path.Combine(root, relative, "w3dbav90.dll");
                        if (!File.Exists(path)) continue;
                        // Absolute vendor-install path only, never current directory
                        // or a download. This also searches its folder for dependencies
                        // without changing PATH or the process default DLL search rules.
                        IntPtr handle = LoadLibraryEx(path, IntPtr.Zero, 0x00000008 /* LOAD_WITH_ALTERED_SEARCH_PATH */);
                        if (handle != IntPtr.Zero)
                        {
                            runtimeHandle = handle;
                            // Actian dynamically loads additional components during
                            // PvStart. Make the same installed directory available to
                            // this process only; never change user/machine PATH.
                            string processPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(path) + ";" + processPath, EnvironmentVariableTarget.Process);
                            SageSdkDiagnostics.Capture("native-runtime-preloaded", companies: null);
                            try { Program.WriteToFile("Sage native runtime recovered from installed path: " + path); } catch { }
                            return true;
                        }
                        int code = Marshal.GetLastWin32Error();
                        SageSdkDiagnostics.Capture("native-runtime-preload-failed", new Win32Exception(code, "Could not load installed runtime at " + path + ": " + new Win32Exception(code).Message));
                    }
                }
                return false;
            }
        }
    }
}
