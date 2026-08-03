using Sage.Peachtree.API;
using Sage.Peachtree.API.Collections.Generic;
using Sage50Connector.Models.Rutter;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Sage50Connector.Helpers
{
    public class Sage50Repository
    {
        private static Sage50Repository m_sage50repository;
        CompanyManager m_compManager;
        private Sage50Repository()
        {
            m_compManager = CompanyManager.Instance;
        }
        public static Sage50Repository Instance
        {
            get
            {
                if (m_sage50repository == null)
                    m_sage50repository = new Sage50Repository();

                return m_sage50repository;
            }
        }
        public bool CurrentCompanyDesconnected
        {
            get
            {
                return Sage50Connector.Instance.CurrentCompany == null;
            }
        }
        public string CurrentCompanyName
        {
            get
            {
                return Sage50Connector.Instance.CurrentCompany.CompanyIdentifier.CompanyName;
            }
        }
        public List<string> Companies
        {
            get
            {
                return m_compManager.CompaniesName;
            }
        }
        public string OpenCompany(string compName)
        {
            int index = -1;
            index = this.Companies.IndexOf(compName);

            if (index > -1)
            {
                return m_compManager.OpenSelectedCompany(index);
            }
            else
            {
                
                return "Error: There are no companies with that name";
            }
        }
        public void CloseCompany()
        {
            if (m_compManager != null)
            {
                m_compManager.CloseCompany();
            }
        }

        public List<ChartofAccount> GetAccounts(string companyName)
        {
            EnsureCompanyConnected(companyName);
            if (!CurrentCompanyDesconnected)
            {
                AccountList acctList = CompanyManager.Instance.CurrentCompany.Factories.AccountFactory.List();
                acctList.Load();
                List<ChartofAccount> accounts = acctList.Select(acct => new ChartofAccount
                {
                    ID = acct.ID,
                    Description = acct.Description,
                    Classification = acct.Classification.ToString(),
                    IsInactive = acct.IsInactive,
                    Key = acct.Key, // Assuming 'Guid' is the property name in the Account class
                }).ToList();
                return accounts;
            }
            return new List<ChartofAccount>();
        }

        /// <summary>
        /// The company itself — Sage's one company-level record.
        ///
        /// There is no factory for this and no list to page: the fields hang off
        /// the open Company object, so "fetching" it is opening the company. It is
        /// returned as a one-element list so the LIST_FETCH path pages it like
        /// anything else and finishes in a single page.
        ///
        /// Sage records no timestamp against the company, so there is nothing to
        /// filter on and updatedAt is deliberately not a parameter — an
        /// incremental sync re-sends this record and Rutter dedupes it on $.id.
        /// It is one row.
        /// </summary>
        public CompanyInfo GetCompanyInfo(string companyName)
        {
            EnsureCompanyConnected(companyName);
            if (CurrentCompanyDesconnected)
            {
                return null;
            }

            Company company = CompanyManager.Instance.CurrentCompany;
            CompanyIdentifier identifier = company.CompanyIdentifier;
            NameAndAddress companyAddress = company.Address;
            Address address = companyAddress == null ? null : companyAddress.Address;

            var info = new CompanyInfo
            {
                ID = identifier.Guid.ToString(),
                Name = identifier.CompanyName ?? "",
                LegalName = companyAddress == null ? "" : (companyAddress.Name ?? ""),
                AccountingMethod = company.AccountingMethod.ToString(),
                Address1 = address == null ? "" : (address.Address1 ?? ""),
                Address2 = address == null ? "" : (address.Address2 ?? ""),
                City = address == null ? "" : (address.City ?? ""),
                State = address == null ? "" : (address.State ?? ""),
                Zip = address == null ? "" : (address.Zip ?? ""),
                Country = address == null ? "" : (address.Country ?? ""),
                DatabaseName = identifier.DatabaseName ?? "",
                ServerName = identifier.ServerName ?? "",
                SchemaVersion = identifier.SchemaVersion ?? "",
            };

            // A company can have none of these configured, and one that will not
            // tell us its periods is still a company worth reporting.
            //
            // The collection is sparse, not an ordered dense range: Sage divides a
            // fiscal year into "as many as 13 periods" and the SDK notes that the
            // internal set "include[s] a thirteenth period, which many companies
            // may not use". Unused slots carry DateTime.MinValue, so indexing [0]
            // and [Count-1] reported 0001-01-01 for Bellwether — a company with
            // perfectly ordinary periods. Take the extremes of the populated
            // entries instead.
            try
            {
                var periods = company.Defaults.GeneralLedger.AccountingPeriods;
                var configured = periods == null
                    ? new List<AccountingPeriod>()
                    : periods.Where(p => p.From != DateTime.MinValue && p.To != DateTime.MinValue).ToList();

                if (configured.Count > 0)
                {
                    info.FiscalYearStart = configured.Min(p => p.From).ToString("yyyy-MM-dd");
                    info.FiscalYearEnd = configured.Max(p => p.To).ToString("yyyy-MM-dd");
                }
                else if (periods != null && periods.Count > 0)
                {
                    global::Sage50Connector.Program.WriteToFile(
                        "COMPANY_INFO: " + periods.Count + " accounting period slot(s), none populated.");
                }
            }
            catch (Exception ex)
            {
                global::Sage50Connector.Program.WriteToFile(
                    "COMPANY_INFO: could not read accounting periods: " + ex.Message);
            }

            global::Sage50Connector.Program.WriteToFile(
                "COMPANY_INFO: company '" + info.Name + "' (" + info.ID + "), "
                    + info.AccountingMethod + ", periods "
                    + (info.FiscalYearStart ?? "unknown") + " to " + (info.FiscalYearEnd ?? "unknown"));

            return info;
        }

        /// <summary>
        /// Whether Sage actually recorded when this record last changed. Records
        /// that have not been touched since the company was created come back with
        /// LastSavedAt null (or DateTime default).
        /// </summary>
        private static bool HasTimestamp(DateTime? lastSavedAt)
        {
            return lastSavedAt != null && lastSavedAt != default(DateTime);
        }

        /// <summary>
        /// Incremental-fetch predicate. A null nullable compares false against any
        /// bound in C#, so filtering on LastSavedAt alone drops every record Sage
        /// never timestamped - permanently, since no later cutoff brings them back.
        /// When Sage cannot say when a record changed, include it and let Rutter
        /// dedupe on the primary key.
        /// </summary>
        private static bool ChangedSince(DateTime? lastSavedAt, DateTime? cutoff)
        {
            if (cutoff == null) return true;
            if (!HasTimestamp(lastSavedAt)) return true;
            return lastSavedAt >= cutoff;
        }

        private static void LogFilterOutcome(string entity, int total, int withoutTimestamp, int returned, DateTime? cutoff)
        {
            global::Sage50Connector.Program.WriteToFile(
                string.Format(
                    "{0}: Sage returned {1}; {2} had no LastSavedAt; {3} passed the updated_at cutoff ({4}).",
                    entity,
                    total,
                    withoutTimestamp,
                    returned,
                    cutoff.HasValue ? cutoff.Value.ToString("o") : "none"
                )
            );
        }

        #region transaction reads

        /// <summary>Parses the job's updated_at cutoff, or null for "everything".</summary>
        private static DateTime? ParseCutoff(string updatedAt)
        {
            if (string.IsNullOrEmpty(updatedAt))
            {
                return null;
            }
            return DateTime.Parse(updatedAt);
        }

        /// <summary>
        /// A date with no time, which is what Sage stores for transaction dates.
        /// Sending a date rather than a timestamp keeps a timezone conversion
        /// somewhere downstream from moving a transaction to the previous day.
        /// </summary>
        private static string DateOnly(DateTime? value)
        {
            if (value == null || value == default(DateTime))
            {
                return null;
            }
            return value.Value.ToString("yyyy-MM-dd");
        }

        /// <summary>ISO 8601, or null when Sage never set the value.</summary>
        private static string Timestamp(DateTime? value)
        {
            if (!HasTimestamp(value))
            {
                return null;
            }
            return value.Value.ToString("o");
        }

        /// <summary>
        /// Reads the lists whose IDs transaction references point at, and indexes
        /// them by key. Only what a given entity needs is loaded — a journal entry
        /// never names a customer.
        /// </summary>
        private ReferenceIndex BuildReferenceIndex(bool accounts, bool customers, bool vendors)
        {
            var index = new ReferenceIndex();
            var factories = CompanyManager.Instance.CurrentCompany.Factories;

            if (accounts)
            {
                var list = factories.AccountFactory.List();
                list.Load();
                foreach (Account account in list)
                {
                    index.Add(account.Key, account.ID);
                }
            }

            if (customers)
            {
                var list = factories.CustomerFactory.List();
                list.Load();
                foreach (Customer customer in list)
                {
                    index.Add(customer.Key, customer.ID);
                }
            }

            if (vendors)
            {
                var list = factories.VendorFactory.List();
                list.Load();
                foreach (Vendor vendor in list)
                {
                    index.Add(vendor.Key, vendor.ID);
                }
            }

            return index;
        }

        /// <summary>
        /// Sage pads a transaction's line collection with unused slots, the same
        /// way it pads accounting periods. An unused line is not a zero-amount
        /// line, it is not a line at all, so it must not be reported.
        /// </summary>
        private static bool IsRealLine(TransactionLine line)
        {
            return line != null && line.IsUsed;
        }

        private static SageAddressBody MapAddress(NameAndAddress source)
        {
            if (source == null)
            {
                return null;
            }

            Address address = source.Address;
            return new SageAddressBody
            {
                Name = source.Name,
                Address1 = address == null ? null : address.Address1,
                Address2 = address == null ? null : address.Address2,
                City = address == null ? null : address.City,
                State = address == null ? null : address.State,
                Zip = address == null ? null : address.Zip,
                Country = address == null ? null : address.Country,
            };
        }

        /// <summary>
        /// Fills the fields every transaction shares. The concrete reads add the
        /// party and the lines.
        /// </summary>
        private static void MapTransactionHeader(TransactionBodyBase body, Transaction source, ReferenceIndex index)
        {
            body.ID = ReferenceIndex.GuidOf(source.Key);
            body.ReferenceNumber = source.ReferenceNumber;
            body.Date = DateOnly(source.Date);
            body.Amount = source.Amount;
            body.IsPosted = source.IsPosted;
            body.AccountID = index.Resolve(source.AccountReference);
            body.LastSavedAt = Timestamp(source.LastSavedAt);
        }

        private static void MapLineBase(TransactionLineBodyBase body, TransactionLine source, ReferenceIndex index)
        {
            body.ID = ReferenceIndex.GuidOf(source.Key);
            body.AccountID = index.Resolve(source.AccountReference);
            body.Amount = source.Amount;
            body.Description = source.Description;
        }

        /// <summary>
        /// General journal entries. The only entity here whose lines are the
        /// whole point — the header carries no party at all.
        /// </summary>
        public List<JournalEntryBody> GetJournalEntries(string companyName, string updatedAt = null)
        {
            var results = new List<JournalEntryBody>();
            EnsureCompanyConnected(companyName);
            if (CurrentCompanyDesconnected)
            {
                return results;
            }

            DateTime? cutoff = ParseCutoff(updatedAt);
            ReferenceIndex index = BuildReferenceIndex(accounts: true, customers: false, vendors: false);

            var entries = CompanyManager.Instance.CurrentCompany.Factories.GeneralJournalEntryFactory.List();
            entries.Load();

            int total = 0;
            int withoutTimestamp = 0;
            foreach (GeneralJournalEntry entry in entries)
            {
                total++;
                if (!HasTimestamp(entry.LastSavedAt))
                {
                    withoutTimestamp++;
                }
                if (!ChangedSince(entry.LastSavedAt, cutoff))
                {
                    continue;
                }

                var body = new JournalEntryBody
                {
                    IsReversingTransaction = entry.IsReversingTransaction,
                    PartnerGuid = ReferenceIndex.GuidOf(entry.PartnerReference),
                };
                MapTransactionHeader(body, entry, index);

                if (entry.GeneralJournalEntryLines != null)
                {
                    foreach (GeneralJournalEntryLine line in entry.GeneralJournalEntryLines)
                    {
                        if (!IsRealLine(line))
                        {
                            continue;
                        }
                        var lineBody = new JournalEntryLineBody
                        {
                            JobGuid = ReferenceIndex.GuidOf(line.JobReference),
                        };
                        MapLineBase(lineBody, line, index);
                        body.Lines.Add(lineBody);
                    }
                }

                results.Add(body);
            }

            LogFilterOutcome("JOURNAL_ENTRIES", total, withoutTimestamp, results.Count, cutoff);
            return results;
        }

        /// <summary>Sales invoices — accounts receivable.</summary>
        public List<InvoiceBody> GetInvoices(string companyName, string updatedAt = null)
        {
            var results = new List<InvoiceBody>();
            EnsureCompanyConnected(companyName);
            if (CurrentCompanyDesconnected)
            {
                return results;
            }

            DateTime? cutoff = ParseCutoff(updatedAt);
            ReferenceIndex index = BuildReferenceIndex(accounts: true, customers: true, vendors: false);

            var invoices = CompanyManager.Instance.CurrentCompany.Factories.SalesInvoiceFactory.List();
            invoices.Load();

            int total = 0;
            int withoutTimestamp = 0;
            foreach (SalesInvoice invoice in invoices)
            {
                total++;
                if (!HasTimestamp(invoice.LastSavedAt))
                {
                    withoutTimestamp++;
                }
                if (!ChangedSince(invoice.LastSavedAt, cutoff))
                {
                    continue;
                }

                var body = new InvoiceBody
                {
                    CustomerID = index.Resolve(invoice.CustomerReference),
                    AmountDue = invoice.AmountDue,
                    DateDue = DateOnly(invoice.DateDue),
                    DiscountAmount = invoice.DiscountAmount,
                    DiscountDate = DateOnly(invoice.DiscountDate),
                    FreightAmount = invoice.FreightAmount,
                    SalesTaxAmount = invoice.SalesTaxAmount,
                    CustomerPurchaseOrderNumber = invoice.CustomerPurchaseOrderNumber,
                    TermsDescription = invoice.TermsDescription,
                    ShipDate = DateOnly(invoice.ShipDate),
                    ShipVia = invoice.ShipVia,
                    DropShip = invoice.DropShip,
                    CustomerNote = invoice.CustomerNote,
                    InternalNote = invoice.InternalNote,
                    StatementNote = invoice.StatementNote,
                    FreightAccountID = index.Resolve(invoice.FreightAccountReference),
                    SalesRepresentativeGuid = ReferenceIndex.GuidOf(invoice.SalesRepresentativeReference),
                    SalesTaxCodeGuid = ReferenceIndex.GuidOf(invoice.SalesTaxCodeReference),
                    ShipToAddress = MapAddress(invoice.ShipToAddress),
                };
                MapTransactionHeader(body, invoice, index);

                if (invoice.ApplyToSalesLines != null)
                {
                    foreach (SalesInvoiceSalesLine line in invoice.ApplyToSalesLines)
                    {
                        if (!IsRealLine(line))
                        {
                            continue;
                        }
                        var lineBody = new InvoiceLineBody
                        {
                            Quantity = line.Quantity,
                            UnitPrice = line.UnitPrice,
                            InventoryItemGuid = ReferenceIndex.GuidOf(line.InventoryItemReference),
                            JobGuid = ReferenceIndex.GuidOf(line.JobReference),
                        };
                        MapLineBase(lineBody, line, index);
                        body.Lines.Add(lineBody);
                    }
                }

                results.Add(body);
            }

            LogFilterOutcome("INVOICES", total, withoutTimestamp, results.Count, cutoff);
            return results;
        }

        /// <summary>Purchase invoices — accounts payable.</summary>
        public List<BillBody> GetBills(string companyName, string updatedAt = null)
        {
            var results = new List<BillBody>();
            EnsureCompanyConnected(companyName);
            if (CurrentCompanyDesconnected)
            {
                return results;
            }

            DateTime? cutoff = ParseCutoff(updatedAt);
            ReferenceIndex index = BuildReferenceIndex(accounts: true, customers: false, vendors: true);

            var bills = CompanyManager.Instance.CurrentCompany.Factories.PurchaseInvoiceFactory.List();
            bills.Load();

            int total = 0;
            int withoutTimestamp = 0;
            foreach (PurchaseInvoice bill in bills)
            {
                total++;
                if (!HasTimestamp(bill.LastSavedAt))
                {
                    withoutTimestamp++;
                }
                if (!ChangedSince(bill.LastSavedAt, cutoff))
                {
                    continue;
                }

                var body = new BillBody
                {
                    VendorID = index.Resolve(bill.VendorReference),
                    AmountDue = bill.AmountDue,
                    DateDue = DateOnly(bill.DateDue),
                    DiscountAmount = bill.DiscountAmount,
                    DiscountDate = DateOnly(bill.DiscountDate),
                    CustomerSalesOrderNumber = bill.CustomerSalesOrderNumber,
                    TermsDescription = bill.TermsDescription,
                    ShipVia = bill.ShipVia,
                    DropShip = bill.DropShip,
                    VendorNote = bill.VendorNote,
                    InternalNote = bill.InternalNote,
                    WaitingForBill = bill.WaitingForBill,
                    ShipToAddress = MapAddress(bill.ShipToAddress),
                };
                MapTransactionHeader(body, bill, index);

                if (bill.ApplyToPurchasesLines != null)
                {
                    foreach (PurchaseInvoicePurchasesLine line in bill.ApplyToPurchasesLines)
                    {
                        if (!IsRealLine(line))
                        {
                            continue;
                        }
                        var lineBody = new BillLineBody
                        {
                            Quantity = line.Quantity,
                            UnitPrice = line.UnitPrice,
                            InventoryItemGuid = ReferenceIndex.GuidOf(line.InventoryItemReference),
                            JobGuid = ReferenceIndex.GuidOf(line.JobReference),
                        };
                        MapLineBase(lineBody, line, index);
                        body.Lines.Add(lineBody);
                    }
                }

                results.Add(body);
            }

            LogFilterOutcome("BILLS", total, withoutTimestamp, results.Count, cutoff);
            return results;
        }

        /// <summary>
        /// Payments. Sage models "write a check" and "pay a bill" as one type, so
        /// both line collections are reported: expense lines hit GL accounts
        /// directly, invoice lines settle bills that already exist.
        /// </summary>
        public List<ExpenseBody> GetExpenses(string companyName, string updatedAt = null)
        {
            var results = new List<ExpenseBody>();
            EnsureCompanyConnected(companyName);
            if (CurrentCompanyDesconnected)
            {
                return results;
            }

            DateTime? cutoff = ParseCutoff(updatedAt);
            ReferenceIndex index = BuildReferenceIndex(accounts: true, customers: false, vendors: true);

            var payments = CompanyManager.Instance.CurrentCompany.Factories.PaymentFactory.List();
            payments.Load();

            int total = 0;
            int withoutTimestamp = 0;
            int expenseLineCount = 0;
            int invoiceLineCount = 0;
            foreach (Payment payment in payments)
            {
                total++;
                if (!HasTimestamp(payment.LastSavedAt))
                {
                    withoutTimestamp++;
                }
                if (!ChangedSince(payment.LastSavedAt, cutoff))
                {
                    continue;
                }

                var body = new ExpenseBody
                {
                    VendorID = index.Resolve(payment.VendorReference),
                    Memo = payment.Memo,
                    PaymentMethod = payment.PaymentMethod,
                    DateSent = DateOnly(payment.DateSent),
                    IsElectronicPayment = payment.IsElectronicPayment,
                    ElectronicIdentifier = payment.ElectronicIdentifier,
                    DiscountAccountID = index.Resolve(payment.DiscountAccountReference),
                    MainAddress = MapAddress(payment.MainAddress),
                };
                MapTransactionHeader(body, payment, index);

                if (payment.ApplyToExpenseLines != null)
                {
                    foreach (PaymentExpenseLine line in payment.ApplyToExpenseLines)
                    {
                        if (!IsRealLine(line))
                        {
                            continue;
                        }
                        var lineBody = new ExpenseLineBody
                        {
                            Quantity = line.Quantity,
                            UnitPrice = line.UnitPrice,
                            InventoryItemGuid = ReferenceIndex.GuidOf(line.InventoryItemReference),
                            JobGuid = ReferenceIndex.GuidOf(line.JobReference),
                        };
                        MapLineBase(lineBody, line, index);
                        body.ExpenseLines.Add(lineBody);
                        expenseLineCount++;
                    }
                }

                if (payment.ApplyToInvoiceLines != null)
                {
                    foreach (PaymentInvoiceLine line in payment.ApplyToInvoiceLines)
                    {
                        if (!IsRealLine(line))
                        {
                            continue;
                        }
                        var lineBody = new PaymentAppliedInvoiceLineBody
                        {
                            AmountPaid = line.AmountPaid,
                            DiscountAmount = line.DiscountAmount,
                            DiscountAccountID = index.Resolve(line.DiscountAccountReference),
                            InvoiceGuid = ReferenceIndex.GuidOf(line.InvoiceReference),
                            JobGuid = ReferenceIndex.GuidOf(line.JobReference),
                        };
                        MapLineBase(lineBody, line, index);
                        body.InvoiceLines.Add(lineBody);
                        invoiceLineCount++;
                    }
                }

                results.Add(body);
            }

            LogFilterOutcome("EXPENSES", total, withoutTimestamp, results.Count, cutoff);
            global::Sage50Connector.Program.WriteToFile(
                "EXPENSES: " + expenseLineCount + " expense line(s) and "
                    + invoiceLineCount + " applied-to-bill line(s) across "
                    + results.Count + " payment(s).");
            return results;
        }

        #endregion

        public List<ChartofVendor> GetVendors(string companyName, string updatedAt = null)
        {
            EnsureCompanyConnected(companyName);
            if (!CurrentCompanyDesconnected)
            {
                VendorList vendorList = CompanyManager.Instance.CurrentCompany.Factories.VendorFactory.List();
                vendorList.Load();

                DateTime? updatedAtDate = null;
                if (!string.IsNullOrEmpty(updatedAt))
                {
                    updatedAtDate = DateTime.Parse(updatedAt);
                }

                int totalFromSage = 0;
                int withoutTimestamp = 0;
                List<ChartofVendor> chartofVendors = new List<ChartofVendor>();
                foreach (var vendor in vendorList)
                {
                    totalFromSage++;
                    if (!HasTimestamp(vendor.LastSavedAt))
                    {
                        withoutTimestamp++;
                    }

                    if (ChangedSince(vendor.LastSavedAt, updatedAtDate))
                    {
                        chartofVendors.Add(new ChartofVendor
                        {
                            AccountNumber = vendor.AccountNumber,
                            ID = vendor.ID,
                            Name = vendor.Name,
                            Email = vendor.Email,
                            TaxIDNumber = vendor.TaxIDNumber,
                            WebSiteURL = vendor.WebSiteURL,
                            // Map other fields as necessary
                        });
                    }
                }

                LogFilterOutcome("VENDORS", totalFromSage, withoutTimestamp, chartofVendors.Count, updatedAtDate);
                return chartofVendors;
            }
            return new List<ChartofVendor>();
        }

        public List<ChartofCustomer> GetCustomers(string companyName, string updatedAt = null)
        {
            EnsureCompanyConnected(companyName);
            if (!CurrentCompanyDesconnected)
            {
                CustomerList customerList = CompanyManager.Instance.CurrentCompany.Factories.CustomerFactory.List();
                customerList.Load();

                DateTime? updatedAtDate = null;
                if (!string.IsNullOrEmpty(updatedAt))
                {
                    updatedAtDate = DateTime.Parse(updatedAt);
                }

                int totalFromSage = 0;
                int withoutTimestamp = 0;
                List<ChartofCustomer> chartofCustomers = new List<ChartofCustomer>();
                foreach (var customer in customerList)
                {
                    totalFromSage++;
                    if (!HasTimestamp(customer.LastSavedAt))
                    {
                        withoutTimestamp++;
                    }

                    if (ChangedSince(customer.LastSavedAt, updatedAtDate))
                    {
                        chartofCustomers.Add(new ChartofCustomer
                        {
                            ID = customer.ID,
                            Name = customer.Name,
                            Email = customer.Email,
                            AccountNumber = customer.AccountNumber,
                            WebSiteURL = customer.WebSiteURL,

                            // Map other fields as necessary
                        });
                    }
                }

                LogFilterOutcome("CUSTOMERS", totalFromSage, withoutTimestamp, chartofCustomers.Count, updatedAtDate);
                return chartofCustomers;
            }
            return new List<ChartofCustomer>();
        }
        public Vendor GetVendor(string companyName, string id)
        {
            return GetEntityFromPath<Vendor>(companyName, "CompanyManager.Instance.CurrentCompany.Factories.VendorFactory.List()", id);
        }

        public bool UpdateVendor(string companyName, Vendor vendor)
        {
            return false;
        }

        /// <summary>
        /// Applies the supplied fields to an existing vendor and returns it as
        /// Sage holds it afterwards.
        ///
        /// Only fields actually present on the request are written — a null means
        /// "not supplied", not "clear this". Sage's own ID is immutable, so it is
        /// the lookup key rather than something we can set.
        /// </summary>
        public VendorBody UpdateVendor(string companyName, string vendorId, VendorBody changes)
        {
            EnsureCompanyConnected(companyName);
            if (CurrentCompanyDesconnected)
            {
                return null;
            }

            var vendorList = CompanyManager.Instance.CurrentCompany.Factories.VendorFactory.List();
            vendorList.Load();
            var vendor = vendorList.FirstOrDefault(v => v.ID == vendorId);
            if (vendor == null)
            {
                throw new InvalidOperationException("No vendor with ID '" + vendorId + "' in company '" + companyName + "'.");
            }

            if (changes.Name != null) vendor.Name = changes.Name;
            if (changes.Email != null) vendor.Email = changes.Email;
            if (changes.TaxIDNumber != null) vendor.TaxIDNumber = changes.TaxIDNumber;
            if (changes.WebSiteURL != null) vendor.WebSiteURL = changes.WebSiteURL;
            if (changes.AccountNumber != null) vendor.AccountNumber = changes.AccountNumber;

            vendor.Save();

            return new VendorBody
            {
                AccountNumber = vendor.AccountNumber,
                ID = vendor.ID,
                Name = vendor.Name,
                Email = vendor.Email,
                TaxIDNumber = vendor.TaxIDNumber,
                WebSiteURL = vendor.WebSiteURL,
            };
        }

        /// <summary>
        /// Deletes a vendor.
        ///
        /// Sage refuses to delete a vendor that has activity against it, which
        /// surfaces as an exception from Delete() — that is a legitimate outcome
        /// to report back, not something to swallow.
        /// </summary>
        public bool DeleteVendor(string companyName, string vendorId)
        {
            EnsureCompanyConnected(companyName);
            if (CurrentCompanyDesconnected)
            {
                return false;
            }

            var vendorList = CompanyManager.Instance.CurrentCompany.Factories.VendorFactory.List();
            vendorList.Load();
            var vendor = vendorList.FirstOrDefault(v => v.ID == vendorId);
            if (vendor == null)
            {
                // Already gone. Rutter's copy should still be removed, so this is
                // success rather than an error.
                return false;
            }

            vendor.Delete();
            return true;
        }

        public ChartofAccount CreateAccount(string companyName, ChartofAccount account)
        {
            EnsureCompanyConnected(companyName);
            if (!CurrentCompanyDesconnected)
            {
                var accountFactory = CompanyManager.Instance.CurrentCompany.Factories.AccountFactory;
                var newAccount = accountFactory.Create();
                newAccount.ID = account.ID;
                newAccount.Description = account.Description;
                newAccount.Classification = (AccountClassification)Enum.Parse(typeof(AccountClassification), account.Classification);
                newAccount.IsInactive = account.IsInactive;
                newAccount.Save();
                return account;
            }
            return null;
        }
        public VendorBody CreateVendor(string companyName, VendorBody vendorBody)
        {
            EnsureCompanyConnected(companyName);
            if (!CurrentCompanyDesconnected)
            {
                var vendorFactory = CompanyManager.Instance.CurrentCompany.Factories.VendorFactory;
                var newVendor = vendorFactory.Create();
                newVendor.ID = vendorBody.ID;
                newVendor.Name = vendorBody.Name;
                newVendor.Email = vendorBody.Email;

                //newVendor.ExpenseAccountReference = vendorBody.ExpenseAccountReference; 
                try
                {
                    newVendor.Save(); // Save the vendor to Sage 50

                    // Creating a ChartofVendor instance to return
                    var createdVendor = new VendorBody
                    {
                        AccountNumber = newVendor.AccountNumber,
                        ID = newVendor.ID,
                        Name = newVendor.Name,
                        Email = newVendor.Email,
                        TaxIDNumber = newVendor.TaxIDNumber,
                        WebSiteURL = newVendor.WebSiteURL
                    };

                    return createdVendor;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(DateTime.Now + ": Error saving Vendor in Sage 50: " + ex.Message);
                    throw; // Optionally rethrow the exception to handle it further up the call stack
                };
            }
            return null;
        }

        public VendorBody GetVendorById(string companyName, string vendorId)
        {
            EnsureCompanyConnected(companyName);
            if (!CurrentCompanyDesconnected)
            {
                var vendorFactory = CompanyManager.Instance.CurrentCompany.Factories.VendorFactory;
                var vendors = vendorFactory.List();
                // Load() is not optional: the list is lazy, so without it the
                // enumeration is empty and every lookup returns null. That made
                // ID_FETCH always answer "not found", and made CREATE fail to read
                // back the vendor it had just written — leaving the record in Sage
                // but the job unreported, so a retry then hit a duplicate key.
                vendors.Load();

                // Find the vendor with the matching ID
                var vendor = vendors.FirstOrDefault(v => v.ID == vendorId);
                if (vendor != null)
                {
                    return new VendorBody
                    {
                        AccountNumber = vendor.AccountNumber,
                        ID = vendor.ID,
                        Name = vendor.Name,
                        Email = vendor.Email,
                        TaxIDNumber = vendor.TaxIDNumber,
                        WebSiteURL = vendor.WebSiteURL,
                    };
                }
            }
            return null;
        }
        public T GetEntityFromPath<T>(string companyName, string path, string id = null)
        {
            EnsureCompanyConnected(companyName);
            if (!CurrentCompanyDesconnected)
            {
                var entityList = GetListFromPath(path);
                if(entityList != null)
                {
                    var filter = FilterExpression.Equal(FilterExpression.Property("ID"), FilterExpression.Constant(id));
                    var modifiers = LoadModifiers.Create();
                    modifiers.Filters = filter;
                    entityList.Load(modifiers);
                    foreach(var entity in entityList)
                    {
                        return (T)entity;
                    }

                }
            }
            return default;
        }
        public List<T> GetEntitiesFromPath<T>(string companyName, string path, string updatedAt = null)
        {
            EnsureCompanyConnected(companyName);
            if (!CurrentCompanyDesconnected)
            {
                var entityList = GetListFromPath(path);
                return entityList;
            }
            return new List<T>();
        }

        private dynamic GetListFromPath(string path)
        {
            var parts = path.Split('.');
            object currentObject = typeof(CompanyManager); // Start with the CompanyManager type for static access

            foreach (var part in parts)
            {
                if (part.Contains("()"))
                {
                    var methodName = part.TrimEnd('(', ')');
                    MethodInfo methodInfo = currentObject is Type ? ((Type)currentObject).GetMethod(methodName) : currentObject.GetType().GetMethod(methodName);

                    if (methodInfo == null)
                    {
                        throw new Exception($"Method {methodName} not found on {(currentObject is Type ? ((Type)currentObject).FullName : currentObject.GetType().FullName)}");
                    }

                    currentObject = methodInfo.Invoke(currentObject is Type ? null : currentObject, null);
                }
                else
                {
                    // It's a property or the initial class
                    if (currentObject == typeof(CompanyManager))
                    {
                        // If it's the first part, it's a static class
                        string currentNamespace = "Sage50Connector.Helpers";
                        string typeName = $"{currentNamespace}.{part}";

                        Type type = Type.GetType(typeName);

                        if (type == null)
                        {
                            throw new Exception($"Type {typeName} not found.");
                        }

                        currentObject = type.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                        if (currentObject == null)
                        {
                            throw new Exception($"Static instance property not found for type {typeName}.");
                        }
                    }
                    else
                    {
                        // Otherwise, it's a property
                        PropertyInfo property = currentObject.GetType().GetProperty(part);

                        if (property == null)
                        {
                            throw new Exception($"Property {part} not found on {currentObject.GetType().FullName}.");
                        }

                        currentObject = property.GetValue(currentObject);
                        if (currentObject == null)
                        {
                            throw new Exception($"Property {part} returned null on {currentObject.GetType().FullName}.");
                        }
                    }
                }
            }

            return currentObject;
        }
        public void EnsureCompanyConnected(string companyName)
        {
            if (CurrentCompanyDesconnected)
            {
                var errorMessage = OpenCompany(companyName);
                if (CurrentCompanyDesconnected)
                {
                    throw new InvalidOperationException($"Error: {errorMessage}. Company is disconnected.");
                }
            }
        }

    }
}
