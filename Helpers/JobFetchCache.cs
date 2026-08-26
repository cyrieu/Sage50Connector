using System;
using System.Collections.Generic;

namespace Sage50Connector.Helpers
{
    /// <summary>
    /// In-process cache of the full filtered entity list for an open LIST_FETCH
    /// job. First page loads Sage once; later pages slice from memory. If the
    /// process restarts mid-job, Program asks Rutter to reset the cursor before
    /// loading a new live Sage snapshot.
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
