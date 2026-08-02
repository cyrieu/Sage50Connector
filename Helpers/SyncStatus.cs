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

        public ConnectorState State { get { lock (gate) { return state; } } }
        public string Message { get { lock (gate) { return message; } } }
        public string CurrentEntity { get { lock (gate) { return currentEntity; } } }
        public int RecordsDone { get { lock (gate) { return recordsDone; } } }
        public int RecordsTotal { get { lock (gate) { return recordsTotal; } } }
        public DateTime? LastSyncAt { get { lock (gate) { return lastSyncAt; } } }
        public string CompanyName { get { lock (gate) { return companyName; } } }

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
                message = "Sage 50 has not authorized this connector yet.";
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
