using Sage50Connector.Models.Rutter;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Sage50Connector.Helpers
{
    internal static class GeneralLedgerExporter
    {
        private const int GeneralLedgerRowsObject = 16;
        private const short CsvFileType = 0;
        private const short OverwriteWithoutAsking = 1;
        private const short SortByJournalPostOrder = 0;

        private static readonly Dictionary<string, int> JournalCodeValues =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "GENJ", 0 },
                { "CRJ", 1 },
                { "CDJ", 2 },
                { "SJ", 3 },
                { "PJ", 4 },
                { "PAYJ", 5 },
                { "COGS", 6 },
                { "IAJ", 7 },
                { "AAJ", 8 },
                { "BZIJ", 9 },
                { "PRJ", 5 },
                { "INAJ", 7 },
                { "ASBY", 8 },
            };

        public static List<GlTransactionBody> ExportTransactions(
            string companyName,
            string companyGuid,
            string startDate,
            string endDate,
            string credentialPath)
        {
            DateTime? startDt = ParseDateBoundOrNull(startDate);
            DateTime? endDt = ParseDateBoundOrNull(endDate);

            // Strictly validate supplied date bounds. A blank value means
            // "not supplied" (SIDE_REFRESH sends neither); a supplied value
            // that fails to parse is an error, not a silent no-op.
            if (!string.IsNullOrWhiteSpace(startDate) && !startDt.HasValue)
                throw new InvalidOperationException(
                    $"TRANSACTIONS start_date '{startDate}' could not be parsed as a date.");
            if (!string.IsNullOrWhiteSpace(endDate) && !endDt.HasValue)
                throw new InvalidOperationException(
                    $"TRANSACTIONS end_date '{endDate}' could not be parsed as a date.");
            if (startDt.HasValue && endDt.HasValue && endDt.Value <= startDt.Value)
                throw new InvalidOperationException(
                    $"TRANSACTIONS end_date must be strictly greater than start_date. " +
                    $"Got start_date='{startDate}', end_date='{endDate}'.");

            ComCredential cred = LoadComCredential(credentialPath);

            object loginSelector = null;
            object login = null;
            object application = null;
            object exporter = null;
            string csvPath = null;

            try
            {
                try
                {
                    Type selectorType = Type.GetTypeFromProgID("PeachtreeAccounting.LoginSelector");
                    if (selectorType != null)
                    {
                        loginSelector = Activator.CreateInstance(selectorType);
                        login = InvokeMethod(selectorType, loginSelector, "GetCurrentLoginObject");
                    }
                }
                catch
                {
                    SafeReleaseComObject(ref loginSelector);
                    login = null;
                }

                if (login == null)
                {
                    Type loginType = Type.GetTypeFromProgID("PeachtreeAccounting.Login.33");
                    if (loginType == null)
                        throw new InvalidOperationException(
                            "Sage 50 COM is not registered on this machine. " +
                            "TRANSACTIONS require the Sage COM API (PeachtreeAccounting), " +
                            "which is separate from the .NET SDK. Ensure Sage 50 is installed " +
                            "and COM is registered.");
                    login = Activator.CreateInstance(loginType);
                }

                Type loginObjType = login.GetType();
                application = InvokeMethod(loginObjType, login, "GetApplication",
                    new object[] { cred.UserName, cred.Password });
                cred = default;

                Type appType = application.GetType();

                bool companyIsOpen = (bool)GetProperty(appType, application, "CompanyIsOpen");
                if (!companyIsOpen)
                {
                    InvokeMethod(appType, application, "OpenPreviousCompany");
                    companyIsOpen = (bool)GetProperty(appType, application, "CompanyIsOpen");
                }

                if (!companyIsOpen)
                    throw new InvalidOperationException(
                        "Sage 50 must be open with the configured company. " +
                        "TRANSACTIONS require Sage to be running in the interactive " +
                        "user session because the COM General Ledger Rows exporter " +
                        "attaches to the open company.");

                string comCompanyName = (string)GetProperty(appType, application, "CurrentCompanyName");
                if (!string.Equals(comCompanyName, companyName, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"The Sage COM company '{comCompanyName}' does not match the " +
                        $"configured company '{companyName}'. Open the correct company in Sage 50.");

                string comCompanyGuid = (string)GetProperty(appType, application, "CurrentCompanyGUID");
                if (!string.IsNullOrWhiteSpace(companyGuid) && !string.IsNullOrWhiteSpace(comCompanyGuid))
                {
                    string normalizedCom = comCompanyGuid.Trim('{', '}');
                    if (!string.Equals(normalizedCom, companyGuid, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"The Sage COM company GUID '{comCompanyGuid}' does not match " +
                            $"the configured company GUID '{companyGuid}'.");
                }

                exporter = InvokeMethod(appType, application, "CreateExporter",
                    new object[] { GeneralLedgerRowsObject });
                Type expType = exporter.GetType();

                InvokeMethod(expType, exporter, "ClearExportFieldList");
                for (short i = 0; i <= 13; i++)
                    InvokeMethod(expType, exporter, "AddToExportFieldList", new object[] { i });

                InvokeMethod(expType, exporter, "SetIncludeHeadersFlag", new object[] { (short)1 });
                InvokeMethod(expType, exporter, "SetSortField", new object[] { SortByJournalPostOrder });
                InvokeMethod(expType, exporter, "SetFileType", new object[] { CsvFileType });
                InvokeMethod(expType, exporter, "SetFileExistsOption", new object[] { OverwriteWithoutAsking });

                csvPath = Path.Combine(Path.GetTempPath(), "sage50-gl-" + Guid.NewGuid() + ".csv");
                InvokeMethod(expType, exporter, "SetFilename", new object[] { csvPath });

                // The General Ledger Rows exporter rejects date-range filtering with
                // COM error 0x800436FD (verified against Sage 50 2026.1). Do not
                // attempt SetDateFilterValue at all — always export the whole ledger
                // and apply the half-open date window locally after parsing.
                InvokeMethod(expType, exporter, "Export");

                List<GlTransactionLineBody> allLines = ParseCsv(csvPath);
                List<GlTransactionLineBody> filtered = ApplyDateWindow(allLines, startDt, endDt);
                List<GlTransactionLineBody> posting = filtered
                    .Where(l => l.IncludeInGL)
                    .ToList();

                return GroupByPostingOrder(posting);
            }
            finally
            {
                cred = default;
                SafeReleaseComObject(ref exporter);
                SafeReleaseComObject(ref application);
                SafeReleaseComObject(ref login);
                SafeReleaseComObject(ref loginSelector);
                if (csvPath != null && File.Exists(csvPath))
                {
                    try { File.Delete(csvPath); } catch { }
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        /// <summary>
        /// RFC 4180 CSV parser that handles quoted fields containing newlines
        /// and embedded doubled quotes. Reads the whole file as a stream so
        /// multi-line quoted descriptions are not split across physical lines.
        /// Made public/internal so unit tests can exercise it without COM.
        /// </summary>
        internal static List<string[]> ParseCsvRecords(string csvPath)
        {
            var records = new List<string[]>();
            using (var reader = new StreamReader(csvPath, Encoding.UTF8))
            {
                var parser = new Rfc4180CsvParser(reader);
                while (parser.ReadRecord(out string[] fields))
                {
                    records.Add(fields);
                }
            }
            return records;
        }

        internal static List<GlTransactionLineBody> ParseCsv(string csvPath)
        {
            List<string[]> records = ParseCsvRecords(csvPath);
            if (records.Count < 2) return new List<GlTransactionLineBody>();

            string[] headers = records[0];
            var headerMap = BuildHeaderMap(headers);

            var result = new List<GlTransactionLineBody>(records.Count - 1);
            for (int i = 1; i < records.Count; i++)
            {
                if (records[i].Length == 1 && string.IsNullOrEmpty(records[i][0])) continue;
                result.Add(MapRow(records[i], headerMap, i + 1));
            }
            return result;
        }

        internal static Dictionary<string, int> BuildHeaderMap(string[] headers)
        {
            var map = new Dictionary<string, int>(headers.Length);
            for (int i = 0; i < headers.Length; i++)
            {
                string normalized = NormalizeFieldName(headers[i]);
                if (!map.ContainsKey(normalized))
                    map[normalized] = i;
            }
            return map;
        }

        internal static string NormalizeFieldName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        internal static string ReadField(string[] fields, Dictionary<string, int> map, params string[] names)
        {
            foreach (string name in names)
            {
                string normalized = NormalizeFieldName(name);
                if (map.TryGetValue(normalized, out int idx) && idx < fields.Length)
                    return fields[idx];
            }
            return null;
        }

        internal static GlTransactionLineBody MapRow(string[] fields, Dictionary<string, int> headerMap, int rowNumber)
        {
            string journalPostOrderText = ReadField(fields, headerMap,
                "JournalPostOrder", "Journal Post Order", "Journal Hdr Postorder");
            string journalRowIndexText = ReadField(fields, headerMap,
                "JournalRowIndex", "Journal Row Index");
            string journalTypeText = ReadField(fields, headerMap, "Type", "Jrnl");
            string includeInGlText = ReadField(fields, headerMap,
                "IncludeInGL", "Include In GL", "Include In GL?");
            string dateText = ReadField(fields, headerMap, "Date", "Transaction Date");
            string amountText = ReadField(fields, headerMap,
                "TransactionAmount", "Transaction Amount", "Transaction Amount as DR/CR");
            string idText = ReadField(fields, headerMap, "GUID", "GL GUID");

            // Fail closed on malformed required fields — do not silently default
            // to 0/false and risk grouping corrupt rows under gl:0.
            if (string.IsNullOrWhiteSpace(journalPostOrderText))
                throw new InvalidDataException(
                    $"GL row {rowNumber}: JournalPostOrder is missing or blank. " +
                    "Cannot group rows without a posting order.");
            if (!long.TryParse(journalPostOrderText, out long journalPostOrder) || journalPostOrder <= 0)
                throw new InvalidDataException(
                    $"GL row {rowNumber}: JournalPostOrder '{journalPostOrderText}' is not a positive integer. " +
                    "A zero or negative posting order would corrupt grouping.");

            if (string.IsNullOrWhiteSpace(journalRowIndexText))
                throw new InvalidDataException(
                    $"GL row {rowNumber}: JournalRowIndex is missing or blank.");
            if (!int.TryParse(journalRowIndexText, out int journalRowIndex) || journalRowIndex < 0)
                throw new InvalidDataException(
                    $"GL row {rowNumber}: JournalRowIndex '{journalRowIndexText}' is not a non-negative integer.");

            if (string.IsNullOrWhiteSpace(idText))
                throw new InvalidDataException(
                    $"GL row {rowNumber}: GL GUID is missing or blank. " +
                    "The row GUID is the line identity and must be present.");

            if (string.IsNullOrWhiteSpace(dateText))
                throw new InvalidDataException(
                    $"GL row {rowNumber}: Date is missing or blank. " +
                    "A GL row without a posting date cannot be date-filtered or grouped.");

            DateTime? rowDate = ParseDateOrNull(dateText);
            if (!rowDate.HasValue)
                throw new InvalidDataException(
                    $"GL row {rowNumber}: Date '{dateText}' could not be parsed as a date.");
            string dateStr = rowDate.Value.ToString("yyyy-MM-dd");

            if (string.IsNullOrWhiteSpace(includeInGlText))
                throw new InvalidDataException(
                    $"GL row {rowNumber}: IncludeInGL is missing or blank. " +
                    "Cannot determine whether this row posts to the General Ledger.");
            bool includeInGL = ParseIncludeInGL(includeInGlText, rowNumber);

            if (string.IsNullOrWhiteSpace(amountText))
                throw new InvalidDataException(
                    $"GL row {rowNumber}: TransactionAmount is missing or blank.");
            decimal amount = ParseDecimalOrThrow(amountText, rowNumber);

            // JournalType is nullable: unknown codes must not be mislabeled as
            // General (0). journalTypeCode is always preserved as-is.
            int? journalType = null;
            if (!string.IsNullOrWhiteSpace(journalTypeText))
            {
                if (int.TryParse(journalTypeText, out int numericType))
                    journalType = numericType;
                else if (JournalCodeValues.TryGetValue(journalTypeText.Trim(), out int mappedType))
                    journalType = mappedType;
                // else: unknown code → journalType stays null, journalTypeCode preserved
            }

            string accountId = ReadField(fields, headerMap,
                "GLAccountId", "GL Account ID", "Account ID");
            if (string.IsNullOrWhiteSpace(accountId))
                throw new InvalidDataException(
                    $"GL row {rowNumber}: GLAccountId is missing or blank. " +
                    "A GL row without an account ID cannot be linked to an account.");

            return new GlTransactionLineBody
            {
                ID = idText,
                JournalPostOrder = journalPostOrder,
                JournalRowIndex = journalRowIndex,
                AccountID = accountId,
                AccountGuid = ReadField(fields, headerMap,
                    "GLAccountGUID", "GL Account GUID", "General Ledger Account GUID"),
                Date = dateStr,
                JournalType = journalType,
                JournalTypeCode = journalTypeText,
                Reference = ReadField(fields, headerMap,
                    "TransactionReference", "Transaction Reference", "Reference"),
                Description = ReadField(fields, headerMap, "Description", "Trans Description"),
                JobId = ReadField(fields, headerMap, "JobId", "Job ID"),
                JobGuid = ReadField(fields, headerMap, "JobGUID", "Job GUID"),
                Amount = amount,
                DateCleared = ReadField(fields, headerMap,
                    "DateCleared", "Date Cleared", "Cleared Date"),
                IncludeInGL = includeInGL,
            };
        }

        internal static List<GlTransactionBody> GroupByPostingOrder(List<GlTransactionLineBody> postingRows)
        {
            var groups = postingRows
                .GroupBy(r => r.JournalPostOrder)
                .OrderBy(g => g.Key);

            var transactions = new List<GlTransactionBody>();
            foreach (var group in groups)
            {
                var ordered = group.OrderBy(r => r.JournalRowIndex).ThenBy(r => r.ID ?? "").ToList();
                var first = ordered[0];
                var typeCodes = ordered
                    .Select(r => r.JournalTypeCode)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct()
                    .OrderBy(s => s)
                    .ToList();
                var allDates = ordered.Select(r => r.Date).ToList();
                var allRefs = ordered.Select(r => r.Reference).ToList();

                // headerConsistent means every line shares one normalized date
                // and one normalized reference. All-blank is consistent (zero
                // references on any line). Mixed blank/nonblank is NOT consistent.
                // Multiple journal type codes are expected (sales + COGS
                // companions) and do NOT make a group inconsistent.
                bool headerConsistent =
                    ValuesAreConsistent(allDates) && ValuesAreConsistent(allRefs);

                decimal totalAmount = ordered.Sum(r => r.Amount);

                transactions.Add(new GlTransactionBody
                {
                    ID = "gl:" + group.Key,
                    JournalPostOrder = group.Key,
                    Date = first.Date,
                    Amount = totalAmount,
                    JournalTypeCodes = typeCodes,
                    References = allRefs
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct()
                        .ToList(),
                    HeaderConsistent = headerConsistent,
                    Lines = ordered,
                });
            }
            return transactions;
        }

        /// <summary>
        /// Returns true if all values are blank, or if all values are non-blank
        /// and share one normalized (trimmed) value. Mixed blank/nonblank is
        /// false. An empty list is vacuously consistent.
        /// </summary>
        internal static bool ValuesAreConsistent(List<string> values)
        {
            if (values == null || values.Count == 0) return true;
            bool allBlank = values.All(s => string.IsNullOrWhiteSpace(s));
            if (allBlank) return true;
            bool anyBlank = values.Any(s => string.IsNullOrWhiteSpace(s));
            if (anyBlank) return false;
            var distinct = values.Select(s => s.Trim()).Distinct().ToList();
            return distinct.Count <= 1;
        }

        internal static List<GlTransactionLineBody> ApplyDateWindow(
            List<GlTransactionLineBody> rows, DateTime? startDate, DateTime? endDate)
        {
            if (!startDate.HasValue && !endDate.HasValue) return rows;
            return rows.Where(r =>
            {
                if (string.IsNullOrWhiteSpace(r.Date)) return false;
                DateTime dt;
                if (!DateTime.TryParseExact(r.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out dt))
                    return false;
                if (startDate.HasValue && dt < startDate.Value.Date) return false;
                if (endDate.HasValue && dt >= endDate.Value.Date) return false;
                return true;
            }).ToList();
        }

        /// <summary>
        /// Loads Sage COM partner credentials from a CLIXML file produced by
        /// PowerShell Export-Clixml (as Set-GeneralLedgerComCredential.ps1 does).
        /// The password is stored as a SecureString serialized to CLIXML: the
        /// ciphertext is DPAPI-encrypted and encoded as a hex string in the SS
        /// element, not Base64.
        /// </summary>
        private static ComCredential LoadComCredential(string credentialPath)
        {
            if (string.IsNullOrWhiteSpace(credentialPath))
                throw new InvalidOperationException(
                    "Sage COM partner credential path is not configured. " +
                    "Run Set-GeneralLedgerComCredential.ps1 as the interactive Sage user " +
                    "to create the credential file. TRANSACTIONS require the Sage COM " +
                    "General Ledger Rows exporter, which uses separate Sage-issued partner " +
                    "credentials that are not stored in sage50Config.json.");

            if (!File.Exists(credentialPath))
                throw new InvalidOperationException(
                    $"Sage COM partner credential not found at '{credentialPath}'. " +
                    "Run Set-GeneralLedgerComCredential.ps1 as the interactive Sage user " +
                    "to create it. TRANSACTIONS require the Sage COM General Ledger Rows " +
                    "exporter, which uses separate Sage-issued partner credentials.");

            string xml = File.ReadAllText(credentialPath);
            var doc = XDocument.Parse(xml);

            string userName = null;
            string encryptedPasswordHex = null;

            foreach (var elem in doc.Descendants())
            {
                string nAttr = elem.Attribute("N")?.Value;
                if (nAttr == "UserName" && elem.Name.LocalName == "S")
                    userName = elem.Value;
                if (nAttr == "Password" && elem.Name.LocalName == "SS")
                    encryptedPasswordHex = elem.Value;
            }

            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(encryptedPasswordHex))
                throw new InvalidOperationException(
                    $"The credential file at '{credentialPath}' is missing the Sage COM " +
                    "partner account name or password. Re-run " +
                    "Set-GeneralLedgerComCredential.ps1 as the interactive Sage user.");

            // Export-Clixml stores SecureString ciphertext as a hex string.
            byte[] encryptedBytes = HexStringToBytes(encryptedPasswordHex);
            byte[] decryptedBytes = ProtectedData.Unprotect(
                encryptedBytes, null, DataProtectionScope.CurrentUser);
            // SecureString DPAPI blob decodes to UTF-16LE (Unicode) plaintext.
            string password = Encoding.Unicode.GetString(decryptedBytes);

            if (string.IsNullOrWhiteSpace(password))
                throw new InvalidOperationException(
                    "The Sage COM partner password is blank. Re-run " +
                    "Set-GeneralLedgerComCredential.ps1.");

            return new ComCredential { UserName = userName, Password = password };
        }

        /// <summary>
        /// Converts a hex string (e.g. "01000000d08c9ddf...") to a byte array.
        /// Export-Clixml stores SecureString ciphertext in this encoding.
        /// </summary>
        private static byte[] HexStringToBytes(string hex)
        {
            if (hex == null) throw new ArgumentNullException(nameof(hex));
            hex = hex.Trim();
            if (hex.Length % 2 != 0)
                throw new FormatException(
                    "The encrypted password in the credential file is not a valid hex string " +
                    "(odd length). The file may be corrupted. Re-run " +
                    "Set-GeneralLedgerComCredential.ps1.");
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }

        internal static decimal ParseDecimalOrThrow(string value, int rowNumber)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException(
                    $"GL row {rowNumber}: TransactionAmount is missing or blank.");
            var styles = NumberStyles.Number | NumberStyles.AllowCurrencySymbol | NumberStyles.AllowParentheses;
            if (decimal.TryParse(value, styles, CultureInfo.CurrentCulture, out decimal result))
                return result;
            if (decimal.TryParse(value, styles, CultureInfo.InvariantCulture, out result))
                return result;
            throw new InvalidDataException(
                $"GL row {rowNumber}: TransactionAmount '{value}' could not be parsed as a decimal. " +
                "The connector will not silently default to 0 because that would corrupt " +
                "the transaction balance.");
        }

        internal static DateTime? ParseDateOrNull(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime result))
                return result.Date;
            if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out result))
                return result.Date;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
                return result.Date;
            return null;
        }

        /// <summary>
        /// Parses an API date bound without converting an ISO timestamp to the
        /// Windows machine's local timezone. Historical jobs send UTC ISO
        /// timestamps, but the bound represents a Sage calendar date; converting
        /// midnight UTC to Eastern time would incorrectly move it to the prior day.
        /// </summary>
        internal static DateTime? ParseDateBoundOrNull(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            string trimmed = value.Trim();
            if (DateTime.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime dateOnly))
                return dateOnly.Date;

            if (trimmed.Length > 10 && trimmed[10] == 'T' &&
                DateTime.TryParseExact(trimmed.Substring(0, 10), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out dateOnly) &&
                DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _))
                return dateOnly.Date;

            return null;
        }

        /// <summary>
        /// Strict parser for the IncludeInGL flag. Accepts only true/false,
        /// yes/no, and 1/0 (case-insensitive). Any other value throws
        /// InvalidDataException so a corrupt export does not silently drop or
        /// include a real posting row.
        /// </summary>
        internal static bool ParseIncludeInGL(string value, int rowNumber)
        {
            string v = value.Trim();
            if (v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                v == "1")
                return true;
            if (v.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                v == "0")
                return false;
            throw new InvalidDataException(
                $"GL row {rowNumber}: IncludeInGL '{value}' is not a recognised " +
                "boolean (expected true/false, yes/no, or 1/0). The connector " +
                "will not guess because a wrong value would silently drop or " +
                "include a real posting row.");
        }

        private static object InvokeMethod(Type type, object target, string name, object[] args = null)
        {
            return type.InvokeMember(name, BindingFlags.InvokeMethod, null, target, args ?? new object[0]);
        }

        private static object GetProperty(Type type, object target, string name)
        {
            return type.InvokeMember(name, BindingFlags.GetProperty, null, target, null);
        }

        private static void SafeReleaseComObject(ref object obj)
        {
            if (obj != null && Marshal.IsComObject(obj))
            {
                try { Marshal.FinalReleaseComObject(obj); } catch { }
            }
            obj = null;
        }

        private struct ComCredential
        {
            public string UserName;
            public string Password;
        }
    }
}
