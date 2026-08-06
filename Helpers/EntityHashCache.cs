using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Sage50Connector.Helpers
{
    /// <summary>
    /// Per-connection id → content hash so unchanged rows can be omitted from
    /// LIST_FETCH reports. Rutter already dedupes on upsert; this only saves
    /// wire/CPU on the customer's machine.
    /// </summary>
    public static class EntityHashCache
    {
        private static string PathFor(string connectionId, string entity)
        {
            string root = Path.Combine(ConnectorConfig.ConfigDirectory, "cache", "hashes");
            return Path.Combine(root, Sanitize(connectionId) + "_" + Sanitize(entity) + ".hashes");
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "_";
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(c, '_');
            }
            return value;
        }

        public static Dictionary<string, string> Load(string connectionId, string entity)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                string path = PathFor(connectionId, entity);
                if (!File.Exists(path)) return map;
                foreach (string line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    string id = line.Substring(0, tab);
                    string hash = line.Substring(tab + 1);
                    if (!string.IsNullOrEmpty(id)) map[id] = hash;
                }
            }
            catch
            {
                // start fresh
            }
            return map;
        }

        public static void Save(string connectionId, string entity, Dictionary<string, string> map)
        {
            try
            {
                string path = PathFor(connectionId, entity);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var lines = map.Select(kv => kv.Key + "\t" + kv.Value).ToArray();
                File.WriteAllLines(path, lines);
            }
            catch (Exception ex)
            {
                global::Sage50Connector.Program.WriteToFile(
                    DateTime.Now + ": EntityHashCache.Save failed: " + ex.Message);
            }
        }

        public static string HashRecord(object record, JsonSerializerSettings settings)
        {
            string json = JsonConvert.SerializeObject(record, settings);
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(json ?? ""));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

    }
}
