using System;
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
        private int _recordNumber;
        private int _lineNumber = 1;
        private readonly StringBuilder _lineSoFar = new StringBuilder();

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

            _recordNumber++;
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
                    // Sage pads some values with spaces before the opening
                    // quote, mirroring the padding it puts after the closing
                    // quote. Padding-only content means this quote opens the
                    // field; drop the padding.
                    if (field.Length > 0 && IsPadding(field))
                    {
                        field.Clear();
                    }
                    else if (field.Length > 0)
                    {
                        // A quote inside an unquoted value is a literal (an inch
                        // mark: 12" PVC). Sage only quotes values containing a
                        // comma. Commas still delimit, so columns cannot shift.
                        field.Append('"');
                        continue;
                    }
                    // Quoted field — read until closing quote, handling doubled
                    // quotes as escaped quotes and embedded newlines literally.
                    int quoteOpenedOnLine = _lineNumber;
                    while (true)
                    {
                        c = Read();
                        if (c < 0)
                        {
                            throw Fail(record.Count,
                                $"unterminated quoted field opened on line {quoteOpenedOnLine} (end of file reached before closing quote).");
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
                        throw Fail(record.Count,
                            $"unexpected character '{Mask((char)after)}' after closing quote and optional " +
                            "padding. Expected a comma, newline, or end of file.");
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
            int c;
            if (_peekedChar != -2)
            {
                c = _peekedChar;
                _peekedChar = -2;
            }
            else
            {
                c = _reader.Read();
            }

            // Track the physical line so a parse error can say where it is.
            if (c == '\n')
            {
                _lineNumber++;
                _lineSoFar.Clear();
            }
            else if (c >= 0 && c != '\r' && _lineSoFar.Length < MaxContext)
            {
                _lineSoFar.Append((char)c);
            }
            return c;
        }

        private const int MaxContext = 240;

        private static bool IsPadding(StringBuilder field)
        {
            for (int i = 0; i < field.Length; i++)
            {
                if (field[i] != ' ' && field[i] != '\t') return false;
            }
            return true;
        }

        /// <summary>
        /// Build a parse error that pinpoints the record, physical line, column
        /// and character, plus the line's *shape*: letters become X/x and digits
        /// 9, while quotes, commas and whitespace are kept. That is enough to
        /// see the quoting pattern Sage produced without putting customer
        /// ledger text into Rutter's logs.
        /// </summary>
        private CsvParseException Fail(int column, string reason)
        {
            int position = _lineSoFar.Length; // 1-based index of the offending char
            int line = _lineNumber;
            var context = new StringBuilder(_lineSoFar.ToString());
            int c;
            while (context.Length < MaxContext && (c = Peek()) >= 0 && c != '\r' && c != '\n')
            {
                context.Append((char)Read());
            }
            return new CsvParseException(_recordNumber, line, column, position, Mask(context.ToString()), reason);
        }

        internal static string Mask(string text)
        {
            var masked = new StringBuilder(text.Length);
            foreach (char ch in text) masked.Append(Mask(ch));
            return masked.ToString();
        }

        internal static char Mask(char ch)
        {
            if (char.IsDigit(ch)) return '9';
            if (char.IsLetter(ch)) return char.IsUpper(ch) ? 'X' : 'x';
            return ch;
        }
    }

    /// <summary>
    /// A CSV structural error with its location. <see cref="Column"/> is the
    /// zero-based field index; the GL exporter maps it to the header name.
    /// </summary>
    internal sealed class CsvParseException : Exception
    {
        internal int Record { get; }
        internal int Line { get; }
        internal int Column { get; }
        internal int Position { get; }
        internal string MaskedLine { get; }
        internal string Reason { get; }

        internal CsvParseException(int record, int line, int column, int position, string maskedLine, string reason)
            : base($"CSV parse error at record {record} (line {line}), field {column + 1}, char {position}: {reason} "
                + $"Masked line: [{maskedLine}]")
        {
            Record = record;
            Line = line;
            Column = column;
            Position = position;
            MaskedLine = maskedLine;
            Reason = reason;
        }
    }
}
