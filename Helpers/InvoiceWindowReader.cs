using System;
using System.Collections.Generic;
using System.Linq;
using Sage50Connector.Models.Rutter;

namespace Sage50Connector.Helpers
{
    /// <summary>
    /// A job-scoped key inventory and date traversal. Only one unacknowledged
    /// DTO page is retained. SDK lists and invoice objects never cross HTTP waits.
    /// Cursors belong to this process session; a lost session requires server reset.
    /// </summary>
    internal sealed class InvoiceWindowReader : IDisposable
    {
        internal sealed class Page
        {
            public List<InvoiceBody> Records { get; internal set; }
            public string NextCursor { get; internal set; }
        }
        private readonly HashSet<Guid> remaining;
        private readonly Stack<Tuple<DateTime, DateTime>> windows = new Stack<Tuple<DateTime, DateTime>>();
        private readonly Func<DateTime, DateTime, List<Guid>> loadKeys;
        private readonly Func<Guid, InvoiceBody> map;
        private readonly string session = Guid.NewGuid().ToString("N");
        private IEnumerator<Guid> keys;
        private Page pending;
        private string expectedCursor;
        private int sequence;
        private bool disposed;
        public int AcceptedCount { get; private set; }

        public InvoiceWindowReader(IEnumerable<Guid> inventory, DateTime? first, DateTime? last,
            Func<DateTime, DateTime, List<Guid>> loadKeys, Func<Guid, InvoiceBody> map)
        {
            remaining = new HashSet<Guid>(inventory);
            this.loadKeys = loadKeys;
            this.map = map;
            if (first.HasValue && last.HasValue && first <= last)
            {
                // MaxValue cannot form a half-open end: its keys are covered by fallback.
                DateTime upper = last.Value == DateTime.MaxValue.Date ? last.Value : last.Value.AddDays(1);
                var initial = new List<Tuple<DateTime, DateTime>>();
                for (var lower = first.Value; lower < upper;)
                {
                    var end = (upper - lower).TotalDays > 31 ? lower.AddDays(31) : upper;
                    initial.Add(Tuple.Create(lower, end)); lower = end;
                }
                for (int i = initial.Count - 1; i >= 0; i--) windows.Push(initial[i]);
            }
        }

        public bool MatchesCursor(string cursor)
        {
            return !disposed && string.Equals(cursor ?? "", expectedCursor ?? "", StringComparison.Ordinal);
        }

        public Page ReadPage(string cursor, int limit)
        {
            if (!MatchesCursor(cursor)) throw new InvalidOperationException("Invoice cursor does not match this Sage read session.");
            if (pending != null) return pending;
            // Missing/oversized limits must not turn this into an unbounded full fetch.
            limit = limit > 0 ? Math.Min(limit, 250) : 50;
            var records = new List<InvoiceBody>(limit);
            Guid key;
            while (records.Count < limit && TryNextKey(out key))
            {
                var record = map(key);
                if (record != null) records.Add(record);
            }
            // A full page may be followed by an empty final page; no extra DTO
            // lookahead is retained just to suppress that harmless final report.
            bool final = remaining.Count == 0;
            pending = new Page { Records = records,
                NextCursor = final ? null : "invoice-v1:" + session + ":" + (++sequence) };
            return pending;
        }

        private bool TryNextKey(out Guid key)
        {
            while (remaining.Count > 0)
            {
                if (keys != null)
                {
                    while (keys.MoveNext())
                    {
                        key = keys.Current;
                        if (remaining.Remove(key)) return true;
                    }
                    keys.Dispose(); keys = null;
                }
                if (windows.Count == 0)
                {
                    // Covers missing dates and original records moved into an already
                    // visited window. New records are excluded from this job's inventory.
                    keys = remaining.OrderBy(id => id).ToList().GetEnumerator();
                    continue;
                }
                var window = windows.Pop();
                var loaded = loadKeys(window.Item1, window.Item2);
                int days = (window.Item2 - window.Item1).Days;
                if (loaded.Count > 250 && days > 1)
                {
                    var middle = window.Item1.AddDays(days / 2);
                    windows.Push(Tuple.Create(middle, window.Item2));
                    windows.Push(Tuple.Create(window.Item1, middle));
                    continue;
                }
                // Even a dense single day retains keys only; map loads one invoice
                // independently rather than pinning every hydrated row in a list.
                keys = loaded.GetEnumerator();
            }
            key = Guid.Empty;
            return false;
        }

        public void Accept(Page page)
        {
            // An HTTP acknowledgement can arrive after a network failure released Sage.
            // The report remains valid; the next cursor will trigger a clean restart.
            if (disposed) return;
            if (!ReferenceEquals(page, pending)) throw new InvalidOperationException("Unexpected invoice page acknowledgement.");
            AcceptedCount += page.Records.Count;
            expectedCursor = page.NextCursor;
            pending = null;
        }

        public void Dispose()
        {
            disposed = true;
            keys?.Dispose(); keys = null;
            windows.Clear(); remaining.Clear(); pending = null;
        }
    }

    internal static class InvoiceFetchCache
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, InvoiceWindowReader> Readers = new Dictionary<string, InvoiceWindowReader>();
        public static bool TryGet(string jobId, out InvoiceWindowReader reader)
        {
            lock (Gate) return Readers.TryGetValue(jobId, out reader);
        }
        public static void Put(string jobId, InvoiceWindowReader reader)
        {
            lock (Gate) { Remove(jobId); Readers.Add(jobId, reader); }
        }
        public static void Remove(string jobId)
        {
            lock (Gate)
            {
                InvoiceWindowReader reader;
                if (Readers.TryGetValue(jobId, out reader)) { reader.Dispose(); Readers.Remove(jobId); }
            }
        }
        public static void Clear()
        {
            lock (Gate) { foreach (var reader in Readers.Values) reader.Dispose(); Readers.Clear(); }
        }
    }
}
