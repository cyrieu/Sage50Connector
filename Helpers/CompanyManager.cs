
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Sage.Peachtree.API;

namespace Sage50Connector.Helpers
{
    public class CompanyManager
    {
        private static Sage50Connector myConnector;

        public Company CurrentCompany { get; set; }
        private static CompanyManager m_CompanyManager = null;
        public static CompanyManager Instance
        {
            get
            {
                if (m_CompanyManager == null)
                {
                    myConnector = Sage50Connector.Instance;
                    m_CompanyManager = new CompanyManager();
                }
                return m_CompanyManager;
            }
        }

        public CompanyManager()
        {
        }

        public List<CompanyIdentifier> Companies
        {
            get
            {
                return ListCompanies().ToList();
            }
        }

        public List<string> CompaniesName
        {
            get
            {
                return ListCompanies().Select(comp => comp.CompanyName).ToList();
            }
        }

        /// <summary>
        /// CompanyList() has two independent native call paths in the SDK — the
        /// parameterless overload and the per-server overload. A machine whose
        /// company catalog is broken for one has, in practice, been seen to still
        /// answer the other. Try both, logging each failure, before giving up.
        /// </summary>
        private IEnumerable<CompanyIdentifier> ListCompanies()
        {
            CompanyIdentifierList list;
            try
            {
                list = myConnector.CompanyList;
            }
            catch (Exception ex)
            {
                global::Sage50Connector.Program.WriteToFile("Sage 50 company enumeration failed (default server): " + ex.GetType().Name + ": " + ex.Message);
                try
                {
                    list = myConnector.CompanyListForServer(Environment.MachineName);
                }
                catch (Exception ex2)
                {
                    global::Sage50Connector.Program.WriteToFile("Sage 50 company enumeration failed (server '" + Environment.MachineName + "'): " + ex2.GetType().Name + ": " + ex2.Message);
                    throw;
                }
            }

            return from comp in list select comp;
        }

        /// <summary>
        /// Last-resort discovery: the user has browsed directly to a Sage 50
        /// company data folder because it never showed up (or Sage 50's company
        /// catalog is broken and CompanyList()/CompanyListForServer both failed).
        ///
        /// Tries the folder's leaf name as a guess at Sage's internal database
        /// name via the supported LookupCompanyIdentifier() call first — cheap,
        /// safe, and returns Sage's own validated identity when the guess is
        /// right. Only if that fails does it fall back to constructing a
        /// CompanyIdentifier directly (unsupported — see
        /// CompanyIdentifierReflectionFactory) and validating it with a bounded
        /// RequestAccess call.
        /// </summary>
        /// <summary>
        /// Resolves one company directly by its Sage-internal database name via
        /// the supported LookupCompanyIdentifier() call — no full CompanyList()
        /// enumeration involved. Used both by the folder-browse fallback below
        /// (a guessed name) and by Sage50Repository.OpenCompany at every normal
        /// reconnect (the real name, captured once at setup time), so an ordinary
        /// reconnect no longer depends on enumeration succeeding at all.
        /// </summary>
        public CompanyIdentifier ResolveByDatabaseName(string databaseName)
        {
            return myConnector.LookupCompanyIdentifier(Environment.MachineName, databaseName);
        }

        public CompanyIdentifier ResolveFolder(string folderPath)
        {
            string guessedName = new DirectoryInfo(folderPath.TrimEnd('\\', '/')).Name;
            string serverName = Environment.MachineName;

            try
            {
                CompanyIdentifier identifier = ResolveByDatabaseName(guessedName);
                if (identifier != null)
                {
                    global::Sage50Connector.Program.WriteToFile("Folder '" + folderPath + "' resolved to Sage 50 company '" + identifier.CompanyName + "' via LookupCompanyIdentifier.");
                    return identifier;
                }
            }
            catch (Exception ex)
            {
                global::Sage50Connector.Program.WriteToFile("LookupCompanyIdentifier could not resolve folder '" + folderPath + "' (guessed database name '" + guessedName + "'): " + ex.GetType().Name + ": " + ex.Message);
            }

            if (!CompanyIdentifierReflectionFactory.IsAvailable)
            {
                throw new InvalidOperationException("Rutter could not identify a Sage 50 company at that folder.");
            }

            global::Sage50Connector.Program.WriteToFile("Falling back to direct company-identifier construction for folder '" + folderPath + "' (unsupported Sage SDK path; guessed database name '" + guessedName + "').");
            CompanyIdentifier manual = CompanyIdentifierReflectionFactory.Build(guessedName, folderPath, guessedName, serverName);

            // ponytail: bounded to 20s. companyPath always comes from a folder the
            // user just browsed to, so it exists — the one combination seen to
            // hang the native Btrieve engine (a real database name paired with a
            // nonexistent path) shouldn't arise here. Still unverified by Sage's
            // own catalog, unlike the LookupCompanyIdentifier path above, so this
            // call is what actually confirms Sage can reach it.
            string validation;
            try
            {
                Task<string> task = Task.Run(() => Sage50Connector.Instance.RequestAccess(manual));
                if (!task.Wait(TimeSpan.FromSeconds(20)))
                {
                    global::Sage50Connector.Program.WriteToFile("Direct construction validation for '" + folderPath + "' did not respond within 20s; abandoning (the Sage 50 SDK call may still be running in the background).");
                    throw new InvalidOperationException("Rutter could not verify Sage 50 access for that folder (no response after 20 seconds).");
                }
                validation = task.Result;
            }
            catch (AggregateException aex)
            {
                Exception inner = aex.InnerException ?? aex;
                global::Sage50Connector.Program.WriteToFile("Direct construction validation for '" + folderPath + "' failed: " + inner.GetType().Name + ": " + inner.Message);
                throw new InvalidOperationException("Rutter could not open a Sage 50 company at that location: " + inner.Message);
            }

            global::Sage50Connector.Program.WriteToFile("Direct construction validation for '" + folderPath + "': " + validation);
            return manual;
        }

        public string OpenCompany(CompanyIdentifier comp)
        {
            string result;
            result = Sage50Connector.Instance.RequestAccess(comp);

            if (result.Contains("Granted"))
            {
                if (Sage50Connector.Instance.OpenCompany(comp))
                {
                    CurrentCompany = Sage50Connector.Instance.CurrentCompany;
                    return "Success";
                }
            }

            return result;
        }

        public string OpenSelectedCompany(int index)
        {
            return this.OpenCompany(this.Companies[index]);
        }

        public void CloseCompany()
        {
            Sage50Connector.Instance.CloseCompany();
        }
    }
}
