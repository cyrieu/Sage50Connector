# Sage 50 Connector 1.1.10

Fetch invoices through bounded SDK date windows and page-sized mapping rather
than retaining the complete hydrated invoice collection. Preserve invoice
payloads, modification filters and all four line families. Retain pending
reports for retry, invalidate readers when Sage disconnects, and request
restart-from-beginning when a saved cursor no longer has a live read session.

Live owned-lab verification: complete invoice payload parity, 652 invoices /
8,149 lines persisted and normalized; ten new invoices synchronized incrementally;
future modification cutoff returned zero changes. Synthetic paging and transport
checks cover dense-day splitting, fallback keys, pending-page replay, failed HTTP
acknowledgements and lost-reader restart. Large-scale invoice validation was
cancelled; GUID inventories/reference indexes and unusually large invoices still
consume memory. Forced-kill Sage SDK license-seat recovery remains a known limit.

COM G/L behavior is unchanged. Its memory optimization is deferred in FND-3841.

Customers must reopen their company as a Sage administrator and approve
Always Allow Access for this version after upgrading.
