// Isolated lab diagnostic only. It reads one Sage company and never posts ingest jobs.
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
using System.Threading;

namespace Sage50Connector.Diagnostics
{
    internal static class InvoicePagingBenchmark
    {
        private const int PageSize = 50;
        private static Process process;
        private static Timer sampler;
        private static readonly object sampleLock = new object();
        private static long basePrivate, baseWorking, peakPrivate, peakWorking, peakManaged;
        private static long samples;
        private static string output;
        private static bool dumpPayloads;
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
        };

        private static void Sample(object ignored)
        {
            try
            {
                process.Refresh();
                long p = process.PrivateMemorySize64;
                long w = process.WorkingSet64;
                long g = GC.GetTotalMemory(false);
                lock (sampleLock)
                {
                    samples++;
                    if (p > peakPrivate) peakPrivate = p;
                    if (w > peakWorking) peakWorking = w;
                    if (g > peakManaged) peakManaged = g;
                }
            }
            catch { }
        }

        private static object Property(object source, string name)
        {
            if (source == null) return null;
            return source.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(p => p.Name == name && p.GetIndexParameters().Length == 0)?.GetValue(source, null);
        }

        private static string Sha(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }

        private static string PayloadFingerprint(Models.Rutter.InvoiceBody body)
        {
            return Sha(PayloadJson(body));
        }

        private static string PayloadJson(Models.Rutter.InvoiceBody body)
        {
            return JsonConvert.SerializeObject(body, JsonSettings);
        }

        private static void SavePayload(Models.Rutter.InvoiceBody body)
        {
            if (!dumpPayloads) return;
            File.AppendAllText(output + ".payloads.jsonl", JsonConvert.SerializeObject(new { id = body.ID, payload = PayloadJson(body) }) + Environment.NewLine);
        }

        private static void AddFingerprint(IncrementalHash hash, string id, string fingerprint)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(id + ":" + fingerprint + "\n");
            hash.AppendData(bytes);
        }

        private static IncrementalHash NewHash()
        {
            return IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }

        private static string FinishHash(IncrementalHash hash)
        {
            return BitConverter.ToString(hash.GetHashAndReset()).Replace("-", "").ToLowerInvariant();
        }

        private static object InvokePrivate(MethodInfo method, object target, params object[] args)
        {
            try { return method.Invoke(target, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }

        private static Models.Rutter.InvoiceBody MapInvoice(object invoice, object index, Type repositoryType)
        {
            var body = new Models.Rutter.InvoiceBody
            {
                CustomerID = (string)InvokePrivate(index.GetType().GetMethod("Resolve", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), index, Property(invoice, "CustomerReference")),
                AmountDue = Convert.ToDecimal(Property(invoice, "AmountDue"), CultureInfo.InvariantCulture),
                DiscountAmount = Convert.ToDecimal(Property(invoice, "DiscountAmount"), CultureInfo.InvariantCulture),
                FreightAmount = Convert.ToDecimal(Property(invoice, "FreightAmount"), CultureInfo.InvariantCulture),
                SalesTaxAmount = Convert.ToDecimal(Property(invoice, "SalesTaxAmount"), CultureInfo.InvariantCulture),
                CustomerPurchaseOrderNumber = (string)Property(invoice, "CustomerPurchaseOrderNumber"),
                TermsDescription = (string)Property(invoice, "TermsDescription"),
                ShipVia = (string)Property(invoice, "ShipVia"),
                DropShip = Convert.ToBoolean(Property(invoice, "DropShip")),
                CustomerNote = (string)Property(invoice, "CustomerNote"),
                InternalNote = (string)Property(invoice, "InternalNote"),
                StatementNote = (string)Property(invoice, "StatementNote"),
            };

            string[] dateProperties = { "DateDue", "DiscountDate", "ShipDate" };
            string[] bodyProperties = { "DateDue", "DiscountDate", "ShipDate" };
            MethodInfo dateOnly = repositoryType.GetMethod("DateOnly", BindingFlags.Static | BindingFlags.NonPublic);
            for (int i = 0; i < dateProperties.Length; i++)
            {
                object value = Property(invoice, dateProperties[i]);
                string formatted = (string)InvokePrivate(dateOnly, null, value);
                typeof(Models.Rutter.InvoiceBody).GetProperty(bodyProperties[i]).SetValue(body, formatted, null);
            }

            var resolve = index.GetType().GetMethod("Resolve", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            body.FreightAccountID = (string)InvokePrivate(resolve, index, Property(invoice, "FreightAccountReference"));
            body.SalesRepresentativeGuid = GuidOf(Property(invoice, "SalesRepresentativeReference"));
            body.SalesTaxCodeGuid = GuidOf(Property(invoice, "SalesTaxCodeReference"));
            body.ShipToAddress = (Models.Rutter.SageAddressBody)InvokePrivate(repositoryType.GetMethod("MapAddress", BindingFlags.Static | BindingFlags.NonPublic), null, Property(invoice, "ShipToAddress"));
            InvokePrivate(repositoryType.GetMethod("MapTransactionHeader", BindingFlags.Static | BindingFlags.NonPublic), null, body, invoice, index);

            MapCollection(invoice, "ApplyToSalesLines", "sales", "SalesInvoiceSalesLine", body, index, repositoryType);
            MapCollection(invoice, "ApplyToSalesOrderLines", "salesOrder", "SalesInvoiceSalesOrderLine", body, index, repositoryType);
            MapCollection(invoice, "ApplyToProposalLines", "proposal", "SalesInvoiceProposalLine", body, index, repositoryType);
            MapCollection(invoice, "WithholdRetainageLines", "retainage", "SalesInvoiceRetainageLine", body, index, repositoryType);
            return body;
        }

        private static string GuidOf(object reference)
        {
            return Helpers.ReferenceIndex.GuidOf(reference as EntityReference);
        }

        private static void MapCollection(object invoice, string collectionName, string lineType, string sdkLineTypeName,
            Models.Rutter.InvoiceBody body, object index, Type repositoryType)
        {
            var collection = Property(invoice, collectionName) as IEnumerable;
            if (collection == null) return;
            MethodInfo isReal = repositoryType.GetMethod("IsRealLine", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (object line in collection)
            {
                if (line == null || !(bool)InvokePrivate(isReal, null, line)) continue;
                Models.Rutter.InvoiceLineBody mapped;
                if (lineType == "retainage")
                {
                    var method = repositoryType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                        .Single(m => m.Name == "MakeBaseLine").MakeGenericMethod(typeof(Models.Rutter.InvoiceLineBody));
                    mapped = (Models.Rutter.InvoiceLineBody)InvokePrivate(method, null, line, lineType, Property(line, "JobReference"), index);
                }
                else
                {
                    var method = repositoryType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                        .Single(m => m.Name == "MakeItemLine").MakeGenericMethod(typeof(Models.Rutter.InvoiceLineBody));
                    mapped = (Models.Rutter.InvoiceLineBody)InvokePrivate(method, null, line, lineType,
                        Property(line, "Quantity"), Property(line, "UnitPrice"), Property(line, "InventoryItemReference"),
                        Property(line, "JobReference"), index);
                }
                body.Lines.Add(mapped);
            }
        }

        private static object ReferenceIndex(Type repositoryType)
        {
            MethodInfo build = repositoryType.GetMethod("BuildReferenceIndex", BindingFlags.Instance | BindingFlags.NonPublic);
            return InvokePrivate(build, Helpers.Sage50Repository.Instance, true, true, false, true, null);
        }

        private static object ReadIndex(object list, object key)
        {
            PropertyInfo item = list.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .First(p => p.Name == "Item" && p.GetIndexParameters().Length == 1 && p.GetIndexParameters()[0].ParameterType.IsInstanceOfType(key));
            return item.GetValue(list, new[] { key });
        }

        private static string KeyGuid(object key)
        {
            object value = Property(key, "Guid");
            return value == null ? key.ToString() : value.ToString();
        }

        private static List<object> LoadDateWindow(object factory, DateTime start, DateTime end)
        {
            object list = factory.GetType().GetMethod("List", Type.EmptyTypes).Invoke(factory, null);
            var modifiers = LoadModifiers.Create();
            modifiers.Filters = FilterExpression.AndAlso(
                FilterExpression.GreaterThanOrEqual(FilterExpression.Property("SalesInvoice.Date"), FilterExpression.Constant(start)),
                FilterExpression.LessThan(FilterExpression.Property("SalesInvoice.Date"), FilterExpression.Constant(end)));
            InvokePrivate(list.GetType().GetMethod("Load", new[] { typeof(LoadModifiers) }), list, modifiers);
            return ((IEnumerable)Property(list, "Keys")).Cast<object>().OrderBy(KeyGuid, StringComparer.Ordinal)
                .Select(key => ReadIndex(list, key)).ToList();
        }

        private static int Run(string company, string mode, string outputPath, DateTime? rangeStart = null, DateTime? rangeEnd = null)
        {
            output = outputPath;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
            process = Process.GetCurrentProcess();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            process.Refresh(); basePrivate = process.PrivateMemorySize64; baseWorking = process.WorkingSet64;
            peakPrivate = basePrivate; peakWorking = baseWorking; peakManaged = GC.GetTotalMemory(false);
            sampler = new Timer(Sample, null, 0, 100);

            Stopwatch watch = null;
            try
            {
                watch = Stopwatch.StartNew();
                Program.CompanyName = company;
                string open = Helpers.Sage50Repository.Instance.OpenCompany(company);
                if (Helpers.CompanyManager.Instance.CurrentCompany == null)
                {
                    File.WriteAllText(output, JsonConvert.SerializeObject(new { status = "company-open-failed", company, result = open }, Formatting.Indented));
                    return 2;
                }
                Type repositoryType = Helpers.Sage50Repository.Instance.GetType();
                object index = null;
                object factory = null;
                object list = null;
                double loadMs = 0;
                if (mode == "retained-keys" || mode == "date-windows")
                {
                    index = ReferenceIndex(repositoryType);
                    var factories = Helpers.CompanyManager.Instance.CurrentCompany.Factories;
                    factory = factories.GetType().GetProperty("SalesInvoiceFactory").GetValue(factories, null);
                    if (mode == "retained-keys")
                    {
                        list = factory.GetType().GetMethod("List", Type.EmptyTypes).Invoke(factory, null);
                        var loadWatch = Stopwatch.StartNew(); InvokePrivate(list.GetType().GetMethod("Load", Type.EmptyTypes), list);
                        loadMs = loadWatch.Elapsed.TotalMilliseconds;
                    }
                }

                List<Models.Rutter.InvoiceBody> all = null;
                int sourceCount = 0, matchedCount = 0, pages = 0, lineCount = 0, missingSaved = 0;
                long maxPayloadBytes = 0, serializedBytes = 0;
                double firstPageMs = 0;
                using (var hash = NewHash())
                {
                    if (mode == "full-dto")
                    {
                        var mapWatch = Stopwatch.StartNew();
                        all = Helpers.Sage50Repository.Instance.GetInvoices(company);
                        matchedCount = all.Count;
                        all = all.OrderBy(x => x.ID, StringComparer.Ordinal).ToList();
                        for (int offset = 0; offset < all.Count; offset += PageSize)
                        {
                            var page = all.Skip(offset).Take(PageSize).ToList();
                            string json = JsonConvert.SerializeObject(new { data = page }, JsonSettings);
                            long bytes = Encoding.UTF8.GetByteCount(json); serializedBytes += bytes; maxPayloadBytes = Math.Max(bytes, maxPayloadBytes); pages++;
                            Sample(null);
                            if (pages == 1) firstPageMs = watch.Elapsed.TotalMilliseconds;
                            foreach (var row in page) { lineCount += row.Lines.Count; if (string.IsNullOrEmpty(row.LastSavedAt)) missingSaved++; SavePayload(row); AddFingerprint(hash, row.ID, PayloadFingerprint(row)); }
                        }
                    }
                    else if (mode == "retained-keys")
                    {
                        var keysWatch = Stopwatch.StartNew();
                        List<object> keys;
                        keys = ((IEnumerable)Property(list, "Keys")).Cast<object>().OrderBy(KeyGuid, StringComparer.Ordinal).ToList();
                        double keyCaptureMs = keysWatch.Elapsed.TotalMilliseconds;
                        sourceCount = keys.Count;
                        var page = new List<Models.Rutter.InvoiceBody>(PageSize);
                        foreach (object key in keys)
                        {
                            object invoice = ReadIndex(list, key);
                            object saved = Property(invoice, "LastSavedAt");
                            if (saved == null || (DateTime)saved == default(DateTime)) missingSaved++;
                            matchedCount++;
                            var body = MapInvoice(invoice, index, repositoryType);
                            page.Add(body); lineCount += body.Lines.Count;
                            SavePayload(body);
                            AddFingerprint(hash, body.ID, PayloadFingerprint(body));
                            if (page.Count == PageSize || matchedCount == keys.Count)
                            {
                                string json = JsonConvert.SerializeObject(new { data = page }, JsonSettings);
                                long bytes = Encoding.UTF8.GetByteCount(json); serializedBytes += bytes; maxPayloadBytes = Math.Max(bytes, maxPayloadBytes); pages++;
                                Sample(null);
                                if (pages == 1) firstPageMs = watch.Elapsed.TotalMilliseconds;
                                page.Clear();
                            }
                        }
                        if (keys.Count == 0) firstPageMs = watch.Elapsed.TotalMilliseconds;
                        var summary = new { mode, company, sourceCount, matchedCount, pages, lineCount, missingSavedAt = missingSaved, loadMs, keyCaptureMs, firstPageMs, totalMs = watch.Elapsed.TotalMilliseconds, serializedBytes, maxPayloadBytes };
                        File.WriteAllText(output, JsonConvert.SerializeObject(new { status = "completed", summary, fingerprint = FinishHash(hash) }, Formatting.Indented));
                        return 0;
                    }
                    else if (mode == "date-windows")
                    {
                        // Explicit daily half-open windows include exact day boundaries in the
                        // requested range without relying on the company's current fiscal year.
                        // A temporary list is released after each window; memory samples reveal
                        // whether the SDK also releases its native objects during that lifecycle.
                        var fingerprints = new List<KeyValuePair<string, string>>();
                        var page = new List<Models.Rutter.InvoiceBody>(PageSize);
                        int windowCount = 0;
                        if (!rangeStart.HasValue || !rangeEnd.HasValue || rangeEnd.Value <= rangeStart.Value)
                            throw new ArgumentException("date-windows requires an explicit nonempty half-open start/end range.");
                        DateTime lower = rangeStart.Value.Date;
                        DateTime upper = rangeEnd.Value.Date;
                        for (DateTime start = lower; start < upper; start = start.AddDays(1))
                        {
                            DateTime end = start.AddDays(1);
                            List<object> windowRecords = LoadDateWindow(factory, start, end);
                            windowCount++;
                            sourceCount += windowRecords.Count;
                            foreach (object invoice in windowRecords)
                            {
                                object saved = Property(invoice, "LastSavedAt");
                                if (saved == null || (DateTime)saved == default(DateTime)) missingSaved++;
                                var body = MapInvoice(invoice, index, repositoryType);
                                matchedCount++; lineCount += body.Lines.Count;
                                SavePayload(body);
                                fingerprints.Add(new KeyValuePair<string, string>(body.ID, PayloadFingerprint(body)));
                                page.Add(body);
                                if (page.Count == PageSize)
                                {
                                    string json = JsonConvert.SerializeObject(new { data = page }, JsonSettings);
                                    long bytes = Encoding.UTF8.GetByteCount(json); serializedBytes += bytes; maxPayloadBytes = Math.Max(bytes, maxPayloadBytes); pages++;
                                    Sample(null);
                                    if (pages == 1) firstPageMs = watch.Elapsed.TotalMilliseconds;
                                    page.Clear();
                                }
                            }
                            windowRecords.Clear();
                            GC.Collect(); GC.WaitForPendingFinalizers();
                        }
                        if (page.Count > 0)
                        {
                            string json = JsonConvert.SerializeObject(new { data = page }, JsonSettings);
                            long bytes = Encoding.UTF8.GetByteCount(json); serializedBytes += bytes; maxPayloadBytes = Math.Max(bytes, maxPayloadBytes); pages++;
                            Sample(null);
                            if (pages == 1) firstPageMs = watch.Elapsed.TotalMilliseconds;
                        }
                        if (pages == 0) firstPageMs = watch.Elapsed.TotalMilliseconds;
                        foreach (var entry in fingerprints.OrderBy(x => x.Key, StringComparer.Ordinal)) AddFingerprint(hash, entry.Key, entry.Value);
                        var summary = new { mode, company, sourceCount, matchedCount, pages, lineCount, missingSavedAt = missingSaved, windowCount, loadMs, firstPageMs, totalMs = watch.Elapsed.TotalMilliseconds, serializedBytes, maxPayloadBytes };
                        File.WriteAllText(output, JsonConvert.SerializeObject(new { status = "completed", summary, fingerprint = FinishHash(hash) }, Formatting.Indented));
                        return 0;
                    }
                    else if (mode == "production-windows")
                    {
                        var fingerprints = new List<KeyValuePair<string, string>>();
                        using (var reader = Helpers.Sage50Repository.Instance.OpenInvoiceReader(company, null, null, true, null, null))
                        {
                            string cursor = null;
                            do
                            {
                                var page = reader.ReadPage(cursor, PageSize);
                                if (!ReferenceEquals(page, reader.ReadPage(cursor, PageSize))) throw new Exception("Unacknowledged page changed");
                                string json = JsonConvert.SerializeObject(new { data = page.Records }, JsonSettings);
                                long bytes = Encoding.UTF8.GetByteCount(json); serializedBytes += bytes; maxPayloadBytes = Math.Max(bytes, maxPayloadBytes); pages++;
                                Sample(null);
                                if (pages == 1) firstPageMs = watch.Elapsed.TotalMilliseconds;
                                foreach (var row in page.Records)
                                {
                                    matchedCount++; lineCount += row.Lines.Count;
                                    if (string.IsNullOrEmpty(row.LastSavedAt)) missingSaved++;
                                    SavePayload(row); fingerprints.Add(new KeyValuePair<string, string>(row.ID, PayloadFingerprint(row)));
                                }
                                cursor = page.NextCursor; reader.Accept(page);
                            } while (cursor != null);
                        }
                        foreach (var entry in fingerprints.OrderBy(x => x.Key, StringComparer.Ordinal)) AddFingerprint(hash, entry.Key, entry.Value);
                        File.WriteAllText(output, JsonConvert.SerializeObject(new { status = "completed", mode, company, matchedCount, pages, lineCount, missingSavedAt = missingSaved, firstPageMs, totalMs = watch.Elapsed.TotalMilliseconds, serializedBytes, maxPayloadBytes, fingerprint = FinishHash(hash) }, Formatting.Indented));
                        return 0;
                    }
                    else throw new ArgumentException("Mode must be full-dto, retained-keys, or date-windows.");

                    sourceCount = all == null ? 0 : all.Count;
                    string digest = FinishHash(hash);
                    var result = new
                    {
                        status = "completed", mode, company, sourceCount, matchedCount, pages, lineCount,
                        loadMs, firstPageMs, totalMs = watch.Elapsed.TotalMilliseconds,
                        serializedBytes, maxPayloadBytes, fingerprint = digest
                    };
                    File.WriteAllText(output, JsonConvert.SerializeObject(result, Formatting.Indented));
                }
                return 0;
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
                File.WriteAllText(output, JsonConvert.SerializeObject(new { status = "failed", company, mode, error = ex.ToString() }, Formatting.Indented));
                return 1;
            }
            finally
            {
                sampler?.Dispose(); Sample(null);
                lock (sampleLock)
                {
                    string existing = File.Exists(output) ? File.ReadAllText(output) : "{}";
                    var parsed = JsonConvert.DeserializeObject<Dictionary<string, object>>(existing) ?? new Dictionary<string, object>();
                    parsed["memory"] = new { basePrivateBytes = basePrivate, peakPrivateBytes = peakPrivate, privateDeltaBytes = peakPrivate - basePrivate, baseWorkingSetBytes = baseWorking, peakWorkingSetBytes = peakWorking, workingSetDeltaBytes = peakWorking - baseWorking, peakManagedBytes = peakManaged, sampleCount = samples };
                    File.WriteAllText(output, JsonConvert.SerializeObject(parsed, Formatting.Indented));
                }
                Helpers.Sage50Connector.Instance.Shutdown();
            }
        }

        public static int Run(string[] args)
        {
            dumpPayloads = args.Length > 0 && args[args.Length - 1] == "--dump-payloads";
            if (args.Length == 6 && args[2] == "date-windows")
                return Run(args[1], args[2], args[5], DateTime.Parse(args[3], CultureInfo.InvariantCulture), DateTime.Parse(args[4], CultureInfo.InvariantCulture));
            if (args.Length == 7 && args[2] == "date-windows" && dumpPayloads)
                return Run(args[1], args[2], args[5], DateTime.Parse(args[3], CultureInfo.InvariantCulture), DateTime.Parse(args[4], CultureInfo.InvariantCulture));
            if (args.Length == 5 && dumpPayloads)
                return Run(args[1], args[2], args[3]);
            if (args.Length != 4) throw new ArgumentException("Usage: --benchmark-invoices <company> <full-dto|retained-keys> <output.json> OR --benchmark-invoices <company> date-windows <start-inclusive> <end-exclusive> <output.json>");
            return Run(args[1], args[2], args[3]);
        }
    }
}
