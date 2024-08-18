using Microsoft.Win32;
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

        // Define the directory path as a constant since it doesn't change
        private const string LogDirectoryPath = @"C:\Users\Default\Documents\Sage50";

        // Create a file name with the timestamp
        private const string LogFileName = "sage50logs.txt";

        public static string CompanyName;
        public static string AccessKey;
        public static string ConnectionID;

        public static void Main()
        {
            // Ensure the log directory exists
            Directory.CreateDirectory(LogDirectoryPath);

            WriteToLogFile("##########################################################################################################");
            WriteToLogFile("Process Started");

            if (CompanyName == null || AccessKey == null || ConnectionID == null)
            {
                string filePath = @"C:\Users\Default\Documents\Sage50\sage50Config.json";
                string jsonString = File.ReadAllText(filePath);
                JObject json = JObject.Parse(jsonString);

                CompanyName = (string)json["CompanyName"];
                AccessKey = (string)json["AccessKey"];
                ConnectionID = (string)json["ConnectionID"];

                WriteToLogFile($"Read from config file {filePath} CompanyName: {CompanyName}");
                WriteToLogFile($"Read from config file {filePath} ConnectionID: {ConnectionID}");
                WriteToLogFile($"Read from config file {filePath} AccessKey: {AccessKey}");
            }

            MainAsync(AccessKey, CompanyName, ConnectionID).GetAwaiter().GetResult();
        }

        static string GetValue(string jsonString, string key)
        {
            int keyIndex = jsonString.IndexOf("\"" + key + "\":") + key.Length + 3; // Adjust for quotes, colon, and potential spaces
            int endIndex = jsonString.IndexOf(",", keyIndex);
            if (endIndex == -1)
            {
                endIndex = jsonString.IndexOf("}", keyIndex);
            }

            return jsonString.Substring(keyIndex, endIndex - keyIndex);
        }

        static async Task MainAsync(string AccessKey, string CompanyName, string ConnectionID)
        {
            while (true)
            {
                ResponseObject job = await GetJobFromRutterAsync();

                if (job != null)
                {
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
                            WriteToLogFile("Received NOOP job, sleeping for 5 minutes.");
                            await Task.Delay(TimeSpan.FromMinutes(5));
                            break;
                        default:
                            WriteToLogFile("Unknown job type: " + job.type);
                            break;
                    }
                }
                else
                {
                    WriteToLogFile("No job available.");
                    break;
                }
                WriteToLogFile("Process Ended.");
                WriteToLogFile("##########################################################################################################");
            }
        }

        private static async Task<ResponseObject> GetJobFromRutterAsync()
        {
            using (HttpClient client = new HttpClient())
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "https://production.rutterapi.com/versioned/ingest");
                request.Headers.Add("X-Rutter-Version", "2024-04-30");
                request.Headers.Add("Authorization", $"Bearer {AccessKey}");

                var requestBody = new
                {
                    connection = new
                    {
                        id = ConnectionID
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
                    WriteToLogFile($"Failed to fetch job from Rutter. Status code: {response.StatusCode}, Response: {responseContent}");
                    return null;
                }
            }
        }

        private static async Task HandleListFetchJob(ResponseObject job, string AccessKey, string CompanyName)
        {
            WriteToLogFile("Handling LIST_FETCH job for " + job.platform_entity);
            try
            {
                var updatedAt = job.parameters.updated_at;
                var data = GetEntityData(job.platform_entity, CompanyName, updatedAt);

                if (data != null && data.Count > 0)
                {
                    WriteToLogFile("Fetched " + data.Count + " " + job.platform_entity + "(s) from Sage 50 Company: '" + CompanyName + "'");
                    var responseObject = new
                    {
                        connection = new
                        {
                            id = ConnectionID
                        },
                        job_id = job.job_id,
                        type = job.type,
                        platform_entity = job.platform_entity,
                        parameters = job.parameters,
                        data = data,
                    };

                    var jsonObject = JObject.FromObject(responseObject);
                    string jsonString = JsonConvert.SerializeObject(jsonObject, new JsonSerializerSettings
                    {
                        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                    });

                    await PostToRutterAsync(jsonString, AccessKey);
                }
                else
                {
                    WriteToLogFile("No " + job.platform_entity + "(s) to read. Please ensure Agent has permissions to Sage Company '" + CompanyName + "'");
                    var responseObject = new
                    {
                        connection = new
                        {
                            id = ConnectionID
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
                WriteToLogFile("Error handling LIST_FETCH job for " + job.platform_entity + ". Error: " + ex.Message);
                var errorObject = new
                {
                    connection = new
                    {
                        id = ConnectionID
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

        private static List<object> GetEntityData(string entity, string companyName, string updatedAt)
        {
            WriteToLogFile($"Fetching {entity} data for company: {companyName} with updated_at: {updatedAt}");
            List<object> data = new List<object>();

            switch (entity)
            {
                case "VENDORS":
                    var vendors = Sage50Repository.Instance.GetVendors(companyName, updatedAt);
                    WriteToLogFile($"Retrieved {vendors.Count} vendors from Sage 50 before filtering.");
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
                    WriteToLogFile($"Retrieved {accounts.Count} accounts from Sage 50.");
                    data = accounts;
                    break;
                case "CUSTOMERS":
                    var customers = Sage50Repository.Instance.GetCustomers(companyName, updatedAt);
                    WriteToLogFile($"Retrieved {customers.Count} customers from Sage 50.");
                    data = customers.Cast<object>().ToList();
                    break;
                default:
                    throw new ArgumentException("Unknown platform entity: " + entity);
            }

            WriteToLogFile($"Returning {data.Count} {entity}(s) after filtering.");
            return data;
        }

        private static async Task HandleCreateVendorJob(ResponseObject job, string AccessKey, string companyName)
        {
            WriteToLogFile("Handling CREATE job for VENDORS");
            try
            {
                var vendorBody = JsonConvert.DeserializeObject<VendorBody>(job.create_body.data.ToString());
                WriteToLogFile("Creating Vendor in Sage 50: " + vendorBody.Name);

                // Create the vendor in Sage 50
                var createdVendor = Sage50Repository.Instance.CreateVendor(companyName, vendorBody);
                if (createdVendor != null)
                {
                    // Fetch the created vendor to ensure complete details
                    var fetchedVendor = Sage50Repository.Instance.GetVendorById(companyName, createdVendor.ID);
                    if (fetchedVendor != null)
                    {
                        WriteToLogFile("Created Vendor in Sage 50: " + fetchedVendor.Name);

                        var responseObject = new
                        {
                            connection = new
                            {
                                id = ConnectionID
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
                        WriteToLogFile("Failed to fetch the created vendor from Sage 50.");
                    }
                }
                else
                {
                    WriteToLogFile("Failed to create vendor in Sage 50.");
                }
            }
            catch (Exception ex)
            {
                WriteToLogFile("Error handling CREATE job for VENDORS. Error: " + ex.Message);
                var errorObject = new
                {
                    connection = new
                    {
                        id = ConnectionID
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
                var request = new HttpRequestMessage(HttpMethod.Post, "https://production.rutterapi.com/versioned/ingest");
                request.Headers.Add("X-Rutter-Version", "2024-04-30");
                request.Headers.Add("Authorization", $"Bearer {AccessKey}");
                request.Content = new StringContent(jsonString, Encoding.UTF8, "application/json");

                var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    WriteToLogFile($"Failed to post to Rutter. Status code: {response.StatusCode}, Response: {responseContent}");
                }
                else
                {
                    WriteToLogFile("Successfully posted to Rutter.");
                }
            }
        }


        private static readonly object logLock = new object();
        public static void WriteToLogFile(string message)
        {
            string logMessage = DateTime.Now.ToString() + ": " + message;
            Console.WriteLine(logMessage);

            // Ensure thread safety if the application becomes multi-threaded
            lock (logLock)
            {
                using (StreamWriter writer = new StreamWriter(Path.Combine(LogDirectoryPath, LogFileName), true))
                {
                    writer.WriteLine(logMessage);
                    writer.Close();
                }
            }
        }
    }
}
