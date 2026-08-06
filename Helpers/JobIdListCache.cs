using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sage50Connector.Helpers
{
    /// <summary>
    /// Ordered id list for a desktop LIST_FETCH job, persisted under
    /// %ProgramData%\Rutter\Sage50Connector\cache\{jobId}\{entity}.ids so a
    /// re-served job after a connector restart does not need a protocol change.
    /// Bodies are held in-process by <see cref="JobFetchCache"/>.
    /// </summary>
    public static class JobIdListCache
    {
        public static string CacheRoot
        {
            get
            {
                return Path.Combine(ConnectorConfig.ConfigDirectory, "cache");
            }
        }

        private static string PathFor(string jobId, string entity)
        {
            string safeJob = Sanitize(jobId);
            string safeEntity = Sanitize(entity);
            return Path.Combine(CacheRoot, safeJob, safeEntity + ".ids");
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

        public static bool TryGet(string jobId, string entity, out List<string> orderedIds)
        {
            orderedIds = null;
            try
            {
                string path = PathFor(jobId, entity);
                if (!File.Exists(path)) return false;
                orderedIds = File.ReadAllLines(path)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToList();
                return orderedIds.Count > 0 || File.Exists(path);
            }
            catch
            {
                orderedIds = null;
                return false;
            }
        }

        public static void Put(string jobId, string entity, IList<string> orderedIds)
        {
            try
            {
                string path = PathFor(jobId, entity);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, orderedIds ?? new string[0]);
            }
            catch (Exception ex)
            {
                global::Sage50Connector.Program.WriteToFile(
                    DateTime.Now + ": JobIdListCache.Put failed: " + ex.Message);
            }
        }

        public static void Remove(string jobId, string entity)
        {
            try
            {
                string path = PathFor(jobId, entity);
                if (File.Exists(path)) File.Delete(path);
                string dir = Path.GetDirectoryName(path);
                if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                {
                    Directory.Delete(dir);
                }
            }
            catch
            {
                // best-effort
            }
        }
    }

    /// <summary>
    /// In-process cache of the full filtered entity list for an open LIST_FETCH
    /// job. First page loads Sage once; later pages slice from memory. Cleared
    /// when the job finishes or the process exits.
    /// </summary>
    public static class JobFetchCache
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, List<object>> ByJob =
            new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);

        private static string Key(string jobId, string entity)
        {
            return (jobId ?? "") + "\0" + (entity ?? "");
        }

        public static bool TryGet(string jobId, string entity, out List<object> records)
        {
            lock (Gate)
            {
                return ByJob.TryGetValue(Key(jobId, entity), out records);
            }
        }

        public static void Put(string jobId, string entity, List<object> records)
        {
            lock (Gate)
            {
                ByJob[Key(jobId, entity)] = records ?? new List<object>();
            }
        }

        public static void Remove(string jobId, string entity)
        {
            lock (Gate)
            {
                ByJob.Remove(Key(jobId, entity));
            }
        }
    }
}
