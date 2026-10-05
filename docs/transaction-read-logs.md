# Diagnosing slow transaction reads

Transaction timing logs cover journal entries, invoices, bills, expenses and
invoice payments. They do not change which records are loaded, filtered or
uploaded. Get the connector log from the customer's currently running build;
installing a changed executable requires the customer's normal Sage approval.

Each read logs:

- Read start, each reference lookup's `Load()` start/end, and enumeration row
  counts (accounts, customers, vendors and inventory items where applicable).
- Transaction `Load()` start/end, first record, and every 250 records reached.
- A heartbeat every minute, with current phase, record ordinal, phase elapsed
  time and total read elapsed time. The phases distinguish lookup loads,
  lookup enumeration, transaction loading, transaction enumeration, header
  reading/mapping and each line collection.
- A slow-phase message when a phase finishes after at least five seconds.
- Completion counts and duration, or the last phase if the read exits early.

For example, `BILLS: still reading; phase=lines ApplyToPurchasesLines; record=17`
means it is still processing the seventeenth bill's purchase lines. A heartbeat
in `transaction Load()` means the factory load has not returned. Long lookup
enumeration identifies reference-index work separately from transaction reads.
Record ordinals are local to this read; no transaction IDs, notes, amounts or
line contents are added to these diagnostic messages.

The heartbeat only reads local timing state. It does not call Sage from the
timer thread, and it is disposed on success or exception. Diagnostic write
failures do not fail the read. Progress is records reached, not necessarily
records fully mapped; the final `scanned`/`returned` counts summarize completion.
