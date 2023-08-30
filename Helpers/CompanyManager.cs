
using System;
using System.Collections.Generic;
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
                var companies = from comp in myConnector.CompanyList
                                select comp;

                return companies.ToList<CompanyIdentifier>();
            }
        }

        public List<string> CompaniesName
        {
            get
            {
                var companies = from comp in myConnector.CompanyList
                                select comp.CompanyName;

                return companies.ToList<string>();
            }
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

        public string VerifyCompanyAccess(CompanyIdentifier comp)
        {
            return Sage50Connector.Instance.RequestAccess(comp);
        }

        public string VerifySelectedCompanyAccess(int index)
        {
            return this.VerifyCompanyAccess(this.Companies[index]);
        }

        public void CloseCompany()
        {
            Sage50Connector.Instance.CloseCompany();
        }
    }
}
