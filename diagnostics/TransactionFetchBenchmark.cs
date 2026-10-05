// Compiled only into the isolated lab build by Build-TransactionFetchBenchmark.ps1.
// Reads Bellwether sample data; never polls Rutter, edits transactions or loads credentials.
using Newtonsoft.Json;
using Sage.Peachtree.API;
using Sage.Peachtree.API.Collections.Generic;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Sage50Connector.Diagnostics
{
    internal static class TransactionFetchBenchmark
    {
        private static string output;
        private static string entityType;
        private static string PathOf(string name) { return entityType + "." + name; }
        private static readonly List<object> observations = new List<object>();
        private static int failures;
        private sealed class Row
        {
            public string Id;
            public DateTime? Date;
            public DateTime? Saved;
            public int Lines;
            public string Hash;
        }
        private static PropertyInfo FindProperty(Type type, string name)
        {
            for (; type != null; type = type.BaseType)
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (property != null) return property;
            }
            return null;
        }
        private static object Property(object value, string name)
        {
            return value == null ? null : FindProperty(value.GetType(), name)?.GetValue(value);
        }
        private static DateTime? DateOf(object value) { return value is DateTime ? (DateTime?)value : null; }
        private static bool Missing(DateTime? date) { return date == null || date.Value == DateTime.MinValue; }
        private static string GuidOf(object reference) { return Convert.ToString(Property(reference, "Guid"), CultureInfo.InvariantCulture); }
        private static string Hash(object value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value)))).Replace("-", "");
        }
        private static Row Snapshot(object record)
        {
            var header = new SortedDictionary<string, object>();
            foreach (var name in new[] { "ReferenceNumber", "Date", "LastSavedAt", "Amount", "AmountDue", "DiscountAmount", "FreightAmount", "SalesTaxAmount", "WaitingForBill" })
                if (FindProperty(record.GetType(), name) != null) header[name] = Property(record, name);
            var lines = new List<object>();
            foreach (var name in new[] { "GeneralJournalEntryLines", "ApplyToSalesLines", "ApplyToSalesOrderLines", "ApplyToProposalLines", "WithholdRetainageLines", "ApplyToPurchasesLines", "ApplyToOrderLines", "ApplyToExpenseLines", "ApplyToInvoiceLines", "ApplyToRevenuesLines" })
            {
                var collection = Property(record, name) as IEnumerable;
                if (collection == null) continue;
                foreach (var line in collection)
                {
                    if (Property(line, "IsUsed") is bool && !(bool)Property(line, "IsUsed")) continue;
                    var data = new SortedDictionary<string, object> { { "collection", name } };
                    foreach (var field in new[] { "Amount", "DebitAmount", "CreditAmount", "AmountPaid", "DiscountAmount", "Quantity", "UnitPrice", "Description", "AccountReference", "InventoryItemReference", "JobReference", "InvoiceReference" })
                    {
                        if (FindProperty(line.GetType(), field) == null) continue;
                        var value = Property(line, field);
                        data[field] = field.EndsWith("Reference") ? (object)GuidOf(value) : value;
                    }
                    lines.Add(data);
                }
            }
            header["lines"] = lines;
            return new Row { Id = GuidOf(Property(record, "Key")), Date = DateOf(Property(record, "Date")), Saved = DateOf(Property(record, "LastSavedAt")), Lines = lines.Count, Hash = Hash(header) };
        }
        private static void Save() { File.WriteAllText(output, JsonConvert.SerializeObject(new { failures, observations }, Formatting.Indented)); }
        private static void Observe(object value) { observations.Add(value); Save(); }
        private static object NewList(string factory)
        {
            var factories = Helpers.CompanyManager.Instance.CurrentCompany.Factories;
            var value = Property(factories, factory);
            return value.GetType().GetMethod("List", Type.EmptyTypes).Invoke(value, null);
        }
        private static List<Row> Read(string factory, string mode, FilterExpression filter = null, string sort = null)
        {
            var list = NewList(factory);
            var watch = Stopwatch.StartNew();
            long initialMemory = GC.GetTotalMemory(false);
            if (filter == null && sort == null) list.GetType().GetMethod("Load", Type.EmptyTypes).Invoke(list, null);
            else
            {
                var modifiers = LoadModifiers.Create(); modifiers.Filters = filter;
                if (sort != null) modifiers.Sorts.Add(SortExpression.OrderByAscending(PathOf(sort)));
                list.GetType().GetMethod("Load", new[] { typeof(LoadModifiers) }).Invoke(list, new object[] { modifiers });
            }
            double loadMs = watch.Elapsed.TotalMilliseconds;
            var rows = new List<Row>(); double firstMs = 0, first25Ms = 0, first50Ms = 0, keysMs = 0;
            IEnumerable records = (IEnumerable)list;
            if (mode == "sorted-keys")
            {
                var keys = ((IEnumerable)Property(list, "Keys")).Cast<object>().OrderBy(GuidOf, StringComparer.Ordinal).ToList();
                keysMs = watch.Elapsed.TotalMilliseconds;
                records = keys.Select(key => list.GetType().GetProperties().First(p => p.Name == "Item" && p.GetIndexParameters().Length == 1 && p.GetIndexParameters()[0].ParameterType.IsInstanceOfType(key)).GetValue(list, new[] { key }));
            }
            foreach (var record in records)
            {
                rows.Add(Snapshot(record));
                if (rows.Count == 1) firstMs = watch.Elapsed.TotalMilliseconds;
                if (rows.Count == 25) first25Ms = watch.Elapsed.TotalMilliseconds;
                if (rows.Count == 50) first50Ms = watch.Elapsed.TotalMilliseconds;
            }
            if (rows.Count < 25) first25Ms = watch.Elapsed.TotalMilliseconds;
            if (rows.Count < 50) first50Ms = watch.Elapsed.TotalMilliseconds;
            Observe(new { factory, mode, sort, hasFilter = filter != null, rows = rows.Count, lines = rows.Sum(r => r.Lines), loadMs, keysMs, firstMs, first25Ms, first50Ms, totalMs = watch.Elapsed.TotalMilliseconds, managedMemoryDelta = GC.GetTotalMemory(false) - initialMemory });
            return rows;
        }
        private static void Compare(string factory, string scenario, List<Row> expected, Func<List<Row>> fetch)
        {
            try
            {
                var actual = fetch();
                var left = expected.OrderBy(r => r.Id).Select(r => r.Id + ":" + r.Hash).ToArray();
                var right = actual.OrderBy(r => r.Id).Select(r => r.Id + ":" + r.Hash).ToArray();
                bool passed = left.SequenceEqual(right) && actual.Select(r => r.Id).Distinct().Count() == actual.Count;
                if (!passed) failures++;
                Observe(new { factory, scenario, passed, expected = expected.Count, actual = actual.Count, missing = expected.Select(r => r.Id).Except(actual.Select(r => r.Id)).Count(), extra = actual.Select(r => r.Id).Except(expected.Select(r => r.Id)).Count() });
            }
            catch (Exception ex)
            {
                failures++; while (ex.InnerException != null) ex = ex.InnerException;
                Observe(new { factory, scenario, passed = false, error = ex.GetType().Name + ": " + ex.Message, stack = ex.StackTrace });
            }
        }
        private static FilterExpression Window(string property, DateTime after, DateTime before)
        {
            return FilterExpression.AndAlso(FilterExpression.GreaterThanOrEqual(FilterExpression.Property(PathOf(property)), FilterExpression.Constant(after)), FilterExpression.LessThan(FilterExpression.Property(PathOf(property)), FilterExpression.Constant(before)));
        }
        private static FilterExpression MissingFilter()
        {
            return FilterExpression.OrElse(FilterExpression.Equal(FilterExpression.Property(PathOf("LastSavedAt")), FilterExpression.Constant(typeof(DateTime?), null)), FilterExpression.Equal(FilterExpression.Property(PathOf("LastSavedAt")), FilterExpression.Constant(DateTime.MinValue)));
        }
        private static void TestFactory(string factory)
        {
            entityType = factory.Substring(0, factory.Length - "Factory".Length);
            var baseline = Read(factory, "baseline");
            Observe(new { factory, scenario = "distribution", count = baseline.Count, missingSaved = baseline.Count(r => Missing(r.Saved)), missingDates = baseline.Count(r => Missing(r.Date)), minDate = baseline.Where(r => !Missing(r.Date)).Select(r => r.Date).Min(), maxDate = baseline.Where(r => !Missing(r.Date)).Select(r => r.Date).Max(), distinctSaved = baseline.Where(r => !Missing(r.Saved)).Select(r => r.Saved).Distinct().Count() });
            Compare(factory, "retained-list-incremental", baseline, () => Read(factory, "incremental"));
            Compare(factory, "captured-sorted-keys", baseline, () => Read(factory, "sorted-keys"));
            Compare(factory, "sdk-sort-key-guid", baseline, () => Read(factory, "sdk-sort", sort: "Key.Guid"));
            if (factory == "PurchaseInvoiceFactory" || factory == "SalesInvoiceFactory")
            {
                foreach (bool items in new[] { false, true })
                {
                    var timer = Stopwatch.StartNew();
                    var index = Helpers.Sage50Repository.Instance.GetType().GetMethod("BuildReferenceIndex", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Helpers.Sage50Repository.Instance, new object[] { true, factory == "SalesInvoiceFactory", factory == "PurchaseInvoiceFactory", items, null });
                    Observe(new { factory, scenario = "reference-index", inventoryItems = items, milliseconds = timer.Elapsed.TotalMilliseconds, count = Property(index, "Count") });
                }
                var timerDto = Stopwatch.StartNew();
                IEnumerable bodies = factory == "PurchaseInvoiceFactory" ? (IEnumerable)Helpers.Sage50Repository.Instance.GetBills(Program.CompanyName) : Helpers.Sage50Repository.Instance.GetInvoices(Program.CompanyName);
                var mapped = bodies.Cast<object>().ToList();
                bool idsMatch = baseline.Select(r => r.Id).OrderBy(x => x).SequenceEqual(mapped.Select(b => Convert.ToString(Property(b, "ID"))).OrderBy(x => x));
                Observe(new { factory, scenario = "current-connector-dto", milliseconds = timerDto.Elapsed.TotalMilliseconds, count = mapped.Count, idsMatch });
                if (!idsMatch) failures++;
            }
            var present = baseline.Where(r => !Missing(r.Saved)).Select(r => r.Saved.Value).Distinct().OrderBy(d => d).ToList();
            if (present.Count > 0)
            {
                var after = present[present.Count / 2]; var before = present.Last().AddTicks(1);
                var filter = Window("LastSavedAt", after, before);
                Compare(factory, "saved-window-exclude-missing", baseline.Where(r => !Missing(r.Saved) && r.Saved >= after && r.Saved < before).ToList(), () => Read(factory, "filtered", filter));
                Compare(factory, "saved-window-include-missing", baseline.Where(r => Missing(r.Saved) || (r.Saved >= after && r.Saved < before)).ToList(), () => Read(factory, "filtered", FilterExpression.OrElse(filter, MissingFilter())));
                Compare(factory, "saved-window-widened-days-postfiltered", baseline.Where(r => !Missing(r.Saved) && r.Saved >= after && r.Saved < before).ToList(), () => Read(factory, "filtered-widened", Window("LastSavedAt", after.Date, before.Date.AddDays(1))).Where(r => !Missing(r.Saved) && r.Saved >= after && r.Saved < before).ToList());
                Compare(factory, "saved-whole-day-window", baseline.Where(r => !Missing(r.Saved) && r.Saved >= after.Date && r.Saved < before.Date.AddDays(1)).ToList(), () => Read(factory, "filtered", Window("LastSavedAt", after.Date, before.Date.AddDays(1))));
                Compare(factory, "saved-empty-boundary", new List<Row>(), () => Read(factory, "filtered", Window("LastSavedAt", after, after)));
                Compare(factory, "saved-inclusive-lower", baseline.Where(r => !Missing(r.Saved) && r.Saved >= after).ToList(), () => Read(factory, "filtered", FilterExpression.GreaterThanOrEqual(FilterExpression.Property(PathOf("LastSavedAt")), FilterExpression.Constant(after))));
                Compare(factory, "saved-exclusive-upper", baseline.Where(r => !Missing(r.Saved) && r.Saved < after).ToList(), () => Read(factory, "filtered", FilterExpression.LessThan(FilterExpression.Property(PathOf("LastSavedAt")), FilterExpression.Constant(after))));
                Compare(factory, "saved-nonempty-equal-timestamp", baseline.Where(r => r.Saved == after).ToList(), () => Read(factory, "filtered", FilterExpression.Equal(FilterExpression.Property(PathOf("LastSavedAt")), FilterExpression.Constant(after))));
            }
            Compare(factory, "saved-missing-null", baseline.Where(r => r.Saved == null).ToList(), () => Read(factory, "filtered", FilterExpression.Equal(FilterExpression.Property(PathOf("LastSavedAt")), FilterExpression.Constant(typeof(DateTime?), null))));
            Compare(factory, "saved-missing-min", baseline.Where(r => r.Saved == DateTime.MinValue).ToList(), () => Read(factory, "filtered", FilterExpression.Equal(FilterExpression.Property(PathOf("LastSavedAt")), FilterExpression.Constant(DateTime.MinValue))));
            Compare(factory, "sdk-sort-date", baseline, () => Read(factory, "sdk-sort", sort: "Date"));
            Compare(factory, "saved-missing-only", baseline.Where(r => Missing(r.Saved)).ToList(), () => Read(factory, "filtered", MissingFilter()));
            var months = baseline.Where(r => !Missing(r.Date)).Select(r => new DateTime(r.Date.Value.Year, r.Date.Value.Month, 1)).Distinct().OrderBy(d => d).ToList();
            Compare(factory, "date-window-batches", baseline.Where(r => !Missing(r.Date)).ToList(), () =>
            {
                var result = new List<Row>();
                foreach (var month in months) result.AddRange(Read(factory, "date-batch", Window("Date", month, month.AddMonths(1))));
                return result;
            });
            if (months.Count > 0)
            {
                var date = baseline.First(r => !Missing(r.Date)).Date.Value.Date;
                Compare(factory, "date-empty-boundary", new List<Row>(), () => Read(factory, "filtered", Window("Date", date, date)));
                Compare(factory, "date-single-day", baseline.Where(r => r.Date >= date && r.Date < date.AddDays(1)).ToList(), () => Read(factory, "filtered", Window("Date", date, date.AddDays(1))));
            }
        }
        public static int Run(string path)
        {
            output = path; Directory.CreateDirectory(Path.GetDirectoryName(path));
            const string company = "Bellwether Garden Supply";
            try
            {
                Program.CompanyName = company;
                string result = Helpers.Sage50Repository.Instance.OpenCompany(company);
                if (Helpers.CompanyManager.Instance.CurrentCompany == null) { Observe(new { stage = "open-company", result }); return 2; }
                foreach (var factory in new[] { "PurchaseInvoiceFactory", "SalesInvoiceFactory", "GeneralJournalEntryFactory", "PaymentFactory", "ReceiptFactory" })
                    TestFactory(factory);
                return 0; // Unsupported SDK paths are results, not a crashed benchmark.
            }
            catch (Exception ex) { Observe(new { stage = "benchmark-failed", error = ex.ToString() }); return 1; }
            finally { Helpers.Sage50Connector.Instance.Shutdown(); Save(); }
        }
    }
}
