# Contributing

## Run it locally

Requirements: .NET 10 SDK, Node 22+, Python 3.12+, Docker (for MySQL), PowerShell 7 (optional).
`scripts/preflight.sh` checks all of them.

```bash
cp .env.example .env
docker compose up -d --build          # MySQL + API: REST/SignalR on :8080, FIX on :9878
cd web && npm install && npm run dev  # http://localhost:5173 (proxies /api, /hubs, /fix to :8080)
```

Without Docker, run the API with in-memory persistence (nothing survives a restart):

```bash
dotnet run --project src/Tickwire.Api    # http://localhost:8080/swagger
```

## Test

```bash
dotnet build -warnaserror && dotnet test          # unit, property, QuickFIX/n interop, in-process API
cd web && npm run lint && npm run typecheck && npm test
scripts/smoke.sh http://localhost:8080 localhost:9878
cd web && npx playwright test --grep @local        # needs the API running
Invoke-Pester ./tools/powershell                   # needs the API running
```

The MySQL persistence test uses Testcontainers; without Docker, point `TICKWIRE_TEST_MYSQL` at any MySQL server.

## Conventions

- Conventional commits (`feat(session): ...`, `fix(oms): ...`, `docs: ...`).
- One PR per issue; CI must be green (Ubuntu + Windows, web, E2E, secret scan).
- Warnings are errors. Nullable reference types everywhere.
- Protocol-visible changes (new tags, reject reasons, session behaviour) update `docs/fix-spec.md`.
- Decisions the plan didn't settle go in `docs/DECISIONS.md` (one line) or an ADR in `docs/adr/`.
- Log Analyzer rules exist in C# and TypeScript; both are pinned by `samples/expected.json`. Change both, or neither.
- Regenerate the browser FIX dictionary with `npm run gen:dictionary` if `FIX44.xml` changes, and the sample logs with
  `python3 scripts/gen-sample-logs.py` (then copy them to `web/public/samples/`).
