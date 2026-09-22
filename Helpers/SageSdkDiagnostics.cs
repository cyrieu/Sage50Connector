using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;

namespace Sage50Connector.Helpers
{
    // Passive snapshots of the real connector process. Never loads a DLL to test
    // it, reads configuration contents, or records arguments/setup credentials.
    internal static class SageSdkDiagnostics
    {
        private static readonly object Gate = new object();
        private static readonly List<object> Events = new List<object>();
        public static string ReportPath { get; private set; }

        public static void Capture(string stage, Exception error = null, object companies = null)
        {
            lock (Gate)
            {
                try
                {
                    var errors = new List<object>();
                    for (var e = error; e != null; e = e.InnerException)
                        errors.Add(new { type = e.GetType().FullName, message = e.Message, hresult = "0x" + e.HResult.ToString("X8"), stack = e.StackTrace });
                    var modules = new List<object>();
                    using (var process = Process.GetCurrentProcess())
                    {
                        foreach (ProcessModule module in process.Modules)
                        {
                            string path = module.FileName;
                            if (new[] { "sage", "peach", "actian", "pervasive", "psql", "w3db" }.Any(s => path.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0))
                                modules.Add(FileInfoFor(path));
                        }
                    }
                    var candidates = new List<string>();
                    string x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                    string api = Path.Combine(x86, @"Sage\Peachtree\API");
                    foreach (string root in new[] { AppDomain.CurrentDomain.BaseDirectory, api })
                        foreach (string name in new[] { "Sage.Peachtree.API.dll", "Sage.Peachtree.API.Resolver.dll" })
                            candidates.Add(Path.Combine(root, name));
                    foreach (string root in new[] { Path.Combine(x86, @"Actian\Zen\bin"), Path.Combine(x86, @"Pervasive Software\PSQL\bin"), @"C:\PVSW\bin" })
                        candidates.Add(Path.Combine(root, "w3dbav90.dll"));
                    var snapshot = new
                    {
                        utc = DateTime.UtcNow, stage, errors, companies,
                        executable = FileInfoFor(Assembly.GetExecutingAssembly().Location),
                        processBits = IntPtr.Size * 8, user = WindowsIdentity.GetCurrent().Name,
                        workingDirectory = Environment.CurrentDirectory,
                        baseDirectory = AppDomain.CurrentDomain.BaseDirectory,
                        dllSearchPath = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'),
                        sdkIdentity = "Rutter licensed identifier (value omitted)",
                        assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name.StartsWith("Sage", StringComparison.OrdinalIgnoreCase))
                            .Select(a => new { identity = a.FullName, file = FileInfoFor(a.Location) }).ToArray(),
                        nativeModules = modules, candidateFiles = candidates.Select(FileInfoFor).ToArray()
                    };
                    if (Events.Count == 16) Events.RemoveAt(0);
                    Events.Add(snapshot);
                    string json = JsonConvert.SerializeObject(Events, Formatting.Indented);
                    if (ReportPath != null)
                    {
                        File.WriteAllText(ReportPath, json);
                    }
                    else
                    {
                        string fileName;
                        using (var process = Process.GetCurrentProcess())
                            fileName = "sdk-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + process.Id + ".json";
                        foreach (string directory in new[]
                        {
                            Path.Combine(ConnectorConfig.ConfigDirectory, "diagnostics"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rutter", "Sage50Connector", "diagnostics")
                        })
                        {
                            try
                            {
                                Directory.CreateDirectory(directory);
                                string path = Path.Combine(directory, fileName);
                                File.WriteAllText(path, json);
                                ReportPath = path; // Only expose files actually written.
                                break;
                            }
                            catch { }
                        }
                    }
                    try { Program.WriteToFile("Sage SDK diagnostics: " + stage + "; report=" + ReportPath); } catch { }
                }
                catch
                {
                    // Diagnostics must never replace the original SDK failure.
                }
            }
        }

        private static object FileInfoFor(string path)
        {
            try
            {
                var file = new FileInfo(path);
                return new { path, exists = file.Exists, version = file.Exists ? FileVersionInfo.GetVersionInfo(path).FileVersion : null,
                    bytes = file.Exists ? (long?)file.Length : null };
            }
            catch (Exception e) { return new { path, metadataError = e.GetType().Name }; }
        }

        public static int Run()
        {
            int result = 0;
            Capture("diagnostic-start");
            try
            {
                // Same manager and licensed session as the setup picker. Retry in
                // this process to expose failed-session reuse, without opening data.
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    try
                    {
                        var companies = CompanyManager.Instance.Companies;
                        Capture("diagnostic-attempt-" + attempt, companies: companies.Select(c => new { c.CompanyName, c.Guid, c.DatabaseName, c.ServerName, c.Path }).ToArray());
                    }
                    catch (Exception e) { result = 1; Capture("diagnostic-attempt-" + attempt, e); }
                }
            }
            finally
            {
                try { Sage50Connector.ShutdownExistingSession(); }
                catch (Exception e) { Capture("diagnostic-cleanup-failed", e); result = 1; }
                Capture("diagnostic-end");
            }
            return result;
        }
    }
}
