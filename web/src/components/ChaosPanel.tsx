import { useState } from 'react'
import { api } from '../lib/api'
import { cn } from '../lib/format'

interface Action {
  id: string
  title: string
  expect: string
  count?: boolean
}

const actions: Action[] = [
  { id: 'drop-venue', title: 'Drop venue messages', expect: 'Client sees a gap → ResendRequest (2) → PossDup resends, admin gap-filled (4, 123=Y)', count: true },
  { id: 'seq-gap', title: 'Skip client seq numbers', expect: 'Venue sends ResendRequest → client answers SequenceReset-GapFill', count: true },
  { id: 'corrupt-checksum', title: 'Corrupt a checksum', expect: 'Venue ignores the garbled message; the next one exposes the gap and triggers a resend' },
  { id: 'corrupt-bodylength', title: 'Corrupt a body length', expect: "Framer resyncs on 10=, message ignored as garbled, gap recovered" },
  { id: 'stop-heartbeats', title: 'Stop client heartbeats', expect: 'TestRequest (1) after 12s silence, Logout after 10s more, then reconnect' },
  { id: 'seq-too-low', title: 'Reuse old seq numbers', expect: 'Logout "MsgSeqNum too low"; client reconnects with ResetSeqNumFlag=Y' },
  { id: 'invalid-field', title: 'Send an invalid field', expect: 'Session-level Reject (3) with 373=5, 371=54' },
  { id: 'disconnect', title: 'Drop the connection', expect: 'Client reconnects; both sides resume from stored sequence numbers' },
]

export function ChaosPanel({ token, compId, onFired }: { token: string | null; compId: string | null; onFired: (action: string) => void }) {
  const [count, setCount] = useState(3)
  const [busy, setBusy] = useState<string | null>(null)
  const [result, setResult] = useState<{ action: string; text: string; error?: boolean } | null>(null)

  const fire = async (a: Action) => {
    if (!token || !compId) return
    setBusy(a.id)
    try {
      const res = await api.chaos(token, compId, a.id, a.count ? count : undefined)
      setResult({ action: a.title, text: res.description })
      onFired(a.id)
    } catch (e) {
      setResult({ action: a.title, text: e instanceof Error ? e.message : String(e), error: true })
    } finally {
      setBusy(null)
    }
  }

  return (
    <div className="space-y-3 p-3">
      <p className="text-[12px] leading-relaxed text-ink-2">
        Break the session on purpose and watch the engine recover in the Inspector. Faults apply only to your guest session.
      </p>
      <label className="flex items-center gap-2 text-[11px] text-muted">
        Messages for drop / skip
        <input
          type="number"
          min={1}
          max={20}
          value={count}
          onChange={(e) => setCount(Math.min(20, Math.max(1, Number(e.target.value) || 1)))}
          className="num w-14 rounded border border-line bg-bg px-1.5 py-0.5 text-ink"
        />
      </label>
      <div className="grid grid-cols-1 gap-1.5 sm:grid-cols-2">
        {actions.map((a) => (
          <button
            key={a.id}
            type="button"
            data-testid={`chaos-${a.id}`}
            disabled={!token || busy !== null}
            onClick={() => fire(a)}
            className="group rounded-lg border border-line bg-bg px-3 py-2 text-left transition-colors hover:border-warn/50 disabled:opacity-50"
          >
            <div className="flex items-center justify-between text-[12.5px] font-medium">
              {a.title}
              <span className="text-warn opacity-0 transition-opacity group-hover:opacity-100">{busy === a.id ? '…' : '⚡'}</span>
            </div>
            <div className="mt-0.5 text-[11px] leading-snug text-muted">{a.expect}</div>
          </button>
        ))}
      </div>
      {result && (
        <div className={cn('rounded-lg px-3 py-2 text-[12px] leading-relaxed', result.error ? 'bg-sell-soft text-sell' : 'bg-warn-soft text-warn')} role="status">
          <span className="font-semibold">{result.action}: </span>
          {result.text}
        </div>
      )}
    </div>
  )
}
