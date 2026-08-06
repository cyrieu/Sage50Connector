using System;
using System.Reflection;

namespace Sage50Connector.Helpers
{
    /// <summary>
    /// Running connector version from assembly metadata (keep AssemblyInfo in
    /// sync with Version.props for customer releases).
    /// </summary>
    internal static class AppVersion
    {
        /// <summary>Four-part version e.g. 1.1.0.0 from AssemblyFileVersion.</summary>
        internal static Version Current
        {
            get
            {
                try
                {
                    Assembly asm = Assembly.GetExecutingAssembly();
                    object[] attrs = asm.GetCustomAttributes(typeof(AssemblyFileVersionAttribute), false);
                    if (attrs != null && attrs.Length > 0)
                    {
                        Version parsed;
                        if (Version.TryParse(((AssemblyFileVersionAttribute)attrs[0]).Version, out parsed))
                        {
                            return parsed;
                        }
                    }
                    return asm.GetName().Version ?? new Version(0, 0, 0, 0);
                }
                catch
                {
                    return new Version(0, 0, 0, 0);
                }
            }
        }

        /// <summary>Display string without trailing .0 revision when zero.</summary>
        internal static string Display
        {
            get
            {
                Version v = Current;
                if (v.Revision <= 0 && v.Build >= 0)
                {
                    return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build);
                }
                return v.ToString();
            }
        }

        /// <summary>
        /// Compare three-part release versions (1.2.3). Ignores revision.
        /// Returns &gt;0 if a is newer than b.
        /// </summary>
        internal static int CompareRelease(Version a, Version b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            int c = a.Major.CompareTo(b.Major);
            if (c != 0) return c;
            c = a.Minor.CompareTo(b.Minor);
            if (c != 0) return c;
            return Math.Max(0, a.Build).CompareTo(Math.Max(0, b.Build));
        }

        internal static Version ParseLoose(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            // Allow "1.1.0" or "1.1.0.0"
            Version v;
            if (Version.TryParse(text, out v)) return v;
            // "1.1" → 1.1.0.0
            if (Version.TryParse(text + ".0", out v)) return v;
            if (Version.TryParse(text + ".0.0", out v)) return v;
            return null;
        }
    }
}
