using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;

namespace Sage50Connector.Helpers
{
    /// <summary>
    /// Loads the connector's runtime configuration (sage50Config.json) and owns
    /// every file path the connector touches.
    ///
    /// Config lives at %ProgramData%\Rutter\Sage50Connector\sage50Config.json.
    /// The first version of the connector read it from
    /// C:\Users\Default\Documents\sage50Config.json — that path is still honored
    /// as a fallback so existing setups keep working.
    ///
    /// Shape:
    /// {
    ///   "CompanyName": "<Sage 50 company name>",
    ///   "AccessKey":   "<credential inbound access token (iat_...)>",
    ///   "ConnectionId": "<Rutter item id>",
    ///   "ApiBaseUrl":  "https://production.rutterapi.com"   // optional; defaults to prod
    /// }
    /// </summary>
    class ConnectorConfig
    {
        public const string DefaultApiBaseUrl = "https://production.rutterapi.com";

        public static readonly string ConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Rutter",
            "Sage50Connector"
        );

        public static readonly string LegacyConfigDirectory = @"C:\Users\Default\Documents";

        public static readonly string ConfigFilePath = Path.Combine(ConfigDirectory, "sage50Config.json");
        public static readonly string LegacyConfigFilePath = Path.Combine(LegacyConfigDirectory, "sage50Config.json");

        public static readonly string LogFilePath = Path.Combine(ConfigDirectory, "log.txt");
        public static readonly string LegacyLogFilePath = Path.Combine(LegacyConfigDirectory, "log.txt");

        public string CompanyName { get; private set; }
        public string CompanyGuid { get; private set; }
        public string AccessKey { get; private set; }
        public string ConnectionId { get; private set; }
        public string ApiBaseUrl { get; private set; }
        public string LoadedFromPath { get; private set; }

        public string IngestUrl => ApiBaseUrl.TrimEnd('/') + "/versioned/ingest";
        public string SaveIdUrl => ApiBaseUrl.TrimEnd('/') + "/sage-50/save-id";

        /// <summary>
        /// The config path the connector reads: the ProgramData location when it
        /// exists, otherwise the legacy location, otherwise the ProgramData path
        /// (so load errors point operators at the right place).
        /// </summary>
        public static string ResolveConfigFilePath()
        {
            if (File.Exists(ConfigFilePath))
            {
                return ConfigFilePath;
            }
            if (File.Exists(LegacyConfigFilePath))
            {
                return LegacyConfigFilePath;
            }
            return ConfigFilePath;
        }

        /// <summary>
        /// The log path: ProgramData when its directory can be created, otherwise
        /// the legacy documents folder.
        /// </summary>
        public static string ResolveLogFilePath()
        {
            try
            {
                Directory.CreateDirectory(ConfigDirectory);
                return LogFilePath;
            }
            catch (Exception)
            {
                return LegacyLogFilePath;
            }
        }

        public static ConnectorConfig Load()
        {
            string path = ResolveConfigFilePath();
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "sage50Config.json not found. Run 'Sage50Connector.exe --setup <CompanyName> <OrgId>' "
                        + "or place the config file at " + ConfigFilePath,
                    path
                );
            }

            JObject json = JObject.Parse(File.ReadAllText(path));
            return new ConnectorConfig
            {
                CompanyName = GetRequiredValue(json, "CompanyName"),
                CompanyGuid = json.Value<string>("CompanyGuid"),
                AccessKey = GetRequiredValue(json, "AccessKey"),
                ConnectionId = GetRequiredValue(json, "ConnectionId"),
                ApiBaseUrl = json.Value<string>("ApiBaseUrl") ?? DefaultApiBaseUrl,
                LoadedFromPath = path,
            };
        }

        /// <summary>
        /// Writes sage50Config.json to the ProgramData directory, creating it if
        /// needed. Never logs the access key.
        /// </summary>
        public static ConnectorConfig Save(
            string companyName,
            string accessKey,
            string connectionId,
            string apiBaseUrl,
            string companyGuid = null)
        {
            Directory.CreateDirectory(ConfigDirectory);
            var config = new ConnectorConfig
            {
                CompanyName = companyName,
                CompanyGuid = companyGuid,
                AccessKey = accessKey,
                ConnectionId = connectionId,
                ApiBaseUrl = string.IsNullOrWhiteSpace(apiBaseUrl) ? DefaultApiBaseUrl : apiBaseUrl,
                LoadedFromPath = ConfigFilePath,
            };

            var json = new JObject
            {
                ["CompanyName"] = config.CompanyName,
                ["AccessKey"] = config.AccessKey,
                ["ConnectionId"] = config.ConnectionId,
            };
            if (!string.IsNullOrWhiteSpace(config.CompanyGuid))
            {
                json["CompanyGuid"] = config.CompanyGuid;
            }
            if (!string.Equals(config.ApiBaseUrl, DefaultApiBaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                json["ApiBaseUrl"] = config.ApiBaseUrl;
            }
            File.WriteAllText(ConfigFilePath, json.ToString(Formatting.Indented));
            return config;
        }

        private static string GetRequiredValue(JObject config, string key)
        {
            string value = config.Value<string>(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException("sage50Config.json is missing required value: " + key);
            }
            return value;
        }
    }
}
