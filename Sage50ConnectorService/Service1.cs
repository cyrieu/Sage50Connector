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
    }
}
