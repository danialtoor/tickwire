# Progress

Status log for the build. Newest first.

## 2026-09-27: M0–M7 built and verified locally; publishing pending

| Milestone | Status | Evidence |
|---|---|---|
| M0 Scaffold | done (repo not yet public) | solution, build props, ADRs, CI workflows |
| M1 Codec | done | 40 tests incl. FsCheck properties; BenchmarkDotNet baseline |
| M2 Session | done | 14 deterministic session tests; QuickFIX/n interop over TCP (logon, order, gaps both ways, logout) |
| M3 Engine + Venue | done | 41 tests: book, state machine, OMS lifecycle, risk, pricing vs Hull, put-call parity |
| M4 API + Persistence | done | EF migrations, Dapper store, SignalR, guests, WebSocket FIX; 13 integration tests incl. MySQL restart |
| M5 Web UI | done | trader, Inspector, ops, onboarding; Playwright |
| M6 Differentiators | done | chaos panel, analyzer (C# + TS parity on 5 samples), CLI, PowerShell module, Python client + load generator |
| M7 Ship | local assets done | Dockerfile, compose, Fly/Vercel config, workflows, docs, replay; deploy needs accounts |

### Resource names

| Resource | Name | Status |
|---|---|---|
| GitHub repo | `<owner>/tickwire` | not created (needs `gh auth login`) |
| Vercel project | `tickwire` | not created (needs `vercel login`) |
| Fly API app | `tickwire-api` | not created (needs `fly auth login` + card) |
| Fly DB app | `tickwire-db` | not created |

### Known issues / next

- Docker isn't installed on the build machine, so the image and `docker compose` path are verified only in CI.
- PowerShell module is exercised by Pester in CI (no `pwsh` locally).
- Stretch goals not started: multileg (AB), smart order router, drop copy, FIX 5.0 SP2, source-generated messages,
  Windows Service hosting.
