# ADR 0001: Write the FIX engine instead of wrapping QuickFIX/n

Date: 2026-09-27 · Status: accepted

## Context

Tickwire exists to show how a FIX gateway works: framing, BodyLength/CheckSum, sequence numbers, resend and gap
fill, heartbeats, validation. QuickFIX/n is the obvious production choice in .NET and would have made the session
layer a configuration file.

## Decision

Write the codec (`Tickwire.Fix`) and the session layer (`Tickwire.Fix.Session`) from scratch. Use QuickFIX/n only
where an independent implementation adds value: as the counterparty in integration tests, and its `FIX44.xml` as the
data dictionary (vendored with its license).

## Consequences

- Every behaviour the Chaos panel demonstrates is code in this repo, readable and unit tested.
- The engine can expose things QuickFIX doesn't surface: per-message dispositions (queued, resent, garbled,
  dropped) for the Inspector, fault injection on the transport, and write-behind persistence.
- Risk of subtle protocol bugs is real. Mitigations: FsCheck round-trip and corruption properties on the codec,
  deterministic session tests on a fake clock, and CI interop tests where QuickFIX/n logs on, trades, forces gaps in
  both directions and logs out against the Tickwire acceptor.
- Out of scope: FIXT 1.1 / FIX 5.0 SP2, encryption (98 != 0 is refused), repeating-group validation beyond "may repeat".
