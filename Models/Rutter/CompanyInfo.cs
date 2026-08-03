namespace Sage50Connector.Models.Rutter
{
    /// <summary>
    /// The one company-level record Sage 50 exposes, flattened for Rutter's
    /// company_info entity.
    ///
    /// Flat rather than nested: the address is the only nested thing Sage gives
    /// (Company.Address is a NameAndAddress wrapping an Address), and flattening
    /// it keeps the JSONPath the Rutter mapper reads shallow.
    ///
    /// Sage 50 US has no company-level currency and no company-level timestamp,
    /// so neither is sent — see GetCompanyInfo.
    /// </summary>
    public class CompanyInfo
    {
        /// <summary>
        /// Sage's own company GUID. Stable across renames and across a company
        /// being moved to another directory, which the company name is not.
        /// Rutter reads the primary key from $.id.
        /// </summary>
        public string ID { get; set; } = "";

        /// <summary>The company name Sage lists, and the name in sage50Config.json.</summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// The name on the company's **billing** address — the SDK documents
        /// `Company.Address` as "the billing address", so this is not a legal
        /// entity name and should not be presented as one without checking it
        /// against a real company. Sage exposes no separate legal name. Often
        /// equal to Name.
        /// </summary>
        public string LegalName { get; set; } = "";

        /// <summary>"Accrual" or "CashBasis".</summary>
        public string AccountingMethod { get; set; } = "";

        public string Address1 { get; set; } = "";
        public string Address2 { get; set; } = "";
        public string City { get; set; } = "";
        public string State { get; set; } = "";
        public string Zip { get; set; } = "";
        public string Country { get; set; } = "";

        /// <summary>
        /// First and last day of the earliest and latest accounting periods Sage
        /// has defined for the company. Not part of Rutter's company_info schema,
        /// but the only way to know what date range a migration can even ask for
        /// — a transaction fetch outside the defined periods returns nothing.
        /// </summary>
        public string FiscalYearStart { get; set; }
        public string FiscalYearEnd { get; set; }

        /// <summary>
        /// Where this company physically lives. Diagnostics only, and the reason
        /// they are here: when a customer has two similarly named companies, this
        /// is what tells support which one the connector actually opened.
        /// </summary>
        public string DatabaseName { get; set; } = "";
        public string ServerName { get; set; } = "";
        public string SchemaVersion { get; set; } = "";
    }
}
