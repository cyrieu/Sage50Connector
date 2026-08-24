using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Sage50Connector.Helpers
{
    /// <summary>One-use RSA envelope for transferring the shared Sage COM credential.</summary>
    internal sealed class ComCredentialProvisioner : IDisposable
    {
        private readonly RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(2048);

        public ComCredentialProvisioner()
        {
            // The private key exists only long enough to decrypt one response.
            rsa.PersistKeyInCsp = false;
        }

        public object PublicKey
        {
            get
            {
                RSAParameters key = rsa.ExportParameters(false);
                return new
                {
                    modulus = Convert.ToBase64String(key.Modulus),
                    exponent = Convert.ToBase64String(key.Exponent),
                };
            }
        }

        public void DecryptAndSave(string encryptedBase64)
        {
            if (string.IsNullOrWhiteSpace(encryptedBase64))
                throw new InvalidOperationException("Rutter did not return the Sage COM credential.");
            byte[] ciphertext = Convert.FromBase64String(encryptedBase64);
            byte[] plaintext = null;
            try
            {
                plaintext = rsa.Decrypt(ciphertext, true);
                JObject credential = JObject.Parse(Encoding.UTF8.GetString(plaintext));
                ComCredentialStore.Save(
                    credential.Value<string>("username"),
                    credential.Value<string>("password"));
            }
            finally
            {
                Array.Clear(ciphertext, 0, ciphertext.Length);
                if (plaintext != null) Array.Clear(plaintext, 0, plaintext.Length);
            }
        }

        public static async Task EnsureProvisionedAsync(ConnectorConfig config)
        {
            if (ComCredentialStore.CanLoad()) return;
            using (var envelope = new ComCredentialProvisioner())
            using (var client = new HttpClient())
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    config.ApiBaseUrl.TrimEnd('/') + "/sage-50/com-credential");
                request.Headers.Add("Authorization", "Bearer " + config.AccessKey);
                request.Content = new StringContent(JsonConvert.SerializeObject(new
                {
                    connection = new { id = config.ConnectionId },
                    com_credential_public_key = envelope.PublicKey,
                }), Encoding.UTF8, "application/json");
                var response = await client.SendAsync(request);
                string content = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("Rutter could not provision Sage transaction access (HTTP " + (int)response.StatusCode + ").");
                JObject body = JObject.Parse(content);
                envelope.DecryptAndSave(body.Value<string>("com_credential_encrypted"));
            }
        }

        public void Dispose()
        {
            rsa.Clear();
            rsa.Dispose();
        }
    }
}
