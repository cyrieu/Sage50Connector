using System;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace Sage50ConnectorService
{
    public partial class Service1 : ServiceBase
    {
        private Timer timer;
        // Guards against the timer firing while the previous run is still going
        // (a NOOP job sleeps 5 minutes inside a single run).
        private int runInProgress;

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

            // Kick off the first run in the background; the console loop sleeps
            // for 5 minutes on a NOOP job, and SCM fails service start if
            // OnStart doesn't return promptly.
            Task.Run(() => RunConnectorOnce());
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
            RunConnectorOnce();
        }

        private void RunConnectorOnce()
        {
            if (Interlocked.CompareExchange(ref runInProgress, 1, 0) != 0)
            {
                return;
            }

            try
            {
                Sage50Connector.Program.Main(new string[0]);
            }
            catch (Exception ex)
            {
                Sage50Connector.Program.WriteToFile("Connector run failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref runInProgress, 0);
            }
        }
    }
}
