using Microsoft.Win32;
using Sage.Peachtree.API;
using Sage50Connector.Helpers;
using Sage50Connector.Models.Rutter;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
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
        public static void Main()
        {
            //string CompanyName = "Rutter";// GetFromRegistry("CompanyName");
            MainAsync(AccessKey, CompanyName).GetAwaiter().GetResult();
        }

        static async Task MainAsync(String AccessKey, String CompanyName)
        {
            // Read Accounts from Sage
            
            WriteToFile("###############################--------####################################################################################");
            WriteToFile(DateTime.Now + $": Process Started");
            var accounts = Sage50Repository.Instance.GetAccounts(CompanyName);
            if (accounts != null && accounts.Count > 0)
            {
                WriteToFile(DateTime.Now + $": Fetched {accounts.Count} account(s) from Sage 50 Company: '{CompanyName}'");
                Connection connection = new Connection
                {
                    Id = Properties.Settings.Default.id,
                    Platform = Properties.Settings.Default.platform,
                    CompanyId = Properties.Settings.Default.companyId
                };
                string jsonString = JsonSerializer.Serialize(new
                {
                    Connection = connection,
                    Entity = "accounts",
                    Accounts = accounts
                }, new JsonSerializerOptions { WriteIndented = true });

                // This JSON string "jsonString" can be sent to the Rutter POST API 
                WriteToFile(DateTime.Now + $": Posting {accounts.Count} account(s) to Rutter API");
                //await PostToRutterAsync(jsonString);

            }
            else
            {
                WriteToFile(DateTime.Now + $": No account(s) to read. Please ensure Agent has permissions to Sage Company '{CompanyName}'");
            }


            int month = 1;
            var balanceSheet = Sage50Repository.Instance.GetbBalanceSheet(CompanyName, month, Properties.Settings.Default.asset_account_types, Properties.Settings.Default.liabilities_account_types, Properties.Settings.Default.equity_account_types);
            if (balanceSheet != null)
            {
                WriteToFile(DateTime.Now + $": Fetched Balance Sheet of month '{month}' from Sage 50 Company: '{CompanyName}'");
                Connection connection = new Connection
                {
                    Id = Properties.Settings.Default.id,
                    Platform = Properties.Settings.Default.platform,
                    CompanyId = Properties.Settings.Default.companyId
                };
                string jsonString = JsonSerializer.Serialize(new
                {
                    Connection = connection,
                    Entity = "balancesheet",
                    BalanceSheet = balanceSheet
                }, new JsonSerializerOptions { WriteIndented = true });

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

        private static async Task PostToRutterAsync(string jsonString,String AccessKey)
        {

            using (HttpClient client = new HttpClient())
            {
                // Request URL
                string url = $"{Properties.Settings.Default.base_url}/ingest?access_token={AccessKey}";

                // Configure headers
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.Add("X-Rutter-Version", "2023-03-14"); // Adding the X-Rutter-Version header

                // Basic Authentication                
                string authHeaderValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{Properties.Settings.Default.client_id}:{Properties.Settings.Default.client_secret}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeaderValue);

                // Create the request content
                StringContent content = new StringContent(jsonString, Encoding.UTF8, "application/json");
                var request = new HttpRequestMessage
                {
                    Method =  HttpMethod.Post,
                    Content = content,
                    RequestUri = new Uri(url),
                };
                // Send the POST request
                HttpResponseMessage response = await client.PostAsync(url, content);
                string responseBody = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    WriteToFile(DateTime.Now + $": Successfully posted to Rutter. Status code: {response.StatusCode} , Response: {responseBody}");                    
                }
                else
                {
                    WriteToFile(DateTime.Now + $": Failed to post to Rutter. Status code: {response.StatusCode}, Response: {responseBody}");
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
