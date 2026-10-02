using System;
using System.Threading.Tasks;

namespace Sage50Connector.Helpers
{
    internal sealed class CompanySwitchResult
    {
        public bool Succeeded { get; private set; }
        public bool Unchanged { get; private set; }
        public string Message { get; private set; }
        public string CompanyName { get; private set; }
        public string ApiBaseUrl { get; private set; }

        public static CompanySwitchResult AlreadyActive(StoredConnection active)
        {
            return new CompanySwitchResult
            {
                Succeeded = true,
                Unchanged = true,
                CompanyName = active == null ? null : active.CompanyName,
                ApiBaseUrl = active == null ? null : active.ApiBaseUrl,
            };
        }

        public static CompanySwitchResult Switched(StoredConnection active)
        {
            return new CompanySwitchResult
            {
                Succeeded = true,
                CompanyName = active.CompanyName,
                ApiBaseUrl = active.ApiBaseUrl,
                Message = "Now syncing " + active.CompanyName + ".",
            };
        }

        public static CompanySwitchResult Failed(string message)
        {
            return new CompanySwitchResult
            {
                Succeeded = false,
                Message = message,
            };
        }
    }

    /// <summary>
    /// Makes a previously set-up company the one sage50Config.json points at.
    /// One company syncs at a time. Any failure leaves the previous company active.
    /// </summary>
    internal static class CompanySwitcher
    {
        /// <summary>
        /// How long to wait for the current job to finish posting. Past this the
        /// switch is abandoned; the worker still stops at its next job boundary
        /// and restarts on the company that was already active.
        /// </summary>
        internal static readonly TimeSpan PauseTimeout = TimeSpan.FromMinutes(3);

        public static async Task<CompanySwitchResult> SwitchToAsync(string connectionId)
        {
            StoredConnection target = ConnectionRegistry.Find(connectionId);
            if (target == null)
            {
                return CompanySwitchResult.Failed(
                    "That company is no longer in the list on this computer.");
            }

            ConnectorConfig previous;
            try
            {
                previous = ConnectorConfig.Load();
            }
            catch (Exception ex)
            {
                return CompanySwitchResult.Failed(
                    "The current company configuration could not be read: " + ex.Message);
            }

            if (string.Equals(previous.ConnectionId, target.ConnectionId, StringComparison.OrdinalIgnoreCase))
                return CompanySwitchResult.AlreadyActive(target);

            RutterProbeResult probe = await ConnectionProbe.ProbeAsync(
                target.ApiBaseUrl,
                target.ConnectionId,
                target.AccessKey).ConfigureAwait(false);
            try
            {
                ConnectionRegistry.RecordProbe(target.ConnectionId, probe.Status);
            }
            catch (Exception ex)
            {
                global::Sage50Connector.Program.WriteToFile(
                    "Could not save the connection probe result: " + ex.Message);
            }
            global::Sage50Connector.Program.WriteToFile(
                "Rutter connection probe for '"
                    + target.CompanyName
                    + "' ConnectionId='"
                    + target.ConnectionId
                    + "' AccessKeyLength="
                    + (target.AccessKey == null ? 0 : target.AccessKey.Length)
                    + " result="
                    + probe.Status
                    + " http="
                    + (probe.StatusCode.HasValue ? probe.StatusCode.Value.ToString() : "none"));
            if (!probe.IsAlive)
                return CompanySwitchResult.Failed(probe.UserMessage);

            bool paused = false;
            bool resumed = false;
            try
            {
                paused = await Task.Run(
                    () => global::Sage50Connector.Program.PauseWorkerAtJobBoundary(PauseTimeout))
                    .ConfigureAwait(false);
                if (!paused)
                {
                    // The cancel is already in flight. Release the hold so the
                    // worker restarts on the company that is still configured.
                    ResumeWorker();
                    resumed = true;
                    return CompanySwitchResult.Failed(
                        "Sync is still finishing the current job. Nothing was changed — try again in a moment.");
                }

                // The worker's own finally already released the session and spent
                // the once-flag. Calling Release again is a no-op until the next
                // RunHeadless re-arms it. The Sage file check opens a session of
                // its own and must close that session itself.
                global::Sage50Connector.Program.ReleaseSageSession();
                SageCompanyLookupResult presence = SageCompanyLocator.Lookup(
                    target.DatabaseName,
                    target.CompanyGuid,
                    target.CompanyName);
                if (!presence.Present)
                {
                    ResumeWorker();
                    resumed = true;
                    return CompanySwitchResult.Failed(presence.Message);
                }

                bool replacedConfig = false;
                try
                {
                    ConnectorConfig.Save(
                        target.CompanyName,
                        target.AccessKey,
                        target.ConnectionId,
                        target.ApiBaseUrl,
                        target.CompanyGuid,
                        target.DatabaseName);
                    replacedConfig = true;
                    ConnectionRegistry.MarkActivated(target.ConnectionId);
                    // Static identity, the page cache, and the tray totals are all
                    // cleared before the worker is allowed to poll again. RunHeadless
                    // reloads sage50Config.json into the statics; nothing may post
                    // with the previous connection still in memory.
                    JobFetchCache.Clear();
                    global::Sage50Connector.Program.ClearCachedCompanyState();
                    SyncStatus.Instance.ResetForCompany(target.CompanyName);
                    global::Sage50Connector.Program.WriteToFile(
                        "Switched active company from '"
                            + previous.CompanyName
                            + "' ("
                            + previous.ConnectionId
                            + ") to '"
                            + target.CompanyName
                            + "' ("
                            + target.ConnectionId
                            + ")");
                    ResumeWorker();
                    resumed = true;
                    return CompanySwitchResult.Switched(target);
                }
                catch (Exception ex)
                {
                    global::Sage50Connector.Program.WriteToFile(
                        "Company switch failed: " + ex.GetType().Name + ": " + ex.Message);
                    if (replacedConfig)
                    {
                        try
                        {
                            ConnectorConfig.Save(
                                previous.CompanyName,
                                previous.AccessKey,
                                previous.ConnectionId,
                                previous.ApiBaseUrl,
                                previous.CompanyGuid,
                                previous.DatabaseName);
                            ConnectionRegistry.MarkActivated(previous.ConnectionId);
                        }
                        catch (Exception restoreEx)
                        {
                            global::Sage50Connector.Program.WriteToFile(
                                "Failed to restore the previous company configuration: " + restoreEx.Message);
                            global::Sage50Connector.Program.ClearCachedCompanyState();
                            try { Sage50Connector.Instance.Shutdown(); } catch { }
                            ResumeWorker();
                            resumed = true;
                            return CompanySwitchResult.Failed(
                                "The switch failed, and the previous company could not be restored: " + restoreEx.Message);
                        }
                    }

                    global::Sage50Connector.Program.ClearCachedCompanyState();
                    try { Sage50Connector.Instance.Shutdown(); } catch { }
                    ResumeWorker();
                    resumed = true;
                    return CompanySwitchResult.Failed(
                        "Couldn't switch company. " + ex.Message + " Still syncing " + previous.CompanyName + ".");
                }
            }
            finally
            {
                // PauseWorkerAtJobBoundary sets the hold before it waits. If it
                // throws, or a later step returns without resuming, the worker
                // would stay paused forever.
                if (!resumed)
                {
                    if (paused)
                    {
                        global::Sage50Connector.Program.ClearCachedCompanyState();
                        try { Sage50Connector.Instance.Shutdown(); } catch { }
                    }
                    ResumeWorker();
                }
            }
        }

        private static void ResumeWorker()
        {
            global::Sage50Connector.Program.ResumeWorkerAfterSwitch();
        }
    }
}
