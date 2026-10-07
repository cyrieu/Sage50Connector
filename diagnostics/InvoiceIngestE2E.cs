// Lab-only driver for a real connector -> local Rutter ingest path. The input
// config is a protected file; access keys are never included in output.
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Sage50Connector.Diagnostics
{
    internal static class InvoiceIngestE2E
    {
        private const string AllowedCompany = "Rutter Test Co";
        private const int MaxPages = 20000; // Up to one million rows at 50/page.
        private static string accessKeyForRedaction;

        public static int Run(string[] args)
        {
            if (args == null || args.Length != 3)
            {
                Console.Error.WriteLine("Usage: --e2e-invoice-fetch <protected-config.json> <result.json>");
                return 2;
            }

            string resultPath = Path.GetFullPath(args[2]);
            try
            {
                JObject input = JObject.Parse(File.ReadAllText(args[1]));
                string companyName = Required(input, "CompanyName");
                string accessKey = Required(input, "AccessKey");
                accessKeyForRedaction = accessKey;
                string connectionId = Required(input, "ConnectionId");
                string apiBaseUrl = Required(input, "ApiBaseUrl").TrimEnd('/');

                if (!string.Equals(companyName, AllowedCompany, StringComparison.Ordinal))
                    throw new InvalidOperationException("This diagnostic can run only for the Rutter Test Co lab company.");

                Uri api;
                if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out api)
                    || api.Scheme != Uri.UriSchemeHttp
                    || !(api.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                        || api.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("ApiBaseUrl must be a local HTTP endpoint or reverse-SSH tunnel.");

                ConfigureConnector(companyName, accessKey, connectionId, apiBaseUrl,
                    input.Value<string>("CompanyGuid"), input.Value<string>("DatabaseName"));

                int pages = 0;
                int noops = 0;
                bool sawInvoiceJob = false;
                bool completed = false;
                var pageSizes = new List<int>();
                MethodInfo getNext = typeof(Program).GetMethod("GetNextJobAsync", BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo handleList = typeof(Program).GetMethod("HandleListFetchJob", BindingFlags.Static | BindingFlags.NonPublic);
                if (getNext == null || handleList == null)
                    throw new MissingMethodException("The production ingest poll or LIST_FETCH handler was not found.");

                for (int request = 0; request < MaxPages + 2; request++)
                {
                    object job = AwaitResult(getNext.Invoke(null, new object[] { accessKey, CancellationToken.None }));
                    if (job == null)
                        throw new InvalidOperationException("The local backend returned no job before completing the invoice fetch.");

                    string type = ReadString(job, "type");
                    if (string.Equals(type, "NOOP", StringComparison.OrdinalIgnoreCase))
                    {
                        noops++;
                        completed = sawInvoiceJob;
                        break;
                    }

                    string entity = ReadString(job, "platform_entity");
                    string jobId = ReadString(job, "job_id");
                    if (!string.Equals(type, "LIST_FETCH", StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(entity, "INVOICES", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Unexpected job returned to the isolated invoice diagnostic.");
                    if (string.IsNullOrWhiteSpace(jobId))
                        throw new InvalidDataException("The invoice LIST_FETCH response has no job_id.");

                    sawInvoiceJob = true;
                    pages++;
                    if (pages > MaxPages)
                        throw new InvalidOperationException("The invoice diagnostic exceeded the page safety limit.");

                    object parameters = job.GetType().GetProperty("parameters")?.GetValue(job, null);
                    int limit = ReadInt(parameters, "limit");
                    pageSizes.Add(limit > 0 ? limit : 0);
                    AwaitResult(handleList.Invoke(null, new object[] { job, accessKey, companyName }));
                    // An SDK error is reported successfully to Rutter, which can then
                    // return NOOP. That is not a successful end-to-end sync.
                    if (Helpers.SyncStatus.Instance.State == Helpers.ConnectorState.Error
                        || Helpers.SyncStatus.Instance.State == Helpers.ConnectorState.NeedsAuthorization)
                        throw new InvalidOperationException(Helpers.SyncStatus.Instance.Message);

                }

                if (!completed || pages == 0)
                    throw new InvalidOperationException("The invoice job did not reach NOOP after at least one page.");

                WriteResult(resultPath, new
                {
                    status = "completed",
                    company = companyName,
                    connectionId,
                    apiBaseUrl,
                    pages,
                    noops,
                    reportedPageLimits = pageSizes,
                    completedUtc = DateTime.UtcNow.ToString("o")
                });
                Console.WriteLine("INVOICE INGEST E2E COMPLETED; company=" + companyName
                    + "; pages=" + pages + "; noops=" + noops);
                return 0;
            }
            catch (Exception error)
            {
                while (error is TargetInvocationException && error.InnerException != null)
                    error = error.InnerException;
                try
                {
                    WriteResult(resultPath, new
                    {
                        status = "failed",
                        error = Redact(error.ToString()),
                        completedUtc = DateTime.UtcNow.ToString("o")
                    });
                }
                catch { }
                Console.Error.WriteLine("INVOICE INGEST E2E FAILED: " + Redact(error.Message));
                return 1;
            }
            finally
            {
                try { Program.ReleaseSageSession(); }
                catch (Exception error) { Console.Error.WriteLine("Sage session cleanup failed: " + Redact(error.Message)); }
            }
        }

        private static void ConfigureConnector(string companyName, string accessKey, string connectionId,
            string apiBaseUrl, string companyGuid, string databaseName)
        {
            Type configType = typeof(Program).Assembly.GetType("Sage50Connector.Helpers.ConnectorConfig", true);
            object config = Activator.CreateInstance(configType, true);
            SetPrivateProperty(configType, config, "CompanyName", companyName);
            SetPrivateProperty(configType, config, "AccessKey", accessKey);
            SetPrivateProperty(configType, config, "ConnectionId", connectionId);
            SetPrivateProperty(configType, config, "ApiBaseUrl", apiBaseUrl);
            SetPrivateProperty(configType, config, "CompanyGuid", companyGuid);
            SetPrivateProperty(configType, config, "DatabaseName", databaseName);
            SetPrivateProperty(configType, config, "LoadedFromPath", "<protected diagnostic config>");

            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            typeof(Program).GetField("Config", flags).SetValue(null, config);
            typeof(Program).GetField("CompanyName", flags).SetValue(null, companyName);
            typeof(Program).GetField("CompanyGuid", flags).SetValue(null, companyGuid);
            typeof(Program).GetField("DatabaseName", flags).SetValue(null, databaseName);
            typeof(Program).GetField("AccessKey", flags).SetValue(null, accessKey);
            typeof(Program).GetField("ConnectionId", flags).SetValue(null, connectionId);
        }

        private static void SetPrivateProperty(Type type, object instance, string name, object value)
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo setter = property?.GetSetMethod(true);
            if (setter == null) throw new MissingMemberException(type.FullName, name);
            setter.Invoke(instance, new[] { value });
        }

        private static object AwaitResult(object value)
        {
            Task task = value as Task;
            if (task == null) throw new InvalidOperationException("Expected an asynchronous connector handler.");
            task.GetAwaiter().GetResult();
            PropertyInfo result = task.GetType().GetProperty("Result");
            return result == null ? null : result.GetValue(task, null);
        }

        private static string ReadString(object source, string propertyName)
        {
            return source?.GetType().GetProperty(propertyName)?.GetValue(source, null)?.ToString();
        }

        private static int ReadInt(object source, string propertyName)
        {
            object value = source?.GetType().GetProperty(propertyName)?.GetValue(source, null);
            int result;
            return value != null && int.TryParse(value.ToString(), out result) ? result : 0;
        }

        private static string Required(JObject input, string name)
        {
            string value = input.Value<string>(name);
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Protected config is missing " + name + ".");
            return value;
        }

        private static string Redact(string text)
        {
            return string.IsNullOrEmpty(accessKeyForRedaction)
                ? text
                : text.Replace(accessKeyForRedaction, "[REDACTED]");
        }

        private static void WriteResult(string path, object value)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonConvert.SerializeObject(value, Formatting.Indented));
        }
    }
}
