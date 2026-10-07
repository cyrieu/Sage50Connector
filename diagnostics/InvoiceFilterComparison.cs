using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Sage50Connector.Helpers;
using Sage50Connector.Models.Rutter;
namespace Sage50Connector.Diagnostics
{
    internal static class InvoiceFilterComparison
    {
        public static int Run(string[] args)
        {
            var results = new List<object>();
            var settings = new JsonSerializerSettings { ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver() };
            try
            {
                foreach (var test in new[] {
                    new { Name="missing-included", After=(string)null, Before=(string)null, Missing=true, Start=(string)null, End=(string)null },
                    new { Name="missing-excluded", After=(string)null, Before=(string)null, Missing=false, Start=(string)null, End=(string)null },
                    new { Name="incremental-window", After="2026-01-01", Before="2027-01-01", Missing=true, Start=(string)null, End=(string)null },
                    new { Name="inclusive-date-boundary", After=(string)null, Before=(string)null, Missing=true, Start="2021-01-01", End="2021-03-31" }
                })
                {
                    var baseline = Sage50Repository.Instance.GetInvoices(args[1], test.After, test.Before, test.Missing)
                        .Where(row => string.IsNullOrEmpty(row.Date) || ((test.Start == null || string.CompareOrdinal(row.Date, test.Start) >= 0)
                        && (test.End == null || string.CompareOrdinal(row.Date, test.End) <= 0))).OrderBy(row => row.ID, StringComparer.Ordinal).ToList();
                    var actual = new List<InvoiceBody>();
                    using (var reader = Sage50Repository.Instance.OpenInvoiceReader(args[1], test.After, test.Before, test.Missing, test.Start, test.End))
                    {
                        string cursor = null;
                        do { var page = reader.ReadPage(cursor, 7); actual.AddRange(page.Records); cursor = page.NextCursor; reader.Accept(page); } while (cursor != null);
                    }
                    var expectedJson = JsonConvert.SerializeObject(baseline, settings);
                    var actualJson = JsonConvert.SerializeObject(actual.OrderBy(row => row.ID, StringComparer.Ordinal), settings);
                    if (expectedJson != actualJson) throw new Exception("Invoice payload mismatch in " + test.Name);
                    results.Add(new { test.Name, Count = actual.Count, MissingTimestamps = actual.Count(row => row.LastSavedAt == null) });
                }
                File.WriteAllText(args[2], JsonConvert.SerializeObject(new { status="completed", results }, Formatting.Indented));
                return 0;
            }
            catch (Exception ex) { File.WriteAllText(args[2], JsonConvert.SerializeObject(new { status="failed", error=ex.ToString(), results }, Formatting.Indented)); return 1; }
            finally { Helpers.Sage50Connector.Instance.Shutdown(); }
        }
    }
}
