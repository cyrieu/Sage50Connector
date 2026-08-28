using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Sage.Peachtree.API;
using Sage50Connector.Helpers;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Sage50Connector.Ui
{
    /// <summary>
    /// First-run setup deliberately gets the company from Sage's CompanyList.
    /// A typed company name is both error-prone and unstable; the identifier's
    /// Guid is the durable identity and the SDK-provided name is the exact value
    /// Sage expects when the company is opened.
    /// </summary>
    public sealed class CompanySelectionForm : Form
    {
        private readonly string setupToken;
        private readonly string apiBaseUrl;
        private readonly ComboBox companies = new ComboBox();
        private readonly Label detail = new Label();
        private readonly Label error = new Label();
        private readonly LinkLabel browseLink = new LinkLabel();
        private readonly Button connect = new Button();

        public CompanySelectionForm(string setupToken, string apiBaseUrl)
        {
            this.setupToken = setupToken;
            this.apiBaseUrl = apiBaseUrl.TrimEnd('/');

            Text = "Connect Rutter to Sage 50";
            ClientSize = new Size(520, 290);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;
            Icon = TrayApplicationContext.LoadIcon();

            var heading = new Label
            {
                Text = "Choose the Sage 50 company to connect",
                Font = new Font(Font, FontStyle.Bold),
            };
            heading.SetBounds(18, 18, 480, 22);

            var explanation = new Label
            {
                Text = "These companies come directly from Sage 50, so you do not need to type the company name.",
            };
            explanation.SetBounds(18, 46, 480, 34);

            companies.DropDownStyle = ComboBoxStyle.DropDownList;
            companies.SetBounds(18, 86, 480, 26);
            companies.SelectedIndexChanged += (s, e) => RenderSelection();

            detail.SetBounds(18, 120, 480, 34);
            detail.ForeColor = SystemColors.GrayText;

            error.SetBounds(18, 158, 480, 50);
            error.ForeColor = Color.FromArgb(153, 27, 27);

            browseLink.Text = "Can't find your company? Browse for its folder…";
            browseLink.SetBounds(18, 212, 340, 20);
            browseLink.LinkClicked += async (s, e) => await BrowseForCompanyFolderAsync();

            connect.Text = "Connect company";
            connect.SetBounds(366, 248, 132, 28);
            connect.Enabled = false;
            connect.Click += async (s, e) => await CompleteSetupAsync();

            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            cancel.SetBounds(276, 248, 82, 28);

            Controls.AddRange(new Control[]
            {
                heading, explanation, companies, detail, error, browseLink, connect, cancel,
            });
            AcceptButton = connect;
            CancelButton = cancel;
            Shown += (s, e) => LoadCompanies();
        }

        private void LoadCompanies()
        {
            try
            {
                var available = CompanyManager.Instance.Companies
                    .OrderBy(company => company.CompanyName)
                    .Select(company => new CompanyChoice(company))
                    .ToArray();
                companies.Items.AddRange(available);
                if (available.Length == 0)
                {
                    error.Text = "No Sage 50 companies were found for this Windows user. "
                        + "Use \"Browse for its folder\" below instead.";
                    return;
                }
                companies.SelectedIndex = 0;
                if (available.Length == 1)
                {
                    detail.Text = "Rutter found one company and selected it automatically.";
                }
            }
            catch (Exception ex)
            {
                error.Text = "Rutter could not read the Sage 50 company list: " + ex.Message
                    + " Use \"Browse for its folder\" below instead.";
            }
        }

        private CompanyChoice SelectedCompany
        {
            get { return companies.SelectedItem as CompanyChoice; }
        }

        private async Task BrowseForCompanyFolderAsync()
        {
            string defaultRoot = @"C:\Sage\Peachtree\Company";
            using (var dialog = new FolderBrowserDialog
            {
                Description = "Select the Sage 50 company's data folder",
                SelectedPath = Directory.Exists(defaultRoot) ? defaultRoot : string.Empty,
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                browseLink.Enabled = false;
                companies.Enabled = false;
                connect.Enabled = false;
                error.Text = string.Empty;
                detail.Text = "Checking Sage 50 access for that folder… this can take up to 20 seconds.";

                try
                {
                    string folderPath = dialog.SelectedPath;
                    CompanyIdentifier identifier = await Task.Run(() => CompanyManager.Instance.ResolveFolder(folderPath));
                    companies.Items.Clear();
                    companies.Items.Add(new CompanyChoice(identifier));
                    companies.SelectedIndex = 0;
                }
                catch (Exception ex)
                {
                    detail.Text = string.Empty;
                    error.Text = ex.Message;
                }
                finally
                {
                    browseLink.Enabled = true;
                    companies.Enabled = true;
                }
            }
        }

        private void RenderSelection()
        {
            CompanyChoice selected = SelectedCompany;
            connect.Enabled = selected != null;
            if (selected == null) return;
            detail.Text = selected.Details;
            error.Text = string.Empty;
        }

        private async Task CompleteSetupAsync()
        {
            CompanyChoice selected = SelectedCompany;
            if (selected == null) return;

            connect.Enabled = false;
            connect.Text = "Connecting…";
            error.Text = string.Empty;
            try
            {
                using (var credentialEnvelope = new ComCredentialProvisioner())
                using (var client = new HttpClient())
                {
                    var payload = new
                    {
                        setup_token = setupToken,
                        company_guid = selected.Identifier.Guid.ToString(),
                        company_name = selected.Identifier.CompanyName,
                        database_name = selected.Identifier.DatabaseName,
                        server_name = selected.Identifier.ServerName,
                        com_credential_public_key = credentialEnvelope.PublicKey,
                    };
                    var response = await client.PostAsync(
                        apiBaseUrl + "/sage-50/complete-setup",
                        new StringContent(
                            JsonConvert.SerializeObject(payload),
                            Encoding.UTF8,
                            "application/json"));
                    string responseContent = await response.Content.ReadAsStringAsync();
                    JObject body = JObject.Parse(responseContent);
                    if (!response.IsSuccessStatusCode || body.Value<bool?>("is_successful") != true)
                    {
                        throw new InvalidOperationException(
                            body.Value<string>("reason")
                                ?? body.Value<string>("error_message")
                                ?? "Rutter could not complete setup.");
                    }

                    JObject config = body["sage50_config"] as JObject;
                    if (config == null)
                    {
                        throw new InvalidOperationException("Rutter did not return the connector configuration.");
                    }
                    ConnectorConfig.Save(
                        config.Value<string>("CompanyName"),
                        config.Value<string>("AccessKey"),
                        config.Value<string>("ConnectionId"),
                        apiBaseUrl,
                        config.Value<string>("CompanyGuid"),
                        config.Value<string>("DatabaseName"));
                    credentialEnvelope.DecryptAndSave(
                        body.Value<string>("com_credential_encrypted"));
                    DialogResult = DialogResult.OK;
                    Close();
                }
            }
            catch (Exception ex)
            {
                error.Text = ex.Message;
                connect.Enabled = true;
                connect.Text = "Connect company";
            }
        }

        private sealed class CompanyChoice
        {
            public CompanyIdentifier Identifier { get; private set; }

            public CompanyChoice(CompanyIdentifier identifier)
            {
                Identifier = identifier;
            }

            public string Details
            {
                get
                {
                    string database = Identifier.DatabaseName ?? "local database";
                    string server = Identifier.ServerName ?? "this computer";
                    return "Database: " + database + "   Server: " + server;
                }
            }

            public override string ToString()
            {
                return Identifier.CompanyName;
            }
        }
    }
}
