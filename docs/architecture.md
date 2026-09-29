# Architecture

## Components

```mermaid
flowchart LR
  subgraph Browser
    UI[React trader<br/>Inspector · Chaos · Ops · Analyzer]
  end
  subgraph Vercel
    Static[Static site<br/>+ /api rewrite]
  end
  subgraph Fly["Fly.io: tickwire-api (one process)"]
    REST[REST + Swagger]
    Hub[SignalR /hubs/live]
    GT[GuestTrader<br/>FIX initiator per guest]
    Acc[FixAcceptor<br/>TCP 9878 · /fix/ws · in-memory]
    subgraph Sessions[FIX sessions]
      S1[FixSession loop]
      S2[FixSession loop]
    end
    GW[FixOrderGateway<br/>FIX ↔ OMS]
    OMS[OrderManager loop<br/>state machine · risk]
    subgraph Venue[Simulated venue]
      SPY[Shard SPY<br/>books · makers · GBM]
      AAPL[Shard AAPL]
      TSLA[Shard TSLA]
      NVDA[Shard NVDA]
    end
    MD[(MarketDataCache)]
    Tap[WireTap]
  end
  DB[(MySQL 8<br/>tickwire-db)]
  Ext[Your FIX engine<br/>QuickFIX/n · QuickFIX/J · simplefix]

  UI -- REST --> Static --> REST
  UI -- WebSocket --> Hub
  REST --> GT -- FIX over pipe --> Acc
  Ext -- FIX over TCP --> Acc
  Acc --> S1 & S2
  S1 & S2 <--> GW <--> OMS <--> Venue
  Venue --> MD --> Hub
  S1 & S2 --> Tap --> Hub
  S1 & S2 -. write-behind .-> DB
  OMS -. journal .-> DB
  REST -. config .-> DB
```

## One order, end to end

```mermaid
sequenceDiagram
  participant B as Browser
  participant C as GuestTrader (initiator)
  participant V as FixSession (acceptor)
  participant G as FixOrderGateway
  participant O as OrderManager
  participant S as VenueShard
  B->>C: POST /api/orders
  C->>V: 35=D (seq n)
  V->>V: sequence check, dictionary validation
  V->>G: OnMessageAsync
  G->>O: NewOrderRequest (channel)
  O->>O: pre-trade risk, create order (PendingNew)
  O-->>G: ER 150=A
  O->>S: Submit (channel)
  S->>S: match against book
  S-->>O: OrderSubmitted (fills, resting qty)
  O-->>G: ER 150=0, then 150=F per fill
  G->>V: Send (stamped, stored, transmitted)
  V->>C: 35=8
  Note over V,B: WireTap streams every message to the Inspector
  S-->>O: PassiveFill later, as makers requote through the price
```

## Threading model

Stateful parts each run a single loop over a channel; nothing on the hot path takes a lock
([ADR 0002](adr/0002-single-threaded-loops.md)).

- **FixSession**, one per session: inbound bytes (from a per-connection read task), outbound sends, a 250 ms timer
  tick, admin commands. Owns sequence numbers, the out-of-order queue and heartbeat state.
- **OrderManager**, one: client requests and venue events share one channel, so a client always sees its reports
  in a consistent order.
- **VenueShard**, one per underlying ([ADR 0003](adr/0003-venue-sharded-by-underlying.md)): order submissions and the
  250 ms market tick (GBM step, Black-Scholes reprice, maker requote, noise trades).
- **Background writers**: the session store and the order/wire journal batch MySQL writes off the hot path
  ([ADR 0004](adr/0004-write-behind-session-store.md)).
- **LivePublisher** pushes to SignalR: session events as they happen, market snapshots at 4 Hz when data changed,
  ops metrics at 1 Hz.

Readers on other threads use immutable snapshots (`OrderView`, `QuoteSnapshot`) or ask the loop with an
`InvokeAsync` message.

## Projects

| Project | Depends on | What |
|---|---|---|
| `Tickwire.Fix` | | tags, parser, builder, framer, checksum, FIX 4.4 data dictionary |
| `Tickwire.Fix.Session` | Fix | session state machine, stores, transports (TCP, pipes, fault injection), acceptor/initiator |
| `Tickwire.Pricing` | | Black-Scholes and greeks, implied vol, OCC symbology, vol surface, GBM |
| `Tickwire.Venue` | Pricing | order books, market-making shards, market data cache |
| `Tickwire.Engine` | Fix.Session, Venue | OMS, order state machine, pre-trade risk, FIX gateway, metrics |
| `Tickwire.Persistence` | Engine | EF Core model and migrations, Dapper session store and journal |
| `Tickwire.LogAnalyzer` | Fix | log parsing, lifecycle reconstruction, diagnostics |
| `Tickwire.Api` | all | ASP.NET Core host: REST, SignalR, guests, WebSocket FIX, hosted services |
| `Tickwire.Cli` | LogAnalyzer | `tickwire` dotnet tool |

## Deployment

```mermaid
flowchart LR
  V[Vercel<br/>tickwire-fix.vercel.app] -- "/api/* rewrite" --> A
  Br[Browser] --> V
  Br -- "wss /hubs/live" --> A
  Q[FIX clients] -- "TCP 9878 (dedicated IPv4)" --> A
  A[Fly app tickwire-api<br/>1 machine, always on] -- "tickwire-db.internal:3306" --> D[Fly app tickwire-db<br/>MySQL 8 + volume]
  GH[GitHub Actions] -- "flyctl deploy" --> A
  GH -- "smoke every 30 min" --> A
```

The frontend is static on Vercel. The backend must be a long-running process (matching engine, market makers, raw
TCP listener, WebSockets), which Vercel's serverless model can't host, so it runs on Fly.io. SignalR connects to
the API host directly because Vercel rewrites don't proxy WebSockets; CORS allows the Vercel production and preview
domains.
