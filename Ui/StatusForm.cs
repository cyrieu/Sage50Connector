using Sage50Connector.Helpers;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Sage50Connector.Ui
{
    /// <summary>
    /// The window behind the tray icon: what is syncing, how far along, and what
    /// to do when Sage has not authorized us yet.
    ///
    /// Built in code rather than with a designer so the whole layout is reviewable
    /// in one file.
    /// </summary>
    public class StatusForm : Form
    {
        private readonly Label companyLabel = new Label();
        private readonly Label stateLabel = new Label();
        private readonly Label authorizationLabel = new Label();
        private readonly Label lastSyncLabel = new Label();
        private readonly Label versionLabel = new Label();
        private readonly Label updateLabel = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly ListView entityList = new ListView();
        private readonly Panel authPanel = new Panel();
        private readonly Button syncNowButton = new Button();

        public event EventHandler SyncNowRequested;

        public StatusForm()
        {
            Text = RuntimeEnvironment.DisplayName;
            ClientSize = new Size(470, 458);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;
            Icon = TrayApplicationContext.LoadIcon();

            companyLabel.SetBounds(14, 14, 440, 20);
            companyLabel.Font = new Font(Font, FontStyle.Bold);

            stateLabel.SetBounds(14, 38, 440, 36);

            progress.SetBounds(14, 78, 440, 16);
            progress.Minimum = 0;
            progress.Maximum = 100;

            authorizationLabel.SetBounds(14, 100, 440, 20);
            authorizationLabel.Font = new Font(Font, FontStyle.Bold);

            lastSyncLabel.SetBounds(14, 120, 440, 20);
            lastSyncLabel.ForeColor = SystemColors.GrayText;

            versionLabel.SetBounds(14, 140, 440, 18);
            versionLabel.ForeColor = SystemColors.GrayText;
            versionLabel.Text = "Version " + AppVersion.Display
                + (RuntimeEnvironment.IsInstalled ? "" : " (development build)");

            updateLabel.SetBounds(14, 158, 440, 36);
            updateLabel.ForeColor = SystemColors.GrayText;

            entityList.SetBounds(14, 196, 440, 100);
            entityList.View = View.Details;
            entityList.FullRowSelect = true;
            entityList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            entityList.Columns.Add("Data", 170);
            entityList.Columns.Add("Records", 90);
            entityList.Columns.Add("Last synced", 170);

            BuildAuthPanel();

            syncNowButton.SetBounds(14, 418, 100, 26);
            syncNowButton.Text = "Sync now";
            syncNowButton.Click += (s, e) =>
            {
                EventHandler h = SyncNowRequested;
                if (h != null) h(this, EventArgs.Empty);
            };

            Button logsButton = new Button();
            logsButton.SetBounds(122, 418, 100, 26);
            logsButton.Text = "Open logs";
            logsButton.Click += (s, e) => OpenLogFolder();

            Button closeButton = new Button();
            closeButton.SetBounds(354, 418, 100, 26);
            closeButton.Text = "Close";
            closeButton.Click += (s, e) => Hide();

            Controls.AddRange(new Control[]
            {
                companyLabel, stateLabel, progress, authorizationLabel, lastSyncLabel,
                versionLabel, updateLabel, entityList,
                authPanel, syncNowButton, logsButton, closeButton,
            });

            // Closing the window should leave the connector running in the tray.
            FormClosing += (s, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                }
            };

            SyncStatus.Instance.Changed += OnStatusChanged;
            Render();
        }

        /// <summary>
        /// The screen that earns its keep. "Authorization result = Pending" is
        /// meaningless to a customer; these are the actual steps, and the prompt
        /// only appears when the company is opened, which is the part everyone
        /// misses.
        /// </summary>
        private void BuildAuthPanel()
        {
            authPanel.SetBounds(14, 304, 440, 96);
            authPanel.BackColor = Color.FromArgb(255, 248, 225);
            authPanel.BorderStyle = BorderStyle.FixedSingle;
            authPanel.Visible = false;

            Label heading = new Label();
            heading.SetBounds(10, 8, 420, 18);
            heading.Font = new Font(Font, FontStyle.Bold);
            heading.Text = "Sage 50 needs to approve this version";

            Label steps = new Label();
            steps.SetBounds(10, 28, 420, 62);
            steps.Text =
                "In Sage 50, sign in as an administrator, then:\r\n" +
                "   1.  File → Close Company\r\n" +
                "   2.  Open the company again — the request appears as it opens\r\n" +
                "   3.  Choose “Always Allow Access”";

            authPanel.Controls.AddRange(new Control[] { heading, steps });
        }

        private void OnStatusChanged(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((Action)Render); } catch (ObjectDisposedException) { }
            }
            else
            {
                Render();
            }
        }

        private void Render()
        {
            SyncStatus s = SyncStatus.Instance;

            companyLabel.Text = string.IsNullOrEmpty(s.CompanyName)
                ? "No company configured"
                : s.CompanyName;
            stateLabel.Text = s.Message;

            switch (s.State)
            {
                case ConnectorState.NeedsAuthorization:
                    stateLabel.ForeColor = Color.FromArgb(146, 64, 14);
                    break;
                case ConnectorState.Error:
                case ConnectorState.Offline:
                    stateLabel.ForeColor = Color.FromArgb(153, 27, 27);
                    break;
                default:
                    stateLabel.ForeColor = SystemColors.ControlText;
                    break;
            }

            switch (s.SageAuthorization)
            {
                case SageAuthorizationState.Granted:
                    authorizationLabel.Text = "Sage access: Approved for this version";
                    authorizationLabel.ForeColor = Color.FromArgb(22, 101, 52);
                    break;
                case SageAuthorizationState.Required:
                    authorizationLabel.Text = "Sage access: Approval required for this version";
                    authorizationLabel.ForeColor = Color.FromArgb(146, 64, 14);
                    break;
                case SageAuthorizationState.Checking:
                    authorizationLabel.Text = "Sage access: Checking this version…";
                    authorizationLabel.ForeColor = SystemColors.GrayText;
                    break;
                default:
                    authorizationLabel.Text = "Sage access: Not checked yet";
                    authorizationLabel.ForeColor = SystemColors.GrayText;
                    break;
            }

            bool needsAuthorization = s.SageAuthorization == SageAuthorizationState.Required;
            authPanel.Visible = needsAuthorization;
            syncNowButton.Text = needsAuthorization ? "Check access" : "Sync now";

            bool syncing = s.State == ConnectorState.Syncing
                || s.SageAuthorization == SageAuthorizationState.Checking;
            progress.Visible = syncing;
            if (syncing)
            {
                if (s.RecordsTotal > 0)
                {
                    progress.Style = ProgressBarStyle.Continuous;
                    int pct = (int)Math.Round(100.0 * s.RecordsDone / s.RecordsTotal);
                    progress.Value = Math.Max(0, Math.Min(100, pct));
                }
                else
                {
                    progress.Style = ProgressBarStyle.Marquee;
                }
            }

            lastSyncLabel.Text = s.LastSyncAt.HasValue
                ? "Last synced " + s.LastSyncAt.Value.ToString("g")
                : "Not synced yet";

            versionLabel.Text = "Version " + AppVersion.Display
                + (RuntimeEnvironment.IsInstalled ? "" : " (development build)");

            if (!string.IsNullOrEmpty(s.UpdateMessage))
            {
                updateLabel.Text = s.UpdateMessage;
                if (s.UpdateAvailability == UpdateAvailability.RequiredUpdate
                    || s.UpdateAvailability == UpdateAvailability.OptionalUpdate)
                {
                    updateLabel.ForeColor = Color.FromArgb(146, 64, 14);
                }
                else if (s.UpdateAvailability == UpdateAvailability.CheckFailed)
                {
                    updateLabel.ForeColor = SystemColors.GrayText;
                }
                else
                {
                    updateLabel.ForeColor = Color.FromArgb(22, 101, 52);
                }
            }
            else
            {
                updateLabel.Text = "Use the tray menu → Check for updates to install a newer build. "
                    + "Updates always require re-approval in Sage 50.";
                updateLabel.ForeColor = SystemColors.GrayText;
            }

            entityList.BeginUpdate();
            entityList.Items.Clear();
            foreach (EntityStat stat in s.Entities)
            {
                ListViewItem item = new ListViewItem(SyncStatus.Friendly(stat.Entity));
                item.SubItems.Add(stat.RecordCount.ToString());
                item.SubItems.Add(stat.LastSyncedAt.HasValue
                    ? stat.LastSyncedAt.Value.ToString("g")
                    : "—");
                entityList.Items.Add(item);
            }
            entityList.EndUpdate();
        }

        private static void OpenLogFolder()
        {
            try
            {
                Process.Start("explorer.exe", ConnectorConfig.ConfigDirectory);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the log folder: " + ex.Message,
                    RuntimeEnvironment.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
