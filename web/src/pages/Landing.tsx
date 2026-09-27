import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { REPO_URL } from '../lib/config'
import { api } from '../lib/api'
import { micros } from '../lib/format'
import { useStore } from '../state/store'

const features = [
  {
    title: 'FIX engine written from scratch',
    body: 'Zero-copy parser and pooled serializer over System.IO.Pipelines. BodyLength, CheckSum and the full FIX 4.4 data dictionary, validated against QuickFIX/n in CI.',
  },
  {
    title: 'Session recovery you can break',
    body: 'Drop messages, corrupt checksums, go silent or rewind sequence numbers. Watch ResendRequest, PossDup resends, GapFill and TestRequest do their jobs.',
  },
  {
    title: 'Options OMS with pre-trade risk',
    body: 'Explicit order state machine, cancel/replace chains, and per-client limits: size, notional, fat-finger band vs theo, throttle and kill switch.',
  },
  {
    title: 'A simulated options market',
    body: 'Price-time priority books for SPY, AAPL, TSLA and NVDA chains, Black-Scholes theo on a skewed surface, and market makers that requote on every tick.',
  },
  {
    title: 'Bring your own FIX client',
    body: 'One click provisions CompIDs and risk limits, with ready-made QuickFIX/n, QuickFIX/J and Python configs. Point a real engine at port 9878.',
  },
  {
    title: 'Support tooling',
    body: 'Paste any FIX log into the analyzer to rebuild each order and flag gaps, bad state transitions and quantity breaks. Plus a CLI, a PowerShell module and an ops dashboard.',
  },
]

export function Landing() {
  const mode = useStore((s) => s.mode)
  const metrics = useQuery({ queryKey: ['metrics'], queryFn: api.metrics, enabled: mode === 'live', refetchInterval: 5000 })
  const m = metrics.data

  return (
    <div className="mx-auto max-w-6xl px-4 py-12 sm:py-16">
      <section className="grid items-center gap-10 lg:grid-cols-[1.1fr_1fr]">
        <div className="space-y-6">
          <p className="inline-flex items-center gap-2 rounded-full border border-line bg-panel px-3 py-1 text-xs text-ink-2">
            <span className="size-1.5 rounded-full bg-accent" /> C# / .NET 10 · FIX 4.4 · listed options
          </p>
          <h1 className="text-4xl font-semibold leading-tight tracking-tight sm:text-5xl">
            An options execution gateway, down to the <span className="text-accent">FIX bytes</span>.
          </h1>
          <p className="max-w-xl text-base leading-relaxed text-ink-2">
            Tickwire is a hand-written FIX engine, an order management system with pre-trade risk, and a simulated options exchange. Every click
            in the trader produces real FIX messages you can inspect, break and watch recover.
          </p>
          <div className="flex flex-wrap gap-3">
            <Link to="/trade" className="rounded-lg bg-accent px-5 py-2.5 text-sm font-semibold text-accent-ink hover:opacity-90">
              Launch Trader
            </Link>
            <Link to="/connect" className="rounded-lg border border-line bg-panel px-5 py-2.5 text-sm font-medium hover:border-line-strong">
              Connect via FIX
            </Link>
            <a href={REPO_URL} target="_blank" rel="noreferrer" className="rounded-lg px-3 py-2.5 text-sm text-ink-2 hover:text-ink">
              Read the code →
            </a>
          </div>
          <p className="text-xs text-muted">No signup. You get a guest account with conservative limits for 24 hours.</p>
        </div>

        <div className="panel overflow-hidden">
          <div className="flex items-center justify-between border-b border-line px-4 py-2 text-[11px] text-muted">
            <span>ExecutionReport · decoded</span>
            <span className="num">34=412</span>
          </div>
          <pre className="num overflow-x-auto p-4 text-[11.5px] leading-6 text-ink-2">
            {[
              ['35', 'MsgType', '8', 'ExecutionReport'],
              ['11', 'ClOrdID', 'K7Q2-1a4-3', ''],
              ['150', 'ExecType', 'F', 'Trade'],
              ['39', 'OrdStatus', '1', 'PartiallyFilled'],
              ['55', 'Symbol', 'SPY', ''],
              ['201', 'PutOrCall', '1', 'Call'],
              ['202', 'StrikePrice', '560', ''],
              ['32', 'LastQty', '12', ''],
              ['31', 'LastPx', '4.39', ''],
              ['14', 'CumQty', '12', ''],
              ['151', 'LeavesQty', '18', ''],
              ['20001', 'TheoValue', '4.3127', 'custom tag'],
            ].map(([tag, name, value, meaning]) => (
              <div key={tag}>
                <span className="inline-block w-12 text-info">{tag}</span>
                <span className="inline-block w-28 font-sans text-muted">{name}</span>
                <span className="text-ink">{value}</span>
                {meaning && <span className="ml-2 font-sans text-accent">{meaning}</span>}
              </div>
            ))}
          </pre>
        </div>
      </section>

      {m && (
        <section className="mt-12 grid grid-cols-2 gap-3 sm:grid-cols-4">
          <Stat label="Sessions online" value={String(m.sessionsActive)} />
          <Stat label="Messages processed" value={(m.msgInTotal + m.msgOutTotal).toLocaleString('en-US')} />
          <Stat label="Order → ack p50" value={m.orderToAckMicros.samples ? micros(m.orderToAckMicros.p50) : '—'} />
          <Stat label="Contracts traded" value={m.contractsTraded.toLocaleString('en-US')} />
        </section>
      )}

      <section className="mt-16 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {features.map((f) => (
          <div key={f.title} className="panel p-5">
            <h3 className="font-semibold">{f.title}</h3>
            <p className="mt-2 text-sm leading-relaxed text-ink-2">{f.body}</p>
          </div>
        ))}
      </section>

      <section className="panel mt-16 p-6">
        <h2 className="panel-title">How an order travels</h2>
        <ol className="mt-4 grid gap-3 text-sm md:grid-cols-5">
          {[
            ['Browser', 'REST command; a server-side FIX initiator sends 35=D'],
            ['FIX session', 'Sequence check, dictionary validation, gap recovery'],
            ['OMS', 'Pre-trade risk, state machine, 35=8 back to the client'],
            ['Venue shard', 'Price-time matching against market-maker quotes'],
            ['MySQL', 'Sequence numbers, resend store, orders, audit'],
          ].map(([title, body], i) => (
            <li key={title} className="rounded-lg border border-line bg-bg p-3">
              <div className="num text-[11px] text-accent">0{i + 1}</div>
              <div className="font-medium">{title}</div>
              <div className="mt-1 text-xs leading-relaxed text-muted">{body}</div>
            </li>
          ))}
        </ol>
      </section>
    </div>
  )
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="panel px-4 py-3">
      <div className="text-[11px] text-muted">{label}</div>
      <div className="num mt-1 text-xl font-semibold">{value}</div>
    </div>
  )
}
