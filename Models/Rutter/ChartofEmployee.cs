namespace Sage50Connector.Models.Rutter
{
    /// <summary>
    /// A Sage Employee. Sage 50 keeps a short list — id, name, email, inactive
    /// flag, and whether they are a sales representative — not a full HRIS
    /// record. There is no LastSavedAt on employees, so every sync re-sends the
    /// list and Rutter upserts on $.id.
    /// </summary>
    public class ChartofEmployee
    {
        /// <summary>Sage employee ID string — the platform_id, not a GUID.</summary>
        public string ID { get; set; }

        public string Name { get; set; }
        public string Email { get; set; }
        public bool IsInactive { get; set; }
        public bool IsSalesRepresentative { get; set; }

        /// <summary>First phone number Sage has on the employee, if any.</summary>
        public string Phone { get; set; }
    }
}
