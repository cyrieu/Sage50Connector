using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Configuration.Install;
using System.ServiceProcess;
using Microsoft.Win32;
using System.Collections;
using System.IO;
using System.Threading;
using System.Diagnostics;
using Newtonsoft.Json;

namespace Sage50ConnectorService
{
    [RunInstaller(true)]
    
    public class MyServiceInstaller : Installer
    {
        private ServiceProcessInstaller serviceProcessInstaller;
        private ServiceInstaller serviceInstaller;

        public MyServiceInstaller()
        {
            InitializeComponent();
        }

        // Call this during installation
        public override void Install(IDictionary stateSaver)
        {
            base.Install(stateSaver);

            var data = new
            {
                CompanyName = Context.Parameters["CompanyName"],
                AccessKey = Context.Parameters["AccessKey"],
                ConnectionID = Context.Parameters["ConnectionID"]
            };

            // Specify the file path
            string filePath = @"C:\Users\Default\Documents\Sage50\sage50Config.json";

            // Serialize the data to JSON using Newtonsoft.Json
            string jsonData = JsonConvert.SerializeObject(data, Formatting.Indented);

            // Write the JSON data to the file
            File.WriteAllText(filePath, jsonData);

            // Proceed with the service installation
            InstallService();
        }
        
        private void InstallService()
        {

            StopService();
            using (ServiceController serviceController = new ServiceController(serviceInstaller.ServiceName))
            {
                serviceController.Start();
            }
        }
        
        private void RemoveService(string serviceName)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = $"delete {serviceName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process process = new Process { StartInfo = psi })
                {
                    process.Start();
                    process.WaitForExit();

                    // Check the exit code to determine if the operation was successful
                    if (process.ExitCode == 0)
                    {
                        Console.WriteLine($"Service '{serviceName}' successfully deleted.");
                    }
                    else
                    {
                        Console.WriteLine($"Error deleting service '{serviceName}'. Exit code: {process.ExitCode}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
            }
        }
        
        private void StopService()
        {
            try
            {
                using (ServiceController serviceController = new ServiceController(serviceInstaller.ServiceName))
                {
                    if (serviceController.Status == ServiceControllerStatus.Running)
                    {
                        serviceController.Stop();
                        serviceController.WaitForStatus(ServiceControllerStatus.Stopped);

                    }
                    Installers.Remove(serviceInstaller);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error stopping service: {ex.Message}");
            }
        }
        
        public override void Uninstall(IDictionary savedState)
        {
            //StopService();
            base.Uninstall(savedState);

            // Add code to uninstall the service during uninstallation
        }

        // TODO: Actually use this method to save the data to the registry
        private void SaveToRegistry(string keyName, string value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Sage\\Sage50Connector", true))
                {
                    var k = key;
                    if (k == null)
                    {
                        // Create the registry key if it doesn't exist
                        k = Registry.CurrentUser.CreateSubKey("SOFTWARE\\Sage\\Sage50Connector");
                    }

                    // Set the registry value
                    k.SetValue(keyName, value);
                }
            }
            catch (Exception ex)
            {
                // Handle any exceptions that may occur during registry access
                Console.WriteLine($"Error saving to registry: {ex.Message}");
            }
        }

        private void RemoveFromRegistry(string keyName)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Sage\\Sage50Connector", true))
                {
                    var k = key;
                    if (k != null)
                    {
                        // Remove the registry value
                        k.DeleteValue(keyName, false);

                        // Optional: Delete the registry key if it has no values
                        if (k.GetValueNames().Length == 0)
                        {
                            Registry.CurrentUser.DeleteSubKey("SOFTWARE\\Sage\\Sage50Connector");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Handle any exceptions that may occur during registry access
                Console.WriteLine($"Error removing from registry: {ex.Message}");
            }
        }
        
        private void InitializeComponent()
        {
            // Create the ServiceProcessInstaller
            this.serviceProcessInstaller = new ServiceProcessInstaller();
            this.serviceProcessInstaller.Account = ServiceAccount.LocalSystem;

            // Create the ServiceInstaller
            this.serviceInstaller = new ServiceInstaller();

            this.serviceInstaller.ServiceName = "Sage50ConnectorService"; // Set your service name
            this.serviceInstaller.DisplayName = "Sage 50 Connector"; // Set your service display name
            this.serviceInstaller.Description = "Sage 50 Connector Service"; // Set your service description
            this.serviceInstaller.StartType = ServiceStartMode.Automatic;

            // Add installers to the collection
            Installers.Add(serviceProcessInstaller);
            Installers.Add(serviceInstaller);

        }
        
        private void ClearApplicationFolder()
        {
            try
            {
                Thread.Sleep(3000);
                string applicationFolder = "C:\\Program Files (x86)\\Sage\\Sage50ConnectorSetup";
                
                // Ensure the folder exists before trying to delete it
                if (Directory.Exists(applicationFolder))
                {
                    // Delete all files in the folder

                    foreach (string file in Directory.GetFiles(applicationFolder))
                    {
                        File.Delete(file);
                    }

                    // Delete all subdirectories and their files
                    foreach (string subDirectory in Directory.GetDirectories(applicationFolder))
                    {
                        Directory.Delete(subDirectory, true);
                    }

                    // Finally, delete the main folder
                    Directory.Delete(applicationFolder);

                }
            }
            catch (Exception ex)
            {
                File.WriteAllText("C:\\3.txt", $"Error clearing application folder: {ex.Message}");
            }
        }
    }
}
