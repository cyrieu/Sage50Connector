using System;
using System.Reflection;
using Sage.Peachtree.API;

namespace Sage50Connector.Helpers
{
    /// <summary>
    /// Sage.Peachtree.API.CompanyIdentifier has no public constructor — the SDK
    /// only ever hands one back from CompanyList() or LookupCompanyIdentifier().
    /// Verified by reflecting on Sage.Peachtree.API.dll 2026.1: it does have a
    /// private ctor(Guid, string dbName, string companyPath, string companyName,
    /// string serverName). This reaches that ctor via reflection so a user-picked
    /// folder can still produce something to try when neither SDK entry point
    /// resolves the company.
    ///
    /// ponytail: unsupported by Sage. If a future Sage.Peachtree.API.dll changes
    /// this constructor's signature, IsAvailable goes false and callers fall back
    /// to an ordinary error instead of breaking.
    /// </summary>
    internal static class CompanyIdentifierReflectionFactory
    {
        private static readonly ConstructorInfo Ctor = typeof(CompanyIdentifier).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new[] { typeof(Guid), typeof(string), typeof(string), typeof(string), typeof(string) },
            null);

        public static bool IsAvailable
        {
            get { return Ctor != null; }
        }

        public static CompanyIdentifier Build(Guid companyGuid, string databaseName, string companyPath, string companyName, string serverName)
        {
            if (Ctor == null)
            {
                throw new InvalidOperationException(
                    "Sage.Peachtree.API.CompanyIdentifier no longer has the expected constructor.");
            }

            return (CompanyIdentifier)Ctor.Invoke(new object[]
            {
                companyGuid, databaseName, companyPath, companyName, serverName,
            });
        }
    }
}
