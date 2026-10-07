// No Sage access. Compile into an isolated diagnostic copy of the connector.
using System;
using System.Collections.Generic;
using System.Linq;
using Sage50Connector.Helpers;
using Sage50Connector.Models.Rutter;
namespace Sage50Connector.Diagnostics
{
    internal static class InvoiceWindowReaderTests
    {
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }
        public static InvoiceWindowReader CreateDeliveryReader()
        {
            var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
            return new InvoiceWindowReader(ids, null, null, (a,b) => null,
                id => new InvoiceBody { ID = id.ToString() });
        }
        public static int Run()
        {
            var ids = Enumerable.Range(0, 603).Select(i => Guid.NewGuid()).ToList();
            var day = new DateTime(2026, 8, 1);
            int maps = 0, loads = 0;
            using (var reader = new InvoiceWindowReader(ids, day, day.AddDays(30), (a, b) =>
            {
                loads++;
                // Dense single day; last three ids represent missing or moved dates.
                return a <= day && day < b ? ids.Take(600).Concat(new[] { Guid.NewGuid() }).ToList() : new List<Guid>();
            }, id => { maps++; return new InvoiceBody { ID = id.ToString() }; }))
            {
                var seen = new HashSet<string>(); string cursor = null;
                do
                {
                    var page = reader.ReadPage(cursor, 50);
                    int prior = maps;
                    Assert(ReferenceEquals(page, reader.ReadPage(cursor, 50)) && prior == maps, "rejected upload retains exact page without SDK reads");
                    Assert(page.Records.Count <= 50, "dense day keeps page bounded");
                    foreach (var row in page.Records) if (!seen.Add(row.ID)) throw new Exception("Duplicate original key");
                    cursor = page.NextCursor; reader.Accept(page);
                } while (cursor != null);
                Assert(seen.Count == 603 && maps == 603, "missing/moved dates covered; new keys excluded");
                Assert(loads > 1, "dense multi-day window split before mapping");
                Assert(!reader.MatchesCursor("invoice-v1:other:1"), "foreign session cursor rejected");
                Assert(reader.AcceptedCount == 603, "acknowledged progress counted");
            }
            using (var reader = new InvoiceWindowReader(ids, null, null, (a,b) => { throw new Exception("No dated inventory"); }, id => null))
            {
                var page = reader.ReadPage(null, 0);
                Assert(page.Records.Count == 0 && page.NextCursor == null, "all filtered and missing-date-only inventory completes");
                reader.Dispose(); reader.Accept(page);
                Assert(!reader.MatchesCursor(null), "lost Sage session invalidates cursor and late acknowledgement is safe");
            }
            using (var reader = new InvoiceWindowReader(ids, null, null, (a,b) => null, id => new InvoiceBody { ID = id.ToString() }))
            {
                var page = reader.ReadPage(null, int.MaxValue);
                Assert(page.Records.Count == 250, "oversized page request capped");
                Assert(reader.AcceptedCount == 0, "unacknowledged report not counted");
            }
            Console.WriteLine("INVOICE WINDOW READER TESTS PASSED"); return 0;
        }
    }
}
