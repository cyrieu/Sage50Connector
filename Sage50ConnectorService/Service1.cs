using System;
using System.Globalization;
using System.ServiceProcess;
using System.Timers;
using Microsoft.Win32;
using Sage50Connector;
using Sage50Connector.Helpers;
using System.IO;
using System.Security.Principal;
namespace Sage50ConnectorService
{
    public partial class Service1 : ServiceBase
    {
        private Timer timer;
        public Service1()
        {
            InitializeComponent();
            
        }

        protected override void OnStart(string[] args)
        {
            // Initialize and start the timer
            timer = new Timer();
            timer.Elapsed += new ElapsedEventHandler(OnTimerElapsed);
            timer.Interval = 60000; // 1 minute in milliseconds
            timer.Enabled = true;

            // Call the Proc.Main function immediately on service start
            //string CompanyName = Environment.GetEnvironmentVariable("CompanyName", EnvironmentVariableTarget.Machine);
            //var (accessKey, CompanyName) = GetFromConfigFile("C:\\Users\\RutterQuickbooks!\\Documents\\Sage\\config.json");
            string CompanyName = File.ReadAllText($"C:\\Users\\Default\\Documents\\sage50Config-CompanyName.txt");
            string AccessKey = File.ReadAllText($"C:\\Users\\Default\\Documents\\sage50Config-AccessKey.txt");
            Sage50Connector.Program.CompanyName = CompanyName;
            Sage50Connector.Program.AccessKey = AccessKey;
            Sage50Connector.Program.Main();
        }

        protected override void OnStop()
        {
            // Stop the timer when the service is stopped
            timer.Enabled = false;
            timer.Dispose();
        }

        private void OnTimerElapsed(object source, ElapsedEventArgs e)
        {
            // This method will be called every 1 minute
            Sage50Connector.Program.Main();
        }
        private (string AccessKey, string CompanyName) GetFromConfigFile(string filePath)
        {
            try
            {
                // Read the JSON content from the file
                string json = System.IO.File.ReadAllText(filePath);

                // Deserialize the JSON to an object
                var config = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(json);

                // Extract the values
                string accessKey = config.AccessKey;
                string companyName = config.CompanyName;

                return (accessKey, companyName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting from config file: {ex.Message}");
                return (null, null);
            }
        }

        // Call this in your service logic
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
