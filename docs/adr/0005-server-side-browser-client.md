# ADR 0005: The browser trader's FIX client runs on the server

Date: 2026-09-27 · Status: accepted

## Context

The Trader page should produce real FIX traffic so the Inspector shows genuine wire messages. Options: implement a
FIX session in TypeScript in the browser (over WebSocket), or run a FIX initiator per guest on the server.

## Decision

Each guest gets a server-side `FixSession` in the initiator role (`GuestTrader`), connected to the acceptor through
an in-memory pipe that goes through the same `FixAcceptor.AcceptAsync` path TCP connections use. The browser sends
REST commands; the initiator turns them into NewOrderSingle / OrderCancelRequest / OrderCancelReplaceRequest.

## Consequences

- Both ends of every guest session are the same audited engine, so chaos faults can be injected on either side and
  the Inspector can show the venue view, the client view, or both.
- No second session implementation in TypeScript to keep in sync.
- The client rebuilds its own order state from the ExecutionReports it receives, and the blotter shows whether that
  view matches the OMS, which demonstrates reconciliation after a gap.
- External clients still have raw TCP (9878) and FIX-over-WebSocket (`/fix/ws`).
