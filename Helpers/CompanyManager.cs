
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
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
        /// First matches the selected path against CompanyList() when enumeration
        /// is available. If it is not, CrystalReports.udl inside every tested Sage
        /// company folder supplies the real PSQL Data Source/database name. Sage
        /// does not require that name to equal the folder leaf (for example,
        /// rutoddlo can contain database rutteroddlocationtes), so the leaf is
        /// only a final compatibility guess. Only if supported lookup cannot
        /// return an identifier for the selected physical directory does this
        /// fall back to the unsupported reflection factory.
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
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                throw new DirectoryNotFoundException("The selected Sage 50 company folder does not exist.");
            }

            folderPath = Path.GetFullPath(folderPath.TrimEnd('\\', '/'));
            string leafName = new DirectoryInfo(folderPath).Name;
            string serverName = Environment.MachineName;
            CompanyIdentifier lookupMetadata = null;

            // This is the most authoritative path when CompanyList works: Sage
            // supplied both the identifier and the directory. Resolve junctions
            // before comparing so a company physically stored outside the normal
            // root still matches a Sage-visible junction.
            try
            {
                CompanyIdentifier pathMatch = ListCompanies().FirstOrDefault(
                    identifier => PathsReferToSameDirectory(folderPath, identifier.Path));
                if (pathMatch != null)
                {
                    global::Sage50Connector.Program.WriteToFile(
                        "Folder '" + folderPath + "' resolved to Sage 50 company '"
                        + pathMatch.CompanyName + "' by matching Sage's company-list path.");
                    return pathMatch;
                }
            }
            catch (Exception ex)
            {
                // ListCompanies already logs both SDK enumeration failures. The
                // folder fallback exists specifically so those failures are not
                // fatal, so continue with metadata local to the selected folder.
                global::Sage50Connector.Program.WriteToFile(
                    "Could not match folder '" + folderPath
                    + "' through Sage's company list; continuing with folder metadata: "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            var databaseNames = new List<string>();
            string udlDatabaseName = ReadDatabaseNameFromFolder(folderPath);
            if (!string.IsNullOrWhiteSpace(udlDatabaseName))
            {
                databaseNames.Add(udlDatabaseName);
                global::Sage50Connector.Program.WriteToFile(
                    "Folder '" + folderPath + "' declares Sage database name '"
                    + udlDatabaseName + "' in CrystalReports.udl.");
            }
            if (!databaseNames.Contains(leafName, StringComparer.OrdinalIgnoreCase))
            {
                databaseNames.Add(leafName);
            }

            foreach (string databaseName in databaseNames)
            {
                try
                {
                    CompanyIdentifier identifier = ResolveByDatabaseName(databaseName);
                    if (identifier == null) continue;

                    lookupMetadata = identifier;
                    if (PathsReferToSameDirectory(folderPath, identifier.Path))
                    {
                        global::Sage50Connector.Program.WriteToFile(
                            "Folder '" + folderPath + "' resolved to Sage 50 company '"
                            + identifier.CompanyName + "' via LookupCompanyIdentifier using database name '"
                            + databaseName + "'.");
                        return identifier;
                    }

                    global::Sage50Connector.Program.WriteToFile(
                        "LookupCompanyIdentifier resolved database '" + databaseName
                        + "', but Sage reports path '" + identifier.Path
                        + "' instead of the selected folder '" + folderPath
                        + "'; validating the selected folder directly.");
                }
                catch (Exception ex)
                {
                    global::Sage50Connector.Program.WriteToFile(
                        "LookupCompanyIdentifier could not resolve folder '" + folderPath
                        + "' using database name '" + databaseName + "': "
                        + ex.GetType().Name + ": " + ex.Message);
                }
            }

            if (lookupMetadata != null)
            {
                // A copied folder can contain a perfectly valid UDL for a
                // database that is still registered somewhere else. The SDK's
                // private CompanyIdentifier constructor does not override that
                // registration: RequestAccess follows the registered database
                // and can appear to validate the wrong copy. Never report that
                // as success. A moved company must first be opened/registered in
                // Sage (or exposed through a junction whose physical target is
                // this directory), after which the canonical paths match above.
                throw new InvalidOperationException(
                    "That folder contains Sage database '" + lookupMetadata.DatabaseName
                    + "', but Sage 50 has it registered at '" + lookupMetadata.Path
                    + "'. Open the company from its new location in Sage 50, then try again."
                );
            }

            if (!CompanyIdentifierReflectionFactory.IsAvailable)
            {
                throw new InvalidOperationException("Rutter could not identify a Sage 50 company at that folder.");
            }

            string selectedDatabaseName = databaseNames.First();
            Guid companyGuid = lookupMetadata == null ? Guid.Empty : lookupMetadata.Guid;
            string companyName = lookupMetadata == null ? selectedDatabaseName : lookupMetadata.CompanyName;
            string resolvedServerName = lookupMetadata == null ? serverName : lookupMetadata.ServerName;
            global::Sage50Connector.Program.WriteToFile(
                "Falling back to direct company-identifier construction for folder '" + folderPath
                + "' (unsupported Sage SDK path; database name '" + selectedDatabaseName + "').");
            CompanyIdentifier manual = CompanyIdentifierReflectionFactory.Build(
                companyGuid, selectedDatabaseName, folderPath, companyName, resolvedServerName);

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

        private static string ReadDatabaseNameFromFolder(string folderPath)
        {
            string udlPath = Path.Combine(folderPath, "CrystalReports.udl");
            if (!File.Exists(udlPath)) return null;

            try
            {
                string connectionString = string.Join(
                    string.Empty,
                    File.ReadAllLines(udlPath)
                        .Select(line => line.Trim())
                        .Where(line => line.Length > 0 && !line.StartsWith("[") && !line.StartsWith(";")));
                var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
                object value;
                if (builder.TryGetValue("Data Source", out value))
                {
                    return Convert.ToString(value).Trim();
                }
            }
            catch (Exception ex)
            {
                global::Sage50Connector.Program.WriteToFile(
                    "Could not read Sage database metadata from '" + udlPath + "': "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            return null;
        }

        private static bool PathsReferToSameDirectory(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
            return string.Equals(
                NormalizeDirectoryPath(first),
                NormalizeDirectoryPath(second),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeDirectoryPath(string path)
        {
            string fullPath = Path.GetFullPath(path.TrimEnd('\\', '/'));
            using (SafeFileHandle handle = CreateFile(
                fullPath,
                0,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                IntPtr.Zero,
                3,
                0x02000000,
                IntPtr.Zero))
            {
                if (!handle.IsInvalid)
                {
                    var target = new StringBuilder(1024);
                    uint length = GetFinalPathNameByHandle(handle, target, (uint)target.Capacity, 0);
                    if (length > 0 && length < target.Capacity)
                    {
                        string resolved = target.ToString();
                        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                            resolved = @"\\" + resolved.Substring(8);
                        else if (resolved.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                            resolved = resolved.Substring(4);
                        return resolved.TrimEnd('\\', '/');
                    }
                }
            }
            return fullPath;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder filePath,
            uint filePathLength,
            uint flags);

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
