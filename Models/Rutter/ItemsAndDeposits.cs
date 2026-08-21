using System.Collections.Generic;

namespace Sage50Connector.Models.Rutter
{
    public class InventoryItemBody
    {
        public string ID { get; set; }
        public string Guid { get; set; }
        public string Description { get; set; }
        public string PartNumber { get; set; }
        public string ItemType { get; set; }
        public bool IsInactive { get; set; }
        public string LastSavedAt { get; set; }
        public decimal? QuantityOnHand { get; set; }
        public string SalesDescription { get; set; }
        public string PurchaseDescription { get; set; }
        public string SalesAccountID { get; set; }
        public string InventoryAccountID { get; set; }
        public string CogsAccountID { get; set; }
        public List<object> PriceLevels { get; set; } = new List<object>();
    }

    public class BankDepositReceiptBody
    {
        public string ID { get; set; }
        public string ReferenceNumber { get; set; }
        public string CustomerID { get; set; }
        public decimal Amount { get; set; }
        public string Date { get; set; }
    }

    public class BankDepositBody
    {
        public string ID { get; set; }
        public string DepositTicketID { get; set; }
        public string Date { get; set; }
        public string AccountID { get; set; }
        public decimal Amount { get; set; }
        public List<BankDepositReceiptBody> Receipts { get; set; } = new List<BankDepositReceiptBody>();
    }
}
