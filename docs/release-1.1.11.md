# Sage 50 Connector 1.1.11

Fix General Ledger (TRANSACTIONS) CSV parsing for real company files. The
parser now treats a double quote inside an unquoted value as a literal
character (an inch mark, `12" PVC`) and accepts padding before an opening
quote. Previously either pattern failed the whole export with "a double-quote
appeared inside a non-empty unquoted field", which blocked every GL sync for
FatPipe (DualEntry).

Remaining structural CSV errors now name the record, physical line, field,
column header and character, plus a masked copy of the line (letters X/x,
digits 9, punctuation kept) so a future failure is diagnosable without
customer ledger text. GL failures also log the full exception locally.

Verification: `diagnostics/CsvParserTests.cs` passes on the lab VM; old and new
parsers produce identical records on seven native Sage GL exports
(35,802–286,409 rows, 14 columns each).

Customers must reopen their company as a Sage administrator and approve
Always Allow Access for this version after upgrading, and approve the
transaction (COM) prompt again.
