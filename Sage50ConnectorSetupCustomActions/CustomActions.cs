using Microsoft.Deployment.WindowsInstaller;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Sage50ConnectorSetupCustomActions
{
    /// <summary>
    /// Deferred MSI custom actions for the Sage 50 connector installer.
    /// Writes sage50Config.json (the same shape Helpers\ConnectorConfig.cs
    /// reads) to %ProgramData%\Rutter\Sage50Connector\ from the installer
    /// properties COMPANYNAME / ACCESSKEY / CONNECTIONID.
    /// </summary>
    public static class CustomActions
    {
        [CustomAction]
        public static ActionResult WriteSageConfigJson(Session session)
        {
            try
            {
                string companyName = CustomActionDataValue(session, "CompanyName");
                string accessKey = CustomActionDataValue(session, "AccessKey");
                string connectionId = CustomActionDataValue(session, "ConnectionId");

                if (string.IsNullOrWhiteSpace(companyName)
                    || string.IsNullOrWhiteSpace(accessKey)
                    || string.IsNullOrWhiteSpace(connectionId))
                {
                    session.Log(
                        "COMPANYNAME/ACCESSKEY/CONNECTIONID were not all provided; "
                            + "skipping sage50Config.json. Provision later with "
                            + "'Sage50Connector.exe --setup <CompanyName> <OrgId>'."
                    );
                    return ActionResult.Success;
                }

                string configDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Rutter",
                    "Sage50Connector"
                );
                Directory.CreateDirectory(configDirectory);
                string configPath = Path.Combine(configDirectory, "sage50Config.json");
                // AccessKey is a credential — never write it to the MSI log.
                session.Log("Writing sage50Config.json to " + configPath + " for company '" + companyName + "'");

                string json =
                    "{\"CompanyName\":\""
                    + EscapeJsonString(companyName)
                    + "\",\"AccessKey\":\""
                    + EscapeJsonString(accessKey)
                    + "\",\"ConnectionId\":\""
                    + EscapeJsonString(connectionId)
                    + "\"}";
                File.WriteAllText(configPath, json + Environment.NewLine);
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("Failed to write sage50Config.json: " + ex.Message);
                return ActionResult.Failure;
            }
        }

        private static string CustomActionDataValue(Session session, string key)
        {
            session.CustomActionData.TryGetValue(key, out string value);
            return value;
        }

        private static string EscapeJsonString(string input)
        {
            var builder = new StringBuilder(input.Length + 8);
            foreach (char c in input)
            {
                switch (c)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u");
                            builder.Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(c);
                        }
                        break;
                }
            }
            return builder.ToString();
        }
    }
}
