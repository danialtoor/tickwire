# Decisions log

One line per choice the plan left open. Significant choices get an ADR in `docs/adr/`.

- 2026-09-27: Build and verify everything locally first; create the GitHub repo and Vercel/Fly resources at the end (the plan put hosting in M0). Keeps account setup off the critical path.
- 2026-09-27: Solution file uses the .NET 10 default `.slnx` format instead of `.sln`.
- 2026-09-27: FluentAssertions pinned to 7.2.0 (Apache-2.0). v8+ requires a commercial license.
- 2026-09-27: FIX 4.4 dictionary is the QuickFIX `FIX44.xml`, vendored with its license (`src/Tickwire.Fix/Spec/QUICKFIX-LICENSE.txt`). Only the spec file is reused; no QuickFIX code runs in production.
- 2026-09-27: Garbled messages (bad CheckSum or BodyLength) are dropped without a Reject, as FIX 4.4 vol. 2 requires. The following message then shows a sequence gap and triggers a ResendRequest. The Chaos panel's "corrupt checksum" fault demonstrates exactly that; "invalid field" faults produce Reject(3).
- 2026-09-27: Tags 5000+ (user-defined range) pass validation without a dictionary entry. Tickwire's custom tags are 20001 TheoValue and 20002 UnderlyingLastPx.
