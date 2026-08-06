using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Sage50Connector.Helpers
{
    /// <summary>
    /// Assisted (not silent) auto-update:
    /// check manifest → download signed MSI → verify SHA-256 → elevated msiexec → restart.
    /// After a real version bump the customer must re-approve in Sage (MD5 grant).
    /// </summary>
    internal static class UpdateService
    {
        /// <summary>
        /// Public fallback when ApiBaseUrl has no /sage-50/connector-release yet.
        /// Overwrite this object on each customer release (see docs/updates.md).
        /// </summary>
        internal const string DefaultPublicManifestUrl =
            "https://rutterpublicimages.s3.us-east-2.amazonaws.com/sage50-connector/release.json";

        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
        private static readonly object Gate = new object();
        private static DateTime lastBackgroundCheckUtc = DateTime.MinValue;
        private static UpdateCheckResult lastResult;
        private static int applyInProgress;

        internal static UpdateCheckResult LastResult
        {
            get { lock (Gate) { return lastResult; } }
        }

        internal static string ResolveApiBaseUrl(string preferred = null)
        {
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                return preferred.Trim();
            }
            try
            {
                ConnectorConfig loaded = ConnectorConfig.Load();
                if (loaded != null && !string.IsNullOrWhiteSpace(loaded.ApiBaseUrl))
                {
                    return loaded.ApiBaseUrl.Trim();
                }
            }
            catch { /* not set up yet — public manifest still works */ }

            return ConnectorConfig.DefaultApiBaseUrl;
        }

        internal static string ManifestUrlFor(string apiBaseUrl)
        {
            if (!string.IsNullOrWhiteSpace(apiBaseUrl))
            {
                return apiBaseUrl.TrimEnd('/') + "/sage-50/connector-release";
            }
            return DefaultPublicManifestUrl;
        }

        /// <summary>
        /// Background check: at most once per <see cref="CheckInterval"/> unless forced.
        /// </summary>
        internal static async Task<UpdateCheckResult> CheckForUpdatesAsync(
            string apiBaseUrl = null,
            bool force = false)
        {
            lock (Gate)
            {
                if (!force
                    && lastResult != null
                    && DateTime.UtcNow - lastBackgroundCheckUtc < CheckInterval)
                {
                    return lastResult;
                }
            }

            UpdateCheckResult result = await CheckOnceAsync(ResolveApiBaseUrl(apiBaseUrl))
                .ConfigureAwait(false);
            lock (Gate)
            {
                lastBackgroundCheckUtc = DateTime.UtcNow;
                lastResult = result;
            }

            SyncStatus.Instance.SetUpdateAvailability(result);
            return result;
        }

        private static async Task<UpdateCheckResult> CheckOnceAsync(string apiBaseUrl)
        {
            Version local = AppVersion.Current;
            string primary = ManifestUrlFor(apiBaseUrl);
            try
            {
                ConnectorRelease release = await FetchManifestAsync(primary).ConfigureAwait(false);
                if (release == null || release.ParsedVersion == null)
                {
                    // Fall back to public S3 if API has no route yet.
                    if (!string.Equals(primary, DefaultPublicManifestUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        release = await FetchManifestAsync(DefaultPublicManifestUrl).ConfigureAwait(false);
                    }
                }

                if (release == null || release.ParsedVersion == null)
                {
                    return new UpdateCheckResult
                    {
                        Availability = UpdateAvailability.CheckFailed,
                        LocalVersion = local,
                        ErrorMessage = "No release manifest available.",
                    };
                }

                if (string.IsNullOrWhiteSpace(release.MsiUrl))
                {
                    return new UpdateCheckResult
                    {
                        Availability = UpdateAvailability.CheckFailed,
                        LocalVersion = local,
                        Release = release,
                        ErrorMessage = "Release manifest is missing msi_url.",
                    };
                }

                Version remote = release.ParsedVersion;
                Version min = release.ParsedMinVersion;
                int cmp = AppVersion.CompareRelease(remote, local);
                if (cmp <= 0)
                {
                    // Still enforce min_version if somehow running something older than policy.
                    if (min != null && AppVersion.CompareRelease(local, min) < 0)
                    {
                        return new UpdateCheckResult
                        {
                            Availability = UpdateAvailability.RequiredUpdate,
                            LocalVersion = local,
                            Release = release,
                        };
                    }
                    return new UpdateCheckResult
                    {
                        Availability = UpdateAvailability.UpToDate,
                        LocalVersion = local,
                        Release = release,
                    };
                }

                bool forced = min != null && AppVersion.CompareRelease(local, min) < 0;
                return new UpdateCheckResult
                {
                    Availability = forced
                        ? UpdateAvailability.RequiredUpdate
                        : UpdateAvailability.OptionalUpdate,
                    LocalVersion = local,
                    Release = release,
                };
            }
            catch (Exception ex)
            {
                return new UpdateCheckResult
                {
                    Availability = UpdateAvailability.CheckFailed,
                    LocalVersion = local,
                    ErrorMessage = ex.Message,
                };
            }
        }

        private static async Task<ConnectorRelease> FetchManifestAsync(string url)
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "RutterSage50Connector/" + AppVersion.Display);
                HttpResponseMessage response = await client.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<ConnectorRelease>(body);
            }
        }

        /// <summary>
        /// Download MSI, verify hash, start elevated install + restart script, then exit.
        /// </summary>
        internal static async Task ApplyUpdateAsync(
            ConnectorRelease release,
            Action<string> log)
        {
            if (release == null) throw new ArgumentNullException("release");
            if (Interlocked.Exchange(ref applyInProgress, 1) == 1)
            {
                throw new InvalidOperationException("An update is already in progress.");
            }

            try
            {
                if (log != null) log("Downloading connector " + release.Version + "…");
                SyncStatus.Instance.SetUpdateProgress("Downloading update " + release.Version + "…");

                string tempDir = Path.Combine(Path.GetTempPath(), "RutterSage50Update");
                Directory.CreateDirectory(tempDir);
                string msiPath = Path.Combine(
                    tempDir,
                    "RutterSage50ConnectorSetup-" + SanitizeFilePart(release.Version) + ".msi");

                await DownloadFileAsync(release.MsiUrl, msiPath).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(release.Sha256))
                {
                    string actual = ComputeSha256Hex(msiPath);
                    if (!string.Equals(actual, release.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "Downloaded MSI SHA-256 did not match the release manifest.");
                    }
                    if (log != null) log("SHA-256 verified.");
                }
                else if (log != null)
                {
                    log("Manifest has no sha256; skipping hash verify.");
                }

                string installDir = Path.GetDirectoryName(RuntimeEnvironment.ExecutablePath)
                    ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                        RuntimeEnvironment.ProductName);
                string restartExe = Path.Combine(installDir, "Sage50Connector.exe");

                string scriptPath = Path.Combine(tempDir, "apply-update.cmd");
                // Wait for this process to exit so the EXE unlocks, then install and relaunch.
                string script =
                    "@echo off\r\n" +
                    "setlocal\r\n" +
                    "echo Waiting for connector to exit...\r\n" +
                    "timeout /t 3 /nobreak >nul\r\n" +
                    "echo Installing Rutter Sage 50 Connector " + release.Version + "...\r\n" +
                    "msiexec /i \"" + msiPath + "\" /qn /norestart /l*v \"" +
                    Path.Combine(tempDir, "msi-install.log") + "\"\r\n" +
                    "set ERR=%ERRORLEVEL%\r\n" +
                    "if not %ERR%==0 if not %ERR%==3013 (\r\n" +
                    "  echo Install failed with code %ERR%\r\n" +
                    "  exit /b %ERR%\r\n" +
                    ")\r\n" +
                    "echo Starting connector...\r\n" +
                    "start \"\" \"" + restartExe + "\"\r\n" +
                    "endlocal\r\n";

                File.WriteAllText(scriptPath, script, Encoding.ASCII);

                if (log != null)
                {
                    log("Starting elevated installer. The connector will exit; Sage re-approval will be required after upgrade.");
                }
                SyncStatus.Instance.SetUpdateProgress(
                    "Installing update… The connector will restart. You must re-approve this version in Sage 50.");

                var psi = new ProcessStartInfo
                {
                    FileName = scriptPath,
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = tempDir,
                };
                Process.Start(psi);
            }
            finally
            {
                Interlocked.Exchange(ref applyInProgress, 0);
            }
        }

        private static async Task DownloadFileAsync(string url, string destPath)
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromMinutes(10);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "RutterSage50Connector/" + AppVersion.Display);
                using (HttpResponseMessage response = await client.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    using (Stream remote = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (FileStream local = File.Create(destPath))
                    {
                        await remote.CopyToAsync(local).ConfigureAwait(false);
                    }
                }
            }
        }

        private static string ComputeSha256Hex(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }

        private static string SanitizeFilePart(string version)
        {
            if (string.IsNullOrEmpty(version)) return "update";
            char[] bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(version.Length);
            foreach (char c in version)
            {
                sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            }
            return sb.ToString();
        }
    }
}
