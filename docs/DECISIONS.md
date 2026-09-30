# Decisions log

One line per choice the plan left open. Significant choices get an ADR in `docs/adr/`.

- 2026-09-27: Build and verify everything locally first; create the GitHub repo and Vercel/Fly resources at the end (the plan put hosting in M0). Keeps account setup off the critical path.
- 2026-09-27: Solution file uses the .NET 10 default `.slnx` format instead of `.sln`.
- 2026-09-27: FluentAssertions pinned to 7.2.0 (Apache-2.0). v8+ requires a commercial license.
- 2026-09-27: FIX 4.4 dictionary is the QuickFIX `FIX44.xml`, vendored with its license (`src/Tickwire.Fix/Spec/QUICKFIX-LICENSE.txt`). Only the spec file is reused; no QuickFIX code runs in production.
- 2026-09-27: Garbled messages (bad CheckSum or BodyLength) are dropped without a Reject, as FIX 4.4 vol. 2 requires. The following message then shows a sequence gap and triggers a ResendRequest. The Chaos panel's "corrupt checksum" fault demonstrates exactly that; "invalid field" faults produce Reject(3).
- 2026-09-27: Tags 5000+ (user-defined range) pass validation without a dictionary entry. Tickwire's custom tags are 20001 TheoValue and 20002 UnderlyingLastPx.
- 2026-09-27: MySQL provider is Pomelo 9.0 (EF Core 9), the newest stable release; it runs on .NET 10. EF handles config CRUD, Dapper the hot-path writes.
- 2026-09-27: The browser trader's FIX client runs server-side as a real initiator session, connected to the acceptor through an in-memory pipe via the same accept path TCP uses. The browser sends REST commands; every order still produces genuine FIX on both sides. A browser-side FIX engine would duplicate the session layer in TypeScript for no gain. A raw FIX-over-WebSocket endpoint (`/fix/ws`) is still available for external clients.
- 2026-09-27: One SignalR hub (`/hubs/live`) with topic groups (chain, book, orders, fix session, ops) instead of four hubs: one WebSocket per browser tab.
- 2026-09-27: Session store is write-behind (in-memory authoritative, batched MySQL writes). See ADR 0004.
- 2026-09-27: Order numbers start from seconds-since-2025 × 1000 and ExecIDs carry a boot tag, so neither repeats across restarts.
- 2026-09-27: Guests manage only their own client (limits, kill switch, sessions, chaos). Admin endpoints (`X-Admin-Key`) cover global kill switch, audit log and the nightly reset.
- 2026-09-27: Garbled first messages and unknown CompIDs get a best-effort Logout with a reason before disconnect, instead of a silent close; silent disconnects are the most common onboarding support ticket.
- 2026-09-29: In production the browser calls the API host directly (CORS) instead of through the Vercel /api rewrite, which returned intermittent 502s (DNS_HOSTNAME_EMPTY) right after the Fly IPs were allocated. The rewrite stays as a fallback. Safe requests (GETs, guest creation) retry on 502/503/504.
- 2026-09-29: External data providers are shown as text wordmarks, not logo files, and SpiderRock is listed by name at the owner's request (overriding the original no-SpiderRock-branding rule). Live quotes display only to the visitor who connected them. See docs/market-data.md.
- 2026-09-30: Underlyings mean-revert to their starting price (Ornstein-Uhlenbeck on log price, kappa 12/yr) and a roller delists passed expiries (resting orders get ExecType=Expired) and lists the next Friday with strikes centred on the current price, so the demo stays realistic when the server runs for weeks.
- 2026-09-30: Spreads (35=AB) execute against the outright books all-or-none per unit (leg-ratio matching at top of book) rather than in a separate complex order book; spread orders don't show in outright depth. Replace isn't supported for spreads (cancel and resend).

## Positions are rebuilt in memory, not stored

The PositionKeeper is an OMS listener that updates each client's positions on the same loop that emits fills, so
the Positions tab and the delta/vega checks never disagree with the blotter. Positions aren't persisted: a restart
starts everyone flat. Orders and executions are still in MySQL, so a replay on startup would be straightforward,
but the demo resets guests daily anyway and it kept the risk path free of database reads.
