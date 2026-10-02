using Sage50Connector.Helpers;
using System;
using System.Collections.Generic;
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
        private readonly Label companyFileLabel = new Label();
        private readonly ComboBox companyCombo = new ComboBox();
        private readonly Button removeCompanyButton = new Button();
        private readonly Label companyLabel = new Label();
        private bool suppressCompanyEvent;
        private bool companySwitchInProgress;
        private string switchBanner;
        private readonly Label stateLabel = new Label();
        private readonly Label authorizationLabel = new Label();
        private readonly Label lastSyncLabel = new Label();
        private readonly Label versionLabel = new Label();
        private readonly Label updateLabel = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly ListView entityList = new ListView();
        private readonly Panel authPanel = new Panel();
        private readonly Label authHeading = new Label();
        private readonly Label authSteps = new Label();
        private readonly Button syncNowButton = new Button();
        private readonly Button updateButton = new Button();

        public event EventHandler SyncNowRequested;
        public event EventHandler UpdateRequested;
        public event EventHandler CompanyActivated;

        public StatusForm()
        {
            Text = RuntimeEnvironment.DisplayName;
            ClientSize = new Size(470, 498);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;
            Icon = TrayApplicationContext.LoadIcon();

            companyFileLabel.SetBounds(14, 12, 440, 16);
            companyFileLabel.Text = "Company file";

            companyCombo.SetBounds(14, 30, 300, 24);
            companyCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            companyCombo.DropDownWidth = 440;
            companyCombo.SelectedIndexChanged += OnCompanySelected;

            removeCompanyButton.SetBounds(320, 28, 134, 26);
            removeCompanyButton.Text = "Remove from list";
            removeCompanyButton.Enabled = false;
            removeCompanyButton.Click += (s, e) => RemoveSelectedCompany();

            companyLabel.SetBounds(14, 62, 440, 20);
            companyLabel.Font = new Font(Font, FontStyle.Bold);

            stateLabel.SetBounds(14, 86, 440, 36);

            progress.SetBounds(14, 124, 440, 16);
            progress.Minimum = 0;
            progress.Maximum = 100;

            authorizationLabel.SetBounds(14, 146, 440, 20);
            authorizationLabel.Font = new Font(Font, FontStyle.Bold);

            lastSyncLabel.SetBounds(14, 166, 440, 20);
            lastSyncLabel.ForeColor = SystemColors.GrayText;

            versionLabel.SetBounds(14, 186, 440, 18);
            versionLabel.ForeColor = SystemColors.GrayText;
            versionLabel.Text = "Version " + AppVersion.Display
                + (RuntimeEnvironment.IsInstalled ? "" : " (development build)");

            updateLabel.SetBounds(14, 204, 440, 36);
            updateLabel.ForeColor = SystemColors.GrayText;

            entityList.SetBounds(14, 242, 440, 100);
            entityList.View = View.Details;
            entityList.FullRowSelect = true;
            entityList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            entityList.Columns.Add("Data", 170);
            entityList.Columns.Add("Records", 90);
            entityList.Columns.Add("Last synced", 170);

            BuildAuthPanel();

            syncNowButton.SetBounds(14, 458, 100, 26);
            syncNowButton.Text = "Sync now";
            syncNowButton.Click += (s, e) =>
            {
                EventHandler h = SyncNowRequested;
                if (h != null) h(this, EventArgs.Empty);
            };

            Button logsButton = new Button();
            logsButton.SetBounds(122, 458, 100, 26);
            logsButton.Text = "Open logs";
            logsButton.Click += (s, e) => OpenLogFolder();

            updateButton.SetBounds(230, 458, 116, 26);
            updateButton.Text = "Install update";
            updateButton.Visible = false;
            updateButton.Click += (s, e) =>
            {
                EventHandler h = UpdateRequested;
                if (h != null) h(this, EventArgs.Empty);
            };

            Button closeButton = new Button();
            closeButton.SetBounds(354, 458, 100, 26);
            closeButton.Text = "Close";
            closeButton.Click += (s, e) => Hide();

            Controls.AddRange(new Control[]
            {
                companyFileLabel, companyCombo, removeCompanyButton,
                companyLabel, stateLabel, progress, authorizationLabel, lastSyncLabel,
                versionLabel, updateLabel, entityList,
                authPanel, syncNowButton, logsButton, updateButton, closeButton,
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
            RefreshCompanies();
            Render();
        }

        /// <summary>Opens the window with the company list ready for a choice.</summary>
        public void FocusCompanyList()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            if (companyCombo.Enabled)
                companyCombo.Focus();
        }

        /// <summary>
        /// The screen that earns its keep. "Authorization result = Pending" is
        /// meaningless to a customer; these are the actual steps, and the prompt
        /// only appears when the company is opened, which is the part everyone
        /// misses.
        /// </summary>
        private void BuildAuthPanel()
        {
            authPanel.SetBounds(14, 348, 440, 96);
            authPanel.BackColor = Color.FromArgb(255, 248, 225);
            authPanel.BorderStyle = BorderStyle.FixedSingle;
            authPanel.Visible = false;

            authHeading.SetBounds(10, 8, 420, 18);
            authHeading.Font = new Font(Font, FontStyle.Bold);
            authHeading.Text = "Sage 50 needs to approve this version";

            authSteps.SetBounds(10, 28, 420, 62);
            authSteps.Text =
                "In Sage 50, sign in as an administrator, then:\r\n" +
                "   1.  File → Close Company\r\n" +
                "   2.  Open the company again — the request appears as it opens\r\n" +
                "   3.  Choose “Always Allow Access”";

            authPanel.Controls.AddRange(new Control[] { authHeading, authSteps });
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
            stateLabel.Text = string.IsNullOrEmpty(switchBanner) ? s.Message : switchBanner;

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

            if (s.SageAuthorization == SageAuthorizationState.Granted
                && s.ComAuthorization == SageAuthorizationState.Granted)
            {
                authorizationLabel.Text = "Sage access: Fully approved";
                authorizationLabel.ForeColor = Color.FromArgb(22, 101, 52);
            }
            else if (s.ComAuthorization == SageAuthorizationState.Required)
            {
                authorizationLabel.Text = "Sage access: Transaction approval required";
                authorizationLabel.ForeColor = Color.FromArgb(146, 64, 14);
            }
            else switch (s.SageAuthorization)
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

            bool showingComApproval = s.SageAuthorization == SageAuthorizationState.Granted
                && (s.ComAuthorization == SageAuthorizationState.Required
                    || s.ComAuthorization == SageAuthorizationState.Checking);
            if (showingComApproval)
            {
                authHeading.Text = "Approve Sage 50 transaction access";
                authSteps.Text =
                    "Keep the selected company open in Sage 50, then:\r\n" +
                    "   1.  Find the “Peachtree Software” access prompt\r\n" +
                    "   2.  Check “Remember this setting”\r\n" +
                    "   3.  Click “Yes”";
            }
            else
            {
                authHeading.Text = "Sage 50 needs to approve this version";
                authSteps.Text =
                    "In Sage 50, sign in as an administrator, then:\r\n" +
                    "   1.  File → Close Company\r\n" +
                    "   2.  Open the company again — the request appears as it opens\r\n" +
                    "   3.  Choose “Always Allow Access”";
            }

            bool needsAuthorization = s.SageAuthorization == SageAuthorizationState.Required
                || showingComApproval;
            authPanel.Visible = needsAuthorization;
            syncNowButton.Text = needsAuthorization ? "Check access" : "Sync now";

            bool syncing = s.State == ConnectorState.Syncing
                || s.SageAuthorization == SageAuthorizationState.Checking
                || s.ComAuthorization == SageAuthorizationState.Checking;
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

            bool updateAvailable = s.UpdateAvailability == UpdateAvailability.RequiredUpdate
                || s.UpdateAvailability == UpdateAvailability.OptionalUpdate;
            updateButton.Visible = updateAvailable;
            updateButton.Enabled = updateAvailable;
            updateButton.Text = string.IsNullOrWhiteSpace(s.AvailableVersion)
                ? "Install update"
                : "Update to " + s.AvailableVersion;

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

            if (companySwitchInProgress)
            {
                companyCombo.Enabled = false;
                removeCompanyButton.Enabled = false;
                syncNowButton.Enabled = false;
                updateButton.Enabled = false;
            }
            else
            {
                syncNowButton.Enabled = true;
            }
        }

        private void RefreshCompanies()
        {
            IList<StoredConnection> connections;
            try
            {
                connections = ConnectionRegistry.List();
            }
            catch (Exception ex)
            {
                Program.WriteToFile("Could not list companies: " + ex.Message);
                connections = new List<StoredConnection>();
            }

            string activeId = null;
            try
            {
                activeId = ConnectorConfig.Load().ConnectionId;
            }
            catch
            {
                // Not set up yet: the list is empty and the dropdown stays disabled.
            }

            var nameCounts = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            foreach (StoredConnection connection in connections)
            {
                string name = connection.CompanyName ?? string.Empty;
                int count;
                nameCounts.TryGetValue(name, out count);
                nameCounts[name] = count + 1;
            }

            suppressCompanyEvent = true;
            try
            {
                companyCombo.Items.Clear();
                int activeIndex = -1;
                for (int i = 0; i < connections.Count; i++)
                {
                    StoredConnection connection = connections[i];
                    bool isActive = string.Equals(
                        connection.ConnectionId, activeId, StringComparison.OrdinalIgnoreCase);
                    int count;
                    nameCounts.TryGetValue(connection.CompanyName ?? string.Empty, out count);
                    companyCombo.Items.Add(new CompanyFileItem(
                        connection,
                        ConnectionRegistry.FormatLabel(connection, count > 1, isActive)));
                    if (isActive) activeIndex = i;
                }
                if (activeIndex >= 0)
                    companyCombo.SelectedIndex = activeIndex;
                else if (companyCombo.Items.Count > 0)
                    companyCombo.SelectedIndex = 0;

                companyFileLabel.Text = string.IsNullOrEmpty(ConnectionRegistry.LoadError)
                    ? "Company file"
                    : "Company file (list unreadable)";
                companyCombo.Enabled = !companySwitchInProgress
                    && string.IsNullOrEmpty(ConnectionRegistry.LoadError)
                    && companyCombo.Items.Count > 1;
                UpdateRemoveButton();
            }
            finally
            {
                suppressCompanyEvent = false;
            }
        }

        private async void OnCompanySelected(object sender, EventArgs e)
        {
            if (suppressCompanyEvent || companySwitchInProgress) return;
            CompanyFileItem item = companyCombo.SelectedItem as CompanyFileItem;
            if (item == null) return;

            string activeId = null;
            string activeName = SyncStatus.Instance.CompanyName;
            try
            {
                ConnectorConfig active = ConnectorConfig.Load();
                activeId = active.ConnectionId;
                if (!string.IsNullOrWhiteSpace(active.CompanyName))
                    activeName = active.CompanyName;
            }
            catch
            {
                // Fall through and let the switcher report the missing config.
            }

            if (!string.IsNullOrEmpty(activeId)
                && string.Equals(item.Connection.ConnectionId, activeId, StringComparison.OrdinalIgnoreCase))
            {
                UpdateRemoveButton();
                return;
            }

            removeCompanyButton.Enabled = false;
            DialogResult answer = MessageBox.Show(
                "Switch syncing to " + item.Connection.CompanyName + "? "
                    + (string.IsNullOrWhiteSpace(activeName) ? "The current company" : activeName)
                    + " will stop syncing until you switch back.",
                RuntimeEnvironment.DisplayName,
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);
            if (answer != DialogResult.OK)
            {
                RefreshCompanies();
                return;
            }

            companySwitchInProgress = true;
            switchBanner = "Checking " + item.Connection.CompanyName + "…";
            Render();

            CompanySwitchResult result;
            try
            {
                result = await CompanySwitcher.SwitchToAsync(item.Connection.ConnectionId);
            }
            catch (Exception ex)
            {
                result = CompanySwitchResult.Failed(ex.Message);
            }

            companySwitchInProgress = false;
            switchBanner = null;
            if (IsDisposed) return;
            RefreshCompanies();
            Render();

            if (result != null && result.Succeeded && !result.Unchanged)
            {
                EventHandler activated = CompanyActivated;
                if (activated != null) activated(this, EventArgs.Empty);
            }
            else if (result != null && !result.Succeeded && !string.IsNullOrWhiteSpace(result.Message))
            {
                MessageBox.Show(
                    result.Message,
                    RuntimeEnvironment.DisplayName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void RemoveSelectedCompany()
        {
            if (companySwitchInProgress) return;
            CompanyFileItem item = companyCombo.SelectedItem as CompanyFileItem;
            if (item == null) return;
            if (!string.Equals(
                item.Connection.LastProbeResult,
                ConnectionProbeResults.Disconnected,
                StringComparison.Ordinal))
            {
                return;
            }

            DialogResult answer = MessageBox.Show(
                "Remove " + item.Connection.CompanyName
                    + " from the company list on this computer? This does not delete the company in Sage 50 or the connection in Rutter.",
                RuntimeEnvironment.DisplayName,
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return;

            try
            {
                if (!ConnectionRegistry.Remove(item.Connection.ConnectionId))
                {
                    MessageBox.Show(
                        "That company is still the one being synced, so it stays in the list.",
                        RuntimeEnvironment.DisplayName,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    ex.Message,
                    RuntimeEnvironment.DisplayName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            RefreshCompanies();
        }

        private void UpdateRemoveButton()
        {
            CompanyFileItem item = companyCombo.SelectedItem as CompanyFileItem;
            string activeId = null;
            try { activeId = ConnectorConfig.Load().ConnectionId; }
            catch { }
            bool disconnected = item != null
                && string.Equals(
                    item.Connection.LastProbeResult,
                    ConnectionProbeResults.Disconnected,
                    StringComparison.Ordinal);
            bool active = item != null
                && string.Equals(item.Connection.ConnectionId, activeId, StringComparison.OrdinalIgnoreCase);
            removeCompanyButton.Enabled = !companySwitchInProgress && disconnected && !active;
        }

        private sealed class CompanyFileItem
        {
            public StoredConnection Connection { get; private set; }
            private readonly string label;

            public CompanyFileItem(StoredConnection connection, string label)
            {
                Connection = connection;
                this.label = label;
            }

            public override string ToString()
            {
                return label ?? string.Empty;
            }
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
