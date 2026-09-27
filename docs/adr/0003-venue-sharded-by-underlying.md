# ADR 0003: Shard the simulated venue by underlying

Date: 2026-09-27 · Status: accepted

## Context

About 540 option books need repricing on every underlying tick, and market makers requote all of them. A loop per
book would mean hundreds of loops that all need the same underlying price; one loop for everything serializes
unrelated names.

## Decision

One `VenueShard` per underlying owns the price process, all of that underlying's books and the market makers
quoting them. A tick reprices every option on the shard (Black-Scholes on a skewed surface), then makers pull and
re-post quotes. Client orders are routed to the shard of the contract's underlying.

## Consequences

- Repricing and requoting for an underlying is one atomic step, so makers never quote against a stale theo.
- Makers cancel all their quotes on a contract before re-posting, so they never trade with each other.
- A requote that crosses a resting client order trades with it, which is how resting orders fill as the market moves.
- The venue tracks each client order's filled quantity, so a replace sized against stale OMS state can't overfill.
