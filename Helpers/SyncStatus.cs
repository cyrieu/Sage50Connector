using System;
using System.Collections.Generic;
using System.Linq;

namespace Sage50Connector.Helpers
{
    public enum ConnectorState
    {
        Starting,
        Idle,
        Syncing,
        NeedsAuthorization,
        Offline,
        Error,
    }

    public enum SageAuthorizationState
    {
        Unknown,
        Checking,
        Granted,
        Required,
    }

    public class EntityStat
    {
        public string Entity { get; set; }
        public int RecordCount { get; set; }
        public DateTime? LastSyncedAt { get; set; }
    }

    /// <summary>
    /// What the connector is doing right now, in terms a person can read.
    ///
    /// The sync loop already knows all of this — it logs "Fetched 50 of 156
    /// ACCOUNTS(s)" to a file nobody opens. This is the same information kept
    /// somewhere the tray UI can bind to.
    ///
    /// Every setter raises Changed; the UI marshals to its own thread.
    /// </summary>
    public class SyncStatus
    {
        private static readonly SyncStatus instance = new SyncStatus();
        public static SyncStatus Instance => instance;

        private readonly object gate = new object();
        private readonly Dictionary<string, EntityStat> entities =
            new Dictionary<string, EntityStat>(StringComparer.OrdinalIgnoreCase);

        public event EventHandler Changed;

        private ConnectorState state = ConnectorState.Starting;
        private string message = "Starting…";
        private string currentEntity;
        private int recordsDone;
        private int recordsTotal;
        private DateTime? lastSyncAt;
        private string companyName;
        private SageAuthorizationState sageAuthorization = SageAuthorizationState.Unknown;
        private SageAuthorizationState comAuthorization = SageAuthorizationState.Unknown;
        private UpdateAvailability updateAvailability = UpdateAvailability.Unknown;
        private string updateMessage;
        private string availableVersion;

        public ConnectorState State { get { lock (gate) { return state; } } }
        public string Message { get { lock (gate) { return message; } } }
        public string CurrentEntity { get { lock (gate) { return currentEntity; } } }
        public int RecordsDone { get { lock (gate) { return recordsDone; } } }
        public int RecordsTotal { get { lock (gate) { return recordsTotal; } } }
        public DateTime? LastSyncAt { get { lock (gate) { return lastSyncAt; } } }
        public string CompanyName { get { lock (gate) { return companyName; } } }
        public SageAuthorizationState SageAuthorization { get { lock (gate) { return sageAuthorization; } } }
        public SageAuthorizationState ComAuthorization { get { lock (gate) { return comAuthorization; } } }
        public UpdateAvailability UpdateAvailability { get { lock (gate) { return updateAvailability; } } }
        public string UpdateMessage { get { lock (gate) { return updateMessage; } } }
        public string AvailableVersion { get { lock (gate) { return availableVersion; } } }

        public List<EntityStat> Entities
        {
            get
            {
                lock (gate)
                {
                    return entities.Values
                        .OrderBy(e => e.Entity, StringComparer.Ordinal)
                        .Select(e => new EntityStat
                        {
                            Entity = e.Entity,
                            RecordCount = e.RecordCount,
                            LastSyncedAt = e.LastSyncedAt,
                        })
                        .ToList();
                }
            }
        }

        public void SetCompany(string company)
        {
            lock (gate) { companyName = company; }
            Raise();
        }

        public void SetUpdateAvailability(UpdateCheckResult result)
        {
            if (result == null) return;
            lock (gate)
            {
                updateAvailability = result.Availability;
                availableVersion = result.Release != null ? result.Release.Version : null;
                switch (result.Availability)
                {
                    case UpdateAvailability.OptionalUpdate:
                        updateMessage = "Update " + (availableVersion ?? "")
                            + " is available. Installing requires re-approval in Sage 50.";
                        break;
                    case UpdateAvailability.RequiredUpdate:
                        updateMessage = "Update " + (availableVersion ?? "")
                            + " is required. Sync may be limited until you upgrade and re-approve in Sage.";
                        break;
                    case UpdateAvailability.UpToDate:
                        updateMessage = "Connector is up to date (" + AppVersion.Display + ").";
                        break;
                    case UpdateAvailability.CheckFailed:
                        updateMessage = "Could not check for updates"
                            + (string.IsNullOrEmpty(result.ErrorMessage)
                                ? "."
                                : ": " + result.ErrorMessage);
                        break;
                    default:
                        updateMessage = null;
                        break;
                }
            }
            Raise();
        }

        public void SetUpdateProgress(string text)
        {
            lock (gate)
            {
                updateMessage = text;
            }
            Raise();
        }

        public void SetIdle(string text)
        {
            lock (gate)
            {
                state = ConnectorState.Idle;
                message = text;
                currentEntity = null;
                recordsDone = 0;
                recordsTotal = 0;
            }
            Raise();
        }

        /// <summary>
        /// Rutter had no work queued.
        ///
        /// Worth its own message rather than "up to date": the connector cannot
        /// decide to send data, it can only answer what Rutter asks for. Pressing
        /// "Sync now" when nothing is queued genuinely does nothing, and saying so
        /// is better than a button that looks broken.
        /// </summary>
        public void SetNothingRequested()
        {
            lock (gate)
            {
                state = ConnectorState.Idle;
                currentEntity = null;
                recordsDone = 0;
                recordsTotal = 0;
                message = "Connected. Rutter has not requested any data — checked "
                    + DateTime.Now.ToString("t") + ", checking again in 5 minutes.";
            }
            Raise();
        }

        /// <summary>
        /// Asked Rutter for work. Distinct from Syncing because there may be
        /// nothing to do — this is the feedback for the "Sync now" button, which
        /// otherwise looks dead whenever the queue is empty.
        /// </summary>
        public void SetChecking()
        {
            lock (gate)
            {
                state = ConnectorState.Syncing;
                message = "Checking Rutter for changes…";
                currentEntity = null;
                recordsDone = 0;
                recordsTotal = 0;
            }
            Raise();
        }

        public void SetCheckingAuthorization()
        {
            lock (gate)
            {
                state = ConnectorState.Starting;
                sageAuthorization = SageAuthorizationState.Checking;
                message = "Checking whether Sage 50 approved this version…";
                currentEntity = null;
                recordsDone = 0;
                recordsTotal = 0;
            }
            Raise();
        }

        public void SetAuthorizationGranted()
        {
            lock (gate)
            {
                state = ConnectorState.Idle;
                sageAuthorization = SageAuthorizationState.Granted;
                message = "Sage 50 approved this version. Checking Rutter…";
                currentEntity = null;
            }
            Raise();
        }

        public void SetAuthorizationCheckFailed(string text)
        {
            lock (gate)
            {
                state = ConnectorState.Error;
                sageAuthorization = SageAuthorizationState.Unknown;
                message = text;
                currentEntity = null;
            }
            Raise();
        }

        public void SetCheckingComAuthorization(string text)
        {
            lock (gate)
            {
                state = ConnectorState.Starting;
                comAuthorization = SageAuthorizationState.Checking;
                message = text;
                currentEntity = null;
            }
            Raise();
        }

        public void SetComAuthorizationGranted()
        {
            lock (gate)
            {
                state = ConnectorState.Idle;
                comAuthorization = SageAuthorizationState.Granted;
                message = "All Sage 50 access is approved. Checking Rutter…";
                currentEntity = null;
            }
            Raise();
        }

        public void SetNeedsComAuthorization(string text)
        {
            lock (gate)
            {
                state = ConnectorState.NeedsAuthorization;
                comAuthorization = SageAuthorizationState.Required;
                message = text;
                currentEntity = null;
            }
            Raise();
        }

        public void SetComAuthorizationCheckFailed(string text)
        {
            lock (gate)
            {
                state = ConnectorState.Error;
                comAuthorization = SageAuthorizationState.Unknown;
                message = text;
                currentEntity = null;
            }
            Raise();
        }

        public void SetSyncing(string entity, int done, int total)
        {
            lock (gate)
            {
                state = ConnectorState.Syncing;
                currentEntity = entity;
                recordsDone = done;
                recordsTotal = total;
                message = total > 0
                    ? string.Format("Syncing {0} — {1} of {2}", Friendly(entity), done, total)
                    : string.Format("Syncing {0}…", Friendly(entity));
            }
            Raise();
        }

        /// <summary>Records a completed entity so the UI can show a per-entity total.</summary>
        public void SetEntitySynced(string entity, int recordCount)
        {
            lock (gate)
            {
                EntityStat stat;
                if (!entities.TryGetValue(entity, out stat))
                {
                    stat = new EntityStat { Entity = entity };
                    entities[entity] = stat;
                }
                stat.RecordCount = recordCount;
                stat.LastSyncedAt = DateTime.Now;
                lastSyncAt = DateTime.Now;
            }
            Raise();
        }

        /// <summary>
        /// Sage will not release company data until an administrator approves the
        /// connector — the single most common reason a customer sees no data. The
        /// UI turns this into instructions rather than a line in a log file.
        /// </summary>
        public void SetNeedsAuthorization()
        {
            lock (gate)
            {
                state = ConnectorState.NeedsAuthorization;
                sageAuthorization = SageAuthorizationState.Required;
                message = "Sage 50 approval is required for this version.";
                currentEntity = null;
            }
            Raise();
        }

        public void SetOffline(string text)
        {
            lock (gate)
            {
                state = ConnectorState.Offline;
                message = text;
                currentEntity = null;
            }
            Raise();
        }

        public void SetError(string text)
        {
            lock (gate)
            {
                state = ConnectorState.Error;
                // Sage's licence message is accurate but tells the reader nothing
                // about what to do, and it is the one error most likely to be
                // caused by us rather than by them.
                message = text != null && text.IndexOf("License is currently unavailable", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "Sage 50 has no free connections. Close other Sage integrations, or restart the "
                        + "\"Sage 50 Connect\" Windows service to release ones left behind by a crash."
                    : text;
                currentEntity = null;
            }
            Raise();
        }

        public static string Friendly(string entity)
        {
            if (string.IsNullOrEmpty(entity)) return entity;
            string lower = entity.ToLowerInvariant();
            return char.ToUpperInvariant(lower[0]) + lower.Substring(1);
        }

        private void Raise()
        {
            EventHandler handler = Changed;
            if (handler != null)
            {
                // Never let a UI handler break the sync loop.
                try { handler(this, EventArgs.Empty); } catch { }
            }
        }
    }
}
