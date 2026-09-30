# Changelog

## Unreleased

- **Positions and portfolio risk**: average-cost positions from outright and spread leg fills, realized and
  unrealized P&L marked at mid, portfolio delta/gamma/vega/theta, cash settlement at intrinsic on expiry.
  New pre-trade delta and vega limits (orders that reduce exposure always pass). Positions tab in the Trader,
  `GET /api/positions`, and `-MaxAbsDelta`/`-MaxAbsVega` on `Set-RiskLimit`.

## 1.1.0 (2026-09-30)

- **Multi-leg spreads**: NewOrderMultileg (35=AB) with 2–4 legs, net limit price, all-or-none leg execution against the
  outright books, resting spreads re-checked on every tick, strategy (442=3) and leg (442=2) ExecutionReports,
  spread risk checks. Spread ticket in the Trader with vertical, straddle, strangle and butterfly templates.
  QuickFIX/n interop test sends a real NewOrderMultileg.
- **Live options data**: bring your own key for SpiderRock, Databento, Polygon.io, Tradier or Alpaca, plus a demo feed;
  quotes show beside the simulated chain for the visitor who connected them.
- **Market realism**: mean-reverting underlyings and a daily expiry roll (resting orders expire with ExecType=C).
- Horizontal expiry bar with days to expiry; Log Analyzer understands multi-leg logs.

## 1.0.0 (2026-09-27)

First release.

- **FIX codec**: hand-written FIX 4.4 parser, serializer and stream framer over System.IO.Pipelines; BodyLength and
  CheckSum verification; FIX 4.4 data dictionary with session-level validation; FsCheck round-trip and corruption
  properties.
- **Session layer**: logon/logout, heartbeats and TestRequest, gap detection with ResendRequest, PossDup resends and
  GapFill, sequence-too-low handling, persistent sequence numbers (write-behind MySQL store), TCP / WebSocket /
  in-memory transports, fault injection. Interop-tested against QuickFIX/n.
- **OMS**: explicit order state machine, cancel/replace chains, pre-trade risk (size, notional, price band vs theo,
  open orders, allowed products, throttle), per-client and global kill switch, cancel-on-disconnect.
- **Simulated venue**: price-time priority books for SPY, AAPL, TSLA and NVDA option chains, Black-Scholes theo on a
  skewed surface, market makers, noise traders.
- **Web**: trader (chain, ticket, book, blotter), FIX Inspector with venue/client views, Chaos panel, Connect-your-own
  FIX client onboarding, Log Analyzer, Ops dashboard, Replay mode when the API is unreachable.
- **Tooling**: FIX Log Analyzer (C# library, `tickwire` CLI, API, TypeScript port), PowerShell `Tickwire.Admin`
  module, Python client, smoke test and load generator.
- **Delivery**: Docker image, docker compose, Fly.io and Vercel configuration, GitHub Actions CI (Ubuntu + Windows),
  deploy, 30-minute production smoke and nightly reset.
