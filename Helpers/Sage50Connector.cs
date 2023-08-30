using Sage.Peachtree.API.Exceptions;
using Sage.Peachtree.API;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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

        /// <summary>
        /// 
        /// </summary>
        public void Dispose()
        {
            if (PeachtreeSession != null)
            {
                PeachtreeSession.Close(CurrentCompany);
                m_peachtreeSession = null;
            }
        }
        #endregion

        #region properties/fields

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
                    m_peachtreeSession.Begin("j6hHxkAHH31dLcZj3pr7KkJ97ZHBVp3A+yzPcdoPk0dWuW+npaRCig==qkaYdrdCJQAbSJSUCKFY3gdJByefMBYnYWKkyQP+QgvJKev4vbTCsMyaQM3SIy/g8coNs7zcZA8fzCqEbmclgtNp4AefrnrE+fOkJ+EPGJFgSwzZ019vNkKU79dMEWLQPSSm8KpqAspRKJrtxzoRfCW/KB2MlfgTe7i2vskOFM0wpA8RSXgXvBi/gOpnFX0o3ECrO0fEVN681li4DaIeITl3mjdMZazERXvWpibPxwM=");
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
                auth[AuthenticationCredentialKey.SUPPLEMENTAL_OBJECT] = "j6hHxkAHH31dLcZj3pr7KkJ97ZHBVp3A+yzPcdoPk0dWuW+npaRCig==qkaYdrdCJQAbSJSUCKFY3gdJByefMBYnYWKkyQP+QgvJKev4vbTCsMyaQM3SIy/g8coNs7zcZA8fzCqEbmclgtNp4AefrnrE+fOkJ+EPGJFgSwzZ019vNkKU79dMEWLQPSSm8KpqAspRKJrtxzoRfCW/KB2MlfgTe7i2vskOFM0wpA8RSXgXvBi/gOpnFX0o3ECrO0fEVN681li4DaIeITl3mjdMZazERXvWpibPxwM=";

                // Request access to the company; if it is the first time, the return result will be
                // "Pending". The Peachtree Administrator must then open the company in Peachtree and
                // choose "Always Allow Access" for this application.  Once this has been done,
                // the result should be "Granted" and we can continue to open the company.
                authorizationResult = PeachtreeSession.RequestAccess(companyId, auth);

                return "Authorization result = " + authorizationResult.ToString();
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
        /// Verify Company Access
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        internal string VerifyCompAccess(CompanyIdentifier companyId)
        {
            Sage.Peachtree.API.AuthorizationResult authorizationResult = AuthorizationResult.None;

            try
            {
                authorizationResult = m_peachtreeSession.VerifyAccess(companyId);
                return "Authorization result = " + authorizationResult.ToString();
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
                    auth[AuthenticationCredentialKey.SUPPLEMENTAL_OBJECT] = "j6hHxkAHH31dLcZj3pr7KkJ97ZHBVp3A+yzPcdoPk0dWuW+npaRCig==qkaYdrdCJQAbSJSUCKFY3gdJByefMBYnYWKkyQP+QgvJKev4vbTCsMyaQM3SIy/g8coNs7zcZA8fzCqEbmclgtNp4AefrnrE+fOkJ+EPGJFgSwzZ019vNkKU79dMEWLQPSSm8KpqAspRKJrtxzoRfCW/KB2MlfgTe7i2vskOFM0wpA8RSXgXvBi/gOpnFX0o3ECrO0fEVN681li4DaIeITl3mjdMZazERXvWpibPxwM=";

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
            if (PeachtreeSession.SessionActive && CurrentCompany != null && !CurrentCompany.IsClosed)
            {
                PeachtreeSession.Close(CurrentCompany);
                CurrentCompany = null;
            }
        }

        #endregion
    }
}
