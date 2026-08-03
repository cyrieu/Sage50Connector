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
