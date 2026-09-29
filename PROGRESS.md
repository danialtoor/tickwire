# Progress

Status log for the build. Newest first.

## 2026-09-29: published; API deploy blocked on Fly billing

| Resource | Name / URL | Status |
|---|---|---|
| GitHub repo | https://github.com/danialtoor/tickwire | public, CI green (Ubuntu, Windows, web, compose E2E, Pester, gitleaks), `main` protected |
| Vercel | https://tickwire-fix.vercel.app (also tickwire-ten.vercel.app) | live, deploys on every push; runs in Replay mode until the API is up |
| Fly API app | `tickwire-api` | created, secrets staged, **no release**: Fly requires a card (trial org) |
| Fly DB app | `tickwire-db` | created, 1 GB volume `mysqldata`, secret staged, **no release** (same reason) |
| GitHub secrets | `FLY_API_TOKEN`, `TICKWIRE_ADMIN_KEY` | set |
| Project board | GitHub Projects "Tickwire", milestones M0–M7, issues #7–#9 | |

`tickwire.vercel.app` belongs to someone else, so the site uses `tickwire-fix.vercel.app`.

Next (issue #7): add a card at https://fly.io/dashboard/danial-toor/billing, then `fly deploy` the DB and API,
`fly ips allocate-v4 -a tickwire-api`, production smoke and Playwright @prod, then tag v1.0.0.

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

### Known issues / next

- Docker isn't installed on the build machine, so the image and `docker compose` path are verified only in CI.
- PowerShell module is exercised by Pester in CI (no `pwsh` locally).
- Stretch goals not started: multileg (AB), smart order router, drop copy, FIX 5.0 SP2, source-generated messages,
  Windows Service hosting.
