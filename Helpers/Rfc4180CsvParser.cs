using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Sage50Connector.Helpers
{
    /// <summary>
    /// Minimal RFC 4180 CSV reader that handles quoted fields containing
    /// embedded newlines and doubled double-quotes. Reads from a TextReader
    /// one character at a time so multi-line quoted descriptions are not
    /// split across physical line breaks.
    ///
    /// No external package dependency — works on .NET Framework 4.8.
    /// </summary>
    internal sealed class Rfc4180CsvParser
    {
        private readonly TextReader _reader;
        private int _peekedChar = -2;

        internal Rfc4180CsvParser(TextReader reader)
        {
            _reader = reader;
        }

        /// <summary>
        /// Reads one CSV record. Returns false at end of stream.
        /// A blank trailing line (common in Sage exports) yields an empty
        /// record: a single empty-string field.
        /// </summary>
        internal bool ReadRecord(out string[] fields)
        {
            fields = null;
            int c = Peek();
            if (c < 0) return false;

            var record = new List<string>();
            var field = new StringBuilder();

            while (true)
            {
                c = Read();

                if (c < 0)
                {
                    record.Add(field.ToString());
                    fields = record.ToArray();
                    return true;
                }

                if (c == '"')
                {
                    // Per RFC 4180, a quoted field must start at the beginning
                    // of the field. A quote inside a non-empty unquoted field is
                    // a structural error, not the start of a quoted section.
                    if (field.Length > 0)
                    {
                        throw new InvalidDataException(
                            "CSV parse error: a double-quote appeared inside a " +
                            "non-empty unquoted field. Per RFC 4180, a quote is " +
                            "only valid at the start of a field. The General " +
                            "Ledger export may be corrupted.");
                    }
                    // Quoted field — read until closing quote, handling doubled
                    // quotes as escaped quotes and embedded newlines literally.
                    while (true)
                    {
                        c = Read();
                        if (c < 0)
                        {
                            throw new InvalidDataException(
                                "CSV parse error: unterminated quoted field " +
                                "(end of file reached before closing quote). " +
                                "The General Ledger export may be corrupted.");
                        }
                        if (c == '"')
                        {
                            int next = Peek();
                            if (next == '"')
                            {
                                Read(); // consume the second quote

                                // Sage has a non-RFC edge case when a quoted
                                // description itself ends with an inch mark:
                                //
                                //   "Ficus Tree 22" - 26"",,825.00
                                //
                                // The final two quotes mean one literal inch
                                // mark AND the end of the quoted field. Strict
                                // RFC 4180 would require three quotes there.
                                // Treat a doubled quote followed by optional
                                // padding and a record delimiter as Sage's
                                // combined literal-plus-terminator. A standard
                                // RFC escaped quote followed by more field data
                                // remains a literal quote.
                                int afterPair = Peek();
                                var pairPadding = new StringBuilder();
                                while (afterPair == ' ' || afterPair == '\t')
                                {
                                    pairPadding.Append((char)Read());
                                    afterPair = Peek();
                                }

                                field.Append('"');
                                if (afterPair < 0 || afterPair == ',' ||
                                    afterPair == '\r' || afterPair == '\n')
                                {
                                    break;
                                }

                                field.Append(pairPadding);
                            }
                            else
                            {
                                // Sage does not consistently double literal
                                // quotes inside quoted descriptions (for
                                // example an inch mark followed by a hyphen).
                                // A quote is terminal only when the next
                                // non-padding character is a delimiter, record
                                // ending, or EOF. Otherwise preserve it and any
                                // inspected whitespace as field data.
                                var padding = new StringBuilder();
                                while (next == ' ' || next == '\t')
                                {
                                    padding.Append((char)Read());
                                    next = Peek();
                                }

                                if (next < 0 || next == ',' || next == '\r' || next == '\n')
                                {
                                    // End of quoted section. Padding belongs
                                    // to Sage's column formatting, not the
                                    // exported field value.
                                    break;
                                }

                                field.Append('"');
                                field.Append(padding);
                            }
                        }
                        else
                        {
                            field.Append((char)c);
                        }
                    }
                    // Sage's General Ledger exporter pads some quoted values
                    // with spaces before the delimiter. This is not strict RFC
                    // 4180, but the whitespace is formatting rather than field
                    // data, so consume spaces/tabs after the closing quote.
                    int after = Peek();
                    while (after == ' ' || after == '\t')
                    {
                        Read();
                        after = Peek();
                    }

                    // The first non-padding character must still be a comma,
                    // newline, or end of file. Fail closed for any other
                    // character so malformed rows cannot shift columns.
                    if (after >= 0 && after != ',' && after != '\r' && after != '\n')
                    {
                        throw new InvalidDataException(
                            $"CSV parse error: unexpected character '{(char)after}' " +
                            "after closing quote and optional padding. Expected a comma, newline, or end " +
                            "of file. The General Ledger export may be corrupted.");
                    }
                }
                else if (c == ',')
                {
                    record.Add(field.ToString());
                    field.Clear();
                }
                else if (c == '\r')
                {
                    // Check for \r\n
                    int next = Peek();
                    if (next == '\n') Read();
                    record.Add(field.ToString());
                    fields = record.ToArray();
                    return true;
                }
                else if (c == '\n')
                {
                    record.Add(field.ToString());
                    fields = record.ToArray();
                    return true;
                }
                else
                {
                    field.Append((char)c);
                }
            }
        }

        private int Peek()
        {
            if (_peekedChar != -2) return _peekedChar;
            _peekedChar = _reader.Read();
            return _peekedChar;
        }

        private int Read()
        {
            if (_peekedChar != -2)
            {
                int c = _peekedChar;
                _peekedChar = -2;
                return c;
            }
            return _reader.Read();
        }
    }
}
