import { useEffect, useState } from 'react'
import { api } from '../lib/api'
import { cn } from '../lib/format'
import { live } from '../lib/live'
import type { ConnectInfo } from '../lib/types'
import { useGuest } from '../lib/useBackend'
import { useStore } from '../state/store'

export default function Connect() {
  const mode = useStore((s) => s.mode)
  const ops = useStore((s) => s.ops)
  const { ensure } = useGuest()
  const [info, setInfo] = useState<ConnectInfo | null>(null)
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
      setInfo(await api.connect(guest.token))
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

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

      {!info && (
        <div className="panel flex flex-wrap items-center justify-between gap-4 p-5">
          <div>
            <div className="font-medium">Provision FIX credentials</div>
            <div className="text-sm text-muted">Tied to your guest account and its limits; they expire with it after 24 hours.</div>
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
      )}

      {info && (
        <>
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
              Use the standard FIX44.xml from QuickFIX. Tickwire adds custom tags 20001 (TheoValue) and 20002 (UnderlyingLastPx) on
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
