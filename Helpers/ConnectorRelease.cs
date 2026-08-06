using Newtonsoft.Json;
using System;

namespace Sage50Connector.Helpers
{
    /// <summary>
    /// Published release metadata (S3 / Rutter GET /sage-50/connector-release).
    /// Silent auto-update is impossible (Sage grants are MD5 of the EXE); this
    /// drives assisted upgrade + re-approval.
    /// </summary>
    public class ConnectorRelease
    {
        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("min_version")]
        public string MinVersion { get; set; }

        [JsonProperty("msi_url")]
        public string MsiUrl { get; set; }

        [JsonProperty("sha256")]
        public string Sha256 { get; set; }

        [JsonProperty("released_at")]
        public string ReleasedAt { get; set; }

        [JsonProperty("notes")]
        public string Notes { get; set; }

        /// <summary>Always true for real EXE changes; surfaced in the UI.</summary>
        [JsonProperty("requires_sage_reapproval")]
        public bool RequiresSageReapproval { get; set; }

        [JsonIgnore]
        public Version ParsedVersion
        {
            get { return AppVersion.ParseLoose(Version); }
        }

        [JsonIgnore]
        public Version ParsedMinVersion
        {
            get { return AppVersion.ParseLoose(MinVersion); }
        }
    }

    public enum UpdateAvailability
    {
        Unknown,
        UpToDate,
        OptionalUpdate,
        RequiredUpdate,
        CheckFailed,
    }

    public class UpdateCheckResult
    {
        public UpdateAvailability Availability { get; set; }
        public ConnectorRelease Release { get; set; }
        public string ErrorMessage { get; set; }
        public Version LocalVersion { get; set; }

        public bool HasUpdate
        {
            get
            {
                return Availability == UpdateAvailability.OptionalUpdate
                    || Availability == UpdateAvailability.RequiredUpdate;
            }
        }

        public bool IsForced
        {
            get { return Availability == UpdateAvailability.RequiredUpdate; }
        }
    }
}
