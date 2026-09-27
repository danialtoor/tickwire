# Changelog

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
