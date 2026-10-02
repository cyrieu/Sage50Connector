using Sage.Peachtree.API;
using System;
using System.Linq;

namespace Sage50Connector.Helpers
{
    internal sealed class SageCompanyLookupResult
    {
        public bool Present { get; private set; }
        public bool CheckFailed { get; private set; }
        public string Message { get; private set; }

        public static SageCompanyLookupResult Found()
        {
            return new SageCompanyLookupResult
            {
                Present = true,
                Message = "Present",
            };
        }

        public static SageCompanyLookupResult Missing()
        {
            return new SageCompanyLookupResult
            {
                Present = false,
                Message = "Sage 50 can't find this company file on this computer. Restore or re-import it in Sage 50 first.",
            };
        }

        public static SageCompanyLookupResult Failed(string detail)
        {
            return new SageCompanyLookupResult
            {
                Present = false,
                CheckFailed = true,
                Message = "Couldn't check whether this company file is still in Sage 50"
                    + (string.IsNullOrWhiteSpace(detail) ? "." : ": " + detail)
                    + " Nothing was changed.",
            };
        }
    }

    /// <summary>
    /// Answers "is this company file still installed?" without opening it.
    /// Opening would take a Sage seat and can raise the approval prompt.
    /// Uses the process's single Peachtree session and closes it afterward.
    /// </summary>
    internal static class SageCompanyLocator
    {
        public static SageCompanyLookupResult Lookup(string databaseName, string companyGuid, string companyName)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(databaseName))
                {
                    try
                    {
                        CompanyIdentifier identifier = CompanyManager.Instance.ResolveByDatabaseName(databaseName);
                        if (identifier != null)
                            return SageCompanyLookupResult.Found();
                    }
                    catch (Exception ex)
                    {
                        global::Sage50Connector.Program.WriteToFile(
                            "Company lookup by database name '"
                                + databaseName
                                + "' failed, falling back to the company list: "
                                + ex.GetType().Name
                                + ": "
                                + ex.Message);
                    }
                }

                // Same match OpenCompany uses: a saved GUID, otherwise the exact
                // Sage company name. A name match still counts when the GUID is
                // present so a file Sage can open is not reported missing.
                bool present = CompanyManager.Instance.Companies.Any(company =>
                    (!string.IsNullOrWhiteSpace(companyGuid)
                        && string.Equals(company.Guid.ToString(), companyGuid, StringComparison.OrdinalIgnoreCase))
                    || string.Equals(company.CompanyName, companyName, StringComparison.Ordinal));
                if (present)
                    return SageCompanyLookupResult.Found();
                return SageCompanyLookupResult.Missing();
            }
            catch (Exception ex)
            {
                global::Sage50Connector.Program.WriteToFile(
                    "Company file check failed: " + ex.GetType().Name + ": " + ex.Message);
                return SageCompanyLookupResult.Failed(ex.Message);
            }
            finally
            {
                // The worker's ReleaseSageSession once-flag is already spent for
                // this run. Close this probe's session directly or it leaks a seat
                // until the next worker re-arms that flag.
                try { Sage50Connector.Instance.Shutdown(); }
                catch (Exception ex)
                {
                    global::Sage50Connector.Program.WriteToFile(
                        "Error releasing Sage session after company file check: " + ex.Message);
                }
            }
        }
    }
}
