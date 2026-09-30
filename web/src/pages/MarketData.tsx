import { useQuery } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { Link } from 'react-router'
import { ProviderMark } from '../components/ProviderMark'
import { api } from '../lib/api'
import { cn, time } from '../lib/format'
import { live } from '../lib/live'
import type { ProviderInfo } from '../lib/types'
import { useGuest } from '../lib/useBackend'
import { useStore } from '../state/store'

const stateTone: Record<string, string> = {
  Streaming: 'bg-buy-soft text-buy',
  Connecting: 'bg-info-soft text-info',
  Error: 'bg-sell-soft text-sell',
  Stopped: 'bg-panel-2 text-muted',
  Idle: 'bg-panel-2 text-muted',
}

export default function MarketData() {
  const mode = useStore((s) => s.mode)
  const feed = useStore((s) => s.feed)
  const liveQuotes = useStore((s) => s.liveQuotes)
  const { ensure } = useGuest()
  const providers = useQuery({ queryKey: ['providers'], queryFn: api.providers, enabled: mode === 'live', staleTime: Infinity })
  const [token, setToken] = useState<string | null>(null)

  useEffect(() => {
    if (mode !== 'live') return
    let cancelled = false
    void (async () => {
      const g = await ensure()
      if (cancelled) return
      setToken(g.token)
      await live.client(g.token)
      useStore.getState().applyFeed(await api.feed(g.token), true)
    })()
    return () => {
      cancelled = true
    }
  }, [mode, ensure])

  const active = feed && feed.state !== 'Idle' ? feed : null
  const disconnect = async () => {
    if (token) await api.disconnectFeed(token)
    useStore.getState().setFeedStatus({ provider: null, providerName: null, state: 'Idle', message: null, startedAt: null, updates: 0, contracts: 0, lastUpdate: null })
  }

  return (
    <div className="mx-auto max-w-6xl space-y-6 px-4 py-10">
      <header className="space-y-2">
        <h1 className="text-2xl font-semibold">Live options market data</h1>
        <p className="max-w-3xl text-sm leading-relaxed text-ink-2">
          Connect your own account with a market data provider and its quotes appear next to the simulated ones in the Trader's chain,
          matched by OCC symbol. Your credentials go to the Tickwire server for this session only: they're held in memory, never stored
          or logged, and forgotten when you disconnect or after two hours. Orders still trade in the simulated venue.
        </p>
      </header>

      {mode !== 'live' && (
        <div className="panel p-4 text-sm text-warn">Connecting a feed needs the live backend, which is unreachable right now.</div>
      )}

      {active && (
        <section className="panel flex flex-wrap items-center gap-4 p-4">
          <ProviderMark id={active.provider ?? ''} name={active.providerName ?? ''} color={providers.data?.find((p) => p.id === active.provider)?.brandColor ?? '#888'} />
          <span className={cn('rounded px-2 py-0.5 text-xs font-medium', stateTone[active.state])}>{active.state}</span>
          <div className="min-w-0 flex-1 text-[12.5px] text-ink-2">
            <div className="truncate">{active.message}</div>
            <div className="num text-[11px] text-muted">
              {active.updates.toLocaleString('en-US')} updates · {Object.keys(liveQuotes).length} of {active.contracts} contracts quoted
              {active.lastUpdate && ` · last ${time(active.lastUpdate).slice(0, 8)}`}
            </div>
          </div>
          <Link to="/trade" className="rounded-md border border-line px-3 py-1.5 text-xs text-ink-2 hover:text-ink">
            View in chain
          </Link>
          <button type="button" onClick={disconnect} className="rounded-md bg-sell px-3 py-1.5 text-xs font-semibold text-bg">
            Disconnect
          </button>
        </section>
      )}

      <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        {(providers.data ?? []).map((p) => (
          <ProviderCard key={p.id} provider={p} token={token} active={active?.provider === p.id} />
        ))}
        {providers.isLoading && <div className="text-sm text-muted">Loading providers…</div>}
      </div>

      <p className="text-[11.5px] leading-relaxed text-muted">
        Provider names identify which API each adapter speaks; they're trademarks of their owners, and Tickwire isn't affiliated with or
        endorsed by any of them. Adapters marked <em>unverified</em> were built from each provider's public protocol documentation and
        unit-tested against sample messages, but haven't been run against a paid account. Market data licences usually restrict display to
        the subscriber, which is why quotes are shown only to the visitor who connected them.
      </p>
    </div>
  )
}

function ProviderCard({ provider: p, token, active }: { provider: ProviderInfo; token: string | null; active: boolean }) {
  const [values, setValues] = useState<Record<string, string>>(() =>
    Object.fromEntries(p.credentials.map((c) => [c.name, c.defaultValue ?? ''])),
  )
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const connect = async () => {
    if (!token) return
    setBusy(true)
    setError(null)
    try {
      useStore.getState().applyFeed({ status: await api.connectFeed(token, p.id, values), quotes: [], underlyings: [] }, true)
      // Secrets don't need to stay in the page once the server has them.
      setValues((v) => Object.fromEntries(Object.entries(v).map(([k, val]) => [k, p.credentials.find((c) => c.name === k)?.secret ? '' : val])))
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className={cn('panel flex flex-col gap-3 p-4', active && 'border-accent/50')} data-testid={`provider-${p.id}`}>
      <div className="flex items-start justify-between gap-2">
        <ProviderMark id={p.id} name={p.name} color={p.brandColor} />
        {!p.verified && (
          <span className="rounded bg-warn-soft px-1.5 py-0.5 text-[10px] font-medium text-warn" title="Built from public docs; not yet run against a live account">
            unverified
          </span>
        )}
      </div>
      <p className="text-[13px] leading-relaxed text-ink-2">{p.tagline}</p>
      <div className="flex flex-wrap gap-1">
        {p.provides.map((x) => (
          <span key={x} className="rounded bg-panel-2 px-1.5 py-0.5 text-[10.5px] text-ink-2">
            {x}
          </span>
        ))}
        <span className="num rounded border border-line px-1.5 py-0.5 text-[10.5px] text-muted">{p.transport}</span>
      </div>
      <form
        className="mt-auto space-y-2"
        onSubmit={(e) => {
          e.preventDefault()
          void connect()
        }}
        autoComplete="off"
      >
        {p.credentials.map((c) => (
          <label key={c.name} className="block space-y-1">
            <span className="text-[11px] text-muted">{c.label}</span>
            <input
              type={c.secret ? 'password' : 'text'}
              value={values[c.name] ?? ''}
              placeholder={c.placeholder ?? ''}
              onChange={(e) => setValues((v) => ({ ...v, [c.name]: e.target.value }))}
              autoComplete="off"
              spellCheck={false}
              className="num w-full rounded-md border border-line bg-bg px-2 py-1.5 text-[12.5px]"
            />
          </label>
        ))}
        <div className="flex items-center gap-2 pt-1">
          <button type="submit" disabled={!token || busy} className="rounded-md bg-accent px-3 py-1.5 text-xs font-semibold text-accent-ink disabled:opacity-40">
            {busy ? 'Connecting…' : active ? 'Reconnect' : 'Connect'}
          </button>
          <a href={p.docsUrl} target="_blank" rel="noreferrer" className="text-[11.5px] text-ink-2 hover:text-ink">
            Docs
          </a>
          {p.signupUrl && (
            <a href={p.signupUrl} target="_blank" rel="noreferrer" className="text-[11.5px] text-ink-2 hover:text-ink">
              Get a key
            </a>
          )}
        </div>
        {error && <p className="text-[12px] text-sell">{error}</p>}
      </form>
    </section>
  )
}
