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
                                field.Append('"');
                                Read(); // consume the doubled quote
                            }
                            else
                            {
                                // End of quoted section.
                                break;
                            }
                        }
                        else
                        {
                            field.Append((char)c);
                        }
                    }
                    // After a closing quote, the next character must be a comma,
                    // newline, or end of file. Any other character is a
                    // structural error that would corrupt the next field.
                    int after = Peek();
                    if (after >= 0 && after != ',' && after != '\r' && after != '\n')
                    {
                        throw new InvalidDataException(
                            $"CSV parse error: unexpected character '{(char)after}' " +
                            "after closing quote. Expected a comma, newline, or end " +
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
