using Microsoft.Win32;
using Newtonsoft.Json;
using Sage.Peachtree.API;
using Sage50Connector.Helpers;
using Sage50Connector.Models.Rutter;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Runtime.Remoting.Contexts;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sage50Connector
{
    public class Program
    {
        public static string CompanyName;
        public static string AccessKey;
        public static string ConnectionId;
        public static void Main()
        {
            if (CompanyName == null)
            {
                string filePath = @"C:\Users\Default\Documents\sage50Config.json";

                string jsonString = File.ReadAllText(filePath);
                CompanyName = GetValue(jsonString, "CompanyName");


            }
            if (AccessKey == null)
            {
                string filePath = @"C:\Users\Default\Documents\sage50Config.json";

                string jsonString = File.ReadAllText(filePath);
                AccessKey = GetValue(jsonString, "AccessKey");
            }

            if (ConnectionId == null)
            {
                string filePath = @"C:\Users\Default\Documents\sage50Config.json";

                string jsonString = File.ReadAllText(filePath);
                ConnectionId = GetValue(jsonString, "ConnectionId");
            }

            //string CompanyName = "Rutter";// GetFromRegistry("CompanyName");
            MainAsync(AccessKey, CompanyName, ConnectionId ).GetAwaiter().GetResult();
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
        static async Task MainAsync(String AccessKey, String CompanyName, string ConnectionId)
        {
            // Read Accounts from Sage
            
            WriteToFile("###############################--------####################################################################################");
            WriteToFile(DateTime.Now + $": Process Started");
            var accounts = Sage50Repository.Instance.GetAccounts(CompanyName);
            var vendors = Sage50Repository.Instance.GetVendors(CompanyName);
            if (accounts != null && accounts.Count > 0)
            {
                WriteToFile(DateTime.Now + $": Fetched {accounts.Count} account(s) from Sage 50 Company: '{CompanyName}'");
                string jsonString = JsonConvert.SerializeObject(new
                {
                    connection = new
                    {
                        //id = Properties.Settings.Default.id,
                        id = ConnectionId,
                        platform = Properties.Settings.Default.platform,
                        companyId = Properties.Settings.Default.companyId
                    },
                    entity = "ACCOUNTS",
                    data = accounts
                }, new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                });


                // This JSON string "jsonString" can be sent to the Rutter POST API 
                WriteToFile(DateTime.Now + $": Posting {accounts.Count} account(s) to Rutter API");
                await PostToRutterAsync(jsonString,AccessKey);

            }
            else
            {
                WriteToFile(DateTime.Now + $": No account(s) to read. Please ensure Agent has permissions to Sage Company '{CompanyName}'");
            }

            if (vendors != null && vendors.Count > 0)
            {
                WriteToFile(DateTime.Now + $": Fetched {vendors.Count} vendors(s) from Sage 50 Company: '{CompanyName}'");
                string jsonString = JsonConvert.SerializeObject(new
                {
                    connection = new
                    {
                        //id = Properties.Settings.Default.id,
                        id = ConnectionId,
                        platform = Properties.Settings.Default.platform,
                        companyId = Properties.Settings.Default.companyId
                    },
                    entity = "VENDORS",
                    data = vendors
                }, new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                });


                // This JSON string "jsonString" can be sent to the Rutter POST API 
                WriteToFile(DateTime.Now + $": Posting {vendors.Count} vendors(s) to Rutter API");
                await PostToRutterAsync(jsonString, AccessKey);

            }
            else
            {
                WriteToFile(DateTime.Now + $": No vendor(s) to read. Please ensure Agent has permissions to Sage Company '{CompanyName}'");
            }

            int month = 1;
            var balanceSheet =  Sage50Repository.Instance.GetbBalanceSheet(CompanyName, month, Properties.Settings.Default.asset_account_types, Properties.Settings.Default.liabilities_account_types, Properties.Settings.Default.equity_account_types);
            if (balanceSheet != null)
            {
                WriteToFile(DateTime.Now + $": Fetched Balance Sheet of month '{month}' from Sage 50 Company: '{CompanyName}'");
                string jsonString = JsonConvert.SerializeObject(new
                {
                    connection = new Connection
                    {
                        id = Properties.Settings.Default.id,
                        platform = Properties.Settings.Default.platform,
                        companyId = Properties.Settings.Default.companyId
                    },
                    entity = "balancesheet",
                    balanceSheet = balanceSheet
                }, new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                }); 

                // This JSON string "jsonString" can be sent to the Rutter POST API 
                WriteToFile(DateTime.Now + $": Posting Balance Sheet to Rutter API");
                //await PostToRutterAsync(jsonString);

            }
            else
            {
                WriteToFile(DateTime.Now + $": No Balance Sheet to read. Please ensure Agent has permissions to Sage Company '{CompanyName}'");
            }
            WriteToFile(DateTime.Now + $": Process Ended.");
            WriteToFile("###################################################################################################################");
        }

        private static async Task PostToRutterAsync(string jsonString, string AccessKey)
        {
            using (HttpClient client = new HttpClient())
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"https://{Properties.Settings.Default.base_url}");
                request.Headers.Add("X-Rutter-Version", "2023-03-14");
                request.Headers.Add("Authorization", $"Bearer {AccessKey}");
                StringContent content = new StringContent(jsonString, null, "application/json");
                request.Content = content;

                // Send the POST request
                var response = await client.SendAsync(request);
                response.EnsureSuccessStatusCode();
                if (response.IsSuccessStatusCode)
                {
                    WriteToFile(DateTime.Now + $": Successfully posted to Rutter. Status code: {response.StatusCode}, Response: {response.Content}");
                }
                else
                {
                    WriteToFile(DateTime.Now + $": Failed to post to Rutter. Status code: {response.StatusCode}, Response: {response.Content}");
                }
            }
        }

        
        public static void WriteToFile(string Message)
        {
            string path = AppDomain.CurrentDomain.BaseDirectory + @"\Logs";
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            string filepath = AppDomain.CurrentDomain.BaseDirectory + @"Logs\\SageRutterAgentLog_" + DateTime.Now.Date.ToShortDateString().Replace('/', '_') + ".txt";
            if (!File.Exists(filepath))
            {
                // Create a file to write to.   
                using (StreamWriter sw = File.CreateText(filepath))
                {
                    sw.WriteLine(Message);
                }
            }
            else
            {
                using (StreamWriter sw = File.AppendText(filepath))
                {
                    sw.WriteLine(Message);
                }
            }
        }

        static string GetArgumentValue(string[] args, string argumentName)
        {
            foreach (var arg in args)
            {
                if (arg.StartsWith($"/{argumentName}=", StringComparison.OrdinalIgnoreCase))
                {
                    return arg.Substring(argumentName.Length + 2);
                }
            }

            return null;
        }
        private static string GetFromRegistry(string keyName)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Sage\\Sage50Connector", false))
                {
                    var k = key;
                    if (k != null)
                    {
                        // Retrieve the registry value
                        object value = k.GetValue(keyName);

                        // Check if the value is not null before returning
                        if (value != null)
                        {
                            return value.ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Handle any exceptions that may occur during registry access
                Console.WriteLine($"Error getting from registry: {ex.Message}");
            }

            // Return null if the value is not found or an error occurs
            return null;
        }
    }
}
