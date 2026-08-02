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
                Text = "Rutter Sage 50 Connector",
            };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Open Rutter Sage 50 Connector", null, (s, e) => ShowStatus());
            menu.Items.Add("Sync now", null, (s, e) => RequestSyncNow());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => ExitConnector());
            trayIcon.ContextMenuStrip = menu;
            trayIcon.DoubleClick += (s, e) => ShowStatus();

            SyncStatus.Instance.Changed += OnStatusChanged;

            // The sync loop owns a Sage session, so keep it off the UI thread.
            Task.Run(() => Program.RunSyncLoopHeadless(syncNowSignal));
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

            string tip = s.Message ?? string.Empty;
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
                        "Sage 50 authorization needed",
                        "Open Sage 50, close and reopen your company, and choose “Always Allow Access”.",
                        ToolTipIcon.Warning);
                }
                catch { }
            }
            if (s.State == ConnectorState.Syncing || s.State == ConnectorState.Idle)
            {
                authBalloonShown = false;
            }
        }

        private void ShowStatus()
        {
            if (statusForm == null || statusForm.IsDisposed)
            {
                statusForm = new StatusForm();
                statusForm.SyncNowRequested += (s, e) => RequestSyncNow();
            }
            statusForm.Show();
            statusForm.WindowState = FormWindowState.Normal;
            statusForm.Activate();
        }

        private void RequestSyncNow()
        {
            // The loop sleeps between polls; this wakes it early.
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
