using System.Collections.Generic;

namespace Sage50Connector.Models.Rutter
{
    /// <summary>
    /// Sage's NameAndAddress, flattened. Same shape the company info read uses.
    /// </summary>
    public class SageAddressBody
    {
        public string Name { get; set; }
        public string Address1 { get; set; }
        public string Address2 { get; set; }
        public string City { get; set; }
        public string State { get; set; }
        public string Zip { get; set; }
        public string Country { get; set; }
    }

    /// <summary>
    /// Fields shared by every transaction line. Sage puts these on
    /// TransactionLine and the concrete line types add to them.
    ///
    /// Lines whose IsUsed is false are Sage's empty slots, not data — the
    /// repository drops them before building these, so every line here is real.
    /// </summary>
    public abstract class TransactionLineBodyBase
    {
        /// <summary>The line's own Sage key, as a GUID string.</summary>
        public string ID { get; set; }

        /// <summary>
        /// Which Sage collection this line came from.
        ///
        /// Sage splits one document's lines across several collections depending
        /// on what the document was raised from: an invoice typed by hand keeps
        /// its lines in ApplyToSalesLines, but one raised from a sales order keeps
        /// them in ApplyToSalesOrderLines, and from a proposal in
        /// ApplyToProposalLines. They are merged into a single `lines` array here
        /// because they are all lines of the same invoice, and tagged so a mapper
        /// can still tell them apart — retainage in particular is a withholding,
        /// not a sale.
        ///
        /// Reading only the first collection lost every line of 10 of
        /// Bellwether's 107 invoices ($28,333.82) and 4 of its 59 bills.
        /// </summary>
        public string LineType { get; set; }

        /// <summary>
        /// GL account, as the account's Sage **ID** (e.g. "10200-00") rather than
        /// its GUID, so it matches the platform_id of the ACCOUNTS rows Rutter
        /// already holds. Null when the account could not be resolved.
        /// </summary>
        public string AccountID { get; set; }

        public decimal Amount { get; set; }
        public string Description { get; set; }

        /// <summary>
        /// Job (Sage's project tracking) as a raw GUID. Not resolved to an ID
        /// because jobs are not a synced entity yet; named Guid so it is not
        /// mistaken for a platform_id.
        /// </summary>
        public string JobGuid { get; set; }
    }

    /// <summary>Line on a line-item transaction: invoice, bill, or expense.</summary>
    public abstract class ItemLineBodyBase : TransactionLineBodyBase
    {
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }

        /// <summary>
        /// Inventory item as a raw GUID, for the same reason as JobGuid — Sage 50
        /// has no ITEMS entity in Rutter yet, so there is nothing to link to.
        /// </summary>
        public string InventoryItemGuid { get; set; }
    }

    /// <summary>
    /// Common transaction header fields. Sage's Transaction base carries the
    /// date, total, reference number and posting state; the concrete types add
    /// the party and the lines.
    /// </summary>
    public abstract class TransactionBodyBase
    {
        /// <summary>
        /// The transaction's Sage key as a GUID string, and the value Rutter
        /// reads from $.id for platform_id.
        ///
        /// Sage transactions have no ID property the way accounts and vendors do
        /// — the human-facing number is ReferenceNumber, which is not guaranteed
        /// unique (two journal entries can share one). The key is.
        /// </summary>
        public string ID { get; set; }

        /// <summary>The number a person sees in Sage: invoice number, check number.</summary>
        public string ReferenceNumber { get; set; }

        /// <summary>Transaction date, yyyy-MM-dd. Sage has no timezone here, so sending a date avoids shifting it.</summary>
        public string Date { get; set; }

        /// <summary>Sum of the line amounts, per Sage.</summary>
        public decimal Amount { get; set; }

        public bool IsPosted { get; set; }

        /// <summary>The transaction's own GL account — for a payment, the cash account.</summary>
        public string AccountID { get; set; }

        /// <summary>ISO 8601, or null when Sage never recorded one. See ChangedSince.</summary>
        public string LastSavedAt { get; set; }
    }

    public class JournalEntryLineBody : TransactionLineBodyBase
    {
    }

    public class JournalEntryBody : TransactionBodyBase
    {
        public bool IsReversingTransaction { get; set; }

        /// <summary>Sage's link to the paired transaction, raw GUID.</summary>
        public string PartnerGuid { get; set; }

        public List<JournalEntryLineBody> Lines { get; set; } = new List<JournalEntryLineBody>();
    }

    public class InvoiceLineBody : ItemLineBodyBase
    {
    }

    /// <summary>A Sage SalesInvoice — accounts receivable.</summary>
    public class InvoiceBody : TransactionBodyBase
    {
        /// <summary>Customer's Sage ID, matching the CUSTOMERS platform_id.</summary>
        public string CustomerID { get; set; }

        public decimal AmountDue { get; set; }
        public string DateDue { get; set; }
        public decimal DiscountAmount { get; set; }
        public string DiscountDate { get; set; }
        public decimal FreightAmount { get; set; }
        public decimal SalesTaxAmount { get; set; }
        public string CustomerPurchaseOrderNumber { get; set; }
        public string TermsDescription { get; set; }
        public string ShipDate { get; set; }
        public string ShipVia { get; set; }
        public bool DropShip { get; set; }
        public string CustomerNote { get; set; }
        public string InternalNote { get; set; }
        public string StatementNote { get; set; }
        public string FreightAccountID { get; set; }
        public string SalesRepresentativeGuid { get; set; }
        public string SalesTaxCodeGuid { get; set; }
        public SageAddressBody ShipToAddress { get; set; }

        public List<InvoiceLineBody> Lines { get; set; } = new List<InvoiceLineBody>();
    }

    public class BillLineBody : ItemLineBodyBase
    {
    }

    /// <summary>A Sage PurchaseInvoice — accounts payable.</summary>
    public class BillBody : TransactionBodyBase
    {
        /// <summary>Vendor's Sage ID, matching the VENDORS platform_id.</summary>
        public string VendorID { get; set; }

        public decimal AmountDue { get; set; }
        public string DateDue { get; set; }
        public decimal DiscountAmount { get; set; }
        public string DiscountDate { get; set; }
        public string CustomerSalesOrderNumber { get; set; }
        public string TermsDescription { get; set; }
        public string ShipVia { get; set; }
        public bool DropShip { get; set; }
        public string VendorNote { get; set; }
        public string InternalNote { get; set; }

        /// <summary>
        /// Sage types this as an int, not a bool — verified by reflection against
        /// Sage.Peachtree.API 2026.1. Reported as Sage stores it rather than
        /// coerced to a boolean, since a non-zero value may carry more meaning
        /// than "true" and guessing would discard it.
        /// </summary>
        public int WaitingForBill { get; set; }
        public SageAddressBody ShipToAddress { get; set; }

        public List<BillLineBody> Lines { get; set; } = new List<BillLineBody>();
    }

    public class ExpenseLineBody : ItemLineBodyBase
    {
    }

    /// <summary>
    /// A line where a payment was applied against an existing bill, rather than
    /// expensed straight to a GL account. Kept separate because the two mean
    /// different things: these settle AP, expense lines create it.
    /// </summary>
    public class PaymentAppliedInvoiceLineBody : TransactionLineBodyBase
    {
        public decimal AmountPaid { get; set; }
        public decimal DiscountAmount { get; set; }
        public string DiscountAccountID { get; set; }

        /// <summary>
        /// The bill being paid, as a GUID — which is exactly the `id` the BILLS
        /// read reports, so this links without any extra resolution.
        /// </summary>
        public string InvoiceGuid { get; set; }
    }

    /// <summary>
    /// A Sage Payment. Sage models "write a check" and "pay a bill" as one type:
    /// ExpenseLines hit GL accounts directly, InvoiceLines settle existing bills.
    /// Both are reported so a mapper can tell them apart rather than guess.
    /// </summary>
    public class ExpenseBody : TransactionBodyBase
    {
        /// <summary>Vendor's Sage ID, matching the VENDORS platform_id.</summary>
        public string VendorID { get; set; }

        public string Memo { get; set; }
        public string PaymentMethod { get; set; }
        public string DateSent { get; set; }
        public bool IsElectronicPayment { get; set; }
        public string ElectronicIdentifier { get; set; }
        public string DiscountAccountID { get; set; }
        public SageAddressBody MainAddress { get; set; }

        public List<ExpenseLineBody> ExpenseLines { get; set; } = new List<ExpenseLineBody>();
        public List<PaymentAppliedInvoiceLineBody> InvoiceLines { get; set; } =
            new List<PaymentAppliedInvoiceLineBody>();
    }

    /// <summary>
    /// A line where a receipt was applied against an existing sales invoice.
    /// <see cref="InvoiceGuid"/> is the same GUID the INVOICES read reports as
    /// <c>id</c>, so a mapper links without extra resolution.
    /// </summary>
    public class ReceiptAppliedInvoiceLineBody : TransactionLineBodyBase
    {
        public decimal AmountPaid { get; set; }
        public decimal DiscountAmount { get; set; }
        public string DiscountAccountID { get; set; }
        public string InvoiceGuid { get; set; }
    }

    /// <summary>
    /// A cash-sale line on a receipt (not applied to an existing invoice).
    /// Reported so the payload is complete; the INVOICE_PAYMENTS mapper keeps
    /// only receipts that also have applied-invoice lines.
    /// </summary>
    public class ReceiptSalesLineBody : ItemLineBodyBase
    {
    }

    /// <summary>
    /// A Sage Receipt — money received from a customer. Mirrors Payment on the
    /// AR side: invoice lines settle existing sales invoices, sales lines are
    /// cash sales entered on the receipt itself.
    /// </summary>
    public class InvoicePaymentBody : TransactionBodyBase
    {
        /// <summary>Customer's Sage ID, matching the CUSTOMERS platform_id.</summary>
        public string CustomerID { get; set; }

        public string ReceiptNumber { get; set; }
        public string PaymentMethod { get; set; }
        public string DepositTicketID { get; set; }
        public decimal SalesTaxAmount { get; set; }
        public string DiscountAccountID { get; set; }
        public string SalesRepresentativeGuid { get; set; }
        public string SalesTaxCodeGuid { get; set; }
        public SageAddressBody MainAddress { get; set; }

        public List<ReceiptAppliedInvoiceLineBody> InvoiceLines { get; set; } =
            new List<ReceiptAppliedInvoiceLineBody>();
        public List<ReceiptSalesLineBody> SalesLines { get; set; } =
            new List<ReceiptSalesLineBody>();
    }
}
