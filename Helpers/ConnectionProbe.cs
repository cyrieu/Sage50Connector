using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Sage50Connector.Helpers
{
    /// <summary>
    /// Outcome of asking Rutter whether a stored connection can still accept
    /// ingest traffic. No Sage types: the mapping is pure and the HTTP call
    /// uses the same headers as a real poll.
    /// </summary>
    internal sealed class RutterProbeResult
    {
        public string Status { get; private set; }
        public int? StatusCode { get; private set; }
        public string UserMessage { get; private set; }

        public bool IsAlive
        {
            get { return string.Equals(Status, ConnectionProbeResults.Alive, StringComparison.Ordinal); }
        }

        public static RutterProbeResult Alive(int statusCode)
        {
            return new RutterProbeResult
            {
                Status = ConnectionProbeResults.Alive,
                StatusCode = statusCode,
                UserMessage = "Connected",
            };
        }

        public static RutterProbeResult Disconnected(int statusCode)
        {
            return new RutterProbeResult
            {
                Status = ConnectionProbeResults.Disconnected,
                StatusCode = statusCode,
                UserMessage = DisconnectedMessage,
            };
        }

        public static RutterProbeResult Unreachable(int? statusCode)
        {
            return new RutterProbeResult
            {
                Status = ConnectionProbeResults.Unreachable,
                StatusCode = statusCode,
                UserMessage = UnreachableMessage,
            };
        }
    }

    /// <summary>
    /// Side-effect-free check that a stored Rutter connection is still alive.
    /// The mock ingest body returns a canned job and does not dequeue work.
    /// </summary>
    internal static class ConnectionProbe
    {
        internal const string IngestVersionHeaderName = "X-Rutter-Version";
        internal const string IngestVersion = "2024-04-30";
        internal const string DisconnectedMessage =
            "This Rutter connection was removed or its access was revoked. Reconnect this company through Rutter Link.";
        internal const string UnreachableMessage =
            "Couldn't reach Rutter. Check the internet connection and try again.";

        internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Maps an HTTP status to a user-facing probe result.
        /// 401/403/404/410 mean the connection or token is gone.
        /// Anything else that is not a success is retryable: a 5xx, or an
        /// unexpected 4xx, is not proof the connection was revoked.
        /// </summary>
        internal static RutterProbeResult FromHttpStatus(int statusCode)
        {
            if (statusCode >= 200 && statusCode <= 299)
                return RutterProbeResult.Alive(statusCode);
            if (statusCode == 401 || statusCode == 403 || statusCode == 404 || statusCode == 410)
                return RutterProbeResult.Disconnected(statusCode);
            return RutterProbeResult.Unreachable(statusCode);
        }

        public static async Task<RutterProbeResult> ProbeAsync(
            string apiBaseUrl,
            string connectionId,
            string accessKey)
        {
            string baseUrl = ConnectionRegistry.NormalizeApiBaseUrl(apiBaseUrl);
            string url = baseUrl + "/versioned/ingest";
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = Timeout;
                    var request = new HttpRequestMessage(HttpMethod.Post, url);
                    request.Headers.TryAddWithoutValidation(IngestVersionHeaderName, IngestVersion);
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessKey);
                    // Property names are already the wire casing. Do not run this
                    // through a contract resolver that would rename them.
                    request.Content = new StringContent(
                        JsonConvert.SerializeObject(new
                        {
                            connection = new { id = connectionId },
                            mock = "LIST_FETCH",
                        }),
                        Encoding.UTF8,
                        "application/json");

                    using (HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false))
                    {
                        return FromHttpStatus((int)response.StatusCode);
                    }
                }
            }
            catch (Exception ex)
            {
                // Timeout, DNS, TLS, or a cancelled socket. The connection
                // itself was not rejected; the user can try again.
                global::Sage50Connector.Program.WriteToFile(
                    "Rutter connection probe failed: " + ex.GetType().Name + ": " + ex.Message);
                return RutterProbeResult.Unreachable(null);
            }
        }
    }
}
