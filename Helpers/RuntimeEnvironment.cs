using Microsoft.Win32;
using System;
using System.IO;
using System.Reflection;

namespace Sage50Connector.Helpers
{
    /// <summary>
    /// Distinguishes an MSI-installed connector from a source-tree build.
    ///
    /// Both artifacts are compiled with the Release configuration, so build
    /// symbols cannot tell them apart. The installer records its chosen install
    /// directory in HKLM; comparing that directory with the running executable
    /// also works when a customer changes the default install location.
    /// </summary>
    internal static class RuntimeEnvironment
    {
        internal const string ProductName = "Rutter Sage 50 Connector";
        private const string RegistryPath = @"SOFTWARE\Rutter\Sage50Connector";
        private const string InstallPathValue = "InstallPath";

        internal static readonly string ExecutablePath = GetExecutablePath();
        internal static readonly bool IsInstalled = DetectInstalled();

        internal static string ModeName
        {
            get { return IsInstalled ? "Installed" : "Development"; }
        }

        internal static string DisplayName
        {
            get { return IsInstalled ? ProductName : ProductName + " (Development)"; }
        }

        internal static string TrayStatusPrefix
        {
            get { return IsInstalled ? string.Empty : "DEV: "; }
        }

        private static bool DetectInstalled()
        {
            string executableDirectory = NormalizeDirectory(Path.GetDirectoryName(ExecutablePath));
            string registeredDirectory = ReadRegisteredInstallPath();
            if (!string.IsNullOrEmpty(registeredDirectory))
            {
                return string.Equals(
                    executableDirectory,
                    NormalizeDirectory(registeredDirectory),
                    StringComparison.OrdinalIgnoreCase
                );
            }

            // Older installers did not write InstallPath. Keep their default
            // Program Files location recognizable during an upgrade.
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string legacyDefault = Path.Combine(programFilesX86, ProductName);
            return string.Equals(
                executableDirectory,
                NormalizeDirectory(legacyDefault),
                StringComparison.OrdinalIgnoreCase
            );
        }

        private static string ReadRegisteredInstallPath()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(RegistryPath))
                {
                    return key == null ? null : key.GetValue(InstallPathValue) as string;
                }
            }
            catch
            {
                // A missing/unreadable marker must never prevent the connector
                // from starting. The Program Files fallback still handles the
                // standard installed location.
                return null;
            }
        }

        private static string GetExecutablePath()
        {
            try { return Path.GetFullPath(Assembly.GetExecutingAssembly().Location); }
            catch { return Assembly.GetExecutingAssembly().Location; }
        }

        private static string NormalizeDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        }
    }
}
