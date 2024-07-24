using Sage.Peachtree.API;
using System;
using System.Collections.Generic;

namespace Sage50Connector.Models.Rutter
{
    public class ChartofCustomer
    {
        public string AccountNumber { get; set; }
        public int AverageDaysToPayInvoices { get; set; }
        public decimal Balance { get; set; }
        public Contact BillToContact { get; set; }
        public EntityReference<Account> CashAccountReference { get; set; }
        public string Category { get; set; }
        public List<Contact> Contacts { get; set; }
        public string CreditStatus { get; set; }
        public DateTime CustomerSince { get; set; }
        public Dictionary<string, string> CustomFieldValues { get; set; }
        public string Email { get; set; }
        public string ID { get; set; }
        public bool IsInactive { get; set; }
        public bool IsProspect { get; set; }
        public decimal LastInvoiceAmount { get; set; }
        public DateTime? LastInvoiceDate { get; set; }
        public decimal LastPaymentAmount { get; set; }
        public DateTime? LastPaymentDate { get; set; }
        public DateTime? LastSavedAt { get; set; }
        public DateTime? LastStatementDate { get; set; }
        public string Name { get; set; }
        public string OpenPurchaseOrderNumber { get; set; }
        public string PaymentMethod { get; set; }
        public List<PhoneNumber> PhoneNumbers { get; set; }
        public string PriceLevel { get; set; }
        public bool ReplaceInventoryItemIDWithPartNumber { get; set; }
        public bool ReplaceInventoryItemIDWithUPC { get; set; }
        public string ResaleNumber { get; set; }
     //   public EmployeeReference SalesRepresentativeReference { get; set; }
        public Contact ShipToContact { get; set; }
        public string ShipVia { get; set; }
        public string Terms { get; set; }
        public bool UseEmailToDeliverForms { get; set; }
        public EntityReference<Account> UsualSalesAccountReference { get; set; }
        public string WebSiteURL { get; set; }
    }
}
