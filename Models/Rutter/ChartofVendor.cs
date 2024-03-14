using Sage.Peachtree.API;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Sage50Connector.Models.Rutter
{
    public class ChartofVendor
    {
        public string AccountNumber { get; set; }
        public decimal Balance { get; set; }
        public string Category { get; set; }
        public string Email { get; set; }
        public string ID { get; set; }
        public bool IncludePurchaseRepresentativeOnEmailedForms { get; set; }
        public bool IsInactive { get; set; }
        public decimal LastInvoiceAmount { get; set; }
        public DateTime? LastInvoiceDate { get; set; }
        public decimal LastPaymentAmount { get; set; }
        public DateTime? LastPaymentDate { get; set; }
        public string Name { get; set; }
        public string PaymentMethod { get; set; }
        public bool ReplaceInventoryItemIDWithPartNumber { get; set; }
        public bool ReplaceInventoryItemIDWithUPC { get; set; }
        public string ShipVia { get; set; }
        public string TaxIDNumber { get; set; }
        public VendorForm1099Type Form1099Type { get; set; }
        public bool UseEmailToDeliverForms { get; set; }
        public bool UsingPaymentDefaults { get; set; }
        public DateTime? VendorSince { get; set; }
        public string WebSiteURL { get; set; }
        public EntityReference<Account> CashAccountReference { get; set; }
        public ContactList Contacts { get; set; }
        public CustomFieldValueCollection CustomFieldValues { get; set; }
        public EntityReference<Account> ExpenseAccountReference { get; set; }
        public Contact MailToContact { get; set; }
        public Contact PaymentsContact { get; set; }
        public PaymentTerms Terms { get; set; }
        public PhoneNumberCollection PhoneNumbers { get; set; }
        public Contact PurchaseOrdersContact { get; set; }
        public EntityReference<Employee> PurchaseRepresentativeReference { get; set; }
        public Contact ShipmentsContact { get; set; }
        public DateTime? LastSavedAt { get; set; }
    }
}
