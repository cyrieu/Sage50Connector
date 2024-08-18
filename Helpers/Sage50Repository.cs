using Sage.Peachtree.API;
using Sage.Peachtree.API.Collections.Generic;
using Sage50Connector.Models.Rutter;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

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
        public bool CurrentCompanyDisconnected
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

        public bool PostAccount(string companyName, ChartofAccount account) 
        {
            return false;
        }

        public ChartofAccount GetAccount(string companyName, string id)
        {
            return GetEntityFromPath<ChartofAccount>(companyName, "CompanyManager.Instance.CurrentCompany.Factories.AccountFactory.List()", id);
        }

        public BalanceSheet GetBalanceSheet(string companyName, int month, string assets_Accounts, string liability_Accounts, string equity_Accounts)
        {
            string[] assetTypesArray = assets_Accounts.Split(',');
            string[] liabilityTypesArray = liability_Accounts.Split(',');
            string[] equityTypesArray = equity_Accounts.Split(',');
            
            BalanceSheet balanceSheet = new BalanceSheet();
            EnsureCompanyConnected(companyName);
            
            if (!CurrentCompanyDisconnected)
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

                Liabilities liabilities = new Liabilities
                {
                    account_id = "1",
                    name = "Liabilities",
                    value = liabilityAccounts.Sum(item => item.value),
                    items = liabilityAccounts
                };
                Assets assets = new Assets
                {
                    account_id = "1",
                    name = "Assets",
                    value = assetAccounts.Sum(item => item.value),
                    items = assetAccounts
                };
                Equity equity = new Equity
                {
                    account_id = "1",
                    name = "Equity",
                    value = equityAccounts.Sum(item => item.value),
                    items = equityAccounts
                };
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

        public List<object> GetAccounts(string companyName, string updatedAt = null)
        {
            EnsureCompanyConnected(companyName);
            if (!CurrentCompanyDisconnected)
            {
                AccountList accountList = CompanyManager.Instance.CurrentCompany.Factories.AccountFactory.List();
                accountList.Load();

                // TODO: implement updatedAt filter

                List<object> accounts = new List<object>();
                foreach (var account in accountList)
                {
                    accounts.Add(account);
                }

                return accounts;
            }

            throw new Exception("Company is disconnected");
        }

        public List<object> GetVendors(string companyName, string updatedAt = null)
        {
            EnsureCompanyConnected(companyName);
            if (!CurrentCompanyDisconnected)
            {
                VendorList vendorList = CompanyManager.Instance.CurrentCompany.Factories.VendorFactory.List();
                vendorList.Load();

                // TODO: implement updatedAt filter

                List<object> vendors = new List<object>();
                foreach (var vendor in vendorList)
                {
                    vendors.Add(vendor);
                }

                return vendors;
            }

            throw new Exception("Company is disconnected");
        }

        public List<object> GetCustomers(string companyName, string updatedAt = null)
        {
            EnsureCompanyConnected(companyName);
            if (!CurrentCompanyDisconnected)
            {
                CustomerList customerList = CompanyManager.Instance.CurrentCompany.Factories.CustomerFactory.List();
                customerList.Load();

                // TODO: implement updatedAt filter

                List<object> customers = new List<object>();
                foreach (var customer in customerList)
                {
                    customers.Add(customer);
                }

                return customers;
            }

            throw new Exception("Company is disconnected");
        }

        public Vendor GetVendor(string companyName, string id)
        {
            return GetEntityFromPath<Vendor>(companyName, "CompanyManager.Instance.CurrentCompany.Factories.VendorFactory.List()", id);
        }

        public bool UpdateVendor(string companyName, Vendor vendor)
        {
            return false;
        }

        public ChartofAccount CreateAccount(string companyName, ChartofAccount account)
        {
            EnsureCompanyConnected(companyName);
            if (!CurrentCompanyDisconnected)
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
            if (!CurrentCompanyDisconnected)
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
            if (!CurrentCompanyDisconnected)
            {
                var vendorFactory = CompanyManager.Instance.CurrentCompany.Factories.VendorFactory;
                // Get the list of all vendors
                var vendors = vendorFactory.List();

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



        public void VerifyCompanyAccess(int index)
        {
            if (index < 0 || index >= Companies.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Company index is out of range.");
            }
        }
        public T GetEntityFromPath<T>(string companyName, string path, string id = null)
        {
            EnsureCompanyConnected(companyName);
            if (!CurrentCompanyDisconnected)
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
            if (!CurrentCompanyDisconnected)
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
        public async void EnsureCompanyConnected(string companyName)
        {
            if (CurrentCompanyDisconnected)
            {
                var errorMessage = OpenCompany(companyName);
                var count = 0;
                while (errorMessage == "Authorization result = Pending")
                {
                    Program.WriteToLogFile($"Waiting for authorization from company {companyName}. The Sage 50 Instance must be closed and reopened to re-trigger the prompt to give Authorization.");
                    Task.Delay(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    errorMessage = OpenCompany(companyName);

                    if (count++ > 12) break;
                }

                if (CurrentCompanyDisconnected)
                {
                    throw new InvalidOperationException($"{errorMessage}. Company remains disconnected.");
                }
            }

            Program.WriteToLogFile($"Authorization from company {companyName} given.");
        }

    }
}
