using Sage.Peachtree.API.Exceptions;
using Sage.Peachtree.API;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Sage.Peachtree.API.Factories;

namespace Sage50Connector.Helpers
{
    public class Sage50Connector : IDisposable
    {
        #region constructor and dispose
        /// <summary>
        /// This class uses the Singleton pattern, so the only way to instantiate the class is through
        /// the static Instance variable.
        /// </summary>
        private Sage50Connector()
        {
            //Sage.Peachtree.API
            Sage.Peachtree.API.Resolver.AssemblyInitializer.Initialize();
        }

        public void Dispose()
        {
            Shutdown();
        }

        /// <summary>
        /// Release the Sage session and the open company.
        ///
        /// Sage licenses a limited number of concurrent connections and does not
        /// reclaim ours when the process goes away, so failing to do this leaks a
        /// seat until the Sage connect service restarts. Once enough leak, Sage
        /// answers every request with "License is currently unavailable. You have
        /// reached the maximum number of connections".
        ///
        /// Reads m_peachtreeSession directly rather than the PeachtreeSession
        /// property on purpose: the property's getter creates *and begins* a
        /// session on demand, so releasing through it would open the very
        /// connection this is meant to give back.
        ///
        /// Safe to call repeatedly, and safe when nothing was ever opened.
        /// </summary>
        public void Shutdown()
        {
            PeachtreeSession session = m_peachtreeSession;
            m_peachtreeSession = null;
            Company company = CurrentCompany;
            CurrentCompany = null;

            if (session == null)
            {
                return;
            }

            try
            {
                if (company != null && !company.IsClosed && session.SessionActive)
                {
                    session.Close(company);
                }
            }
            catch (Exception ex)
            {
                // Never let cleanup throw - it runs from process-exit paths.
                global::Sage50Connector.Program.WriteToFile("Error closing Sage company: " + ex.Message);
            }

            try
            {
                session.End();
            }
            catch (Exception ex)
            {
                global::Sage50Connector.Program.WriteToFile("Error ending Sage session: " + ex.Message);
            }

            (session as IDisposable)?.Dispose();
        }
        #endregion

        #region properties/fields

        /// <summary>
        /// Sage-issued ApplicationIdentifier for this connector. An empty
        /// identifier only allows access to Peachtree Sample companies; to
        /// access real companies, the connector must present this token
        /// (obtained from Sage; it is Rutter's license, not per-customer).
        /// </summary>
        private const string ApplicationIdentifier = "j6hHxkAHH31dLcZj3pr7KkJ97ZHBVp3A+yzPcdoPk0dWuW+npaRCig==qkaYdrdCJQAbSJSUCKFY3gdJByefMBYnYWKkyQP+QgvJKev4vbTCsMyaQM3SIy/g8coNs7zcZA8fzCqEbmclgtNp4AefrnrE+fOkJ+EPGJFgSwzZ019vNkKU79dMEWLQPSSm8KpqAspRKJrtxzoRfCW/KB2MlfgTe7i2vskOFM0wpA8RSXgXvBi/gOpnFX0o3ECrO0fEVN681li4DaIeITl3mjdMZazERXvWpibPxwM=";

        public Company CurrentCompany { get; set; }

        private PeachtreeSession m_peachtreeSession;
        private PeachtreeSession PeachtreeSession
        {
            get
            {
                if (m_peachtreeSession == null)
                {
                    // Create the Peachtree Session object and provide the application token
                    m_peachtreeSession = new PeachtreeSession();

                    // Note: an empty ApplicationIdentifier will only allow access to Peachtree Sample companies.
                    // To access other companies, you must contact Sage to obtain a valid ApplicationIdentifier
                    //m_peachtreeSession.Begin(string.Empty);
                    m_peachtreeSession.Begin(ApplicationIdentifier);
                }
                return m_peachtreeSession;
            }
        }
        
        private static Sage50Connector m_Sage50Connector = null;
        public static Sage50Connector Instance
        {
            get
            {
                if (m_Sage50Connector == null)
                {
                    m_Sage50Connector = new Sage50Connector();
                }
                return m_Sage50Connector;
            }
        }

        /// <summary>
        /// Wrapper for the session's CompanyList
        /// </summary>
        public CompanyIdentifierList CompanyList
        {
            get
            {
                return PeachtreeSession.CompanyList();
            }
        }
        #endregion

        #region methods
        /// <summary>
        /// Request Company Access
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        internal string RequestAccess(CompanyIdentifier companyId)
        {
            return "Authorization result = " + RequestAccessResult(companyId).ToString();
        }

        /// <summary>
        /// Ask Sage whether this exact executable is approved for the company.
        /// Sage keys the decision to the executable's MD5, so this is also the
        /// reliable current-version check. RequestAccess both reads an existing
        /// decision and registers a request when this build has never been seen.
        /// </summary>
        internal AuthorizationResult RequestAccessResult(CompanyIdentifier companyId)
        {
            Sage.Peachtree.API.AuthorizationResult authorizationResult = AuthorizationResult.None;

            try
            {
                // For our purposes, the authorization credentials object is a dictionary that contains
                // two keys: A string description that will be displayed in Peachtree as part of the
                // request for access; and a serializable object that will be used to uniquely identify
                // this application.  Here, we choose to use a string as the serializable object, but
                // any serializable object (standard or custom) will do.
                //
                // We will use the same string both for the description and the object.
                Dictionary<string, object> auth = new Dictionary<string, object>();
                auth[AuthenticationCredentialKey.SUPPLEMENTAL_DESCRIPTION] = Properties.Resources.ADDIN_TITLE;
                auth[AuthenticationCredentialKey.SUPPLEMENTAL_OBJECT] = ApplicationIdentifier;

                // Request access to the company; if it is the first time, the return result will be
                // "Pending". The Peachtree Administrator must then open the company in Peachtree and
                // choose "Always Allow Access" for this application.  Once this has been done,
                // the result should be "Granted" and we can continue to open the company.
                authorizationResult = PeachtreeSession.RequestAccess(companyId, auth);
                
                return authorizationResult;
            }
            catch (System.Exception ex)
            {
                StringBuilder message = new StringBuilder();
                message.Append(string.Format("ERROR!!! {0} {1}{2}", ex.GetType(), ex.Message, Environment.NewLine));
                if (ex.InnerException != null)
                {
                    message.Append(string.Format("{0}", Environment.NewLine + ex.InnerException.Message));
                }

                if (ex is AuthorizationException)
                {
                    AuthorizationException aEx = (ex as AuthorizationException);
                }

                throw ex;
            }
        }

        /// <summary>
        /// Open the specified company, supplying supplemental authentication information
        ///     so that peachtree can identify it differently from Outlook, the hosting application.
        /// </summary>
        /// <param name="companyId"></param>
        public bool OpenCompany(CompanyIdentifier companyId)
        {
            bool companyOpened = false;
            if (null != companyId)
            {
                try
                {
                    // For our purposes, the authorization credentials object is a dictionary that contains
                    // two keys: A string description that will be displayed in Peachtree as part of the
                    // request for access; and a serializable object that will be used to uniquely identify
                    // this application.  Here, we choose to use a string as the serializable object, but
                    // any serializable object (standard or custom) will do.
                    //
                    // We will use the same string both for the description and the object.
                    Dictionary<string, object> auth = new Dictionary<string, object>();
                    auth[AuthenticationCredentialKey.SUPPLEMENTAL_DESCRIPTION] = Properties.Resources.ADDIN_TITLE;
                    auth[AuthenticationCredentialKey.SUPPLEMENTAL_OBJECT] = ApplicationIdentifier;

                    // Request access to the company; if it is the first time, the return result will be
                    // "Pending". The Peachtree Administrator must then open the company in Peachtree and
                    // choose "Always Allow Access" for this application.  Once this has been done,
                    // the result should be "Granted" and we can continue to open the company.
                    AuthorizationResult authorizationResult = PeachtreeSession.RequestAccess(companyId, auth);

                    if (authorizationResult == AuthorizationResult.Granted)
                    {
                        CurrentCompany = PeachtreeSession.Open(companyId, auth);

                        if (CurrentCompany != null && !CurrentCompany.IsClosed)
                        {
                            companyOpened = true;
                        }
                    }
                }
                catch (Sage.Peachtree.API.Exceptions.LicenseNotAvailableException ex)
                {
                    throw ex;
                }
                catch (System.Exception ex)
                {
                    StringBuilder message = new StringBuilder();
                    message.Append(string.Format("ERROR!!! {0} {1}{2}", ex.GetType(), ex.Message, Environment.NewLine));
                    if (ex.InnerException != null)
                    {
                        message.Append(string.Format("{0}", Environment.NewLine + ex.InnerException.Message));
                    }

                    if (ex is AuthorizationException)
                    {
                        AuthorizationException aEx = (ex as AuthorizationException);
                    }

                    throw ex;
                }
            }
            return companyOpened;
        }

        /// <summary>
        /// Close the specified company.
        /// After completion, set the CurrentCompany and customerList variables to null
        /// </summary>
        /// <param name="companyId"></param>
        /// 
        public void CloseCompany()
        {
            // m_peachtreeSession, not the property: the getter would begin a new
            // session just to close a company that by definition is not open in it.
            if (m_peachtreeSession != null
                && m_peachtreeSession.SessionActive
                && CurrentCompany != null
                && !CurrentCompany.IsClosed)
            {
                m_peachtreeSession.Close(CurrentCompany);
                CurrentCompany = null;
            }
        }

        #endregion
    }
}
