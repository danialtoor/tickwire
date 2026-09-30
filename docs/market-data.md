# External options market data

Visitors can connect their own account with an options data provider on the **Market data** page (`/data`).
The provider's quotes appear in a **Live** column next to the simulated quotes in the Trader's chain, matched by
OCC symbol. Orders still trade in the simulated venue.

## Providers

| Provider | Adapter | Transport | What it streams | Credentials |
|---|---|---|---|---|
| SpiderRock | `SpiderRockProvider` | MLink JSON over WebSocket | OptionNbboQuote, LiveImpliedQuote (IV, delta), StockBookQuote | MLink API key, environment (live/delayed) |
| Databento | `DatabentoProvider` | Raw TCP gateway, CRAM auth, JSON records | OPRA.PILLAR `cbbo-1s` / `cmbp-1` / `tcbbo` by parent symbol (`SPY.OPT`) | API key, schema |
| Polygon.io | `PolygonProvider` | WebSocket JSON | `Q.` quotes and `T.` trades per contract | API key, feed (realtime/delayed) |
| Tradier | `TradierProvider` | HTTPS session + WebSocket JSON | quotes and trades, underlying trades | brokerage access token |
| Alpaca | `AlpacaProvider` | WebSocket MessagePack | quotes and trades (indicative or OPRA feed) | key ID, secret, feed |
| Demo feed | `DemoProvider` | in-process | jittered quotes around the simulated theo | none |

All adapters implement `IOptionFeedProvider` in `src/Tickwire.MarketData`: connect, authenticate, subscribe to the
listed contracts, and translate vendor messages into `ExternalQuote` / `ExternalUnderlying`.

**Status: unverified.** The five real adapters were written from each vendor's public protocol documentation and are
unit-tested against sample messages in that format (`tests/Tickwire.MarketData.Tests`), including symbology and the
Databento CRAM hash. They have not been run against paid accounts. Before relying on one, connect with a real key and
compare against the vendor's own client; field names can differ between API versions (the SpiderRock parser already
accepts several variants).

## Credentials and data handling

- Credentials are posted to `POST /api/feeds/connect`, kept only inside the running feed task, and never written to
  the database or logs. Error messages shown back to the user have credential values scrubbed.
- One feed per visitor, at most 25 concurrent feeds, each stopped after two hours or on disconnect, guest expiry, or
  server restart.
- Quotes go only to the visitor who connected them (SignalR group `feed:{clientId}`): most market data licences allow
  display to the subscriber only.
- Provider names are shown as text wordmarks; no vendor logo files are bundled, and the page states there's no
  affiliation.

## Adding a provider

1. Implement `IOptionFeedProvider` (see `PolygonProvider` for the smallest example) and return a `ProviderInfo`
   listing the credential fields.
2. Register it in `src/Tickwire.Api/Program.cs`.
3. Add parsing tests with sample messages, and a wordmark style in `web/src/components/ProviderMark.tsx`.
