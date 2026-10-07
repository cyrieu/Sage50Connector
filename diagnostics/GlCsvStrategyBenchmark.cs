using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Sage50Connector.Helpers;
using Sage50Connector.Models.Rutter;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Sage50Connector.Diagnostics
{
    /// <summary>
    /// Offline, COM-free comparison of GL CSV materialization strategies.
    /// One mode is run per process so process/private-memory peaks are comparable.
    /// </summary>
    internal static class GlCsvStrategyBenchmark
    {
        private const int PageSize = 50;

        private static readonly JsonSerializerSettings CanonicalSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Culture = CultureInfo.InvariantCulture,
            NullValueHandling = NullValueHandling.Include,
            Formatting = Formatting.None
        };

        internal static int Run(string[] args)
        {
            if (args == null || (args.Length != 4 && args.Length != 6) || args[0] != "--benchmark-gl-csv")
            {
                Console.Error.WriteLine("Usage: --benchmark-gl-csv <csvPath> <baseline|stream-dictionary|stream-groups|disk-spool> <resultPath> [startDate endDate]");
                return 2;
            }

            string csvPath = Path.GetFullPath(args[1]);
            string mode = args[2];
            string resultPath = Path.GetFullPath(args[3]);
            DateTime? startDate = ParseBound(args.Length == 6 ? args[4] : null);
            DateTime? endDate = ParseBound(args.Length == 6 ? args[5] : null);

            if (!File.Exists(csvPath)) throw new FileNotFoundException("GL CSV input was not found.", csvPath);
            if (startDate.HasValue && endDate.HasValue && endDate.Value <= startDate.Value)
                throw new ArgumentException("endDate must be later than startDate.");
            if (mode != "full" && mode != "baseline" && mode != "group-dictionary"
                && mode != "stream-dictionary" && mode != "stream-groups"
                && mode != "contiguous-spool" && mode != "disk-spool")
                throw new ArgumentException("Unknown GL CSV strategy mode: " + mode);

            var summary = new BenchmarkSummary
            {
                mode = mode,
                resultPath = resultPath,
                inputBytes = new FileInfo(csvPath).Length,
                startDate = startDate.HasValue ? startDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                endDateExclusive = endDate.HasValue ? endDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                pageSize = PageSize,
                status = "running"
            };

            using (var sampler = new MemorySampler())
            {
                var timer = Stopwatch.StartNew();
                try
                {
                    if (mode == "full" || mode == "baseline") RunFull(csvPath, startDate, endDate, summary, timer);
                    else if (mode == "group-dictionary" || mode == "stream-dictionary") RunGroupDictionary(csvPath, startDate, endDate, summary, timer);
                    else if (mode == "stream-groups") RunContiguousGroups(csvPath, startDate, endDate, summary, timer);
                    else RunContiguousSpool(csvPath, startDate, endDate, summary, timer);

                    summary.status = "completed";
                    summary.exitCode = 0;
                }
                catch (InvalidDataException ex)
                {
                    summary.status = "candidate-rejected";
                    summary.error = ex.Message;
                    summary.exitCode = 3;
                }
                catch (Exception ex)
                {
                    summary.status = "failed";
                    summary.error = ex.GetType().Name + ": " + ex.Message;
                    summary.exitCode = 2;
                }
                finally
                {
                    timer.Stop();
                    summary.elapsedMs = timer.Elapsed.TotalMilliseconds;
                    summary.managedStartBytes = sampler.StartManagedBytes;
                    summary.managedPeakBytes = sampler.PeakManagedBytes;
                    summary.privateStartBytes = sampler.StartPrivateBytes;
                    summary.privatePeakBytes = sampler.PeakPrivateBytes;
                    summary.privateDeltaBytes = Math.Max(0, sampler.PeakPrivateBytes - sampler.StartPrivateBytes);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(resultPath));
            File.WriteAllText(resultPath, JsonConvert.SerializeObject(summary, Formatting.Indented));
            Console.WriteLine(JsonConvert.SerializeObject(summary, Formatting.Indented));
            return summary.exitCode;
        }

        internal static int RunGenerate(string[] args)
        {
            if (args == null || args.Length != 4 || args[0] != "--generate-gl-csv")
            {
                Console.Error.WriteLine("Usage: --generate-gl-csv <sourceCsv> <outputCsv> <copies>");
                return 2;
            }

            string sourcePath = Path.GetFullPath(args[1]);
            string outputPath = Path.GetFullPath(args[2]);
            int copies;
            if (!int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out copies) || copies < 1)
                throw new ArgumentException("copies must be a positive integer.");
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("Source GL CSV was not found.", sourcePath);
            if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The stress output must be a separate file from the source CSV.");

            // Source exports are intentionally small. Keeping this baseline in memory lets
            // us preserve every original field byte-for-value while only rewriting the
            // posting order and GUID columns in stress copies.
            List<string[]> records = GeneralLedgerExporter.ParseCsvRecords(sourcePath);
            if (records.Count < 2) throw new InvalidDataException("Source GL CSV has no data rows.");
            string[] headers = records[0];
            Dictionary<string, int> headerMap = GeneralLedgerExporter.BuildHeaderMap(headers);
            int postOrderColumn = FindColumn(headerMap, "JournalPostOrder", "Journal Post Order", "Journal Hdr Postorder");
            int rowIndexColumn = FindColumn(headerMap, "JournalRowIndex", "Journal Row Index");
            int guidColumn = FindColumn(headerMap, "GUID", "GL GUID");
            var tracker = new OrderTracker();
            long maxOrder = 0;
            int dataRows = 0;
            var sourceRows = new List<string[]>(records.Count - 1);
            for (int i = 1; i < records.Count; i++)
            {
                string[] fields = records[i];
                if (fields.Length == 1 && string.IsNullOrEmpty(fields[0])) continue;
                GlTransactionLineBody line = GeneralLedgerExporter.MapRow(fields, headerMap, i + 1);
                tracker.Observe(line);
                if (line.JournalPostOrder > maxOrder) maxOrder = line.JournalPostOrder;
                sourceRows.Add(fields);
                dataRows++;
            }
            if (!tracker.PostOrderMonotonic || !tracker.PostOrderContiguous)
                throw new InvalidDataException("Source JournalPostOrder values are not monotonic and contiguous; stress copies would invalidate the source ordering evidence.");
            if (maxOrder <= 0) throw new InvalidDataException("Source has no positive JournalPostOrder values.");
            long stride = checked(maxOrder + 1);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            long outputRows = 0;
            using (var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false)))
            {
                WriteCsvRecord(writer, headers);
                for (int copy = 0; copy < copies; copy++)
                {
                    long offset = checked(stride * copy);
                    for (int rowOrdinal = 0; rowOrdinal < sourceRows.Count; rowOrdinal++)
                    {
                        string[] sourceFields = sourceRows[rowOrdinal];
                        var fields = (string[])sourceFields.Clone();
                        if (copy > 0)
                        {
                            long originalOrder;
                            if (!long.TryParse(fields[postOrderColumn], NumberStyles.Integer, CultureInfo.InvariantCulture, out originalOrder))
                                throw new InvalidDataException("The source post order became invalid during stress generation.");
                            fields[postOrderColumn] = checked(originalOrder + offset).ToString(CultureInfo.InvariantCulture);
                            string identitySeed = (fields[guidColumn] ?? string.Empty) + "|"
                                + fields[postOrderColumn] + "|" + fields[rowIndexColumn] + "|"
                                + rowOrdinal.ToString(CultureInfo.InvariantCulture);
                            fields[guidColumn] = DeterministicGuid(identitySeed, copy);
                        }
                        WriteCsvRecord(writer, fields);
                        outputRows++;
                    }
                }
            }

            var result = new
            {
                status = "completed",
                sourceRows = dataRows,
                copies,
                generatedRows = outputRows,
                sourceGroups = sourceRows.Select(row => long.Parse(row[postOrderColumn], CultureInfo.InvariantCulture)).Distinct().Count(),
                sourceMaxPostOrder = maxOrder,
                generatedMaxPostOrder = checked(maxOrder + stride * (copies - 1)),
                journalPostOrderMonotonic = true,
                sourceLineOrderingPreserved = tracker.LineOrderSorted,
                outputBytes = new FileInfo(outputPath).Length
            };
            string resultJson = JsonConvert.SerializeObject(result, Formatting.Indented);
            File.WriteAllText(outputPath + ".generation.json", resultJson);
            Console.WriteLine(resultJson);
            return 0;
        }

        internal static int RunGenerateAdverse(string[] args)
        {
            if (args == null || args.Length != 3 || args[0] != "--generate-gl-csv-adverse")
            {
                Console.Error.WriteLine("Usage: --generate-gl-csv-adverse <sourceCsv> <outputDirectory>");
                return 2;
            }

            string sourcePath = Path.GetFullPath(args[1]);
            string outputDirectory = Path.GetFullPath(args[2]);
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("Source GL CSV was not found.", sourcePath);
            Directory.CreateDirectory(outputDirectory);

            List<string[]> records = GeneralLedgerExporter.ParseCsvRecords(sourcePath);
            if (records.Count < 2) throw new InvalidDataException("Source GL CSV has no data rows.");
            string[] headers = records[0];
            Dictionary<string, int> map = GeneralLedgerExporter.BuildHeaderMap(headers);
            int postColumn = FindColumn(map, "JournalPostOrder", "Journal Post Order", "Journal Hdr Postorder");
            int lineColumn = FindColumn(map, "JournalRowIndex", "Journal Row Index");
            int guidColumn = FindColumn(map, "GUID", "GL GUID");
            int dateColumn = FindColumn(map, "Date", "Transaction Date");
            int includeColumn = FindColumn(map, "IncludeInGL", "Include In GL", "Include In GL?");
            int descriptionColumn = FindColumn(map, "Description", "Trans Description");

            var groups = new SortedDictionary<long, List<string[]>>();
            var tracker = new OrderTracker();
            long maxOrder = 0;
            int sourceRows = 0;
            for (int i = 1; i < records.Count; i++)
            {
                string[] fields = records[i];
                if (fields.Length == 1 && string.IsNullOrEmpty(fields[0])) continue;
                GlTransactionLineBody line = GeneralLedgerExporter.MapRow(fields, map, i + 1);
                tracker.Observe(line);
                if (!groups.TryGetValue(line.JournalPostOrder, out List<string[]> group))
                {
                    group = new List<string[]>();
                    groups.Add(line.JournalPostOrder, group);
                }
                group.Add(fields);
                if (line.JournalPostOrder > maxOrder) maxOrder = line.JournalPostOrder;
                sourceRows++;
            }
            if (groups.Count < 2) throw new InvalidDataException("At least two source posting groups are required for ordering fixtures.");

            var resultFiles = new List<object>();

            // Reverse complete groups while preserving each group's raw line order.
            // The full grouping baseline should remain equivalent; contiguous streaming must reject.
            string shuffledPath = Path.Combine(outputDirectory, "shuffled-groups.csv");
            WriteGroupedFixture(shuffledPath, headers, groups.Reverse().SelectMany(pair => pair.Value));
            resultFiles.Add(new { file = Path.GetFileName(shuffledPath), expected = "full and dictionary modes preserve hashes; sorted-stream modes reject decreasing JournalPostOrder" });

            // Split a multi-line posting group around the next group to force a reappearance.
            long splitOrder = groups.FirstOrDefault(pair => pair.Value.Count > 1 && groups.Keys.Any(key => key > pair.Key)).Key;
            if (splitOrder > 0)
            {
                long betweenOrder = groups.Keys.First(key => key > splitOrder);
                IEnumerable<string[]> nonContiguousRows = groups
                    .Where(pair => pair.Key < splitOrder)
                    .SelectMany(pair => pair.Value)
                    .Concat(groups[splitOrder].Take(1))
                    .Concat(groups[betweenOrder])
                    .Concat(groups[splitOrder].Skip(1))
                    .Concat(groups.Where(pair => pair.Key > splitOrder && pair.Key != betweenOrder).SelectMany(pair => pair.Value));
                string nonContiguousPath = Path.Combine(outputDirectory, "noncontiguous-group.csv");
                WriteGroupedFixture(nonContiguousPath, headers, nonContiguousRows);
                resultFiles.Add(new { file = Path.GetFileName(nonContiguousPath), expected = "full and dictionary modes preserve hashes; sorted-stream modes reject a repeated group" });
            }

            // Reverse one group's raw lines. Grouping must restore JournalRowIndex/ID order.
            KeyValuePair<long, List<string[]>> reorderGroup = groups.FirstOrDefault(pair => pair.Value.Count > 1);
            if (reorderGroup.Value != null)
            {
                string lineOrderPath = Path.Combine(outputDirectory, "line-order-reversed.csv");
                IEnumerable<string[]> rows = groups.SelectMany(pair => pair.Key == reorderGroup.Key
                    ? pair.Value.AsEnumerable().Reverse()
                    : pair.Value);
                WriteGroupedFixture(lineOrderPath, headers, rows);
                resultFiles.Add(new { file = Path.GetFileName(lineOrderPath), expected = "all modes preserve hashes after group line sorting; inputLineOrderSorted is false" });
            }

            // Add one included row on the inclusive lower boundary, one included row on
            // the exclusive upper boundary, and a non-posting row on the lower boundary.
            string dateBoundaryPath = Path.Combine(outputDirectory, "date-include-boundaries.csv");
            var boundaryRows = groups.SelectMany(pair => pair.Value).ToList();
            var boundaryExpected = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                string[] fields = (string[])groups.First().Value[0].Clone();
                long order = checked(maxOrder + i + 1);
                fields[postColumn] = order.ToString(CultureInfo.InvariantCulture);
                fields[guidColumn] = DeterministicGuid("boundary|" + order.ToString(CultureInfo.InvariantCulture), i + 1);
                fields[dateColumn] = i == 1 ? "2026-09-01" : "2026-08-01";
                fields[includeColumn] = i == 2 ? "False" : "True";
                fields[lineColumn] = "0";
                boundaryRows.Add(fields);
                if (i == 0) boundaryExpected.Add("gl:" + order.ToString(CultureInfo.InvariantCulture));
            }
            WriteGroupedFixture(dateBoundaryPath, headers, boundaryRows);
            resultFiles.Add(new { file = Path.GetFileName(dateBoundaryPath), startInclusive = "2026-08-01", endExclusive = "2026-09-01", includedBoundaryId = boundaryExpected[0], expected = "lower-bound clone included; upper-bound clone and IncludeInGL=false clone excluded" });

            string multilinePath = Path.Combine(outputDirectory, "multiline-quoted-field.csv");
            var multilineRows = groups.SelectMany(pair => pair.Value).ToList();
            string[] multiline = (string[])groups.First().Value[0].Clone();
            long multilineOrder = checked(maxOrder + 4);
            multiline[postColumn] = multilineOrder.ToString(CultureInfo.InvariantCulture);
            multiline[guidColumn] = DeterministicGuid("multiline|" + multilineOrder.ToString(CultureInfo.InvariantCulture), 4);
            multiline[descriptionColumn] = "Quoted, multiline \"GL\" description\r\nsecond line";
            multiline[dateColumn] = "2026-08-15";
            multiline[includeColumn] = "True";
            multilineRows.Add(multiline);
            WriteGroupedFixture(multilinePath, headers, multilineRows);
            resultFiles.Add(new { file = Path.GetFileName(multilinePath), expected = "RFC4180 parser preserves comma, doubled quotes, and embedded CRLF" });

            var result = new
            {
                status = "completed",
                sourceRows,
                sourceGroups = groups.Count,
                sourcePostOrderMonotonic = tracker.PostOrderMonotonic,
                sourcePostOrderContiguous = tracker.PostOrderContiguous,
                sourceLineOrderSorted = tracker.LineOrderSorted,
                fixtureFiles = resultFiles
            };
            string resultJson = JsonConvert.SerializeObject(result, Formatting.Indented);
            File.WriteAllText(Path.Combine(outputDirectory, "fixtures.json"), resultJson);
            Console.WriteLine(resultJson);
            return 0;
        }

        private static void WriteGroupedFixture(string path, string[] headers, IEnumerable<string[]> rows)
        {
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                WriteCsvRecord(writer, headers);
                foreach (string[] row in rows) WriteCsvRecord(writer, row);
            }
        }

        private static int FindColumn(Dictionary<string, int> map, params string[] aliases)
        {
            foreach (string alias in aliases)
            {
                string normalized = GeneralLedgerExporter.NormalizeFieldName(alias);
                int index;
                if (map.TryGetValue(normalized, out index)) return index;
            }
            throw new InvalidDataException("The source CSV is missing a required stress-generator column.");
        }

        private static string DeterministicGuid(string source, int copy)
        {
            byte[] input = Encoding.UTF8.GetBytes("sage50-gl-stress|" + copy.ToString(CultureInfo.InvariantCulture) + "|" + source);
            byte[] digest;
            using (SHA256 sha = SHA256.Create()) digest = sha.ComputeHash(input);
            byte[] guidBytes = new byte[16];
            Array.Copy(digest, guidBytes, guidBytes.Length);
            // Use the conventional RFC 4122 version/variant bits for a parseable GUID.
            guidBytes[7] = (byte)((guidBytes[7] & 0x0f) | 0x50);
            guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
            return new Guid(guidBytes).ToString("D");
        }

        private static void WriteCsvRecord(TextWriter writer, string[] fields)
        {
            for (int i = 0; i < fields.Length; i++)
            {
                if (i != 0) writer.Write(',');
                string value = fields[i] ?? string.Empty;
                bool quote = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                    || (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[value.Length - 1])));
                if (!quote)
                {
                    writer.Write(value);
                    continue;
                }
                writer.Write('"');
                writer.Write(value.Replace("\"", "\"\""));
                writer.Write('"');
            }
            writer.Write("\r\n");
        }

        private static void RunFull(string csvPath, DateTime? start, DateTime? end, BenchmarkSummary summary, Stopwatch timer)
        {
            List<string[]> records = GeneralLedgerExporter.ParseCsvRecords(csvPath);
            summary.rawRows = Math.Max(0, records.Count - 1);
            if (records.Count == 0)
            {
                FinishTransactions(new List<GlTransactionBody>(), summary, timer, null);
                return;
            }

            Dictionary<string, int> map = GeneralLedgerExporter.BuildHeaderMap(records[0]);
            var tracker = new OrderTracker();
            var rows = new List<GlTransactionLineBody>(Math.Max(0, records.Count - 1));
            for (int i = 1; i < records.Count; i++)
            {
                string[] fields = records[i];
                if (fields.Length == 1 && string.IsNullOrEmpty(fields[0])) continue;
                GlTransactionLineBody line = GeneralLedgerExporter.MapRow(fields, map, i + 1);
                tracker.Observe(line);
                rows.Add(line);
            }
            CopyTracker(summary, tracker);

            List<GlTransactionLineBody> filtered = ApplyDateWindow(rows, start, end, summary);
            List<GlTransactionLineBody> posting = filtered.Where(row => row.IncludeInGL).ToList();
            summary.includedRows = posting.Count;
            summary.nonGlRows = filtered.Count - posting.Count;
            List<GlTransactionBody> transactions = GeneralLedgerExporter.GroupByPostingOrder(posting);
            FinishTransactions(transactions, summary, timer, null);
        }

        private static void RunGroupDictionary(string csvPath, DateTime? start, DateTime? end, BenchmarkSummary summary, Stopwatch timer)
        {
            Dictionary<string, int> map = null;
            var groups = new Dictionary<long, List<GlTransactionLineBody>>();
            var tracker = new OrderTracker();

            ReadRows(csvPath, (fields, rowNumber, headerMap) =>
            {
                if (map == null) map = headerMap;
                GlTransactionLineBody line = GeneralLedgerExporter.MapRow(fields, map, rowNumber);
                tracker.Observe(line);
                CopyTracker(summary, tracker);
                if (!InDateWindow(line, start, end))
                {
                    summary.dateFilteredRows++;
                    return;
                }
                if (!line.IncludeInGL)
                {
                    summary.nonGlRows++;
                    return;
                }
                summary.includedRows++;
                List<GlTransactionLineBody> lines;
                if (!groups.TryGetValue(line.JournalPostOrder, out lines))
                {
                    lines = new List<GlTransactionLineBody>();
                    groups.Add(line.JournalPostOrder, lines);
                }
                lines.Add(line);
            }, summary);

            CopyTracker(summary, tracker);
            var postingRows = new List<GlTransactionLineBody>();
            foreach (List<GlTransactionLineBody> lines in groups.Values) postingRows.AddRange(lines);
            List<GlTransactionBody> transactions = GeneralLedgerExporter.GroupByPostingOrder(postingRows);
            FinishTransactions(transactions, summary, timer, null);
        }

        private static void RunContiguousGroups(string csvPath, DateTime? start, DateTime? end, BenchmarkSummary summary, Stopwatch timer)
        {
            Dictionary<string, int> map = null;
            var tracker = new OrderTracker();
            var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var page = new List<GlTransactionBody>(PageSize);
            long previousOrder = -1;
            List<GlTransactionLineBody> currentGroup = null;

            Action flushGroup = () =>
            {
                if (currentGroup == null || currentGroup.Count == 0) return;
                List<GlTransactionBody> made = GeneralLedgerExporter.GroupByPostingOrder(currentGroup);
                if (made.Count != 1)
                    throw new InvalidDataException("A contiguous CSV group did not produce exactly one transaction.");
                GlTransactionBody transaction = made[0];
                AddHash(transaction, hashes, summary);
                summary.transactionCount++;
                summary.lineCount += transaction.Lines.Count;
                if (!transaction.HeaderConsistent) summary.inconsistentHeaderGroups++;
                if (transaction.Amount == 0m) summary.zeroAmountGroups++;
                page.Add(transaction);
                currentGroup = null;
                if (page.Count == PageSize)
                {
                    CountPage(page, summary);
                    if (!summary.firstPageMs.HasValue) summary.firstPageMs = timer.Elapsed.TotalMilliseconds;
                    page.Clear();
                }
            };

            ReadRows(csvPath, (fields, rowNumber, headerMap) =>
            {
                if (map == null) map = headerMap;
                GlTransactionLineBody line = GeneralLedgerExporter.MapRow(fields, map, rowNumber);
                tracker.Observe(line);
                CopyTracker(summary, tracker);
                if (!tracker.PostOrderMonotonic)
                    throw new InvalidDataException("JournalPostOrder decreases in CSV; contiguous-group streaming is unsafe.");
                if (!InDateWindow(line, start, end))
                {
                    summary.dateFilteredRows++;
                    return;
                }
                if (!line.IncludeInGL)
                {
                    summary.nonGlRows++;
                    return;
                }
                summary.includedRows++;

                if (currentGroup == null)
                {
                    currentGroup = new List<GlTransactionLineBody>();
                    previousOrder = line.JournalPostOrder;
                }
                else if (line.JournalPostOrder != previousOrder)
                {
                    if (line.JournalPostOrder < previousOrder)
                        throw new InvalidDataException("JournalPostOrder decreases in CSV; contiguous-group streaming is unsafe.");
                    flushGroup();
                    currentGroup = new List<GlTransactionLineBody>();
                    previousOrder = line.JournalPostOrder;
                }
                currentGroup.Add(line);
            }, summary);

            CopyTracker(summary, tracker);
            flushGroup();
            if (page.Count > 0)
            {
                CountPage(page, summary);
                if (!summary.firstPageMs.HasValue) summary.firstPageMs = timer.Elapsed.TotalMilliseconds;
            }
            WriteHashSidecar(null, hashes, summary);
        }

        private static void RunContiguousSpool(string csvPath, DateTime? start, DateTime? end, BenchmarkSummary summary, Stopwatch timer)
        {
            string spoolPath = Path.Combine(Path.GetTempPath(), "sage50-gl-benchmark-" + Guid.NewGuid().ToString("N") + ".jsonl");
            Dictionary<string, int> map = null;
            var tracker = new OrderTracker();
            var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            long previousOrder = -1;
            List<GlTransactionLineBody> currentGroup = null;
            var buildTimer = Stopwatch.StartNew();
            string writtenSpoolHash;

            try
            {
                using (SHA256 writeHash = SHA256.Create())
                {
                using (var spool = new StreamWriter(spoolPath, false, new UTF8Encoding(false)))
                {
                    spool.NewLine = "\n";
                    Action flushGroup = () =>
                    {
                        if (currentGroup == null || currentGroup.Count == 0) return;
                        List<GlTransactionBody> made = GeneralLedgerExporter.GroupByPostingOrder(currentGroup);
                        if (made.Count != 1)
                            throw new InvalidDataException("A contiguous CSV group did not produce exactly one transaction.");
                        GlTransactionBody transaction = made[0];
                        string json = JsonConvert.SerializeObject(transaction, CanonicalSettings);
                        spool.WriteLine(json);
                        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
                        writeHash.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
                        summary.spoolBytes += bytes.Length;
                        currentGroup = null;
                    };

                    ReadRows(csvPath, (fields, rowNumber, headerMap) =>
                    {
                        if (map == null) map = headerMap;
                        GlTransactionLineBody line = GeneralLedgerExporter.MapRow(fields, map, rowNumber);
                        tracker.Observe(line);
                        CopyTracker(summary, tracker);
                        if (!tracker.PostOrderMonotonic)
                            throw new InvalidDataException("JournalPostOrder decreases in CSV; contiguous-group streaming is unsafe.");
                        if (!InDateWindow(line, start, end))
                        {
                            summary.dateFilteredRows++;
                            return;
                        }
                        if (!line.IncludeInGL)
                        {
                            summary.nonGlRows++;
                            return;
                        }
                        summary.includedRows++;

                        if (currentGroup == null)
                        {
                            currentGroup = new List<GlTransactionLineBody>();
                            previousOrder = line.JournalPostOrder;
                        }
                        else if (line.JournalPostOrder != previousOrder)
                        {
                            if (line.JournalPostOrder < previousOrder)
                                throw new InvalidDataException("JournalPostOrder decreases in CSV; contiguous-group streaming is unsafe.");
                            flushGroup();
                            currentGroup = new List<GlTransactionLineBody>();
                            previousOrder = line.JournalPostOrder;
                        }
                        currentGroup.Add(line);
                    }, summary);

                    CopyTracker(summary, tracker);
                    flushGroup();
                    spool.Flush();
                }
                    writeHash.TransformFinalBlock(new byte[0], 0, 0);
                    writtenSpoolHash = ToHex(writeHash.Hash);
                }
                buildTimer.Stop();
                summary.spoolBuildMs = buildTimer.Elapsed.TotalMilliseconds;

                var readTimer = Stopwatch.StartNew();
                var page = new List<GlTransactionBody>(PageSize);
                using (SHA256 readHash = SHA256.Create())
                using (var spoolReader = new StreamReader(spoolPath, Encoding.UTF8, true))
                {
                    string json;
                    while ((json = spoolReader.ReadLine()) != null)
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
                        readHash.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
                        GlTransactionBody transaction = JsonConvert.DeserializeObject<GlTransactionBody>(json);
                        if (transaction == null) throw new InvalidDataException("A JSONL spool record could not be read back.");
                        AddHash(transaction, hashes, summary);
                        summary.transactionCount++;
                        summary.lineCount += transaction.Lines == null ? 0 : transaction.Lines.Count;
                        if (!transaction.HeaderConsistent) summary.inconsistentHeaderGroups++;
                        if (transaction.Amount == 0m) summary.zeroAmountGroups++;
                        page.Add(transaction);
                        if (page.Count == PageSize)
                        {
                            CountPage(page, summary);
                            if (!summary.firstPageMs.HasValue)
                            {
                                summary.firstPageMs = timer.Elapsed.TotalMilliseconds;
                                summary.firstReadablePageMs = summary.firstPageMs;
                            }
                            page.Clear();
                        }
                    }
                    readHash.TransformFinalBlock(new byte[0], 0, 0);
                    summary.replayedSpoolSha256 = ToHex(readHash.Hash);
                }
                if (page.Count > 0)
                {
                    CountPage(page, summary);
                    if (!summary.firstPageMs.HasValue)
                    {
                        summary.firstPageMs = timer.Elapsed.TotalMilliseconds;
                        summary.firstReadablePageMs = summary.firstPageMs;
                    }
                }
                readTimer.Stop();
                summary.spoolReadMs = readTimer.Elapsed.TotalMilliseconds;
                summary.spoolWriteSha256 = writtenSpoolHash;
                summary.spoolReplayMatched = string.Equals(writtenSpoolHash, summary.replayedSpoolSha256, StringComparison.Ordinal);
                if (summary.spoolReplayMatched != true)
                    throw new InvalidDataException("The disk JSONL spool changed between write and page replay.");

                WriteHashSidecar(spoolPath, hashes, summary);
            }
            finally
            {
                try { if (File.Exists(spoolPath)) File.Delete(spoolPath); } catch { }
                summary.spoolPathDeleted = !File.Exists(spoolPath);
            }
        }

        private static void ReadRows(string csvPath, Action<string[], int, Dictionary<string, int>> action, BenchmarkSummary summary)
        {
            using (var reader = new StreamReader(csvPath, Encoding.UTF8, true))
            {
                var parser = new Rfc4180CsvParser(reader);
                if (!parser.ReadRecord(out string[] headers)) return;
                Dictionary<string, int> map = GeneralLedgerExporter.BuildHeaderMap(headers);
                int rowNumber = 1;
                while (parser.ReadRecord(out string[] fields))
                {
                    rowNumber++;
                    summary.rawRows++;
                    if (fields.Length == 1 && string.IsNullOrEmpty(fields[0])) continue;
                    action(fields, rowNumber, map);
                }
            }
        }

        private static List<GlTransactionLineBody> ApplyDateWindow(
            List<GlTransactionLineBody> rows, DateTime? start, DateTime? end, BenchmarkSummary summary)
        {
            if (!start.HasValue && !end.HasValue) return rows;
            List<GlTransactionLineBody> result = GeneralLedgerExporter.ApplyDateWindow(rows, start, end);
            summary.dateFilteredRows += rows.Count - result.Count;
            return result;
        }

        private static bool InDateWindow(GlTransactionLineBody row, DateTime? start, DateTime? end)
        {
            if (!start.HasValue && !end.HasValue) return true;
            if (string.IsNullOrWhiteSpace(row.Date)) return false;
            DateTime date;
            if (!DateTime.TryParseExact(row.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date)) return false;
            if (start.HasValue && date < start.Value.Date) return false;
            if (end.HasValue && date >= end.Value.Date) return false;
            return true;
        }

        private static void FinishTransactions(
            List<GlTransactionBody> transactions, BenchmarkSummary summary, Stopwatch timer, string spoolPath)
        {
            summary.transactionCount = transactions.Count;
            var ordered = transactions.OrderBy(transaction => transaction.ID, StringComparer.Ordinal).ToList();
            var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (GlTransactionBody transaction in ordered)
            {
                AddHash(transaction, hashes, summary);
                summary.lineCount += transaction.Lines.Count;
                if (!transaction.HeaderConsistent) summary.inconsistentHeaderGroups++;
                if (transaction.Amount == 0m) summary.zeroAmountGroups++;
            }
            for (int offset = 0; offset < ordered.Count; offset += PageSize)
            {
                List<GlTransactionBody> page = ordered.Skip(offset).Take(PageSize).ToList();
                CountPage(page, summary);
                if (!summary.firstPageMs.HasValue) summary.firstPageMs = timer.Elapsed.TotalMilliseconds;
            }
            WriteHashSidecar(spoolPath, hashes, summary);
        }

        private static void CountPage(List<GlTransactionBody> page, BenchmarkSummary summary)
        {
            string json = JsonConvert.SerializeObject(page, CanonicalSettings);
            summary.pageCount++;
            summary.pageJsonBytes += Encoding.UTF8.GetByteCount(json);
        }

        private static void AddHash(
            GlTransactionBody transaction, SortedDictionary<string, string> hashes, BenchmarkSummary summary)
        {
            if (transaction == null || string.IsNullOrWhiteSpace(transaction.ID))
                throw new InvalidDataException("A grouped GL transaction has no ID.");
            if (hashes.ContainsKey(transaction.ID))
                throw new InvalidDataException("Duplicate grouped transaction ID: " + transaction.ID);
            string json = JsonConvert.SerializeObject(transaction, CanonicalSettings);
            summary.canonicalTransactionBytes += Encoding.UTF8.GetByteCount(json);
            using (SHA256 sha = SHA256.Create())
            {
                hashes.Add(transaction.ID, ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(json))));
            }
        }

        private static void WriteHashSidecar(string spoolPath, SortedDictionary<string, string> hashes, BenchmarkSummary summary)
        {
            string outputPath = summary.resultPath;
            if (string.IsNullOrWhiteSpace(outputPath)) return;
            string sidecar = outputPath + ".per-id-sha256.jsonl";
            using (var writer = new StreamWriter(sidecar, false, new UTF8Encoding(false)))
            using (SHA256 aggregate = SHA256.Create())
            {
                // Compute a deterministic aggregate over the exact ID->payload-hash map.
                // The sidecar contains identifiers and hashes only, never financial payloads.
                foreach (KeyValuePair<string, string> pair in hashes)
                {
                    writer.WriteLine(JsonConvert.SerializeObject(new { id = pair.Key, sha256 = pair.Value }));
                    byte[] line = Encoding.UTF8.GetBytes(pair.Key + "\t" + pair.Value + "\n");
                    aggregate.TransformBlock(line, 0, line.Length, line, 0);
                }
                aggregate.TransformFinalBlock(new byte[0], 0, 0);
                summary.transactionMapSha256 = ToHex(aggregate.Hash);
            }
            summary.hashSidecar = sidecar;
            summary.hashEntries = hashes.Count;
        }

        private static void CopyTracker(BenchmarkSummary summary, OrderTracker tracker)
        {
            summary.inputPostOrderMonotonic = tracker.PostOrderMonotonic;
            summary.inputPostOrderContiguous = tracker.PostOrderContiguous;
            summary.inputLineOrderSorted = tracker.LineOrderSorted;
            summary.postOrderDecreaseCount = tracker.PostOrderDecreaseCount;
            summary.nonContiguousPostOrderCount = tracker.NonContiguousPostOrderCount;
            summary.lineOrderInversionCount = tracker.LineOrderInversionCount;
        }

        private static DateTime? ParseBound(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "-") return null;
            DateTime date;
            if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date))
                throw new ArgumentException("Date bounds must use yyyy-MM-dd.");
            return date.Date;
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes) sb.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private sealed class OrderTracker
        {
            private long? _lastOrder;
            private int? _lastRowIndex;
            private string _lastLineId;

            internal bool PostOrderMonotonic { get; private set; } = true;
            internal bool PostOrderContiguous { get; private set; } = true;
            internal bool LineOrderSorted { get; private set; } = true;
            internal int PostOrderDecreaseCount { get; private set; }
            internal int NonContiguousPostOrderCount { get; private set; }
            internal int LineOrderInversionCount { get; private set; }

            internal void Observe(GlTransactionLineBody line)
            {
                if (_lastOrder.HasValue)
                {
                    if (line.JournalPostOrder < _lastOrder.Value)
                    {
                        PostOrderMonotonic = false;
                        PostOrderContiguous = false;
                        PostOrderDecreaseCount++;
                    }
                    if (line.JournalPostOrder != _lastOrder.Value)
                    {
                        if (line.JournalPostOrder < _lastOrder.Value) NonContiguousPostOrderCount++;
                        _lastRowIndex = null;
                        _lastLineId = null;
                    }
                }
                if (_lastOrder == line.JournalPostOrder && _lastRowIndex.HasValue)
                {
                    bool inverted = line.JournalRowIndex < _lastRowIndex.Value
                        || (line.JournalRowIndex == _lastRowIndex.Value
                            && string.CompareOrdinal(line.ID ?? "", _lastLineId ?? "") < 0);
                    if (inverted)
                    {
                        LineOrderSorted = false;
                        LineOrderInversionCount++;
                    }
                }
                _lastOrder = line.JournalPostOrder;
                _lastRowIndex = line.JournalRowIndex;
                _lastLineId = line.ID;
            }
        }

        private sealed class MemorySampler : IDisposable
        {
            private readonly Process _process = Process.GetCurrentProcess();
            private readonly Thread _thread;
            private volatile bool _stop;
            internal long StartPrivateBytes { get; private set; }
            internal long PeakPrivateBytes { get; private set; }
            internal long StartManagedBytes { get; private set; }
            internal long PeakManagedBytes { get; private set; }

            internal MemorySampler()
            {
                StartPrivateBytes = PeakPrivateBytes = _process.PrivateMemorySize64;
                StartManagedBytes = PeakManagedBytes = GC.GetTotalMemory(false);
                _thread = new Thread(SampleLoop) { IsBackground = true, Name = "GL benchmark memory sampler" };
                _thread.Start();
            }

            private void SampleLoop()
            {
                while (!_stop)
                {
                    Sample();
                    Thread.Sleep(20);
                }
                Sample();
            }

            private void Sample()
            {
                try
                {
                    _process.Refresh();
                    long privateBytes = _process.PrivateMemorySize64;
                    long managedBytes = GC.GetTotalMemory(false);
                    if (privateBytes > PeakPrivateBytes) PeakPrivateBytes = privateBytes;
                    if (managedBytes > PeakManagedBytes) PeakManagedBytes = managedBytes;
                }
                catch { }
            }

            public void Dispose()
            {
                _stop = true;
                if (_thread != null) _thread.Join();
                _process.Dispose();
            }
        }

        private sealed class BenchmarkSummary
        {
            public string status { get; set; }
            public string mode { get; set; }
            public string resultPath { get; set; }
            public string error { get; set; }
            public int exitCode { get; set; }
            public long inputBytes { get; set; }
            public string startDate { get; set; }
            public string endDateExclusive { get; set; }
            public int pageSize { get; set; }
            public long rawRows { get; set; }
            public long dateFilteredRows { get; set; }
            public long nonGlRows { get; set; }
            public long includedRows { get; set; }
            public long transactionCount { get; set; }
            public long lineCount { get; set; }
            public long pageCount { get; set; }
            public long canonicalTransactionBytes { get; set; }
            public long pageJsonBytes { get; set; }
            public long spoolBytes { get; set; }
            public double? spoolBuildMs { get; set; }
            public double? spoolReadMs { get; set; }
            public string spoolWriteSha256 { get; set; }
            public string replayedSpoolSha256 { get; set; }
            public bool? spoolReplayMatched { get; set; }
            public string hashCatalogMemoryNote { get; set; } = "Benchmark-only per-ID SHA sidecar uses O(group count) hash metadata; transaction payloads are not retained by the spool candidate.";
            public bool? inputPostOrderMonotonic { get; set; }
            public bool? inputPostOrderContiguous { get; set; }
            public bool? inputLineOrderSorted { get; set; }
            public int postOrderDecreaseCount { get; set; }
            public int nonContiguousPostOrderCount { get; set; }
            public int lineOrderInversionCount { get; set; }
            public long inconsistentHeaderGroups { get; set; }
            public long zeroAmountGroups { get; set; }
            public string transactionMapSha256 { get; set; }
            public string hashSidecar { get; set; }
            public int hashEntries { get; set; }
            public bool spoolPathDeleted { get; set; }
            public double? firstPageMs { get; set; }
            public double? firstReadablePageMs { get; set; }
            public double elapsedMs { get; set; }
            public long managedStartBytes { get; set; }
            public long managedPeakBytes { get; set; }
            public long privateStartBytes { get; set; }
            public long privatePeakBytes { get; set; }
            public long privateDeltaBytes { get; set; }
        }
    }
}
