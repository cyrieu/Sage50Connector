// Diagnostic-only: capture one real owned-lab COM export without materializing it.
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
namespace Sage50Connector.Diagnostics
{
    internal static class GlExportDiagnostic
    {
        internal static string CapturePath;
        public static int Run(string[] args)
        {
            if (args.Length != 4) throw new ArgumentException("Usage: --capture-gl-csv <owned-lab-company> <csv-path> <result-json>");
            string company = args[1];
            if (company != "Rutter Test Co" && company != "Bellwether Garden Supply")
                throw new InvalidOperationException("COM capture is restricted to owned lab/sample companies.");
            string result = Path.GetFullPath(args[3]);
            Directory.CreateDirectory(Path.GetDirectoryName(result));
            CapturePath = Path.GetFullPath(args[2]);
            if (File.Exists(CapturePath)) throw new InvalidOperationException("Use a fresh CSV capture path.");
            Directory.CreateDirectory(Path.GetDirectoryName(CapturePath));
            var timer = Stopwatch.StartNew();
            try
            {
                Program.CompanyName = company;
                Helpers.GeneralLedgerExporter.ExportTransactions(company, null, null, null, null);
                if (!File.Exists(CapturePath)) throw new InvalidOperationException("COM exporter did not produce a captured CSV.");
                File.WriteAllText(result, JsonConvert.SerializeObject(new {
                    status = "completed", company, csv = CapturePath, bytes = new FileInfo(CapturePath).Length,
                    exportMs = timer.Elapsed.TotalMilliseconds, completedUtc = DateTime.UtcNow.ToString("o")
                }, Formatting.Indented));
                return 0;
            }
            catch (Exception error)
            {
                while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                // Message only: never copy credential-bearing COM call arguments.
                File.WriteAllText(result, JsonConvert.SerializeObject(new { status="failed", error=error.Message,
                    hresult=error.HResult.ToString("X8"), elapsedMs=timer.Elapsed.TotalMilliseconds }, Formatting.Indented));
                return 1;
            }
            finally { CapturePath = null; }
        }
    }
}
