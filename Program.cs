using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Sage.Peachtree.API;
using Sage50Connector.Helpers;
using Sage50Connector.Models.Rutter;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Sage50Connector
{
    class ResponseObject
    {
        public string job { get; set; }
        public string platform_entity { get; set; }
        public string type { get; set; }
        public Parameters parameters { get; set; }
        public string job_id { get; set; }
        public object body { get; set; }
        public CreateBody create_body { get; set; }
    }

    class CreateBody
    {
        public object data { get; set; }
    }

    class Parameters
    {
        public string updated_at { get; set; }
        public int limit { get; set; }

        /// <summary>
        /// Set by Rutter on follow-up pages: the record id the previous page
        /// stopped at. Null on the first page of a job.
        /// </summary>
        public string cursor { get; set; }
    }

    public class VendorBody
    {
        public string AccountNumber { get; set; }
        public string ID { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public string TaxIDNumber { get; set; }
        public string WebSiteURL { get; set; }
        /*public EntityReference<Account> ExpenseAccountReference { get; set; }*/

    }

    class AccountBody
    {
        public string ID { get; set; }
        public string Description { get; set; }
        public string Classification { get; set; }
        public bool IsInactive { get; set; }
        public EntityReference<Sage.Peachtree.API.Account> Key { get; set; }
    }

    public class Program
    {
        public static string CompanyName;
        public static string AccessKey;
        public static string ConnectionId;

        private static ConnectorConfig Config;

        public static int Main(string[] args)
        {
            if (args.Length > 0 && string.Equals(args[0], "--setup", StringComparison.OrdinalIgnoreCase))
            {
                return RunSetupAsync(args).GetAwaiter().GetResult();
            }

            try
            {
                Config = ConnectorConfig.Load();
                CompanyName = Config.CompanyName;
                AccessKey = Config.AccessKey;
                ConnectionId = Config.ConnectionId;
            }
            catch (Exception ex)
            {
                WriteToFile("Failed to load connector configuration: " + ex.Message);
                return 1;
            }

            WriteToFile(
                "Loaded configuration from "
                    + Config.LoadedFromPath
                    + "; CompanyName='"
                    + CompanyName
                    + "'; ConnectionId='"
                    + ConnectionId
                    + "'; AccessKeyLength="
                    + AccessKey.Length
                    + "; ApiBaseUrl="
                    + Config.ApiBaseUrl
            );

            MainAsync(AccessKey, CompanyName, ConnectionId).GetAwaiter().GetResult();
            return 0;
        }

        /// <summary>
        /// Fetches this connection's sage50Config.json from the Rutter backend
        /// (POST /sage-50/save-id) and writes it to the ProgramData config path,
        /// so nobody hand-edits JSON on the machine.
        ///
        /// Usage: Sage50Connector.exe --setup &lt;CompanyName&gt; &lt;OrgId&gt; [ApiBaseUrl]
        ///   CompanyName — the Sage 50 company (e.g. "Bellwether Garden Supply")
        ///   OrgId       — the Rutter organization the connection belongs to
        ///   ApiBaseUrl  — optional; defaults to https://production.rutterapi.com
        /// </summary>
        private static async Task<int> RunSetupAsync(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: Sage50Connector.exe --setup <CompanyName> <OrgId> [ApiBaseUrl]");
                return 1;
            }

            string companyName = args[1];
            string orgId = args[2];
            string apiBaseUrl = args.Length > 3 && !string.IsNullOrWhiteSpace(args[3])
                ? args[3]
                : ConnectorConfig.DefaultApiBaseUrl;
            string saveIdUrl = apiBaseUrl.TrimEnd('/') + "/sage-50/save-id";

            try
            {
                using (HttpClient client = new HttpClient())
                {
                    var requestBody = new
                    {
                        company_id = companyName,
                        org_id = orgId
                    };

                    var request = new HttpRequestMessage(HttpMethod.Post, saveIdUrl);
                    request.Content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
                    var response = await client.SendAsync(request);
                    var responseContent = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        WriteToFile("Setup failed. Status code: " + response.StatusCode + ", Response: " + responseContent);
                        return 1;
                    }

                    JObject body = JObject.Parse(responseContent);
                    if (body.Value<bool?>("is_successful") != true)
                    {
                        WriteToFile("Setup failed: " + (body.Value<string>("reason") ?? responseContent));
                        return 1;
                    }

                    JObject sage50Config = body["sage50_config"] as JObject;
                    if (sage50Config == null)
                    {
                        WriteToFile("Setup failed: response did not include sage50_config. Response: " + responseContent);
                        return 1;
                    }

                    var config = ConnectorConfig.Save(
                        companyName,
                        sage50Config.Value<string>("AccessKey"),
                        sage50Config.Value<string>("ConnectionId"),
                        apiBaseUrl
                    );

                    WriteToFile(
                        "Setup complete. Wrote "
                            + config.LoadedFromPath
                            + "; CompanyName='"
                            + config.CompanyName
                            + "'; ConnectionId='"
                            + config.ConnectionId
                            + "'; AccessKeyLength="
                            + config.AccessKey.Length
                            + "; ApiBaseUrl="
                            + config.ApiBaseUrl
                    );
                    return 0;
                }
            }
            catch (Exception ex)
            {
                WriteToFile("Setup failed with an exception: " + ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// Pause between poll/report cycles. Rutter hands the same job back while it
        /// is still in progress, so a job we cannot complete would otherwise be
        /// retried as fast as the network allows.
        /// </summary>
        private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(2);

        /// <summary>
        /// How many consecutive failed polls to ride out before giving up. A single
        /// blip - Rutter restarting, the tunnel dropping, the machine waking from
        /// sleep - used to end the process for good, because a null job broke the
        /// loop. Retrying a few times with backoff survives that; exiting after a
        /// sustained outage is still correct, since the service restarts us a
        /// minute later and a one-shot run should not spin forever.
        /// </summary>
        private const int MaxConsecutivePollFailures = 5;

        static async Task MainAsync(string AccessKey, string CompanyName, string ConnectionId)
        {
            bool firstIteration = true;
            int consecutivePollFailures = 0;
            while (true)
            {
                if (!firstIteration)
                {
                    await Task.Delay(PollDelay);
                }
                firstIteration = false;

                WriteToFile("###############################--------####################################################################################");
                WriteToFile(DateTime.Now + ": Process Started");
                ResponseObject job = await GetJobFromRutterAsync(AccessKey);

                if (job != null)
                {
                    consecutivePollFailures = 0;
                    switch (job.type)
                    {
                        case "LIST_FETCH":
                            await HandleListFetchJob(job, AccessKey, CompanyName);
                            break;
                        case "CREATE":
                            if (job.platform_entity == "VENDORS")
                            {
                                await HandleCreateVendorJob(job, AccessKey, CompanyName);
                            }
                            break;
                        case "NOOP":
                            WriteToFile(DateTime.Now + ": Received NOOP job, sleeping for 5 minutes.");
                            await Task.Delay(TimeSpan.FromMinutes(5));
                            break;
                        default:
                            WriteToFile(DateTime.Now + ": Unknown job type: " + job.type);
                            break;
                    }
                }
                else
                {
                    consecutivePollFailures++;
                    if (consecutivePollFailures >= MaxConsecutivePollFailures)
                    {
                        WriteToFile(
                            DateTime.Now
                                + ": Could not reach Rutter after "
                                + consecutivePollFailures
                                + " attempts. Exiting; the service will start us again."
                        );
                        break;
                    }

                    // 2s, 4s, 8s, 16s.
                    TimeSpan backoff = TimeSpan.FromSeconds(Math.Pow(2, consecutivePollFailures));
                    WriteToFile(
                        DateTime.Now
                            + ": Poll failed ("
                            + consecutivePollFailures
                            + " of "
                            + MaxConsecutivePollFailures
                            + "); retrying in "
                            + backoff.TotalSeconds
                            + "s."
                    );
                    await Task.Delay(backoff);
                }
                WriteToFile(DateTime.Now + ": Process Ended.");
                WriteToFile("###################################################################################################################");
            }
        }

        private static async Task<ResponseObject> GetJobFromRutterAsync(string AccessKey)
        {
            using (HttpClient client = new HttpClient())
            {
                WriteToFile(
                    "Polling Rutter ingest for ConnectionId='"
                        + ConnectionId
                        + "'; AccessKeyLength="
                        + AccessKey.Length
                );
                var request = new HttpRequestMessage(HttpMethod.Post, Config.IngestUrl);
                request.Headers.Add("X-Rutter-Version", "2024-04-30");
                request.Headers.Add("Authorization", $"Bearer {AccessKey}");

                var requestBody = new
                {
                    connection = new
                    {
                        id = ConnectionId
                    }
                };

                request.Content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
                var response = await client.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    return JsonConvert.DeserializeObject<ResponseObject>(responseContent);
                }
                else
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    WriteToFile(DateTime.Now + $": Failed to fetch job from Rutter. Status code: {response.StatusCode}, Response: {responseContent}");
                    return null;
                }
            }
        }

        private static async Task HandleListFetchJob(ResponseObject job, string AccessKey, string CompanyName)
        {
            WriteToFile(DateTime.Now + ": Handling LIST_FETCH job for " + job.platform_entity);
            try
            {
                var updatedAt = job.parameters.updated_at;
                var allRecords = GetEntityData(job.platform_entity, CompanyName, updatedAt);
                var page = TakePage(allRecords, job.parameters, out string nextCursor);

                if (page != null && page.Count > 0)
                {
                    WriteToFile(
                        DateTime.Now
                            + ": Fetched " + page.Count + " of " + allRecords.Count + " "
                            + job.platform_entity + "(s) from Sage 50 Company: '" + CompanyName + "'"
                            + (nextCursor == null ? " (final page)" : "; next_cursor=" + nextCursor)
                    );
                    // next_cursor has to be absent, not null, on the final page:
                    // Rutter types it as an optional string, which accepts a missing
                    // key but rejects an explicit null.
                    object responseObject;
                    if (nextCursor == null)
                    {
                        responseObject = new
                        {
                            connection = new { id = ConnectionId },
                            job_id = job.job_id,
                            type = job.type,
                            platform_entity = job.platform_entity,
                            parameters = job.parameters,
                            data = page,
                        };
                    }
                    else
                    {
                        responseObject = new
                        {
                            connection = new { id = ConnectionId },
                            job_id = job.job_id,
                            type = job.type,
                            platform_entity = job.platform_entity,
                            parameters = job.parameters,
                            data = page,
                            next_cursor = nextCursor,
                        };
                    }

                    // Serialize the payload directly. Going through
                    // JObject.FromObject first materialises the property names
                    // with Sage's own casing (ID, Name, IsInactive), and a
                    // ContractResolver has no effect when serializing a JObject -
                    // the names are already fixed. Rutter extracts the primary key
                    // from $.id, so PascalCase names left every record with a null
                    // platform_id and 156 accounts collapsed onto a single row.
                    string jsonString = JsonConvert.SerializeObject(responseObject, new JsonSerializerSettings
                    {
                        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                    });

                    await PostToRutterAsync(jsonString, AccessKey);
                }
                else
                {
                    WriteToFile(DateTime.Now + ": No " + job.platform_entity + "(s) to read. Please ensure Agent has permissions to Sage Company '" + CompanyName + "'");
                    var responseObject = new
                    {
                        connection = new
                        {
                            id = ConnectionId
                        },
                        job_id = job.job_id,
                        type = job.type,
                        platform_entity = job.platform_entity,
                        parameters = job.parameters,
                        data = new List<object>()
                    };

                    string jsonString = JsonConvert.SerializeObject(responseObject, new JsonSerializerSettings
                    {
                        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                    });

                    await PostToRutterAsync(jsonString, AccessKey);
                }
            }
            catch (Exception ex)
            {
                WriteToFile(DateTime.Now + ": Error handling LIST_FETCH job for " + job.platform_entity + ". Error: " + ex.Message);
                // parameters must be echoed back even on the error path: Rutter
                // validates a LIST_FETCH report against a schema that requires it,
                // and rejects the report with a 500 when it is missing.
                var errorObject = new
                {
                    connection = new
                    {
                        id = ConnectionId
                    },
                    job_id = job.job_id,
                    type = job.type,
                    platform_entity = job.platform_entity,
                    parameters = job.parameters,
                    error_message = ex.Message
                };

                string jsonString = JsonConvert.SerializeObject(errorObject, new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                });

                await PostToRutterAsync(jsonString, AccessKey);
            }
        }

        /// <summary>
        /// Cuts one page out of the records Sage gave us.
        ///
        /// Rutter sends a limit (and, after the first page, the id the last page
        /// ended on) and completes the job only when we answer without a
        /// next_cursor. The connector used to ignore both and post everything in a
        /// single body, which is survivable for a sample company and not for a real
        /// ledger.
        ///
        /// Paging is keyed on the record id rather than an offset: Sage is a live
        /// database, and an offset silently skips a record whenever something is
        /// inserted earlier in the order between pages.
        /// </summary>
        private static List<object> TakePage(List<object> records, Parameters parameters, out string nextCursor)
        {
            nextCursor = null;
            if (records == null)
            {
                return new List<object>();
            }

            var ordered = records
                .OrderBy(record => GetRecordId(record), StringComparer.Ordinal)
                .ToList();

            if (!string.IsNullOrEmpty(parameters?.cursor))
            {
                ordered = ordered
                    .Where(record => StringComparer.Ordinal.Compare(GetRecordId(record), parameters.cursor) > 0)
                    .ToList();
            }

            int limit = parameters != null && parameters.limit > 0 ? parameters.limit : ordered.Count;
            if (ordered.Count > limit)
            {
                var page = ordered.Take(limit).ToList();
                nextCursor = GetRecordId(page[page.Count - 1]);
                return page;
            }

            return ordered;
        }

        /// <summary>
        /// Every record we send Rutter carries an ID property (AccountBody,
        /// ChartofVendor, ChartofCustomer). Reflection keeps the paging code from
        /// having to know which one it is holding.
        /// </summary>
        private static string GetRecordId(object record)
        {
            var property = record?.GetType().GetProperty("ID");
            return property?.GetValue(record)?.ToString() ?? string.Empty;
        }

        private static List<object> GetEntityData(string entity, string companyName, string updatedAt)
        {
            WriteToFile(DateTime.Now + $": Fetching {entity} data for company: {companyName} with updated_at: {updatedAt}");
            List<object> data = new List<object>();

            switch (entity)
            {
                case "VENDORS":
                    var vendors = Sage50Repository.Instance.GetVendors(companyName, updatedAt);
                    WriteToFile(DateTime.Now + $": Retrieved {vendors.Count} vendors from Sage 50 before filtering.");
                    data = vendors.Cast<object>().ToList();
                    break;
                case "ACCOUNTS":
                    var accounts = Sage50Repository.Instance.GetAccounts(companyName).Select(account => new AccountBody
                    {
                        ID = account.ID,
                        Description = account.Description,
                        Classification = account.Classification,
                        IsInactive = account.IsInactive,
                        Key = account.Key
                    }).Cast<object>().ToList();
                    WriteToFile(DateTime.Now + $": Retrieved {accounts.Count} accounts from Sage 50.");
                    data = accounts;
                    break;
                case "CUSTOMERS":
                    var customers = Sage50Repository.Instance.GetCustomers(companyName, updatedAt);
                    WriteToFile(DateTime.Now + $": Retrieved {customers.Count} customers from Sage 50.");
                    data = customers.Cast<object>().ToList();
                    break;
                default:
                    throw new ArgumentException("Unknown platform entity: " + entity);
            }

            WriteToFile(DateTime.Now + $": Returning {data.Count} {entity}(s) after filtering.");
            return data;
        }

        private static async Task HandleCreateVendorJob(ResponseObject job, string AccessKey, string companyName)
        {
            WriteToFile(DateTime.Now + ": Handling CREATE job for VENDORS");
            try
            {
                var vendorBody = JsonConvert.DeserializeObject<VendorBody>(job.create_body.data.ToString());
                WriteToFile(DateTime.Now + ": Creating Vendor in Sage 50: " + vendorBody.Name);

                // Create the vendor in Sage 50
                var createdVendor = Sage50Repository.Instance.CreateVendor(companyName, vendorBody);
                if (createdVendor != null)
                {
                    // Fetch the created vendor to ensure complete details
                    var fetchedVendor = Sage50Repository.Instance.GetVendorById(companyName, createdVendor.ID);
                    if (fetchedVendor != null)
                    {
                        WriteToFile(DateTime.Now + ": Created Vendor in Sage 50: " + fetchedVendor.Name);

                        var responseObject = new
                        {
                            connection = new
                            {
                                id = ConnectionId
                            },
                            job_id = job.job_id,
                            type = job.type,
                            platform_entity = job.platform_entity,
                            data = new List<VendorBody> { fetchedVendor }
                        };

                        string jsonString = JsonConvert.SerializeObject(responseObject, new JsonSerializerSettings
                        {
                            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                        });

                        await PostToRutterAsync(jsonString, AccessKey);
                    }
                    else
                    {
                        WriteToFile(DateTime.Now + ": Failed to fetch the created vendor from Sage 50.");
                    }
                }
                else
                {
                    WriteToFile(DateTime.Now + ": Failed to create vendor in Sage 50.");
                }
            }
            catch (Exception ex)
            {
                WriteToFile(DateTime.Now + ": Error handling CREATE job for VENDORS. Error: " + ex.Message);
                var errorObject = new
                {
                    connection = new
                    {
                        id = ConnectionId
                    },
                    job_id = job.job_id,
                    type = job.type,
                    platform_entity = job.platform_entity,
                    error_message = ex.Message
                };

                string jsonString = JsonConvert.SerializeObject(errorObject, new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                });

                await PostToRutterAsync(jsonString, AccessKey);
            }
        }



        private static async Task PostToRutterAsync(string jsonString, string AccessKey)
        {
            using (HttpClient client = new HttpClient())
            {
                var request = new HttpRequestMessage(HttpMethod.Post, Config.IngestUrl);
                request.Headers.Add("X-Rutter-Version", "2024-04-30");
                request.Headers.Add("Authorization", $"Bearer {AccessKey}");
                request.Content = new StringContent(jsonString, Encoding.UTF8, "application/json");

                var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    WriteToFile(DateTime.Now + $": Failed to post to Rutter. Status code: {response.StatusCode}, Response: {responseContent}");
                }
                else
                {
                    WriteToFile(DateTime.Now + ": Successfully posted to Rutter.");
                }
            }
        }

        private static readonly object logLock = new object();

        public static void WriteToFile(string message)
        {
            string logMessage = DateTime.Now.ToString() + ": " + message;
            Console.WriteLine(logMessage);

            // Ensure thread safety if the application becomes multi-threaded
            lock (logLock)
            {
                using (StreamWriter writer = new StreamWriter(ConnectorConfig.ResolveLogFilePath(), true))
                {
                    writer.WriteLine(logMessage);
                    writer.Close();
                }
            }
        }
    }
}
