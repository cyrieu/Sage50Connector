// No Sage access. Compile standalone with the parser:
//   csc /out:CsvParserTests.exe Helpers\Rfc4180CsvParser.cs diagnostics\CsvParserTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using Sage50Connector.Helpers;

namespace Sage50Connector.Diagnostics
{
    internal static class CsvParserTests
    {
        private static void Assert(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }

        private static List<string[]> Parse(string csv)
        {
            var parser = new Rfc4180CsvParser(new StringReader(csv));
            var rows = new List<string[]>();
            while (parser.ReadRecord(out string[] fields)) rows.Add(fields);
            return rows;
        }

        private static string ParseError(string csv)
        {
            try { Parse(csv); return null; }
            catch (CsvParseException ex) { return ex.Message; }
        }

        public static int Main()
        {
            var rows = Parse("a,b,c\r\n1,12\" PVC elbow,3\r\n");
            Assert(rows[1].Length == 3 && rows[1][1] == "12\" PVC elbow", "inch mark in unquoted field is literal");

            rows = Parse("a,b,c\r\n1,  \"Pipe, 12 in\"  ,3\r\n");
            Assert(rows[1].Length == 3 && rows[1][1] == "Pipe, 12 in", "padding before opening quote is dropped");

            rows = Parse("a,b,c\r\n1,\"Ficus Tree 22\" - 26\"\",825.00\r\n");
            Assert(rows[1].Length == 3 && rows[1][1] == "Ficus Tree 22\" - 26\"", "Sage terminal inch-mark form still parses");

            rows = Parse("a,b\r\n\"line one\r\nline two\",2\r\n");
            Assert(rows.Count == 2 && rows[1][0] == "line one\r\nline two", "embedded newline in quoted field");

            rows = Parse("a,b\r\n\"say \"\"hi\"\"\",2\r\n");
            Assert(rows[1][0] == "say \"hi\"", "RFC doubled quotes");

            string error = ParseError("a,b\r\n1,\"never closed\r\nAcme Corp 42");
            Assert(error != null && error.Contains("opened on line 2"), "unterminated quote names its opening line: " + error);
            Assert(error.Contains("record 2 (line 3), field 2") && error.Contains("[Xxxx Xxxx 99]"), "error located and context masked (letters X/x, digits 9)");
            Assert(!error.Contains("Acme"), "no customer text in error");

            Console.WriteLine("CSV PARSER TESTS PASSED");
            return 0;
        }
    }
}
