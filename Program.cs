using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Sage.Peachtree.API;
using Sage50Connector.Helpers;
using Sage50Connector.Models.Rutter;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Sage50Connector
{
    class ResponseObject
    {
        public string job { get; set; }
        public string platform_entity { get; set; }
        public string type { get; set; }
        public Parameters parameters { get; set; }
        public string job_id { get; set; }
        public object body { get; set; }
        public CreateBody create_body { get; set; }

        /// <summary>Fields to apply, on an UPDATE job.</summary>
        public object update_body { get; set; }
    }

    class CreateBody
    {
        public object data { get; set; }
    }

    class Parameters
    {
        public string updated_at { get; set; }
        public int limit { get; set; }

        /// <summary>
        /// Set by Rutter on follow-up pages: the record id the previous page
        /// stopped at. Null on the first page of a job.
        ///
        /// We echo parameters straight back on every report, and Rutter types
        /// cursor as an optional string — which accepts the key being absent but
        /// rejects an explicit null. Without Ignore, the first page of every job
        /// is rejected with a 500 and nothing ever persists.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string cursor { get; set; }

        /// <summary>The record an ID_FETCH, UPDATE or DELETE job targets.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string platform_id { get; set; }

        /// <summary>Optional transaction date window lower bound (yyyy-MM-dd or ISO 8601), half-open inclusive.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string start_date { get; set; }

        /// <summary>Optional transaction date window upper bound (yyyy-MM-dd or ISO 8601), half-open exclusive.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string end_date { get; set; }

        /// <summary>
        /// Exclusive upper bound for LastSavedAt (QBD-style multi-batch historical).
        /// Deeper batches set this so they do not re-fetch the recent window.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string updated_before { get; set; }

        /// <summary>
        /// When true (side refresh / deepest historical batch), rows with no
        /// LastSavedAt are included. Recent historical windows set false so
        /// untimestamped rows are delivered once in the deep batch.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public bool? include_missing_timestamps { get; set; }
    }

    public class VendorBody
    {
        public string AccountNumber { get; set; }
        public string ID { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public string TaxIDNumber { get; set; }
        public string WebSiteURL { get; set; }
        /*public EntityReference<Account> ExpenseAccountReference { get; set; }*/

    }

    class AccountBody
    {
        public string ID { get; set; }
        public string Description { get; set; }
        public string Classification { get; set; }
        public bool IsInactive { get; set; }
        public EntityReference<Sage.Peachtree.API.Account> Key { get; set; }
    }

    public class Program
    {
        public static string CompanyName;
        public static string CompanyGuid;
        public static string AccessKey;
        public static string ConnectionId;

        private static ConnectorConfig Config;
        private static bool winFormsInitialized;

        private static void InitializeWinForms()
        {
            if (winFormsInitialized) return;
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            winFormsInitialized = true;
        }

        /// <summary>
        /// Entry point.
        ///
        ///   --setup ...   provision sage50Config.json and exit
        ///   --headless    run the sync loop with no UI (used by the service, and
        ///                 handy for scripted testing on the VM)
        ///   (no args)     run as a tray application — the normal way a customer
        ///                 runs this, and the reason the exe is a WinExe: a console
        ///                 window has no business appearing on someone's desktop
        /// </summary>
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0].StartsWith("rutter-sage50:", StringComparison.OrdinalIgnoreCase))
            {
                StopExistingTrayInstance();
                int setupResult = RunSetupFromUri(args[0]);
                return setupResult == 0 ? RunTray() : setupResult;
            }
            if (args.Length > 0 && string.Equals(args[0], "--setup", StringComparison.OrdinalIgnoreCase))
            {
                return RunSetupAsync(args).GetAwaiter().GetResult();
            }

            bool headless = args.Length > 0
                && (string.Equals(args[0], "--headless", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[0], "--console", StringComparison.OrdinalIgnoreCase));

            if (!headless)
            {
                return RunTray();
            }

            return RunHeadless();
        }

        /// <summary>
        /// Signal used by a second launch to surface the running instance's window.
        /// </summary>
        internal const string ShowWindowEventName = @"Local\RutterSage50ConnectorShow";

        /// <summary>
        /// Ask a running instance to shut down cleanly.
        ///
        /// Killing the process instead leaks its Sage connection seat — no handler
        /// runs after TerminateProcess — and a few of those exhaust the licence.
        /// Scripts that restart the connector should signal this and wait.
        /// </summary>
        /// Global\ rather than Local\: Local names are scoped to a logon session,
        /// and the scripts that restart the connector run as SYSTEM in session 0
        /// while the connector runs in the interactive user's session. A Local
        /// name is simply invisible to them.
        internal const string QuitEventName = @"Global\RutterSage50ConnectorQuit";

        /// <summary>
        /// Only one connector may run per machine: two would each hold a Sage
        /// session, and Sage licenses a limited number of concurrent connections.
        /// </summary>
        private static int RunTray()
        {
            bool createdNew;
            using (var single = new System.Threading.Mutex(true, @"Local\RutterSage50Connector", out createdNew))
            {
                if (!createdNew)
                {
                    // Launching it again is how a person asks to see it - the tray
                    // icon is easy to miss behind Windows' overflow chevron. Poke
                    // the instance that is already running and get out of the way.
                    try
                    {
                        using (var show = System.Threading.EventWaitHandle.OpenExisting(ShowWindowEventName))
                        {
                            show.Set();
                        }
                    }
                    catch
                    {
                        System.Windows.Forms.MessageBox.Show(
                            "The Rutter Sage 50 Connector is already running. Look for it in the notification area "
                                + "(click the ^ arrow next to the clock).",
                            "Rutter Sage 50 Connector",
                            System.Windows.Forms.MessageBoxButtons.OK,
                            System.Windows.Forms.MessageBoxIcon.Information);
                    }
                    return 0;
                }

                InitializeWinForms();
                System.Windows.Forms.Application.Run(new Ui.TrayApplicationContext());
                return 0;
            }
        }

        private static int RunHeadless()
        {
            try
            {
                Config = ConnectorConfig.Load();
                CompanyName = Config.CompanyName;
                CompanyGuid = Config.CompanyGuid;
                AccessKey = Config.AccessKey;
                ConnectionId = Config.ConnectionId;
            }
            catch (Exception ex)
            {
                WriteToFile("Failed to load connector configuration: " + ex.Message);
                Helpers.SyncStatus.Instance.SetError(
                    "Not set up yet. " + ConnectorConfig.ConfigFilePath + " is missing or incomplete.");
                return 1;
            }

            InstallSageSessionCleanup();
            Helpers.SyncStatus.Instance.SetCompany(CompanyName);
            Helpers.SyncStatus.Instance.SetIdle("Connecting…");

            WriteToFile(
                "Loaded configuration from "
                    + Config.LoadedFromPath
                    + "; CompanyName='"
                    + CompanyName
                    + "'; ConnectionId='"
                    + ConnectionId
                    + "'; AccessKeyLength="
                    + AccessKey.Length
                    + "; ApiBaseUrl="
                    + Config.ApiBaseUrl
            );
            WriteToFile(
                "RuntimeMode="
                    + RuntimeEnvironment.ModeName
                    + "; ExecutablePath='"
                    + RuntimeEnvironment.ExecutablePath
                    + "'; ConnectorVersion="
                    + AppVersion.Display
            );

            try
            {
                MainAsync(AccessKey, CompanyName, ConnectionId).GetAwaiter().GetResult();
            }
            finally
            {
                ReleaseSageSession();
            }
            return 0;
        }

        private static int RunSetupFromUri(string setupUri)
        {
            try
            {
                Uri uri = new Uri(setupUri);
                Dictionary<string, string> query = ParseQueryString(uri.Query);
                string token;
                if (!query.TryGetValue("token", out token) || string.IsNullOrWhiteSpace(token))
                {
                    throw new InvalidOperationException("The Sage 50 setup link does not contain a setup token.");
                }
                string apiBaseUrl;
                if (!query.TryGetValue("api_base_url", out apiBaseUrl) || string.IsNullOrWhiteSpace(apiBaseUrl))
                {
                    apiBaseUrl = ConnectorConfig.DefaultApiBaseUrl;
                }

                InitializeWinForms();
                using (var form = new Ui.CompanySelectionForm(token, apiBaseUrl))
                {
                    return form.ShowDialog() == System.Windows.Forms.DialogResult.OK ? 0 : 1;
                }
            }
            catch (Exception ex)
            {
                WriteToFile("Interactive setup failed: " + ex.Message);
                System.Windows.Forms.MessageBox.Show(
                    ex.Message,
                    "Rutter Sage 50 Connector Setup",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
                return 1;
            }
            finally
            {
                ReleaseSageSession();
            }
        }

        private static Dictionary<string, string> ParseQueryString(string query)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in (query ?? string.Empty).TrimStart('?').Split('&'))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                string[] pair = part.Split(new[] { '=' }, 2);
                string key = Uri.UnescapeDataString(pair[0].Replace("+", " "));
                string value = pair.Length > 1
                    ? Uri.UnescapeDataString(pair[1].Replace("+", " "))
                    : string.Empty;
                values[key] = value;
            }
            return values;
        }

        private static void StopExistingTrayInstance()
        {
            try
            {
                using (var quit = System.Threading.EventWaitHandle.OpenExisting(QuitEventName))
                {
                    quit.Set();
                }
                System.Threading.Thread.Sleep(750);
            }
            catch
            {
                // No existing tray instance is normal on a fresh installation.
            }
        }

        /// <summary>
        /// The sync loop, for hosts that supply their own UI (the tray app) or no
        /// UI at all (the service). Loads config, reports status, and never throws
        /// at the caller — a host should not die because a sync did.
        /// </summary>
        /// <param name="syncNow">
        /// Optional: set by the host to cut a between-poll sleep short, so
        /// "Sync now" does something immediately.
        /// </param>
        public static void RunSyncLoopHeadless(System.Threading.ManualResetEventSlim syncNow = null)
        {
            SyncNowSignal = syncNow;
            try
            {
                RunHeadless();
            }
            catch (Exception ex)
            {
                WriteToFile("Sync loop ended unexpectedly: " + ex.Message);
                Helpers.SyncStatus.Instance.SetError("Stopped: " + ex.Message);
            }
        }

        private static System.Threading.ManualResetEventSlim SyncNowSignal;
        private static int comAuthorizationRetryRequested;

        /// <summary>
        /// Called by the tray UI when a customer clicks Check access after a
        /// denied or missing COM prompt. Wake the poll loop and perform the COM
        /// probe even if Rutter has not queued another TRANSACTIONS job yet.
        /// </summary>
        public static void RequestComAuthorizationRetry()
        {
            System.Threading.Interlocked.Exchange(
                ref comAuthorizationRetryRequested,
                1);
            SyncNowSignal?.Set();
        }

        /// <summary>
        /// Sleep, but wake early if the user asked for a sync.
        /// </summary>
        private static async Task DelayInterruptible(TimeSpan delay)
        {
            var signal = SyncNowSignal;
            if (signal == null)
            {
                await Task.Delay(delay);
                return;
            }

            await Task.Run(() => signal.Wait(delay));
            signal.Reset();
        }

        private static int sageSessionReleased;

        /// <summary>
        /// Hand the Sage connection back on every exit path the process can
        /// observe: a normal return, Ctrl+C, an unhandled exception, and the
        /// service calling Stop.
        ///
        /// A hard kill (TerminateProcess, Stop-Process -Force, power loss) runs no
        /// handler and still leaks the seat — nothing in-process can fix that.
        /// Recovering from it means restarting the "Sage 50 Connect Service".
        /// </summary>
        private static bool sageCleanupInstalled;

        private static void InstallSageSessionCleanup()
        {
            // The service calls Main once a minute in a single process, so arm the
            // release for this run but only ever register the handlers once.
            System.Threading.Interlocked.Exchange(ref sageSessionReleased, 0);

            if (sageCleanupInstalled)
            {
                return;
            }
            sageCleanupInstalled = true;

            AppDomain.CurrentDomain.ProcessExit += (s, e) => ReleaseSageSession();
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ReleaseSageSession();
            Console.CancelKeyPress += (s, e) => ReleaseSageSession();
        }

        /// <summary>
        /// Idempotent: the exit paths above overlap, and Sage does not enjoy being
        /// closed twice.
        /// </summary>
        public static void ReleaseSageSession()
        {
            if (System.Threading.Interlocked.Exchange(ref sageSessionReleased, 1) != 0)
            {
                return;
            }

            try
            {
                WriteToFile(DateTime.Now + ": Releasing Sage session.");
                Helpers.Sage50Connector.Instance.Shutdown();
            }
            catch (Exception ex)
            {
                WriteToFile(DateTime.Now + ": Error releasing Sage session: " + ex.Message);
            }
        }

        /// <summary>
        /// Fetches this connection's sage50Config.json from the Rutter backend
        /// (POST /sage-50/save-id) and writes it to the ProgramData config path,
        /// so nobody hand-edits JSON on the machine.
        ///
        /// Usage: Sage50Connector.exe --setup &lt;CompanyName&gt; &lt;OrgId&gt; [ApiBaseUrl]
        ///   CompanyName — the Sage 50 company (e.g. "Bellwether Garden Supply")
        ///   OrgId       — the Rutter organization the connection belongs to
        ///   ApiBaseUrl  — optional; defaults to https://production.rutterapi.com
        /// </summary>
        private static async Task<int> RunSetupAsync(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: Sage50Connector.exe --setup <CompanyName> <OrgId> [ApiBaseUrl]");
                return 1;
            }

            string companyName = args[1];
            string orgId = args[2];
            string apiBaseUrl = args.Length > 3 && !string.IsNullOrWhiteSpace(args[3])
                ? args[3]
                : ConnectorConfig.DefaultApiBaseUrl;
            string saveIdUrl = apiBaseUrl.TrimEnd('/') + "/sage-50/save-id";

            try
            {
                using (var credentialEnvelope = new ComCredentialProvisioner())
                using (HttpClient client = new HttpClient())
                {
                    var requestBody = new
                    {
                        company_id = companyName,
                        org_id = orgId,
                        com_credential_public_key = credentialEnvelope.PublicKey
                    };

                    var request = new HttpRequestMessage(HttpMethod.Post, saveIdUrl);
                    request.Content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
                    var response = await client.SendAsync(request);
                    var responseContent = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        WriteToFile("Setup failed. Status code: " + response.StatusCode + ", Response: " + responseContent);
                        return 1;
                    }

                    JObject body = JObject.Parse(responseContent);
                    if (body.Value<bool?>("is_successful") != true)
                    {
                        WriteToFile("Setup failed: " + (body.Value<string>("reason") ?? responseContent));
                        return 1;
                    }

                    JObject sage50Config = body["sage50_config"] as JObject;
                    if (sage50Config == null)
                    {
                        WriteToFile("Setup failed: response did not include sage50_config. Response: " + responseContent);
                        return 1;
                    }

                    var config = ConnectorConfig.Save(
                        companyName,
                        sage50Config.Value<string>("AccessKey"),
                        sage50Config.Value<string>("ConnectionId"),
                        apiBaseUrl
                    );
                    credentialEnvelope.DecryptAndSave(
                        body.Value<string>("com_credential_encrypted"));

                    WriteToFile(
                        "Setup complete. Wrote "
                            + config.LoadedFromPath
                            + "; CompanyName='"
                            + config.CompanyName
                            + "'; ConnectionId='"
                            + config.ConnectionId
                            + "'; AccessKeyLength="
                            + config.AccessKey.Length
                            + "; ApiBaseUrl="
                            + config.ApiBaseUrl
                    );
                    return 0;
                }
            }
            catch (Exception ex)
            {
                WriteToFile("Setup failed with an exception: " + ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// Pause between poll/report cycles. Rutter hands the same job back while it
        /// is still in progress, so a job we cannot complete would otherwise be
        /// retried as fast as the network allows.
        /// </summary>
        private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan AuthorizationRetryDelay = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan AuthorizationWindowPollDelay = TimeSpan.FromMilliseconds(250);

        [System.Runtime.InteropServices.DllImport(
            "user32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        /// <summary>
        /// How many consecutive failed polls to ride out before giving up. A single
        /// blip - Rutter restarting, the tunnel dropping, the machine waking from
        /// sleep - used to end the process for good, because a null job broke the
        /// loop. Retrying a few times with backoff survives that; exiting after a
        /// sustained outage is still correct, since the service restarts us a
        /// minute later and a one-shot run should not spin forever.
        /// </summary>
        private const int MaxConsecutivePollFailures = 5;

        static async Task MainAsync(string AccessKey, string CompanyName, string ConnectionId)
        {
            bool sdkApprovalWasRequired = await WaitForSageAuthorizationAsync(CompanyName);
            if (sdkApprovalWasRequired
                || !ComCredentialStore.IsAuthorizationConfirmed(CompanyGuid, CompanyName))
            {
                bool comApproved = await EnsureComAuthorizationForTransactionsAsync(CompanyName);
                if (!comApproved)
                {
                    WriteToFile(
                        DateTime.Now
                            + ": Sage COM transaction access is unavailable. Continuing to poll Rutter; "
                            + "only TRANSACTIONS jobs will fail until transaction access is approved.");
                }
            }
            else
            {
                Helpers.SyncStatus.Instance.SetComAuthorizationGranted();
                WriteToFile(DateTime.Now + ": Using the remembered Sage COM transaction approval.");
            }

            bool firstIteration = true;
            int consecutivePollFailures = 0;
            while (true)
            {
                if (!firstIteration)
                {
                    await Task.Delay(PollDelay);
                }
                firstIteration = false;

                if (System.Threading.Interlocked.Exchange(
                        ref comAuthorizationRetryRequested,
                        0) != 0)
                {
                    bool comApproved = await EnsureComAuthorizationForTransactionsAsync(
                        CompanyName);
                    if (!comApproved)
                    {
                        WriteToFile(
                            DateTime.Now
                                + ": Requested Sage COM access recheck did not succeed; "
                                + "continuing with SDK-backed Rutter jobs.");
                    }
                }

                WriteToFile("###############################--------####################################################################################");
                WriteToFile(DateTime.Now + ": Process Started");
                ResponseObject job = await GetJobFromRutterAsync(AccessKey);

                if (job != null)
                {
                    consecutivePollFailures = 0;
                    switch (job.type)
                    {
                        case "LIST_FETCH":
                            if (job.platform_entity == "TRANSACTIONS"
                                && !await EnsureComAuthorizationForTransactionsAsync(CompanyName))
                            {
                                await ReportUnsupportedJob(
                                    job,
                                    AccessKey,
                                    "Sage transaction access is not approved for this company. "
                                        + "Open the configured company in Sage 50, approve Rutter transaction access, "
                                        + "then retry the transaction sync.");
                                Helpers.SyncStatus.Instance.SetNeedsComAuthorization(
                                    "Open the configured company in Sage 50 and approve Rutter transaction access, then retry the transaction sync.");
                                break;
                            }
                            await HandleListFetchJob(job, AccessKey, CompanyName);
                            break;
                        case "CREATE":
                            if (job.platform_entity == "VENDORS")
                            {
                                await HandleCreateVendorJob(job, AccessKey, CompanyName);
                            }
                            else
                            {
                                await ReportUnsupportedJob(job, AccessKey,
                                    "CREATE is not supported for " + job.platform_entity + ".");
                            }
                            break;

                        case "ID_FETCH":
                            await HandleIdFetchJob(job, AccessKey, CompanyName);
                            break;

                        case "UPDATE":
                            await HandleUpdateVendorJob(job, AccessKey, CompanyName);
                            break;

                        case "DELETE":
                            await HandleDeleteVendorJob(job, AccessKey, CompanyName);
                            break;
                        case "NOOP":
                            WriteToFile(DateTime.Now + ": Received NOOP job, sleeping for 5 minutes.");
                            // Hand the Sage connection back before going to sleep.
                            // Sage licenses a limited number of concurrent
                            // connections, and holding one for five idle minutes
                            // wastes a seat the customer may need — and turns any
                            // crash or kill during that window into a leaked seat.
                            // Reopening costs a few seconds once every 5 minutes.
                            Helpers.Sage50Connector.Instance.Shutdown();
                            Helpers.SyncStatus.Instance.SetNothingRequested();
                            await DelayInterruptible(TimeSpan.FromMinutes(5));
                            break;
                        default:
                            // Must report, not just log. An unreported job stays
                            // IN_PROGRESS and Rutter hands it back on every poll
                            // forever.
                            await ReportUnsupportedJob(job, AccessKey,
                                "Job type '" + job.type + "' is not supported by this connector.");
                            break;
                    }
                }
                else
                {
                    consecutivePollFailures++;
                    if (consecutivePollFailures >= MaxConsecutivePollFailures)
                    {
                        WriteToFile(
                            DateTime.Now
                                + ": Could not reach Rutter after "
                                + consecutivePollFailures
                                + " attempts. Exiting; the service will start us again."
                        );
                        break;
                    }

                    Helpers.SyncStatus.Instance.SetOffline("Cannot reach Rutter. Retrying…");

                    // 2s, 4s, 8s, 16s.
                    TimeSpan backoff = TimeSpan.FromSeconds(Math.Pow(2, consecutivePollFailures));
                    WriteToFile(
                        DateTime.Now
                            + ": Poll failed ("
                            + consecutivePollFailures
                            + " of "
                            + MaxConsecutivePollFailures
                            + "); retrying in "
                            + backoff.TotalSeconds
                            + "s."
                    );
                    await Task.Delay(backoff);
                }
                WriteToFile(DateTime.Now + ": Process Ended.");
                WriteToFile("###################################################################################################################");
            }
        }

        /// <summary>
        /// RequestAccess(Pending) registers one Sage request. Calling it again
        /// every few seconds queues duplicate Third Party Application Access
        /// dialogs when the company next opens. Instead, watch Sage's first
        /// dialog open and close, then recheck exactly once. Check access can
        /// still wake this immediately, and the long fallback handles a missed
        /// or localized window title.
        /// </summary>
        private static async Task WaitForSageApprovalDialogOrRetryAsync()
        {
            await Task.Run(() =>
            {
                bool sawApprovalDialog = false;
                DateTime retryAt = DateTime.UtcNow.Add(AuthorizationRetryDelay);
                while (DateTime.UtcNow < retryAt)
                {
                    bool dialogVisible = FindWindow(null, "Third Party Application Access") != IntPtr.Zero;
                    if (dialogVisible)
                    {
                        sawApprovalDialog = true;
                    }
                    else if (sawApprovalDialog)
                    {
                        WriteToFile(DateTime.Now + ": Sage approval dialog closed; rechecking authorization once.");
                        return;
                    }

                    var signal = SyncNowSignal;
                    if (signal != null && signal.Wait(AuthorizationWindowPollDelay))
                    {
                        signal.Reset();
                        return;
                    }
                    if (signal == null)
                    {
                        System.Threading.Thread.Sleep(AuthorizationWindowPollDelay);
                    }
                }
            });
        }

        /// <summary>
        /// Prove that Sage granted this exact executable access before accepting
        /// work from Rutter. APIACCSS.DAT contains requested and granted hashes,
        /// so reading that file cannot distinguish approval; RequestAccess can.
        ///
        /// A new build registers as Pending here without consuming and failing a
        /// queued job. The Sage session is released after every probe so a denied
        /// build cannot cache stale authorization or occupy a licence seat.
        /// </summary>
        private static async Task<bool> WaitForSageAuthorizationAsync(string companyName)
        {
            bool approvalWasRequired = false;
            while (true)
            {
                Helpers.SyncStatus.Instance.SetCheckingAuthorization();
                try
                {
                    CompanyIdentifier company = Helpers.CompanyManager.Instance.Companies
                        .FirstOrDefault(c =>
                            (!string.IsNullOrWhiteSpace(CompanyGuid)
                                && string.Equals(
                                    c.Guid.ToString(),
                                    CompanyGuid,
                                    StringComparison.OrdinalIgnoreCase))
                            || string.Equals(
                                c.CompanyName,
                                companyName,
                                StringComparison.Ordinal));

                    if (company == null)
                    {
                        throw new InvalidOperationException(
                            "There are no Sage 50 companies named '" + companyName + "'.");
                    }

                    AuthorizationResult result = Helpers.Sage50Connector.Instance.RequestAccessResult(company);
                    WriteToFile(
                        DateTime.Now
                            + ": Sage authorization check for current executable and company '"
                            + companyName
                            + "': "
                            + result);

                    if (result == AuthorizationResult.Granted)
                    {
                        Helpers.SyncStatus.Instance.SetAuthorizationGranted();
                        return approvalWasRequired;
                    }

                    approvalWasRequired = true;
                    Helpers.SyncStatus.Instance.SetNeedsAuthorization();
                }
                catch (Exception ex)
                {
                    WriteToFile(DateTime.Now + ": Sage authorization check failed: " + ex.Message);
                    Helpers.SyncStatus.Instance.SetAuthorizationCheckFailed(
                        "Could not check Sage 50 approval: " + ex.Message);
                }
                finally
                {
                    Helpers.Sage50Connector.Instance.Shutdown();
                }

                await WaitForSageApprovalDialogOrRetryAsync();
            }
        }

        /// <summary>
        /// The COM General Ledger exporter has its own Sage access handshake.
        /// Startup checks this immediately after the normal SDK grant, but a
        /// denied or unavailable COM grant must not block SDK-backed entities.
        /// The TRANSACTIONS handler calls it again and reports only that job as
        /// failed until Sage's remembered permission is granted.
        /// </summary>
        private static async Task<bool> EnsureComAuthorizationForTransactionsAsync(string companyName)
        {
            Helpers.SyncStatus.Instance.SetCheckingComAuthorization(
                "Checking Sage transaction access…");
            try
            {
                await ComCredentialProvisioner.EnsureProvisionedAsync(Config);
            }
            catch (Exception ex)
            {
                WriteToFile(DateTime.Now + ": Sage COM credential provisioning failed: " + ex.Message);
                Helpers.SyncStatus.Instance.SetComAuthorizationCheckFailed(
                    "Rutter could not prepare Sage transaction access: " + ex.Message);
                return false;
            }

            try
            {
                GeneralLedgerExporter.ProbeAccess(companyName, CompanyGuid);
                ComCredentialStore.MarkAuthorizationConfirmed(CompanyGuid, companyName);
                Helpers.SyncStatus.Instance.SetComAuthorizationGranted();
                WriteToFile(DateTime.Now + ": Sage COM transaction access granted.");
                return true;
            }
            catch (Exception ex)
            {
                WriteToFile(DateTime.Now + ": Sage COM transaction access check failed: " + ex.Message);
                Helpers.SyncStatus.Instance.SetNeedsComAuthorization(
                    "Open the configured company in Sage 50 and approve Rutter transaction access.");
                return false;
            }
        }

        private static async Task<ResponseObject> GetJobFromRutterAsync(string AccessKey)
        {
            using (HttpClient client = new HttpClient())
            {
                WriteToFile(
                    "Polling Rutter ingest for ConnectionId='"
                        + ConnectionId
                        + "'; AccessKeyLength="
                        + AccessKey.Length
                );
                var request = new HttpRequestMessage(HttpMethod.Post, Config.IngestUrl);
                request.Headers.Add("X-Rutter-Version", "2024-04-30");
                request.Headers.Add("Authorization", $"Bearer {AccessKey}");

                var requestBody = new
                {
                    connection = new
                    {
                        id = ConnectionId
                    }
                };

                request.Content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
                var response = await client.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    return JsonConvert.DeserializeObject<ResponseObject>(responseContent);
                }
                else
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    WriteToFile(DateTime.Now + $": Failed to fetch job from Rutter. Status code: {response.StatusCode}, Response: {responseContent}");
                    return null;
                }
            }
        }

        private static async Task HandleListFetchJob(ResponseObject job, string AccessKey, string CompanyName)
        {
            WriteToFile(DateTime.Now + ": Handling LIST_FETCH job for " + job.platform_entity);
            try
            {
                var jsonSettings = new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                };

                // Load once per job into process memory; page from that list so
                // multi-page fetches are O(n) rather than O(n²/limit) Sage loads.
                List<object> allRecords;
                if (!JobFetchCache.TryGet(job.job_id, job.platform_entity, out allRecords))
                {
                    allRecords = GetEntityData(
                        job.platform_entity,
                        CompanyName,
                        job.parameters != null ? job.parameters.updated_at : null,
                        job.parameters != null ? job.parameters.start_date : null,
                        job.parameters != null ? job.parameters.end_date : null,
                        job.parameters != null ? job.parameters.updated_before : null,
                        job.parameters != null ? job.parameters.include_missing_timestamps : null);
                    allRecords = allRecords
                        .OrderBy(record => GetRecordId(record), StringComparer.Ordinal)
                        .ToList();
                    JobFetchCache.Put(job.job_id, job.platform_entity, allRecords);
                    var ids = allRecords.Select(GetRecordId).Where(id => !string.IsNullOrEmpty(id)).ToList();
                    JobIdListCache.Put(job.job_id, job.platform_entity, ids);
                }

                int offset;
                var page = TakePage(allRecords, job.parameters, out string nextCursor, out offset);

                Helpers.SyncStatus.Instance.SetSyncing(
                    job.platform_entity,
                    offset + (page == null ? 0 : page.Count),
                    allRecords == null ? 0 : allRecords.Count);

                // Always report the full page. Unchanged-row dedupe is the
                // server upsert's job (content hash / platform_id match), not
                // the connector's. Delete monitoring is not supported: the Sage
                // Peachtree SDK has no deleted-entity query (no QBD
                // TxnDeleted/ListDeleted equivalent). Detecting hard deletes
                // would require full-inventory id set-diff across syncs; we
                // deliberately do not do that.
                WriteToFile(
                    DateTime.Now
                        + ": Fetched page offset=" + offset
                        + " size=" + (page == null ? 0 : page.Count)
                        + " of " + allRecords.Count + " "
                        + job.platform_entity + "(s)"
                        + (nextCursor == null ? " (final page)" : "; next_cursor=" + nextCursor));

                // next_cursor has to be absent, not null, on the final page.
                object responseObject;
                if (nextCursor == null)
                {
                    responseObject = new
                    {
                        connection = new { id = ConnectionId },
                        job_id = job.job_id,
                        type = job.type,
                        platform_entity = job.platform_entity,
                        parameters = job.parameters,
                        data = page,
                    };
                }
                else
                {
                    responseObject = new
                    {
                        connection = new { id = ConnectionId },
                        job_id = job.job_id,
                        type = job.type,
                        platform_entity = job.platform_entity,
                        parameters = job.parameters,
                        data = page,
                        next_cursor = nextCursor,
                    };
                }

                string jsonString = JsonConvert.SerializeObject(responseObject, jsonSettings);
                await PostToRutterAsync(jsonString, AccessKey);

                if (nextCursor == null)
                {
                    Helpers.SyncStatus.Instance.SetEntitySynced(job.platform_entity, allRecords.Count);
                    JobFetchCache.Remove(job.job_id, job.platform_entity);
                    JobIdListCache.Remove(job.job_id, job.platform_entity);
                }
            }
            catch (Exception ex)
            {
                // "Authorization result = Pending" is not a crash, it is a person
                // needing to click something in Sage. Say so plainly.
                if (ex.Message.IndexOf("Pending", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Helpers.SyncStatus.Instance.SetNeedsAuthorization();
                }
                else
                {
                    Helpers.SyncStatus.Instance.SetError(ex.Message);
                }

                WriteToFile(DateTime.Now + ": Error handling LIST_FETCH job for " + job.platform_entity + ". Error: " + ex.Message);
                // parameters must be echoed back even on the error path: Rutter
                // validates a LIST_FETCH report against a schema that requires it,
                // and rejects the report with a 500 when it is missing.
                var errorObject = new
                {
                    connection = new
                    {
                        id = ConnectionId
                    },
                    job_id = job.job_id,
                    type = job.type,
                    platform_entity = job.platform_entity,
                    parameters = job.parameters,
                    error_message = ex.Message
                };

                string jsonString = JsonConvert.SerializeObject(errorObject, new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                });

                await PostToRutterAsync(jsonString, AccessKey);
            }
        }

        /// <summary>
        /// Cuts one page out of the records Sage gave us.
        ///
        /// Rutter sends a limit (and, after the first page, the id the last page
        /// ended on) and completes the job only when we answer without a
        /// next_cursor. The connector used to ignore both and post everything in a
        /// single body, which is survivable for a sample company and not for a real
        /// ledger.
        ///
        /// Paging is keyed on the record id rather than an offset: Sage is a live
        /// database, and an offset silently skips a record whenever something is
        /// inserted earlier in the order between pages.
        /// </summary>
        private static List<object> TakePage(
            List<object> records,
            Parameters parameters,
            out string nextCursor,
            out int offset)
        {
            nextCursor = null;
            offset = 0;
            if (records == null)
            {
                return new List<object>();
            }

            // Records are expected pre-sorted by id (HandleListFetchJob orders once).
            int startIndex = 0;
            if (!string.IsNullOrEmpty(parameters?.cursor))
            {
                startIndex = records.Count;
                for (int i = 0; i < records.Count; i++)
                {
                    if (StringComparer.Ordinal.Compare(GetRecordId(records[i]), parameters.cursor) > 0)
                    {
                        startIndex = i;
                        break;
                    }
                }
            }

            offset = startIndex;
            int remaining = records.Count - startIndex;
            if (remaining <= 0)
            {
                return new List<object>();
            }

            int limit = parameters != null && parameters.limit > 0 ? parameters.limit : remaining;
            var page = records.Skip(startIndex).Take(Math.Min(limit, remaining)).ToList();
            if (remaining > limit)
            {
                nextCursor = GetRecordId(page[page.Count - 1]);
            }
            return page;
        }

        /// <summary>
        /// Every record we send Rutter carries an ID property (AccountBody,
        /// ChartofVendor, ChartofCustomer). Reflection keeps the paging code from
        /// having to know which one it is holding.
        /// </summary>
        private static string GetRecordId(object record)
        {
            var property = record?.GetType().GetProperty("ID");
            return property?.GetValue(record)?.ToString() ?? string.Empty;
        }

        private static List<object> GetEntityData(
            string entity,
            string companyName,
            string updatedAt,
            string startDate = null,
            string endDate = null,
            string updatedBefore = null,
            bool? includeMissingTimestamps = null)
        {
            // Default true preserves side-refresh over-fetch for untimestamped rows.
            bool includeMissing = includeMissingTimestamps ?? true;
            WriteToFile(DateTime.Now + $": Fetching {entity} data for company: {companyName} with updated_at: {updatedAt}"
                + (updatedBefore != null ? $", updated_before: {updatedBefore}" : "")
                + $", include_missing_timestamps: {includeMissing}"
                + (startDate != null || endDate != null
                    ? $", date window [{startDate ?? ".."}..{endDate ?? ".."}]"
                    : ""));
            List<object> data = new List<object>();

            switch (entity)
            {
                case "VENDORS":
                    var vendors = Sage50Repository.Instance.GetVendors(companyName, updatedAt, updatedBefore, includeMissing);
                    WriteToFile(DateTime.Now + $": Retrieved {vendors.Count} vendors from Sage 50 before filtering.");
                    data = vendors.Cast<object>().ToList();
                    break;
                case "ACCOUNTS":
                    var accounts = Sage50Repository.Instance.GetAccounts(companyName).Select(account => new AccountBody
                    {
                        ID = account.ID,
                        Description = account.Description,
                        Classification = account.Classification,
                        IsInactive = account.IsInactive,
                        Key = account.Key
                    }).Cast<object>().ToList();
                    WriteToFile(DateTime.Now + $": Retrieved {accounts.Count} accounts from Sage 50.");
                    data = accounts;
                    break;
                case "CUSTOMERS":
                    var customers = Sage50Repository.Instance.GetCustomers(companyName, updatedAt, updatedBefore, includeMissing);
                    WriteToFile(DateTime.Now + $": Retrieved {customers.Count} customers from Sage 50.");
                    data = customers.Cast<object>().ToList();
                    break;
                case "COMPANY_INFO":
                    // One record, no timestamp to filter on, so updated_at is not
                    // consulted: this always answers with the company as it is now
                    // and Rutter dedupes on $.id.
                    var companyInfo = Sage50Repository.Instance.GetCompanyInfo(companyName);
                    data = companyInfo == null
                        ? new List<object>()
                        : new List<object> { companyInfo };
                    break;
                case "JOURNAL_ENTRIES":
                    var journalEntries = Sage50Repository.Instance.GetJournalEntries(companyName, updatedAt, updatedBefore, includeMissing);
                    WriteToFile(DateTime.Now + $": Retrieved {journalEntries.Count} journal entries from Sage 50.");
                    data = journalEntries.Cast<object>().ToList();
                    break;
                case "INVOICES":
                    var invoices = Sage50Repository.Instance.GetInvoices(companyName, updatedAt, updatedBefore, includeMissing);
                    WriteToFile(DateTime.Now + $": Retrieved {invoices.Count} invoices from Sage 50.");
                    data = invoices.Cast<object>().ToList();
                    break;
                case "BILLS":
                    var bills = Sage50Repository.Instance.GetBills(companyName, updatedAt, updatedBefore, includeMissing);
                    WriteToFile(DateTime.Now + $": Retrieved {bills.Count} bills from Sage 50.");
                    data = bills.Cast<object>().ToList();
                    break;
                case "EXPENSES":
                    var expenses = Sage50Repository.Instance.GetExpenses(companyName, updatedAt, updatedBefore, includeMissing);
                    WriteToFile(DateTime.Now + $": Retrieved {expenses.Count} payments from Sage 50.");
                    data = expenses.Cast<object>().ToList();
                    break;
                case "ITEMS":
                    var items = Sage50Repository.Instance.GetItems(companyName);
                    WriteToFile(DateTime.Now + $": Retrieved {items.Count} inventory items from Sage 50.");
                    data = items.Cast<object>().ToList();
                    break;
                case "INVOICE_PAYMENTS":
                    var invoicePayments = Sage50Repository.Instance.GetInvoicePayments(companyName, updatedAt, updatedBefore, includeMissing);
                    WriteToFile(DateTime.Now + $": Retrieved {invoicePayments.Count} receipts from Sage 50.");
                    data = invoicePayments.Cast<object>().ToList();
                    break;
                case "EMPLOYEES":
                    var employees = Sage50Repository.Instance.GetEmployees(companyName, updatedAt);
                    WriteToFile(DateTime.Now + $": Retrieved {employees.Count} employees from Sage 50.");
                    data = employees.Cast<object>().ToList();
                    break;
                case "TRANSACTIONS":
                    var transactions = Sage50Repository.Instance.GetTransactions(companyName, startDate, endDate);
                    WriteToFile(DateTime.Now + $": Retrieved {transactions.Count} GL transactions from Sage 50 COM exporter.");
                    data = transactions.Cast<object>().ToList();
                    break;
                default:
                    throw new ArgumentException("Unknown platform entity: " + entity);
            }

            // TRANSACTIONS are already filtered by the COM exporter with a
            // validated half-open date window (start <= date < end). The
            // generic FilterByDateWindow uses string comparison and
            // inclusive-inclusive semantics, which would both double-filter
            // and disagree with the exporter on the end boundary. The
            // backend also sends ISO timestamps as start_date / end_date,
            // so string comparison against yyyy-MM-dd GL dates would be
            // incorrect.
            if (entity != "TRANSACTIONS")
            {
                data = FilterByDateWindow(data, startDate, endDate);
            }
            WriteToFile(DateTime.Now + $": Returning {data.Count} {entity}(s) after filtering.");
            return data;
        }

        /// <summary>
        /// Fiscal / date window on transaction bodies that expose a Date property
        /// (yyyy-MM-dd). Non-transaction entities are unaffected.
        /// </summary>
        private static List<object> FilterByDateWindow(List<object> records, string startDate, string endDate)
        {
            if (records == null || records.Count == 0) return records ?? new List<object>();
            if (string.IsNullOrEmpty(startDate) && string.IsNullOrEmpty(endDate)) return records;

            return records.Where(record =>
            {
                var prop = record?.GetType().GetProperty("Date");
                if (prop == null) return true;
                string date = prop.GetValue(record) as string;
                if (string.IsNullOrEmpty(date)) return true;
                if (!string.IsNullOrEmpty(startDate) && string.CompareOrdinal(date, startDate) < 0) return false;
                if (!string.IsNullOrEmpty(endDate) && string.CompareOrdinal(date, endDate) > 0) return false;
                return true;
            }).ToList();
        }

        /// <summary>
        /// Tells Rutter we cannot service a job, so it reaches a terminal state
        /// instead of being re-served on every poll.
        /// </summary>
        private static async Task ReportUnsupportedJob(ResponseObject job, string AccessKey, string reason)
        {
            WriteToFile(DateTime.Now + ": " + reason);
            Helpers.SyncStatus.Instance.SetError(reason);

            var errorObject = new
            {
                connection = new { id = ConnectionId },
                job_id = job.job_id,
                type = job.type,
                platform_entity = job.platform_entity,
                parameters = job.parameters,
                error_message = reason,
            };

            await PostToRutterAsync(Serialize(errorObject), AccessKey);
        }

        /// <summary>
        /// Reads one record back from Sage by its id. Rutter uses this to confirm
        /// what a write actually produced, rather than trusting what it sent.
        /// </summary>
        private static async Task HandleIdFetchJob(ResponseObject job, string AccessKey, string companyName)
        {
            WriteToFile(DateTime.Now + ": Handling ID_FETCH job for " + job.platform_entity);
            try
            {
                if (job.platform_entity != "VENDORS")
                {
                    await ReportUnsupportedJob(job, AccessKey,
                        "ID_FETCH is not supported for " + job.platform_entity + ".");
                    return;
                }

                string platformId = job.parameters?.platform_id;
                if (string.IsNullOrEmpty(platformId))
                {
                    await ReportUnsupportedJob(job, AccessKey, "ID_FETCH job did not include a platform_id.");
                    return;
                }

                Helpers.SyncStatus.Instance.SetSyncing(job.platform_entity, 0, 1);
                var vendor = Sage50Repository.Instance.GetVendorById(companyName, platformId);
                var data = vendor == null ? new List<VendorBody>() : new List<VendorBody> { vendor };
                WriteToFile(DateTime.Now + ": ID_FETCH found " + data.Count + " vendor(s) for id '" + platformId + "'");

                await PostToRutterAsync(Serialize(new
                {
                    connection = new { id = ConnectionId },
                    job_id = job.job_id,
                    type = job.type,
                    platform_entity = job.platform_entity,
                    parameters = job.parameters,
                    data = data,
                }), AccessKey);

                Helpers.SyncStatus.Instance.SetEntitySynced(job.platform_entity, data.Count);
            }
            catch (Exception ex)
            {
                await ReportJobError(job, AccessKey, ex);
            }
        }

        /// <summary>
        /// Applies an update in Sage, then reports the record as Sage holds it
        /// afterwards.
        /// </summary>
        private static async Task HandleUpdateVendorJob(ResponseObject job, string AccessKey, string companyName)
        {
            WriteToFile(DateTime.Now + ": Handling UPDATE job for " + job.platform_entity);
            try
            {
                if (job.platform_entity != "VENDORS")
                {
                    await ReportUnsupportedJob(job, AccessKey,
                        "UPDATE is not supported for " + job.platform_entity + ".");
                    return;
                }

                string platformId = job.parameters?.platform_id;
                if (string.IsNullOrEmpty(platformId))
                {
                    await ReportUnsupportedJob(job, AccessKey, "UPDATE job did not include a platform_id.");
                    return;
                }
                if (job.update_body == null)
                {
                    await ReportUnsupportedJob(job, AccessKey, "UPDATE job did not include an update_body.");
                    return;
                }

                Helpers.SyncStatus.Instance.SetSyncing(job.platform_entity, 0, 1);
                var changes = JsonConvert.DeserializeObject<VendorBody>(job.update_body.ToString());
                var updated = Sage50Repository.Instance.UpdateVendor(companyName, platformId, changes);
                WriteToFile(DateTime.Now + ": Updated vendor '" + platformId + "' in Sage 50");

                await PostToRutterAsync(Serialize(new
                {
                    connection = new { id = ConnectionId },
                    job_id = job.job_id,
                    type = job.type,
                    platform_entity = job.platform_entity,
                    parameters = job.parameters,
                    data = new List<VendorBody> { updated },
                }), AccessKey);

                Helpers.SyncStatus.Instance.SetEntitySynced(job.platform_entity, 1);
            }
            catch (Exception ex)
            {
                await ReportJobError(job, AccessKey, ex);
            }
        }

        private static async Task HandleDeleteVendorJob(ResponseObject job, string AccessKey, string companyName)
        {
            WriteToFile(DateTime.Now + ": Handling DELETE job for " + job.platform_entity);
            try
            {
                if (job.platform_entity != "VENDORS")
                {
                    await ReportUnsupportedJob(job, AccessKey,
                        "DELETE is not supported for " + job.platform_entity + ".");
                    return;
                }

                string platformId = job.parameters?.platform_id;
                if (string.IsNullOrEmpty(platformId))
                {
                    await ReportUnsupportedJob(job, AccessKey, "DELETE job did not include a platform_id.");
                    return;
                }

                Helpers.SyncStatus.Instance.SetSyncing(job.platform_entity, 0, 1);
                bool deleted = Sage50Repository.Instance.DeleteVendor(companyName, platformId);
                WriteToFile(DateTime.Now + ": DELETE vendor '" + platformId + "' - "
                    + (deleted ? "removed" : "no such vendor, treating as already deleted"));

                await PostToRutterAsync(Serialize(new
                {
                    connection = new { id = ConnectionId },
                    job_id = job.job_id,
                    type = job.type,
                    platform_entity = job.platform_entity,
                    parameters = job.parameters,
                    platform_id = platformId,
                }), AccessKey);
            }
            catch (Exception ex)
            {
                await ReportJobError(job, AccessKey, ex);
            }
        }

        /// <summary>
        /// Reports a job failure, distinguishing "Sage has not authorized us" from
        /// a genuine error so the UI can say something useful.
        /// </summary>
        private static async Task ReportJobError(ResponseObject job, string AccessKey, Exception ex)
        {
            if (ex.Message.IndexOf("Pending", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Helpers.SyncStatus.Instance.SetNeedsAuthorization();
            }
            else
            {
                Helpers.SyncStatus.Instance.SetError(ex.Message);
            }

            WriteToFile(DateTime.Now + ": Error handling " + job.type + " job for " + job.platform_entity + ". Error: " + ex.Message);

            await PostToRutterAsync(Serialize(new
            {
                connection = new { id = ConnectionId },
                job_id = job.job_id,
                type = job.type,
                platform_entity = job.platform_entity,
                parameters = job.parameters,
                error_message = ex.Message,
            }), AccessKey);
        }

        /// <summary>
        /// Serialize a payload for Rutter. Always the anonymous object directly —
        /// see the note in HandleListFetchJob about JObject.FromObject silently
        /// discarding the camelCase resolver.
        /// </summary>
        private static string Serialize(object payload)
        {
            return JsonConvert.SerializeObject(payload, new JsonSerializerSettings
            {
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
            });
        }

        private static async Task HandleCreateVendorJob(ResponseObject job, string AccessKey, string companyName)
        {
            WriteToFile(DateTime.Now + ": Handling CREATE job for VENDORS");
            try
            {
                var vendorBody = JsonConvert.DeserializeObject<VendorBody>(job.create_body.data.ToString());
                WriteToFile(DateTime.Now + ": Creating Vendor in Sage 50: " + vendorBody.Name);

                // Create the vendor in Sage 50
                var createdVendor = Sage50Repository.Instance.CreateVendor(companyName, vendorBody);
                if (createdVendor != null)
                {
                    // Fetch the created vendor to ensure complete details
                    var fetchedVendor = Sage50Repository.Instance.GetVendorById(companyName, createdVendor.ID);
                    if (fetchedVendor != null)
                    {
                        WriteToFile(DateTime.Now + ": Created Vendor in Sage 50: " + fetchedVendor.Name);

                        var responseObject = new
                        {
                            connection = new
                            {
                                id = ConnectionId
                            },
                            job_id = job.job_id,
                            type = job.type,
                            platform_entity = job.platform_entity,
                            data = new List<VendorBody> { fetchedVendor }
                        };

                        string jsonString = JsonConvert.SerializeObject(responseObject, new JsonSerializerSettings
                        {
                            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                        });

                        await PostToRutterAsync(jsonString, AccessKey);
                    }
                    else
                    {
                        WriteToFile(DateTime.Now + ": Failed to fetch the created vendor from Sage 50.");
                    }
                }
                else
                {
                    WriteToFile(DateTime.Now + ": Failed to create vendor in Sage 50.");
                }
            }
            catch (Exception ex)
            {
                WriteToFile(DateTime.Now + ": Error handling CREATE job for VENDORS. Error: " + ex.Message);
                var errorObject = new
                {
                    connection = new
                    {
                        id = ConnectionId
                    },
                    job_id = job.job_id,
                    type = job.type,
                    platform_entity = job.platform_entity,
                    error_message = ex.Message
                };

                string jsonString = JsonConvert.SerializeObject(errorObject, new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                });

                await PostToRutterAsync(jsonString, AccessKey);
            }
        }



        private static async Task PostToRutterAsync(string jsonString, string AccessKey)
        {
            using (HttpClient client = new HttpClient())
            {
                var request = new HttpRequestMessage(HttpMethod.Post, Config.IngestUrl);
                request.Headers.Add("X-Rutter-Version", "2024-04-30");
                request.Headers.Add("Authorization", $"Bearer {AccessKey}");
                request.Content = new StringContent(jsonString, Encoding.UTF8, "application/json");

                var response = await client.SendAsync(request);

                // Always log the status, on both branches. On 2026-08-03 a run
                // logged 39 "Successfully posted to Rutter." while ngrok recorded
                // an HTTP 500 for every one of those reports — the long-standing
                // "Unexplained" entry in CLAUDE.md, reproduced. This code reads
                // correct, so the next occurrence needs the status code in the log
                // to say whether the connector is mis-reporting or genuinely
                // receiving 2xx. Do not remove the code in either message.
                if (!response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    WriteToFile(DateTime.Now + $": Failed to post to Rutter. Status code: {(int)response.StatusCode} {response.StatusCode}, Response: {responseContent}");
                }
                else
                {
                    WriteToFile(DateTime.Now + $": Successfully posted to Rutter. Status code: {(int)response.StatusCode} {response.StatusCode}");
                }
            }
        }

        private static readonly object logLock = new object();

        public static void WriteToFile(string message)
        {
            string logMessage = DateTime.Now.ToString() + ": " + message;
            Console.WriteLine(logMessage);

            // Ensure thread safety if the application becomes multi-threaded
            lock (logLock)
            {
                using (StreamWriter writer = new StreamWriter(ConnectorConfig.ResolveLogFilePath(), true))
                {
                    writer.WriteLine(logMessage);
                    writer.Close();
                }
            }
        }
    }
}
