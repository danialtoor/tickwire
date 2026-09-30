# Tickwire FIX 4.4 Rules of Engagement

Version 1.0 · Simulated options venue · **Not affiliated with any trading firm; simulated markets only, no real orders or money.**

This is the document a client's FIX team reads before connecting: what we accept, what we send back, and how the
session behaves when things go wrong. Every example below is real output from the engine.

## 1. Connectivity

| | Production (demo) | Local |
|---|---|---|
| FIX over TCP | `tickwire-api.fly.dev:9878` (plain TCP, no TLS) | `localhost:9878` |
| FIX over WebSocket | `wss://tickwire-api.fly.dev/fix/ws` | `ws://localhost:8080/fix/ws` |
| Protocol | FIX 4.4 (`8=FIX.4.4`) | |
| TargetCompID (56) | `TICKWIRE` | |
| SenderCompID (49) | issued at onboarding (`BYO-XXXXXX`) | |

Credentials are provisioned on the **Connect via FIX** page, via `POST /api/connect`, or with `New-FixClient` in
the PowerShell module. They are tied to a guest account and expire after 24 hours.

WebSocket frames may carry one or more messages. Text frames may use `|` instead of SOH; replies use the same
convention as the client.

## 2. Session layer

### Logon (A)

| Tag | Field | Rules |
|---|---|---|
| 98 | EncryptMethod | must be `0` |
| 108 | HeartBtInt | 1–300 seconds; the venue adopts your value |
| 141 | ResetSeqNumFlag | `Y` resets both sides to 1 (recommended for test clients without a persistent store) |

The first message on a connection must be a Logon. A connection that sends anything else, or nothing within 10
seconds, is closed. A Logon with unknown CompIDs gets a Logout with the reason in Text(58) before the disconnect:

```
8=FIX.4.4|9=..|35=5|49=TICKWIRE|56=STRANGER|34=1|52=...|58=Unknown session STRANGER->TICKWIRE. Provision credentials on the Connect page first.|10=..|
```

Only one connection per session is allowed; a second Logon while the session is active is refused the same way.

### Sequence numbers

Sequence numbers persist across disconnects and venue restarts (MySQL). Resetting requires `141=Y` on Logon or an
operator reset.

| You send | Venue does |
|---|---|
| MsgSeqNum = expected | processes it |
| MsgSeqNum > expected | queues it and sends `ResendRequest(2)` with `7=<expected>`, `16=0`; processes queued messages in order once the gap is filled |
| MsgSeqNum < expected, `43=Y` | ignores it as a duplicate (requires OrigSendingTime(122)) |
| MsgSeqNum < expected, no `43=Y` | `Logout` with `58=MsgSeqNum too low, expecting X but received Y`, then disconnect |

When you send a ResendRequest, the venue replays application messages with `43=Y` and `122=<original SendingTime>`
and replaces administrative messages (and anything older than its 20,000-message window) with
`SequenceReset-GapFill (35=4, 123=Y, 36=<next>)`.

### Heartbeats and test requests

Send a Heartbeat(0) if you've sent nothing for HeartBtInt seconds. If the venue hears nothing for HeartBtInt × 1.2,
it sends `TestRequest(1)` with a TestReqID; answer with a Heartbeat echoing `112`. No answer within another
HeartBtInt means `Logout` ("Heartbeat timeout") and disconnect.

### Garbled messages

A message whose BodyLength(9) or CheckSum(10) is wrong is **ignored without a reply**, as FIX 4.4 requires. Your next
message will then be ahead of what the venue expects, which triggers the normal ResendRequest recovery.

### Session-level Reject (3)

Messages that parse but break the data dictionary are rejected with Reject(3). The sequence number is consumed.

| 373 | Meaning | Example cause |
|---|---|---|
| 0 | Invalid tag number | tag < 5000 that isn't in FIX 4.4 |
| 1 | Required tag missing | NewOrderSingle without Symbol(55) |
| 2 | Tag not defined for this message type | BeginSeqNo(7) on a NewOrderSingle |
| 5 | Value is incorrect | Side(54)=Z |
| 6 | Incorrect data format | Price(44)=1.2.3 |
| 9 | CompID problem | 49/56 don't match the session (followed by Logout) |
| 10 | SendingTime accuracy problem | SendingTime more than 120 s from venue time (followed by Logout) |
| 11 | Invalid MsgType | |
| 13 | Tag appears more than once | outside a repeating group |

```
35=3|45=2|371=54|372=D|373=5|58=Value 'Z' is not valid for Side(54)
```

Tags 5000 and above are user-defined and accepted without a dictionary entry.

## 3. Application messages

### Supported inbound

| MsgType | Name | Notes |
|---|---|---|
| D | NewOrderSingle | |
| F | OrderCancelRequest | OrigClOrdID(41) = the order's current ClOrdID |
| G | OrderCancelReplaceRequest | price and/or quantity; OrigClOrdID(41) = current ClOrdID |
| H | OrderStatusRequest | answered with ExecType=I |
| AB | NewOrderMultileg | 2 to 4 legs on one underlying, net limit price (see below) |

Any other application MsgType gets `BusinessMessageReject(j)` with `380=3` (unsupported message type).

### Instrument identification

Either put the OCC symbol in Symbol(55):

```
55=SPY   261002C00560000        (21 characters: root padded to 6, yymmdd, C/P, strike × 1000)
```

or the underlying plus option fields:

| Tag | Field | Value |
|---|---|---|
| 55 | Symbol | `SPY`, `AAPL`, `TSLA`, `NVDA` |
| 167 | SecurityType | `OPT` |
| 201 | PutOrCall | `0` put, `1` call |
| 202 | StrikePrice | e.g. `560` |
| 541 | MaturityDate | `yyyyMMdd` (the next four Fridays are listed) |

The listed chain is at `GET /api/chain/{underlying}?expiry=yyyy-MM-dd`.

### NewOrderSingle (D)

| Tag | Field | Req | Values |
|---|---|---|---|
| 11 | ClOrdID | Y | unique per session per day |
| 1 | Account | N | echoed back |
| 54 | Side | Y | `1` buy, `2` sell |
| 38 | OrderQty | Y | whole contracts |
| 40 | OrdType | Y | `1` market, `2` limit |
| 44 | Price | limit | multiple of 0.01 |
| 59 | TimeInForce | N | `0` day (default), `3` IOC, `4` FOK |
| 60 | TransactTime | Y | |
| 77 | PositionEffect | N | accepted, not used |

Market orders never rest: whatever doesn't trade immediately is canceled. IOC cancels the remainder; FOK trades in
full or not at all.

### ExecutionReport (8)

Every report carries the full order state:

| Tag | Field | |
|---|---|---|
| 37 | OrderID | `TW` + 11 digits |
| 11 / 41 | ClOrdID / OrigClOrdID | on cancel/replace reports, 11 is the request's ClOrdID and 41 the previous one |
| 17 | ExecID | unique across restarts |
| 150 | ExecType | `A` PendingNew, `0` New, `F` Trade, `6` PendingCancel, `4` Canceled, `E` PendingReplace, `5` Replaced, `8` Rejected, `I` OrderStatus |
| 39 | OrdStatus | `A`, `0`, `1` PartiallyFilled, `2` Filled, `6`, `4`, `E`, `8` |
| 32 / 31 | LastQty / LastPx | on trades |
| 14 / 151 / 6 | CumQty / LeavesQty / AvgPx | `CumQty + LeavesQty = OrderQty` while the order is live; LeavesQty = 0 once done |
| 48 / 22 | SecurityID / SecurityIDSource | OCC symbol, `8` |
| 103 | OrdRejReason | on rejects (see §4) |
| 58 | Text | reason for rejects and unsolicited cancels |
| 20001 | TheoValue | *custom*: Black-Scholes value at report time |
| 20002 | UnderlyingLastPx | *custom*: underlying price at report time |

A new order produces `PendingNew → New`, then `Trade` reports as it fills. Fills that arrive while a cancel or
replace is pending keep the pending status (39=6 or E) and the order's current ClOrdID.

### Cancel and replace

- `PendingCancel (150=6)` then `Canceled (150=4)`; `11` is the cancel's ClOrdID, `41` the order's.
- `PendingReplace (150=E)` then `Replaced (150=5)` with the new price/quantity; the order's ClOrdID becomes the
  replace's ClOrdID, and later cancels must use it as OrigClOrdID.
- Reducing quantity at the same price keeps time priority; any other change loses it.
- New OrderQty must be greater than CumQty.

OrderCancelReject(9) reasons:

| 102 | When |
|---|---|
| 0 | too late: the order is filled or canceled |
| 1 | unknown order, or OrigClOrdID isn't the order's current ClOrdID |
| 2 | risk or throttle |
| 3 | a cancel or replace is already pending, or the order isn't acknowledged yet |
| 6 | duplicate ClOrdID |
| 99 | new quantity not above CumQty |

`434` is `1` for cancel requests and `2` for replaces.

### NewOrderMultileg (AB): spreads

| Tag | Field | Rules |
|---|---|---|
| 11 | ClOrdID | |
| 54 | Side | `1` buys the strategy as the legs define it, `2` sells it |
| 55 | Symbol | the underlying |
| 38 | OrderQty | number of spread units |
| 40 | OrdType | `2` (limit) only |
| 44 | Price | net price per unit: Σ ratio × leg price, + for bought legs, − for sold legs. May be negative. |
| 59 | TimeInForce | `0` day or `3` IOC (no FOK) |
| 555 | NoLegs | 2 to 4; each leg starts with LegSymbol(600) |
| 602 | LegSecurityID | OCC symbol, **or** 611 LegMaturityDate + 612 LegStrikePrice + 608 LegCFICode `OC…`/`OP…` |
| 623 | LegRatioQty | whole number 1–10 (default 1) |
| 624 | LegSide | `1` buy, `2` sell, as the leg trades when the strategy is bought |

FIX 4.4 has no LegPutOrCall; calls and puts are distinguished by LegCFICode (`OC` / `OP`) or the OCC symbol.

Spreads trade against the outright books: whenever every leg's best price supports at least one unit at a net price
within the limit, all legs execute together (never one without the others). Unfilled units rest (Day) and are
re-checked after every market update, or are canceled (IOC).

Reports: strategy-level ExecutionReports carry `442=3` (MultiLegReportingType) and the NoLegs group; every leg trade
is a separate ExecutionReport with `442=2`, the leg's instrument, side, `32` LastQty and `31` LastPx (CumQty and
LeavesQty on leg reports are the strategy's). Cancel with OrderCancelRequest(F); replace isn't supported for spreads
(OrderCancelReject, `58=Multi-leg orders can't be replaced`).

## 4. Pre-trade risk

Every NewOrderSingle and replace is checked against the client's limits (visible and adjustable on the Ops page and
through `PUT /api/clients/{id}/limits`; changes apply to the next order). Rejects are `ExecutionReport 150=8, 39=8`
with a readable Text(58):

| Check | Guest default | 103 | Text example |
|---|---|---|---|
| Max order quantity | 100 | 3 | `OrderQty 500 exceeds max order quantity 100` |
| Max notional (price × qty × 100) | $50,000 | 3 | `Notional $62,000 exceeds max notional $50,000` |
| Price band vs theo | ±50% (min ±$0.25) | 99 | `Price 13.50 is outside the band of theo 4.31 ± 2.16 (fat-finger check)` |
| Max open orders | 25 | 3 | `Open order limit of 25 reached` |
| Allowed underlyings / order types / TIFs | all | 0 | `Underlying TSLA is not enabled for this account` |
| Message throttle | 10/s | 0 | `Message rate above 10/s` |
| Kill switch | off | 0 | `Kill switch engaged: new orders are blocked` |
| Duplicate ClOrdID | | 6 | `Duplicate ClOrdID ORD-1` |
| Spreads: legs, underlying, band vs strategy theo | 2–4 legs, one underlying | 99 | `Net price 9.99 is outside the band of strategy theo 2.14 ± 3.26` |
| Unknown instrument | | 1 | `Unknown instrument: SPY 2026-10-02 565 call is not listed` |
| Portfolio delta (shares) | ±10,000 | 3 | `Portfolio delta would be 10,450 shares (now 9,900); limit is ±10,000` |
| Portfolio vega ($ per vol point) | ±$10,000 | 3 | `Portfolio vega would be $10,200 per vol point (now $9,950); limit is ±$10,000` |

Delta and vega limits look at the whole portfolio after the order, assuming it fills, and only block orders that
increase the exposure: an order that brings delta or vega back toward zero is always allowed, even over the limit.
Spreads are checked on the net exposure of all their legs. Positions, P&L and greeks are at `GET /api/positions` and
pushed on the `portfolio` SignalR event once a second.

Engaging the kill switch also cancels every open order (`150=4`, `58=Kill switch engaged`). By default a client's
open orders are canceled when its session disconnects (cancel-on-disconnect).

## 5. Example: a partial fill, as sent

```
>> 8=FIX.4.4|9=197|35=D|49=GST-P5GN29|56=TICKWIRE|34=2|52=20260927-20:41:53.367|11=P5GN29-a2f0-1|1=gst-p5gn29|21=1|55=SPY|167=OPT|541=20261002|201=1|202=560|54=1|60=20260927-20:41:53.367|38=30|40=2|44=4.39|59=0|77=O|10=...|
<< 35=8|...|37=TW54852098000|11=P5GN29-a2f0-1|17=...|150=A|39=A|...|151=30|14=0|6=0|
<< 35=8|...|150=0|39=0|...|151=30|14=0|6=0|
<< 35=8|...|150=F|39=1|...|32=12|31=4.39|151=18|14=12|6=4.39|20001=4.3127|20002=560.13|
```

## 6. Test support

- The **Chaos panel** injects faults on browser sessions (drops, corrupt checksums, silence, sequence gaps) so you can
  watch the recovery described in §2.
- The **Log Analyzer** (`/analyzer`, `POST /api/analyzer`, `tickwire fixlog analyze`) explains problems in a pasted
  log. Attach its output to support tickets.
