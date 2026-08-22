using System.Collections.Generic;

namespace Sage50Connector.Models.Rutter
{
    public class GlTransactionLineBody
    {
        public string ID { get; set; }
        public long JournalPostOrder { get; set; }
        public int JournalRowIndex { get; set; }
        public string AccountID { get; set; }
        public string AccountGuid { get; set; }
        public string Date { get; set; }
        /// <summary>
        /// Numeric journal type (0–9), or null when the journal code is unknown
        /// or unmapped. journalTypeCode is always preserved as-is.
        /// </summary>
        public int? JournalType { get; set; }
        public string JournalTypeCode { get; set; }
        public string Reference { get; set; }
        public string Description { get; set; }
        public string JobId { get; set; }
        public string JobGuid { get; set; }
        public decimal Amount { get; set; }
        public string DateCleared { get; set; }
        public bool IncludeInGL { get; set; }
    }

    public class GlTransactionBody
    {
        public string ID { get; set; }
        public long JournalPostOrder { get; set; }
        public string Date { get; set; }
        public decimal Amount { get; set; }
        public List<string> JournalTypeCodes { get; set; } = new List<string>();
        public List<string> References { get; set; } = new List<string>();
        public bool HeaderConsistent { get; set; }
        public List<GlTransactionLineBody> Lines { get; set; } = new List<GlTransactionLineBody>();
    }
}
