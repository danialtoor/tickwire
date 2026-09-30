import { useEffect, useState } from 'react'
import { api } from '../lib/api'
import { cn } from '../lib/format'
import { live } from '../lib/live'
import type { ConnectInfo, SessionRole } from '../lib/types'
import { useGuest } from '../lib/useBackend'
import { useStore } from '../state/store'

export default function Connect() {
  const mode = useStore((s) => s.mode)
  const ops = useStore((s) => s.ops)
  const { ensure } = useGuest()
  const [provisioned, setProvisioned] = useState<ConnectInfo[]>([])
  const [selected, setSelected] = useState<string | null>(null)
  const [role, setRole] = useState<SessionRole>('trading')
  const [version, setVersion] = useState(VERSIONS[0].value)
  const [tab, setTab] = useState('quickfixn.cfg')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)

  useEffect(() => {
    if (mode === 'live') void live.ops()
  }, [mode])

  const provision = async () => {
    setBusy(true)
    setError(null)
    try {
      const guest = await ensure()
      const next = await api.connect(guest.token, role, version)
      setProvisioned((all) => [...all.filter((i) => i.senderCompID !== next.senderCompID), next])
      setSelected(next.senderCompID)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const info = provisioned.find((i) => i.senderCompID === selected) ?? null
  const session = info ? ops?.sessions.find((s) => s.clientCompId === info.senderCompID) : undefined
  const text = info?.configs[tab] ?? ''

  const download = () => {
    const blob = new Blob([text], { type: 'text/plain' })
    const a = document.createElement('a')
    a.href = URL.createObjectURL(blob)
    a.download = tab
    a.click()
    URL.revokeObjectURL(a.href)
  }

  return (
    <div className="mx-auto max-w-5xl space-y-6 px-4 py-10">
      <header className="space-y-2">
        <h1 className="text-2xl font-semibold">Connect your own FIX client</h1>
        <p className="max-w-3xl text-sm leading-relaxed text-ink-2">
          This is the onboarding flow a venue runs for a new client: provision a SenderCompID/TargetCompID pair with risk limits, hand over
          the connection details and a working config, then watch the session come up. Tickwire speaks FIX 4.4 over plain TCP, and over
          WebSocket at <code className="num text-accent">/fix/ws</code> for clients that can't open raw sockets.
        </p>
      </header>

      <div className="panel flex flex-wrap items-center justify-between gap-4 p-5">
        <div className="space-y-3">
          <div>
            <div className="font-medium">Provision FIX credentials</div>
            <div className="text-sm text-muted">Tied to your guest account and its limits; they expire with it after 24 hours.</div>
          </div>
          <div className="flex flex-wrap gap-4">
            <Segmented
              label="Session"
              value={role}
              onChange={(v) => setRole(v as SessionRole)}
              options={[
                { value: 'trading', label: 'Trading', hint: 'Send orders and get their reports' },
                { value: 'dropcopy', label: 'Drop copy', hint: 'Receive-only copy of every ExecutionReport (797=Y)' },
              ]}
            />
            <Segmented label="Version" value={version} onChange={setVersion} options={VERSIONS} />
          </div>
        </div>
        <button
          type="button"
          onClick={provision}
          disabled={busy || mode !== 'live'}
          className="rounded-lg bg-accent px-5 py-2.5 text-sm font-semibold text-accent-ink disabled:opacity-50"
        >
          {busy ? 'Provisioning…' : mode === 'live' ? 'Provision credentials' : 'Needs the live backend'}
        </button>
        {error && <p className="w-full text-sm text-sell">{error}</p>}
      </div>

      {provisioned.length > 1 && (
        <div className="flex flex-wrap gap-2">
          {provisioned.map((p) => (
            <button
              key={p.senderCompID}
              type="button"
              onClick={() => setSelected(p.senderCompID)}
              className={cn(
                'num rounded-lg border px-3 py-1.5 text-xs',
                p.senderCompID === selected ? 'border-accent text-ink' : 'border-line text-muted hover:text-ink-2',
              )}
            >
              {p.senderCompID} · {p.role === 'dropcopy' ? 'drop copy' : 'trading'} · {p.beginString}
            </button>
          ))}
        </div>
      )}

      {info && (
        <>
          {info.role === 'dropcopy' && (
            <p className="rounded-lg border border-accent/30 bg-accent/5 px-4 py-3 text-sm text-ink-2">
              Drop copy: log on and listen. Every ExecutionReport for your account&apos;s orders (from the browser trader or your trading
              session) arrives here too, flagged <code className="num">CopyMsgIndicator(797)=Y</code>. Orders sent on this session get a
              BusinessMessageReject.
            </p>
          )}
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
            <Fact label="SenderCompID (49)" value={info.senderCompID} />
            <Fact label="TargetCompID (56)" value={info.targetCompID} />
            <Fact label="Host : port" value={`${info.host}:${info.port}`} />
            <Fact label="Session" value={session?.state ?? 'Waiting for Logon'} tone={session?.state === 'Active' ? 'good' : 'wait'} />
          </div>

          <div className="panel overflow-hidden">
            <div className="flex flex-wrap items-center gap-1 border-b border-line px-2 py-1.5">
              {Object.keys(info.configs).map((k) => (
                <button
                  key={k}
                  type="button"
                  onClick={() => setTab(k)}
                  className={cn('num rounded-md px-2.5 py-1 text-xs', tab === k ? 'bg-panel-2 text-ink' : 'text-muted hover:text-ink-2')}
                >
                  {k}
                </button>
              ))}
              <div className="ml-auto flex gap-1">
                <button
                  type="button"
                  className="rounded-md px-2.5 py-1 text-xs text-ink-2 hover:bg-panel-2"
                  onClick={() => {
                    void navigator.clipboard.writeText(text).then(() => {
                      setCopied(true)
                      setTimeout(() => setCopied(false), 1500)
                    })
                  }}
                >
                  {copied ? 'Copied' : 'Copy'}
                </button>
                <button type="button" className="rounded-md px-2.5 py-1 text-xs text-ink-2 hover:bg-panel-2" onClick={download}>
                  Download
                </button>
              </div>
            </div>
            <pre className="num scroll-thin max-h-[420px] overflow-auto p-4 text-[12px] leading-relaxed text-ink-2">{text}</pre>
          </div>

          <div className="grid gap-4 md:grid-cols-3">
            <Step n={1} title="Get the dictionary">
              Use the standard {info.beginString === 'FIXT.1.1' ? 'FIXT11.xml and FIX50SP2.xml' : 'FIX44.xml'} from QuickFIX. Tickwire adds custom tags 20001 (TheoValue) and 20002 (UnderlyingLastPx) on
              ExecutionReports, so set <code className="num">ValidateUserDefinedFields=N</code>.
            </Step>
            <Step n={2} title="Log on and trade">
              Send a NewOrderSingle with Symbol=SPY, SecurityType=OPT, PutOrCall, StrikePrice and MaturityDate from the chain, or put an OCC
              symbol in Symbol(55).
            </Step>
            <Step n={3} title="Watch it here">
              Your orders appear in the Trader's blotter under the same account, and the session shows up on the Ops dashboard with live
              sequence numbers.
            </Step>
          </div>
        </>
      )}
    </div>
  )
}

const VERSIONS = [
  { value: 'FIX.4.4', label: 'FIX 4.4' },
  { value: 'FIXT.1.1', label: 'FIXT 1.1 / 5.0 SP2', hint: 'FIX 5.0 SP2 messages over the FIXT 1.1 session layer (DefaultApplVerID 9)' },
]

function Segmented({
  label,
  value,
  onChange,
  options,
}: {
  label: string
  value: string
  onChange: (v: string) => void
  options: { value: string; label: string; hint?: string }[]
}) {
  return (
    <div role="radiogroup" aria-label={label} className="flex items-center gap-2">
      <span className="text-[11px] text-muted">{label}</span>
      <div className="flex rounded-lg bg-bg p-0.5">
        {options.map((o) => (
          <button
            key={o.value}
            type="button"
            role="radio"
            aria-checked={o.value === value}
            title={o.hint}
            onClick={() => onChange(o.value)}
            className={cn('rounded-md px-2.5 py-1 text-xs', o.value === value ? 'bg-panel-2 text-ink' : 'text-muted hover:text-ink-2')}
          >
            {o.label}
          </button>
        ))}
      </div>
    </div>
  )
}

function Fact({ label, value, tone }: { label: string; value: string; tone?: 'good' | 'wait' }) {
  return (
    <div className="panel px-4 py-3">
      <div className="text-[11px] text-muted">{label}</div>
      <div className={cn('num mt-1 font-semibold', tone === 'good' && 'text-buy', tone === 'wait' && 'text-warn')}>{value}</div>
    </div>
  )
}

function Step({ n, title, children }: { n: number; title: string; children: React.ReactNode }) {
  return (
    <div className="panel p-4">
      <div className="num text-[11px] text-accent">STEP {n}</div>
      <div className="mt-1 font-medium">{title}</div>
      <p className="mt-2 text-[13px] leading-relaxed text-ink-2">{children}</p>
    </div>
  )
}
