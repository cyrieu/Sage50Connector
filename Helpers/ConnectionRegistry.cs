using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Sage50Connector.Helpers
{
    /// <summary>Stored values for <see cref="StoredConnection.LastProbeResult"/>.</summary>
    internal static class ConnectionProbeResults
    {
        internal const string Alive = "Alive";
        internal const string Disconnected = "Disconnected";
        internal const string Unreachable = "Unreachable";
    }

    /// <summary>
    /// One Sage 50 company this machine has connected to Rutter.
    /// AccessKey is plaintext only in memory; the file stores DPAPI ciphertext.
    /// </summary>
    internal sealed class StoredConnection
    {
        public string CompanyName { get; set; }
        public string CompanyGuid { get; set; }
        public string DatabaseName { get; set; }
        public string ConnectionId { get; set; }
        public string AccessKey { get; set; }
        public string ApiBaseUrl { get; set; }
        public DateTime AddedAtUtc { get; set; }
        public DateTime? LastActivatedAtUtc { get; set; }
        public DateTime? LastProbeAtUtc { get; set; }
        public string LastProbeResult { get; set; }
    }

    /// <summary>
    /// Every company this Windows user has finished setting up, in
    /// %ProgramData%\Rutter\Sage50Connector\connections.json.
    ///
    /// sage50Config.json remains the single active connection. This file is how
    /// the tray can offer the others without asking the customer to run Rutter
    /// Link again. Access keys are DPAPI-protected to the current user, the
    /// same way the shared COM credential is.
    /// </summary>
    internal static class ConnectionRegistry
    {
        internal const string FileName = "connections.json";

        internal static readonly string FilePath = Path.Combine(
            ConnectorConfig.ConfigDirectory, FileName);

        private static readonly object Gate = new object();

        /// <summary>
        /// Set when connections.json exists but cannot be parsed. The file is
        /// left untouched so a later load can still recover it.
        /// </summary>
        public static string LoadError { get; private set; }

        public static void EnsureLoaded()
        {
            lock (Gate)
            {
                ReadAndReconcile();
            }
        }

        public static IList<StoredConnection> List()
        {
            lock (Gate)
            {
                return ReadAndReconcile()
                    .Select(Clone)
                    .OrderBy(connection => connection.CompanyName ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(connection => connection.AddedAtUtc)
                    .ToList();
            }
        }

        public static StoredConnection Find(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId)) return null;
            lock (Gate)
            {
                StoredConnection found = ReadAndReconcile()
                    .FirstOrDefault(connection => SameConnection(connection.ConnectionId, connectionId));
                return found == null ? null : Clone(found);
            }
        }

        /// <summary>
        /// Insert or replace by ConnectionId. A newer setup for the same Sage
        /// company and API host drops the previous connection so the list does
        /// not fill with dead reconnects. Company GUID is required for that
        /// collapse — legacy rows with no GUID are kept apart.
        /// </summary>
        public static void Upsert(StoredConnection connection, bool activate)
        {
            if (connection == null) throw new ArgumentNullException("connection");
            if (string.IsNullOrWhiteSpace(connection.ConnectionId))
                throw new InvalidOperationException("A connection id is required.");
            if (string.IsNullOrWhiteSpace(connection.AccessKey))
                throw new InvalidOperationException("An access key is required.");

            lock (Gate)
            {
                List<StoredConnection> records = ReadAndReconcile();
                DateTime now = DateTime.UtcNow;
                string api = NormalizeApiBaseUrl(connection.ApiBaseUrl);
                DropSuperseded(records, connection.ConnectionId, connection.CompanyGuid, api);

                StoredConnection existing = records
                    .FirstOrDefault(row => SameConnection(row.ConnectionId, connection.ConnectionId));
                if (existing == null)
                {
                    StoredConnection created = Clone(connection);
                    created.ApiBaseUrl = api;
                    created.AddedAtUtc = now;
                    created.LastActivatedAtUtc = activate ? now : created.LastActivatedAtUtc;
                    records.Add(created);
                }
                else
                {
                    existing.CompanyName = connection.CompanyName;
                    existing.CompanyGuid = connection.CompanyGuid;
                    existing.DatabaseName = connection.DatabaseName;
                    existing.AccessKey = connection.AccessKey;
                    existing.ApiBaseUrl = api;
                    if (activate) existing.LastActivatedAtUtc = now;
                }

                Persist(records);
                LogRegistration(connection, existing == null);
            }
        }

        public static void RecordProbe(string connectionId, string result)
        {
            if (string.IsNullOrWhiteSpace(connectionId)) return;
            lock (Gate)
            {
                List<StoredConnection> records = ReadAndReconcile();
                StoredConnection existing = records
                    .FirstOrDefault(row => SameConnection(row.ConnectionId, connectionId));
                if (existing == null) return;
                existing.LastProbeAtUtc = DateTime.UtcNow;
                existing.LastProbeResult = result;
                Persist(records);
            }
        }

        public static void MarkActivated(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId)) return;
            lock (Gate)
            {
                List<StoredConnection> records = ReadAndReconcile();
                StoredConnection existing = records
                    .FirstOrDefault(row => SameConnection(row.ConnectionId, connectionId));
                if (existing == null) return;
                existing.LastActivatedAtUtc = DateTime.UtcNow;
                Persist(records);
            }
        }

        /// <summary>
        /// Drops a registry row only. Never talks to Rutter or Sage, and never
        /// removes the company that sage50Config.json is currently syncing.
        /// </summary>
        public static bool Remove(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId)) return false;
            lock (Gate)
            {
                if (IsActiveConnection(connectionId)) return false;
                List<StoredConnection> records = ReadAndReconcile();
                int removed = records.RemoveAll(row => SameConnection(row.ConnectionId, connectionId));
                if (removed == 0) return false;
                Persist(records);
                global::Sage50Connector.Program.WriteToFile(
                    "Removed connection '" + connectionId + "' from the local company list.");
                return true;
            }
        }

        public static string FormatLabel(StoredConnection connection, bool nameCollides, bool isActive)
        {
            if (connection == null) return string.Empty;
            string label = string.IsNullOrWhiteSpace(connection.CompanyName)
                ? connection.ConnectionId
                : connection.CompanyName;
            if (nameCollides)
            {
                DateTime? when = connection.LastActivatedAtUtc ?? (DateTime?)connection.AddedAtUtc;
                if (when.HasValue)
                    label += " (" + when.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + ")";
            }
            if (!isActive && !string.IsNullOrWhiteSpace(connection.LastProbeResult))
            {
                string probe = ProbeLabel(connection.LastProbeResult);
                if (!string.IsNullOrEmpty(probe))
                    label += " — " + probe;
            }
            return label;
        }

        public static string ProbeLabel(string lastProbeResult)
        {
            if (string.Equals(lastProbeResult, ConnectionProbeResults.Alive, StringComparison.Ordinal))
                return "Connected";
            if (string.Equals(lastProbeResult, ConnectionProbeResults.Disconnected, StringComparison.Ordinal))
                return "Disconnected — reconnect via Rutter Link";
            if (string.Equals(lastProbeResult, ConnectionProbeResults.Unreachable, StringComparison.Ordinal))
                return "Couldn't reach Rutter";
            return null;
        }

        internal static string NormalizeApiBaseUrl(string apiBaseUrl)
        {
            string value = string.IsNullOrWhiteSpace(apiBaseUrl)
                ? ConnectorConfig.DefaultApiBaseUrl
                : apiBaseUrl.Trim();
            return value.TrimEnd('/');
        }

        private static void LogRegistration(StoredConnection connection, bool created)
        {
            global::Sage50Connector.Program.WriteToFile(
                (created ? "Registered" : "Updated")
                    + " company '"
                    + connection.CompanyName
                    + "' ConnectionId='"
                    + connection.ConnectionId
                    + "'; AccessKeyLength="
                    + (connection.AccessKey == null ? 0 : connection.AccessKey.Length)
                    + "; ApiBaseUrl="
                    + NormalizeApiBaseUrl(connection.ApiBaseUrl));
        }

        private static List<StoredConnection> ReadAndReconcile()
        {
            List<StoredConnection> records;
            if (!File.Exists(FilePath))
            {
                LoadError = null;
                records = new List<StoredConnection>();
                StoredConnection seeded = TryReadActiveConfig();
                if (seeded != null)
                {
                    records.Add(seeded);
                    Persist(records);
                    global::Sage50Connector.Program.WriteToFile(
                        "Seeded the company list from "
                            + ConnectorConfig.ResolveConfigFilePath()
                            + "; CompanyName='"
                            + seeded.CompanyName
                            + "'; ConnectionId='"
                            + seeded.ConnectionId
                            + "'; AccessKeyLength="
                            + (seeded.AccessKey == null ? 0 : seeded.AccessKey.Length));
                }
                return records;
            }

            try
            {
                records = Parse(File.ReadAllText(FilePath));
                LoadError = null;
            }
            catch (Exception ex)
            {
                LoadError = "Couldn't read the company list (" + FileName + ").";
                // The parser message can quote the file. The file holds ciphertext,
                // so keep the exception type only.
                global::Sage50Connector.Program.WriteToFile(
                    "Company list could not be read: " + ex.GetType().Name);
                records = new List<StoredConnection>();
            }

            // Do not rewrite a file we could not parse. Still surface the
            // company that is syncing so the tray is not blank.
            if (LoadError != null)
            {
                StoredConnection active = TryReadActiveConfig();
                if (active != null) records.Add(active);
                return records;
            }

            if (ReconcileActive(records))
            {
                try { Persist(records); }
                catch (Exception ex)
                {
                    global::Sage50Connector.Program.WriteToFile(
                        "Company list could not be updated from the active config: " + ex.Message);
                }
            }
            return records;
        }

        /// <summary>
        /// The active sage50Config.json must appear in the list even if setup
        /// saved the config and then failed before the registry write. Returns
        /// true when the in-memory list changed.
        /// </summary>
        private static bool ReconcileActive(List<StoredConnection> records)
        {
            StoredConnection active = TryReadActiveConfig();
            if (active == null) return false;

            string api = NormalizeApiBaseUrl(active.ApiBaseUrl);
            int removed = 0;
            if (!string.IsNullOrWhiteSpace(active.CompanyGuid))
            {
                removed = records.RemoveAll(row =>
                    !SameConnection(row.ConnectionId, active.ConnectionId)
                    && SameGuid(row.CompanyGuid, active.CompanyGuid)
                    && string.Equals(NormalizeApiBaseUrl(row.ApiBaseUrl), api, StringComparison.OrdinalIgnoreCase));
            }

            StoredConnection existing = records
                .FirstOrDefault(row => SameConnection(row.ConnectionId, active.ConnectionId));
            if (existing != null)
            {
                bool changed = removed > 0;
                if (!string.Equals(existing.CompanyName, active.CompanyName, StringComparison.Ordinal)
                    || !SameGuid(existing.CompanyGuid, active.CompanyGuid)
                    || !string.Equals(existing.DatabaseName ?? string.Empty, active.DatabaseName ?? string.Empty, StringComparison.Ordinal)
                    || !string.Equals(existing.AccessKey, active.AccessKey, StringComparison.Ordinal)
                    || !string.Equals(NormalizeApiBaseUrl(existing.ApiBaseUrl), api, StringComparison.OrdinalIgnoreCase))
                {
                    existing.CompanyName = active.CompanyName;
                    existing.CompanyGuid = active.CompanyGuid;
                    existing.DatabaseName = active.DatabaseName;
                    existing.AccessKey = active.AccessKey;
                    existing.ApiBaseUrl = api;
                    changed = true;
                }
                return changed;
            }

            records.Add(active);
            return true;
        }

        private static StoredConnection TryReadActiveConfig()
        {
            try
            {
                ConnectorConfig config = ConnectorConfig.Load();
                DateTime added = DateTime.UtcNow;
                try
                {
                    if (!string.IsNullOrEmpty(config.LoadedFromPath) && File.Exists(config.LoadedFromPath))
                        added = File.GetLastWriteTimeUtc(config.LoadedFromPath);
                }
                catch { /* the clock on the row is display-only */ }

                return new StoredConnection
                {
                    CompanyName = config.CompanyName,
                    CompanyGuid = config.CompanyGuid,
                    DatabaseName = config.DatabaseName,
                    ConnectionId = config.ConnectionId,
                    AccessKey = config.AccessKey,
                    ApiBaseUrl = NormalizeApiBaseUrl(config.ApiBaseUrl),
                    AddedAtUtc = added,
                    LastActivatedAtUtc = added,
                };
            }
            catch
            {
                return null;
            }
        }

        private static bool IsActiveConnection(string connectionId)
        {
            try
            {
                ConnectorConfig config = ConnectorConfig.Load();
                return SameConnection(config.ConnectionId, connectionId);
            }
            catch
            {
                return false;
            }
        }

        private static void DropSuperseded(
            List<StoredConnection> records,
            string connectionId,
            string companyGuid,
            string apiBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(companyGuid)) return;
            records.RemoveAll(row =>
                !SameConnection(row.ConnectionId, connectionId)
                && SameGuid(row.CompanyGuid, companyGuid)
                && string.Equals(NormalizeApiBaseUrl(row.ApiBaseUrl), apiBaseUrl, StringComparison.OrdinalIgnoreCase));
        }

        private static List<StoredConnection> Parse(string json)
        {
            JObject root = JObject.Parse(json);
            JArray rows = root["Connections"] as JArray ?? new JArray();
            var records = new List<StoredConnection>();
            foreach (JToken token in rows)
            {
                JObject row = token as JObject;
                if (row == null) continue;
                StoredConnection connection = ReadRow(row);
                if (connection == null) continue;
                if (records.Any(existing => SameConnection(existing.ConnectionId, connection.ConnectionId)))
                {
                    global::Sage50Connector.Program.WriteToFile(
                        "Skipping duplicate company-list row for ConnectionId='" + connection.ConnectionId + "'.");
                    continue;
                }
                records.Add(connection);
            }
            return records;
        }

        private static StoredConnection ReadRow(JObject row)
        {
            string connectionId = row.Value<string>("ConnectionId");
            if (string.IsNullOrWhiteSpace(connectionId)) return null;

            string accessKey = Unprotect(row.Value<string>("AccessKeyProtected"), connectionId);
            if (string.IsNullOrWhiteSpace(accessKey)) return null;

            // A plaintext AccessKey in this file would be a mistake. Ignore it.
            if (row["AccessKey"] != null)
            {
                global::Sage50Connector.Program.WriteToFile(
                    "Ignored a plaintext AccessKey field on company-list ConnectionId='" + connectionId + "'.");
            }

            return new StoredConnection
            {
                CompanyName = row.Value<string>("CompanyName"),
                CompanyGuid = row.Value<string>("CompanyGuid"),
                DatabaseName = row.Value<string>("DatabaseName"),
                ConnectionId = connectionId,
                AccessKey = accessKey,
                ApiBaseUrl = NormalizeApiBaseUrl(row.Value<string>("ApiBaseUrl")),
                AddedAtUtc = ReadTime(row, "AddedAt") ?? DateTime.UtcNow,
                LastActivatedAtUtc = ReadTime(row, "LastActivatedAt"),
                LastProbeAtUtc = ReadTime(row, "LastProbeAt"),
                LastProbeResult = row.Value<string>("LastProbeResult"),
            };
        }

        private static void Persist(List<StoredConnection> records)
        {
            if (LoadError != null)
                throw new InvalidOperationException(LoadError);

            var rows = new JArray();
            foreach (StoredConnection connection in records)
            {
                var row = new JObject
                {
                    ["CompanyName"] = connection.CompanyName,
                    ["ConnectionId"] = connection.ConnectionId,
                    ["AccessKeyProtected"] = Protect(connection.AccessKey),
                    ["ApiBaseUrl"] = NormalizeApiBaseUrl(connection.ApiBaseUrl),
                    ["AddedAt"] = WriteTime(connection.AddedAtUtc),
                };
                if (!string.IsNullOrWhiteSpace(connection.CompanyGuid))
                    row["CompanyGuid"] = connection.CompanyGuid;
                if (!string.IsNullOrWhiteSpace(connection.DatabaseName))
                    row["DatabaseName"] = connection.DatabaseName;
                if (connection.LastActivatedAtUtc.HasValue)
                    row["LastActivatedAt"] = WriteTime(connection.LastActivatedAtUtc.Value);
                if (connection.LastProbeAtUtc.HasValue)
                    row["LastProbeAt"] = WriteTime(connection.LastProbeAtUtc.Value);
                if (!string.IsNullOrWhiteSpace(connection.LastProbeResult))
                    row["LastProbeResult"] = connection.LastProbeResult;
                rows.Add(row);
            }

            var root = new JObject { ["Connections"] = rows };
            AtomicFile.WriteAllText(FilePath, root.ToString(Formatting.Indented));
        }

        private static string Protect(string accessKey)
        {
            byte[] plaintext = Encoding.UTF8.GetBytes(accessKey ?? string.Empty);
            try
            {
                byte[] ciphertext = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(ciphertext);
            }
            finally
            {
                Array.Clear(plaintext, 0, plaintext.Length);
            }
        }

        private static string Unprotect(string protectedBase64, string connectionId)
        {
            if (string.IsNullOrWhiteSpace(protectedBase64))
            {
                global::Sage50Connector.Program.WriteToFile(
                    "Company-list ConnectionId='" + connectionId + "' has no protected access key and was skipped.");
                return null;
            }

            byte[] ciphertext;
            try
            {
                ciphertext = Convert.FromBase64String(protectedBase64);
            }
            catch (FormatException)
            {
                global::Sage50Connector.Program.WriteToFile(
                    "Company-list ConnectionId='" + connectionId + "' has an unreadable access key and was skipped.");
                return null;
            }

            byte[] plaintext = null;
            try
            {
                plaintext = ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plaintext);
            }
            catch (Exception ex)
            {
                global::Sage50Connector.Program.WriteToFile(
                    "Company-list ConnectionId='"
                        + connectionId
                        + "' access key could not be decrypted ("
                        + ex.GetType().Name
                        + ") and was skipped.");
                return null;
            }
            finally
            {
                if (plaintext != null) Array.Clear(plaintext, 0, plaintext.Length);
            }
        }

        private static DateTime? ReadTime(JObject row, string name)
        {
            string text = row.Value<string>(name);
            if (string.IsNullOrWhiteSpace(text)) return null;
            DateTime parsed;
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
                return parsed.ToUniversalTime();
            return null;
        }

        private static string WriteTime(DateTime value)
        {
            DateTime utc = value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                : value.ToUniversalTime();
            return utc.ToString("o", CultureInfo.InvariantCulture);
        }

        private static StoredConnection Clone(StoredConnection source)
        {
            return new StoredConnection
            {
                CompanyName = source.CompanyName,
                CompanyGuid = source.CompanyGuid,
                DatabaseName = source.DatabaseName,
                ConnectionId = source.ConnectionId,
                AccessKey = source.AccessKey,
                ApiBaseUrl = source.ApiBaseUrl,
                AddedAtUtc = source.AddedAtUtc,
                LastActivatedAtUtc = source.LastActivatedAtUtc,
                LastProbeAtUtc = source.LastProbeAtUtc,
                LastProbeResult = source.LastProbeResult,
            };
        }

        private static bool SameConnection(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameGuid(string left, string right)
        {
            string a = string.IsNullOrWhiteSpace(left) ? string.Empty : left.Trim();
            string b = string.IsNullOrWhiteSpace(right) ? string.Empty : right.Trim();
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
