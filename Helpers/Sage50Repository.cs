using Sage.Peachtree.API;
using Sage.Peachtree.API.Collections.Generic;
using Sage50Connector.Models.Rutter;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

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
                if (Sage50Connector.Instance.CurrentCompany == null)
                {
                    return true;
                }
                return false;
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

        public bool PostAccount(string companyName, ChartofAccount account) 
        {

            return false;
        }


        public ChartofAccount GetAccount(string companyName, string id)
        {
            ChartofAccount chartofAccount = new ChartofAccount();
            if (CurrentCompanyDesconnected)
            {
                OpenCompany(companyName);
            }
            if (CurrentCompanyDesconnected == false)
            {
                AccountList glList = CompanyManager.Instance.CurrentCompany.Factories.AccountFactory.List();
                FilterExpression filter = FilterExpression.Equal(
                    FilterExpression.Property("Account.ID"),
                    FilterExpression.Constant(id));

                LoadModifiers modifiers = LoadModifiers.Create();
                modifiers.Filters = filter;
                glList.Load(modifiers);

                foreach (Account account in glList)
                {
                    chartofAccount.ID = account.ID;
                    chartofAccount.Description = account.Description;
                    chartofAccount.IsInactive = account.IsInactive;
                    chartofAccount.Classification = account.Classification.ToString();
                   
                }
            }
            return chartofAccount;
        }

        public BalanceSheet GetbBalanceSheet(string companyName, int month, string assets_Accounts, string liability_Accounts, string equity_Accounts)
        {
            //where acct.Classification == AccountClassification.Cash
            string[] assetTypesArray = assets_Accounts.Split(',');
            string[] liabilityTypesArray = liability_Accounts.Split(',');
            string[] equityTypesArray = equity_Accounts.Split(',');
            //AccountList acctList = CompanyManager.Instance.CurrentCompany.Factories.AccountFactory.List();
            //acctList.Load();
            
              
            
            BalanceSheet balanceSheet = new BalanceSheet();
            if (CurrentCompanyDesconnected)
            {
                OpenCompany(companyName);
            }
            if (CurrentCompanyDesconnected == false)
            {
                DateTime lastDayOfMonth = new DateTime(DateTime.Now.Year, month, DateTime.DaysInMonth(DateTime.Now.Year, month));
                AccountList acctList = CompanyManager.Instance.CurrentCompany.Factories.AccountFactory.List();
                acctList.Load();
                List<Item> assetAccounts = (from acct in acctList
                                            where assetTypesArray.Contains(acct.Classification.ToString())
                                            select new Item
                                            {
                                                account_id = acct.ID,
                                                name = acct.Description,
                                                value = acct.GetEndingBalance(lastDayOfMonth),
                                            }).ToList();
                List<Item> liabilityAccounts = (from acct in acctList
                                                  where liabilityTypesArray.Contains(acct.Classification.ToString())
                                                  select new Item
                                                  {
                                                      account_id = acct.ID,
                                                      name = acct.Description,
                                                      value = acct.GetEndingBalance(lastDayOfMonth),
                                                  }).ToList();
                List<Item> equityAccounts = (from acct in acctList
                                                  where equityTypesArray.Contains(acct.Classification.ToString())
                                                  select new Item
                                                  {
                                                      account_id = acct.ID,
                                                      name = acct.Description,
                                                      value = acct.GetEndingBalance(lastDayOfMonth),
                                                  }).ToList();

                Liabilities liabilities = new Liabilities();
                liabilities.account_id = "1";
                liabilities.name = "Liabilities";
                liabilities.value = liabilityAccounts.Sum(item => item.value);
                liabilities.items = liabilityAccounts;
                Assets assets = new Assets();
                assets.account_id = "1";
                assets.name = "Assets";
                assets.value = assetAccounts.Sum(item => item.value);
                assets.items = assetAccounts;
                Equity equity = new Equity();
                equity.account_id = "1";
                equity.name = "Equity";
                equity.value = equityAccounts.Sum(item => item.value);
                equity.items = equityAccounts;
                balanceSheet.id = "1";
                balanceSheet.assets = assets;
                balanceSheet.equity = equity;
                balanceSheet.liabilities = liabilities;
                balanceSheet.total_assets = assets.value;
                balanceSheet.total_equity = equity.value;
                balanceSheet.total_liabilities = liabilities.value;
                balanceSheet.created_at = DateTime.Now;
                balanceSheet.updated_at = DateTime.Now;
                balanceSheet.start_date = lastDayOfMonth.ToString();
                balanceSheet.end_date = lastDayOfMonth.ToString();

            }
            return balanceSheet;
        }

        public List<ChartofAccount> GetAccounts(string companyName) 
        {
            List<ChartofAccount> chartofAccounts = new List<ChartofAccount>();
            if (CurrentCompanyDesconnected)
            {
                OpenCompany(companyName);
            }
            if (CurrentCompanyDesconnected == false)
            {                
                AccountList glList = CompanyManager.Instance.CurrentCompany.Factories.AccountFactory.List();
                glList.Load();
                foreach (Account account in glList) 
                {
                    ChartofAccount chartofAccount = new ChartofAccount();
                    chartofAccount.ID = account.ID;
                    chartofAccount.Description = account.Description;
                    chartofAccount.IsInactive = account.IsInactive;
                    chartofAccount.Classification = account.Classification.ToString();
                    chartofAccounts.Add(chartofAccount);
                }                
            }            
            return chartofAccounts;
        }
        public List<ChartofVendor> GetVendors(string companyName)
        {
            List<ChartofVendor> chartofVendors = new List<ChartofVendor>();
            if (CurrentCompanyDesconnected)
            {
                OpenCompany(companyName);
            }
            if (!CurrentCompanyDesconnected)
            {
                VendorList vendorList = CompanyManager.Instance.CurrentCompany.Factories.VendorFactory.List();
                vendorList.Load();
                foreach (Vendor vendor in vendorList)
                {
                    ChartofVendor chartofVendor = new ChartofVendor();
                    chartofVendor.AccountNumber = vendor.AccountNumber;
                    chartofVendor.Balance = vendor.Balance;
                    chartofVendor.Category = vendor.Category;
                    chartofVendor.Email = vendor.Email;
                    chartofVendor.ID = vendor.ID;
                    chartofVendor.IncludePurchaseRepresentativeOnEmailedForms = vendor.IncludePurchaseRepresentativeOnEmailedForms;
                    chartofVendor.IsInactive = vendor.IsInactive;
                    chartofVendor.LastInvoiceAmount = vendor.LastInvoiceAmount;
                    chartofVendor.LastInvoiceDate = vendor.LastInvoiceDate;
                    chartofVendor.LastPaymentAmount = vendor.LastPaymentAmount;
                    chartofVendor.LastPaymentDate = vendor.LastPaymentDate;
                    chartofVendor.Name = vendor.Name;
                    chartofVendor.PaymentMethod = vendor.PaymentMethod;
                    chartofVendor.ReplaceInventoryItemIDWithPartNumber = vendor.ReplaceInventoryItemIDWithPartNumber;
                    chartofVendor.ReplaceInventoryItemIDWithUPC = vendor.ReplaceInventoryItemIDWithUPC;
                    chartofVendor.ShipVia = vendor.ShipVia;
                    chartofVendor.TaxIDNumber = vendor.TaxIDNumber;
                    chartofVendor.Form1099Type = vendor.Form1099Type;
                    chartofVendor.UseEmailToDeliverForms = vendor.UseEmailToDeliverForms;
                    chartofVendor.UsingPaymentDefaults = vendor.UsingPaymentDefaults;
                    chartofVendor.VendorSince = vendor.VendorSince;
                    chartofVendor.WebSiteURL = vendor.WebSiteURL;
                    chartofVendor.CashAccountReference = vendor.CashAccountReference;
                    chartofVendor.Contacts = vendor.Contacts;
                    chartofVendor.CustomFieldValues = vendor.CustomFieldValues;
                    chartofVendor.ExpenseAccountReference = vendor.ExpenseAccountReference;
                    chartofVendor.MailToContact = vendor.MailToContact;
                    chartofVendor.PaymentsContact = vendor.PaymentsContact;
                    chartofVendor.Terms = vendor.Terms;
                    chartofVendor.PhoneNumbers = vendor.PhoneNumbers;
                    chartofVendor.PurchaseOrdersContact = vendor.PurchaseOrdersContact;
                    chartofVendor.PurchaseRepresentativeReference = vendor.PurchaseRepresentativeReference;
                    chartofVendor.ShipmentsContact = vendor.ShipmentsContact;
                    chartofVendor.LastSavedAt = vendor.LastSavedAt;

                    chartofVendors.Add(chartofVendor);
                }
            }
            return chartofVendors;
        }
        public void CreateVendor(string companyName)
        {
            List<ChartofVendor> chartofVendors = new List<ChartofVendor>();
            if (CurrentCompanyDesconnected)
            {
                OpenCompany(companyName);
            }
            if (!CurrentCompanyDesconnected)
            {
                Vendor v = CompanyManager.Instance.CurrentCompany.Factories.VendorFactory.Create();
                v.AccountNumber = "Test_01";
                v.ID = "Test01";
                //v.Save();
            }
            
        }
        public string VerifyCompanyAccess(int index)
        {
            return m_compManager.VerifySelectedCompanyAccess(index);
        }
    }
}
