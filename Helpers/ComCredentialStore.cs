using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Sage50Connector.Helpers
{
    internal struct SageComCredential
    {
        public string UserName;
        public string Password;
    }

    /// <summary>
    /// Stores Sage's shared COM application credential encrypted to the current
    /// Windows user. That is the same interactive user who grants Sage access
    /// and runs the connector; another account (including SYSTEM) cannot decrypt it.
    /// </summary>
    internal static class ComCredentialStore
    {
        internal static readonly string CredentialFilePath = Path.Combine(
            ConnectorConfig.ConfigDirectory, "sage-com-credential.bin");
        internal static readonly string AuthorizationMarkerFilePath = Path.Combine(
            ConnectorConfig.ConfigDirectory, "sage-com-authorization.json");
        internal static readonly string LegacyCredentialFilePath = Path.Combine(
            ConnectorConfig.ConfigDirectory, "diagnostics", "sage-com-credential.xml");

        public static void Save(string userName, string password)
        {
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
                throw new InvalidOperationException("Rutter received an invalid Sage COM application credential.");

            Directory.CreateDirectory(ConnectorConfig.ConfigDirectory);
            byte[] plaintext = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
            {
                username = userName,
                password = password,
            }));
            byte[] ciphertext = null;
            string temporaryPath = CredentialFilePath + ".tmp";
            try
            {
                ciphertext = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(temporaryPath, ciphertext);
                if (File.Exists(CredentialFilePath)) File.Delete(CredentialFilePath);
                File.Move(temporaryPath, CredentialFilePath);
            }
            finally
            {
                Array.Clear(plaintext, 0, plaintext.Length);
                if (ciphertext != null) Array.Clear(ciphertext, 0, ciphertext.Length);
                if (File.Exists(temporaryPath)) try { File.Delete(temporaryPath); } catch { }
            }
        }

        public static SageComCredential Load()
        {
            if (File.Exists(CredentialFilePath)) return LoadCurrent();
            if (File.Exists(LegacyCredentialFilePath))
            {
                SageComCredential legacy = LoadLegacy();
                Save(legacy.UserName, legacy.Password);
                return legacy;
            }
            throw new FileNotFoundException(
                "The Sage COM application credential has not been provisioned for this Windows user.",
                CredentialFilePath);
        }

        public static bool CanLoad()
        {
            try { Load(); return true; }
            catch { return false; }
        }

        /// <summary>
        /// Avoid requiring Sage to be open on every ordinary connector restart.
        /// The live COM probe is still repeated after an SDK re-approval and
        /// immediately before every TRANSACTIONS export.
        /// </summary>
        public static bool IsAuthorizationConfirmed(string companyGuid, string companyName)
        {
            try
            {
                if (!File.Exists(AuthorizationMarkerFilePath)) return false;
                JObject marker = JObject.Parse(File.ReadAllText(AuthorizationMarkerFilePath));
                string savedGuid = marker.Value<string>("companyGuid");
                if (!string.IsNullOrWhiteSpace(companyGuid))
                    return string.Equals(savedGuid, companyGuid, StringComparison.OrdinalIgnoreCase);
                return string.Equals(
                    marker.Value<string>("companyName"), companyName, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        public static void MarkAuthorizationConfirmed(string companyGuid, string companyName)
        {
            Directory.CreateDirectory(ConnectorConfig.ConfigDirectory);
            string temporaryPath = AuthorizationMarkerFilePath + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(new
                {
                    companyGuid,
                    companyName,
                    confirmedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                }));
                if (File.Exists(AuthorizationMarkerFilePath))
                    File.Delete(AuthorizationMarkerFilePath);
                File.Move(temporaryPath, AuthorizationMarkerFilePath);
            }
            finally
            {
                if (File.Exists(temporaryPath)) try { File.Delete(temporaryPath); } catch { }
            }
        }

        private static SageComCredential LoadCurrent()
        {
            byte[] ciphertext = File.ReadAllBytes(CredentialFilePath);
            byte[] plaintext = null;
            try
            {
                plaintext = ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
                JObject value = JObject.Parse(Encoding.UTF8.GetString(plaintext));
                return Validate(value.Value<string>("username"), value.Value<string>("password"));
            }
            finally
            {
                Array.Clear(ciphertext, 0, ciphertext.Length);
                if (plaintext != null) Array.Clear(plaintext, 0, plaintext.Length);
            }
        }

        private static SageComCredential LoadLegacy()
        {
            XDocument doc = XDocument.Parse(File.ReadAllText(LegacyCredentialFilePath));
            string userName = null;
            string encryptedPasswordHex = null;
            foreach (var element in doc.Descendants())
            {
                string name = element.Attribute("N") != null ? element.Attribute("N").Value : null;
                if (name == "UserName" && element.Name.LocalName == "S") userName = element.Value;
                if (name == "Password" && element.Name.LocalName == "SS") encryptedPasswordHex = element.Value;
            }
            if (string.IsNullOrWhiteSpace(encryptedPasswordHex))
                throw new InvalidDataException("The legacy Sage COM credential is incomplete.");

            byte[] encrypted = HexStringToBytes(encryptedPasswordHex);
            byte[] plaintext = null;
            try
            {
                plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Validate(userName, Encoding.Unicode.GetString(plaintext));
            }
            finally
            {
                Array.Clear(encrypted, 0, encrypted.Length);
                if (plaintext != null) Array.Clear(plaintext, 0, plaintext.Length);
            }
        }

        private static SageComCredential Validate(string userName, string password)
        {
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
                throw new InvalidDataException("The Sage COM application credential is incomplete.");
            return new SageComCredential { UserName = userName, Password = password };
        }

        private static byte[] HexStringToBytes(string hex)
        {
            hex = (hex ?? "").Trim();
            if (hex.Length == 0 || hex.Length % 2 != 0)
                throw new FormatException("The legacy Sage COM credential is corrupt.");
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }
    }
}
