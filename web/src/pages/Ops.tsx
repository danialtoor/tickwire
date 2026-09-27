import { useEffect, useState } from 'react'
import { api } from '../lib/api'
import { cn, micros } from '../lib/format'
import { live } from '../lib/live'
import { useGuest } from '../lib/useBackend'
import { startReplay } from '../replay/player'
import { useStore } from '../state/store'

export default function OpsPage() {
  const mode = useStore((s) => s.mode)
  const ops = useStore((s) => s.ops)
  const { guest } = useGuest()
  const [killState, setKillState] = useState<string | null>(null)

  useEffect(() => {
    if (mode === 'live') {
      void live.ops()
      void api.metrics().then((m) => useStore.getState().setOps(m))
    }
    if (mode === 'replay') return startReplay()
  }, [mode])

  if (!ops) return <div className="p-8 text-sm text-muted">Waiting for metrics…</div>

  const rejects = Object.entries(ops.rejectsByReason).sort((a, b) => b[1] - a[1])
  const maxReject = Math.max(1, ...rejects.map(([, n]) => n))

  const kill = async (engaged: boolean) => {
    if (!guest) return
    try {
      const res = await api.kill(guest.token, guest.clientId, engaged)
      setKillState(engaged ? `Kill switch on: ${res.ordersCanceled} order(s) canceled, new orders blocked.` : 'Kill switch released.')
    } catch (e) {
      setKillState(e instanceof Error ? e.message : String(e))
    }
  }

  return (
    <div className="mx-auto max-w-[1400px] space-y-4 p-4">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <h1 className="text-xl font-semibold">Ops dashboard</h1>
        <span className="text-xs text-muted">
          persistence: {ops.persistence} · up {Math.floor(ops.uptimeSeconds / 60)} min
          {ops.globalKillSwitch && <span className="ml-2 rounded bg-sell-soft px-1.5 py-0.5 text-sell">GLOBAL KILL SWITCH ON</span>}
        </span>
      </div>

      <div className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">
        <Tile label="Active sessions" value={`${ops.sessionsActive}`} sub={`${ops.sessionsTotal} configured`} />
        <Tile label="Msgs in / s" value={ops.msgInPerSec.toFixed(1)} sub={`${ops.msgInTotal.toLocaleString('en-US')} total`} />
        <Tile label="Msgs out / s" value={ops.msgOutPerSec.toFixed(1)} sub={`${ops.msgOutTotal.toLocaleString('en-US')} total`} />
        <Tile label="Order → ack p50" value={ops.orderToAckMicros.samples ? micros(ops.orderToAckMicros.p50) : '—'} sub={`${ops.orderToAckMicros.samples} samples`} />
        <Tile label="Order → ack p99" value={ops.orderToAckMicros.samples ? micros(ops.orderToAckMicros.p99) : '—'} sub={`max ${micros(ops.orderToAckMicros.max)}`} />
        <Tile label="Fills" value={ops.fills.toLocaleString('en-US')} sub={`${ops.contractsTraded.toLocaleString('en-US')} contracts`} />
      </div>

      <div className="grid gap-3 lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
        <section className="panel p-4">
          <div className="flex items-center justify-between">
            <h2 className="panel-title">Message rate · last 60s</h2>
            <span className="flex gap-3 text-[11px]">
              <span className="flex items-center gap-1.5 text-ink-2">
                <span className="h-0.5 w-3 bg-accent" /> in
              </span>
              <span className="flex items-center gap-1.5 text-ink-2">
                <span className="h-0.5 w-3 bg-info" /> out
              </span>
            </span>
          </div>
          <RateChart inSeries={ops.inSeries} outSeries={ops.outSeries} />
        </section>
        <section className="panel p-4">
          <h2 className="panel-title">Risk rejects by reason</h2>
          {rejects.length === 0 ? (
            <p className="mt-4 text-sm text-muted">None yet. Try a 500-lot order in the trader.</p>
          ) : (
            <ul className="mt-3 space-y-2 text-[12px]">
              {rejects.map(([reason, n]) => (
                <li key={reason}>
                  <div className="flex justify-between">
                    <span>{reason.replace(/([a-z])([A-Z])/g, '$1 $2')}</span>
                    <span className="num text-ink-2">{n}</span>
                  </div>
                  <div className="mt-1 h-1.5 rounded-full bg-panel-2">
                    <div className="h-full rounded-full bg-sell/70" style={{ width: `${(n / maxReject) * 100}%` }} />
                  </div>
                </li>
              ))}
            </ul>
          )}
          {guest && mode === 'live' && (
            <div className="mt-6 space-y-2 border-t border-line pt-4">
              <h3 className="panel-title">Your kill switch</h3>
              <p className="text-[12px] text-ink-2">Cancels every open order for {guest.clientId} and blocks new ones until released.</p>
              <div className="flex gap-2">
                <button type="button" onClick={() => kill(true)} className="rounded-md bg-sell px-3 py-1.5 text-xs font-semibold text-bg">
                  Engage
                </button>
                <button type="button" onClick={() => kill(false)} className="rounded-md border border-line px-3 py-1.5 text-xs">
                  Release
                </button>
              </div>
              {killState && <p className="text-[12px] text-warn">{killState}</p>}
            </div>
          )}
        </section>
      </div>

      <section className="panel overflow-hidden">
        <div className="border-b border-line px-4 py-2">
          <h2 className="panel-title">FIX sessions</h2>
        </div>
        <div className="scroll-thin overflow-x-auto">
          <table className="w-full text-left text-[12.5px]">
            <thead className="text-[10.5px] uppercase tracking-wider text-muted">
              <tr className="[&>th]:border-b [&>th]:border-line [&>th]:px-3 [&>th]:py-2 [&>th]:font-medium">
                <th>Client CompID</th>
                <th>State</th>
                <th>Transport</th>
                <th className="text-right">Next out</th>
                <th className="text-right">Next in</th>
                <th className="text-right">HB</th>
                <th>Last heard</th>
              </tr>
            </thead>
            <tbody className="num">
              {ops.sessions.slice(0, 200).map((s) => (
                <tr key={s.key} className={cn('border-b border-line/50', s.key === guest?.sessionKey && 'bg-accent/6')}>
                  <td className="px-3 py-1.5">
                    {s.clientCompId}
                    {s.key === guest?.sessionKey && <span className="ml-2 font-sans text-[10.5px] text-accent">you</span>}
                  </td>
                  <td className="px-3 py-1.5">
                    <span className={cn('rounded px-1.5 py-0.5 font-sans text-[11px]', s.state === 'Active' ? 'bg-buy-soft text-buy' : 'bg-panel-2 text-muted')}>{s.state}</span>
                  </td>
                  <td className="px-3 py-1.5 font-sans text-ink-2">{s.transport === 'memory' ? 'browser' : s.transport}</td>
                  <td className="px-3 py-1.5 text-right">{s.nextSenderSeqNum}</td>
                  <td className="px-3 py-1.5 text-right">{s.nextTargetSeqNum}</td>
                  <td className="px-3 py-1.5 text-right">{s.heartBtInt}s</td>
                  <td className="px-3 py-1.5 text-muted">{s.lastReceived ? `${secondsBetween(s.lastReceived, ops.time)}s ago` : '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
    </div>
  )
}

/** Seconds between two server timestamps (uses the snapshot's own clock, not the browser's). */
function secondsBetween(from: string, to: string): number {
  const parse = (iso: string) => new Date(iso.endsWith('Z') ? iso : `${iso}Z`).getTime()
  return Math.max(0, Math.round((parse(to) - parse(from)) / 1000))
}

function Tile({ label, value, sub }: { label: string; value: string; sub: string }) {
  return (
    <div className="panel px-4 py-3">
      <div className="text-[11px] text-muted">{label}</div>
      <div className="num mt-1 text-2xl font-semibold">{value}</div>
      <div className="num mt-0.5 text-[11px] text-muted">{sub}</div>
    </div>
  )
}

function RateChart({ inSeries, outSeries }: { inSeries: number[]; outSeries: number[] }) {
  const w = 600
  const h = 160
  const max = Math.max(4, ...inSeries, ...outSeries)
  const path = (s: number[]) => s.map((v, i) => `${i === 0 ? 'M' : 'L'}${(i / Math.max(1, s.length - 1)) * w},${h - (v / max) * (h - 10)}`).join(' ')
  return (
    <svg viewBox={`0 0 ${w} ${h}`} className="mt-3 h-44 w-full" preserveAspectRatio="none" role="img" aria-label="Messages per second, last 60 seconds">
      {[0.25, 0.5, 0.75].map((f) => (
        <line key={f} x1={0} x2={w} y1={h * f} y2={h * f} className="stroke-line" strokeWidth={1} />
      ))}
      <path d={path(outSeries)} fill="none" className="stroke-info" strokeWidth={2} vectorEffect="non-scaling-stroke" />
      <path d={path(inSeries)} fill="none" className="stroke-accent" strokeWidth={2} vectorEffect="non-scaling-stroke" />
      <text x={4} y={12} className="fill-muted" fontSize={11}>
        {max}/s
      </text>
    </svg>
  )
}
