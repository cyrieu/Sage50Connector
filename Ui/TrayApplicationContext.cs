using Sage50Connector.Helpers;
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Sage50Connector.Ui
{
    /// <summary>
    /// Hosts the connector as a tray application instead of a console window.
    ///
    /// Running in the logged-on user's session is not just cosmetic: Sage records
    /// third-party access per Windows user, so a connector running here is
    /// automatically the identity that approved it. The Windows service had to be
    /// handed a service account to achieve the same thing.
    /// </summary>
    public class TrayApplicationContext : ApplicationContext
    {
        private readonly NotifyIcon trayIcon;
        private StatusForm statusForm;
        private bool authBalloonShown;
        private readonly ManualResetEventSlim syncNowSignal = new ManualResetEventSlim(false);

        public TrayApplicationContext()
        {
            trayIcon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Visible = true,
                Text = RuntimeEnvironment.DisplayName,
            };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Open " + RuntimeEnvironment.DisplayName, null, (s, e) => ShowStatus());
            menu.Items.Add("Sync now", null, (s, e) => RequestSyncNow());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => ExitConnector());
            trayIcon.ContextMenuStrip = menu;
            trayIcon.DoubleClick += (s, e) => ShowStatus();

            SyncStatus.Instance.Changed += OnStatusChanged;

            // Create the window up front, on the UI thread, and keep it for the
            // lifetime of the app. Everything else marshals onto it, so nothing
            // ends up building a Form from a worker thread.
            statusForm = new StatusForm();
            // A Form does not create its native handle until it is first shown.
            // Returning users start hidden in the tray, so the show/quit listener
            // threads could otherwise call BeginInvoke before a handle existed;
            // that throws and leaves a hidden process impossible to stop cleanly.
            // Force handle creation here on the UI thread before either listener
            // starts.
            if (statusForm.Handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Could not create the connector status window handle.");
            }
            statusForm.SyncNowRequested += (s, e) => RequestSyncNow();

            // The sync loop owns a Sage session, so keep it off the UI thread.
            Task.Run(() => Program.RunSyncLoopHeadless(syncNowSignal));

            // Windows hides new tray icons behind the overflow chevron, so a
            // customer who just installed this sees nothing at all. Show the
            // window once on a fresh install so they know it exists and can find
            // the icon; after that it starts quietly in the tray.
            if (!HasRunBefore())
            {
                MarkHasRun();
                ShowStatus();
            }

            ListenForShowRequests();
            ListenForQuitRequests();
        }

        /// <summary>
        /// The exe's own icon, not a file beside it — the working directory is
        /// whatever launched us (Startup, a service, a scheduled task).
        /// </summary>
        internal static Icon LoadIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { return SystemIcons.Application; }
        }

        private void OnStatusChanged(object sender, EventArgs e)
        {
            SyncStatus s = SyncStatus.Instance;

            string tip = RuntimeEnvironment.TrayStatusPrefix + (s.Message ?? string.Empty);
            // NotifyIcon.Text throws above 63 characters.
            if (tip.Length > 60) tip = tip.Substring(0, 57) + "…";
            try { trayIcon.Text = tip; } catch { }

            // Tell the user once — repeating it every poll would be nagging.
            if (s.State == ConnectorState.NeedsAuthorization && !authBalloonShown)
            {
                authBalloonShown = true;
                try
                {
                    trayIcon.ShowBalloonTip(
                        10000,
                        "Sage 50 approval needed for this version",
                        "Open Sage 50, close and reopen your company, choose “Always Allow Access”, then click Check access.",
                        ToolTipIcon.Warning);
                }
                catch { }
            }
            if (s.State == ConnectorState.Syncing || s.State == ConnectorState.Idle)
            {
                authBalloonShown = false;
            }
        }

        /// <summary>
        /// Running the exe again is how someone asks to see the window; that
        /// second process signals this event and exits.
        /// </summary>
        private void ListenForShowRequests()
        {
            var handle = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowWindowEventName);
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        handle.WaitOne();
                        var form = statusForm;
                        if (form != null && !form.IsDisposed)
                        {
                            form.BeginInvoke((Action)ShowStatus);
                        }
                    }
                    catch { /* keep listening */ }
                }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        /// <summary>
        /// Lets a script stop the connector without killing it, so the Sage
        /// session is handed back instead of leaked.
        /// </summary>
        private void ListenForQuitRequests()
        {
            var handle = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEventName);
            var thread = new Thread(() =>
            {
                try
                {
                    handle.WaitOne();
                    var form = statusForm;
                    if (form != null && !form.IsDisposed)
                    {
                        form.BeginInvoke((Action)ExitConnector);
                    }
                }
                catch { /* falling back to being killed is no worse than before */ }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private static string FirstRunMarker
        {
            get { return System.IO.Path.Combine(ConnectorConfig.ConfigDirectory, ".shown"); }
        }

        private static bool HasRunBefore()
        {
            try { return System.IO.File.Exists(FirstRunMarker); }
            catch { return true; }
        }

        private static void MarkHasRun()
        {
            try
            {
                System.IO.Directory.CreateDirectory(ConnectorConfig.ConfigDirectory);
                System.IO.File.WriteAllText(FirstRunMarker, DateTime.Now.ToString("o"));
            }
            catch { /* worst case we show the window again next launch */ }
        }

        private void ShowStatus()
        {
            if (statusForm == null || statusForm.IsDisposed) return;
            statusForm.Show();
            statusForm.WindowState = FormWindowState.Normal;
            statusForm.Activate();
            statusForm.BringToFront();
        }

        /// <summary>
        /// Cut the between-poll sleep short.
        ///
        /// This cannot force a sync: Rutter decides what work exists and the
        /// connector only asks. If nothing is queued the poll comes back with
        /// nothing to do — so say that, rather than leaving the button looking
        /// broken.
        /// </summary>
        private void RequestSyncNow()
        {
            if (SyncStatus.Instance.SageAuthorization == SageAuthorizationState.Required
                || SyncStatus.Instance.SageAuthorization == SageAuthorizationState.Checking)
            {
                SyncStatus.Instance.SetCheckingAuthorization();
            }
            else
            {
                SyncStatus.Instance.SetChecking();
            }
            syncNowSignal.Set();
        }

        private void ExitConnector()
        {
            trayIcon.Visible = false;
            Program.ReleaseSageSession();
            ExitThread();
        }
    }
}
