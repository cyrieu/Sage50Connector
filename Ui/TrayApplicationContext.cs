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
        private bool updateBalloonShown;
        private readonly ManualResetEventSlim syncNowSignal = new ManualResetEventSlim(false);
        private string apiBaseUrlForUpdates = ConnectorConfig.DefaultApiBaseUrl;

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
            menu.Items.Add("Check for updates…", null, async (s, e) => await CheckForUpdatesInteractiveAsync());
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
            statusForm.UpdateRequested += async (s, e) => await CheckForUpdatesInteractiveAsync();

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

            // Quiet daily check; never auto-installs (Sage re-approval required).
            Task.Run(() => BackgroundUpdateLoop());
        }

        /// <summary>Called from Program once config is loaded so checks hit the right API host.</summary>
        internal void SetApiBaseUrl(string apiBaseUrl)
        {
            if (!string.IsNullOrWhiteSpace(apiBaseUrl))
            {
                apiBaseUrlForUpdates = apiBaseUrl.Trim();
            }
        }

        private async Task BackgroundUpdateLoop()
        {
            // Delay so first boot / setup is not competing with config + Sage probe.
            await Task.Delay(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            while (true)
            {
                try
                {
                    UpdateCheckResult result = await UpdateService
                        .CheckForUpdatesAsync(apiBaseUrlForUpdates, force: false)
                        .ConfigureAwait(false);
                    if (result != null && result.HasUpdate && !updateBalloonShown)
                    {
                        updateBalloonShown = true;
                        string title = result.IsForced
                            ? "Connector update required"
                            : "Connector update available";
                        string body = "Version " + (result.Release != null ? result.Release.Version : "")
                            + " is available. Open the tray menu → Check for updates. "
                            + "Installing requires re-approval in Sage 50.";
                        try
                        {
                            trayIcon.ShowBalloonTip(12000, title, body, ToolTipIcon.Info);
                        }
                        catch { }
                    }
                    if (result != null && result.Availability == UpdateAvailability.UpToDate)
                    {
                        updateBalloonShown = false;
                    }
                }
                catch { /* never crash the tray for update checks */ }

                await Task.Delay(TimeSpan.FromHours(6)).ConfigureAwait(false);
            }
        }

        private async Task CheckForUpdatesInteractiveAsync()
        {
            try
            {
                UpdateCheckResult result = await UpdateService
                    .CheckForUpdatesAsync(apiBaseUrlForUpdates, force: true)
                    .ConfigureAwait(true);

                if (result.Availability == UpdateAvailability.CheckFailed)
                {
                    MessageBox.Show(
                        "Could not check for updates.\r\n\r\n" + (result.ErrorMessage ?? "Unknown error"),
                        RuntimeEnvironment.DisplayName,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                if (!result.HasUpdate)
                {
                    MessageBox.Show(
                        "You are running the latest connector (" + AppVersion.Display + ").",
                        RuntimeEnvironment.DisplayName,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                string notes = result.Release != null ? result.Release.Notes : null;
                string msg =
                    "A new version of the Rutter Sage 50 Connector is available.\r\n\r\n"
                    + "Current:  " + AppVersion.Display + "\r\n"
                    + "Available: " + (result.Release != null ? result.Release.Version : "?") + "\r\n\r\n"
                    + "Installing a new version changes the executable identity. "
                    + "Sage 50 will require you to re-approve access (File → Close Company, "
                    + "reopen company, Always Allow Access).\r\n\r\n"
                    + (string.IsNullOrWhiteSpace(notes) ? "" : notes + "\r\n\r\n")
                    + (result.IsForced
                        ? "This update is required.\r\n\r\n"
                        : "")
                    + "Download and install now? Windows may prompt for administrator permission.";

                DialogResult answer = MessageBox.Show(
                    msg,
                    result.IsForced ? "Update required" : "Update available",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;

                await UpdateService.ApplyUpdateAsync(
                    result.Release,
                    line => Program.WriteToFile(DateTime.Now + ": " + line)).ConfigureAwait(true);

                // Give the elevated script a moment to start, then exit so files unlock.
                await Task.Delay(1500).ConfigureAwait(true);
                ExitConnector();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Update failed: " + ex.Message,
                    RuntimeEnvironment.DisplayName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
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
                    bool needsTransactions = s.SageAuthorization == SageAuthorizationState.Granted
                        && s.ComAuthorization == SageAuthorizationState.Required;
                    trayIcon.ShowBalloonTip(
                        10000,
                        needsTransactions
                            ? "Sage 50 transaction approval needed"
                            : "Sage 50 approval needed for this version",
                        needsTransactions
                            ? "Keep the company open, check “Remember this setting” in the Peachtree Software prompt, then click Yes."
                            : "Open Sage 50, close and reopen your company, choose “Always Allow Access”, then click Check access.",
                        ToolTipIcon.Warning);
                }
                catch { }
            }
            if (s.State == ConnectorState.Syncing || s.State == ConnectorState.Idle)
            {
                authBalloonShown = false;
            }

            if (s.UpdateAvailability == UpdateAvailability.OptionalUpdate
                || s.UpdateAvailability == UpdateAvailability.RequiredUpdate)
            {
                // Keep tip short; full detail is in Check for updates / status form.
                string prefix = RuntimeEnvironment.TrayStatusPrefix;
                string updateTip = prefix + "Update " + (s.AvailableVersion ?? "") + " available";
                if (updateTip.Length > 60) updateTip = updateTip.Substring(0, 57) + "…";
                try { trayIcon.Text = updateTip; } catch { }
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
            else if (SyncStatus.Instance.ComAuthorization == SageAuthorizationState.Required
                || SyncStatus.Instance.ComAuthorization == SageAuthorizationState.Checking)
            {
                SyncStatus.Instance.SetCheckingComAuthorization(
                    "Checking Sage transaction access…");
                // This both retries the optional COM approval and wakes the
                // connector so a successful check is followed by an immediate
                // poll for sync work.
                Program.RequestComAuthorizationRetry();
                return;
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
