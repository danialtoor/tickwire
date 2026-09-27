# ADR 0004: MySQL session store, written behind the session loop

Date: 2026-09-27 · Status: accepted

## Context

A FIX session must survive restarts: sequence numbers and sent messages (for resends) have to be durable.
QuickFIX's default is a file store. Tickwire runs on Fly.io with MySQL already present for configuration, and the
support tooling wants every message queryable.

## Decision

Store `session_state` (next sender/target seq) and `session_messages` in MySQL via Dapper. The session loop writes
to an in-memory store; a background loop applies batches (coalescing sequence updates) every few milliseconds.
The last 20,000 outbound messages per session load into memory at startup. A separate journal archives every inbound
and outbound message, orders and executions for audit and the analyzer.

## Consequences

- The session loop never waits on the database, so a slow write can't delay heartbeats or acks.
- A crash can lose the last few milliseconds of sequence updates. Standard recovery covers it: if we come back
  behind, the counterparty's messages look too high and we send a ResendRequest; if we come back ahead, our resends
  are PossDup and the counterparty ignores what it has already processed.
- Messages older than the in-memory window are answered with a GapFill on resend.
- Testcontainers-backed integration tests (and the CI docker-compose job) restart the API and check sequence numbers
  continue.
