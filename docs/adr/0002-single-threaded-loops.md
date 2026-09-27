# ADR 0002: Single-threaded loops fed by channels, no locks on the hot path

Date: 2026-09-27 · Status: accepted

## Context

A FIX session, an order book and the OMS each have state that must change atomically and in order: sequence numbers,
queued out-of-order messages, price levels, order state. Locking that state from many threads invites deadlocks and
makes ordering hard to reason about.

## Decision

Each stateful component owns its state and runs one loop that reads a `System.Threading.Channels` channel:

| Loop | Owns | Fed by |
|---|---|---|
| `FixSession` (one per session) | sequence numbers, resend queue, heartbeat timers | transport reads, sends from the OMS, timer ticks, admin commands |
| `OrderManager` (one) | every client order, ClOrdID index, throttles | NewOrder/Cancel/Replace requests, venue events |
| `VenueShard` (one per underlying) | that underlying's books, market-maker quotes, price process | orders from the OMS, timer ticks |

Other threads talk to a loop only by posting a message. Reads for REST go through `InvokeAsync` (a message that
returns a result) or through immutable snapshots (`OrderView`, `QuoteSnapshot`, the `MarketDataCache`).

## Consequences

- No locks in the matching, OMS or session code; ordering is the channel's FIFO order.
- A slow consumer can't block a producer; channels are unbounded where losing data is unacceptable (orders) and
  bounded with drop-oldest where it's fine (UI fan-out).
- Latency includes one or two channel hops (a few microseconds). The p50 order→ack figure in the README includes them.
- Tests drive the loops deterministically with `FakeTimeProvider` and "flush" messages.
