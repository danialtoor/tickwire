import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useMemo, useRef, useState } from 'react'
import { Link } from 'react-router'
import { Blotter } from '../components/Blotter'
import { BookLadder } from '../components/BookLadder'
import { ChainTable, type Pick } from '../components/ChainTable'
import { ChaosPanel } from '../components/ChaosPanel'
import { ExpiryBar } from '../components/ExpiryBar'
import { Inspector, type InspectorView } from '../components/Inspector'
import { OrderTicket } from '../components/OrderTicket'
import { SpotChart } from '../components/SpotChart'
import { Positions } from '../components/Positions'
import { SpreadTicket } from '../components/SpreadTicket'
import { api } from '../lib/api'
import { cn, expiryLabel, pct, px, time } from '../lib/format'
import { live } from '../lib/live'
import { useGuest } from '../lib/useBackend'
import { startReplay } from '../replay/player'
import { useStore } from '../state/store'

const NO_POINTS: never[] = []

export default function Trader() {
  const mode = useStore((s) => s.mode)
  const { guest, ensure, reset } = useGuest()
  const underlyings = useStore((s) => s.underlyings)
  const chain = useStore((s) => s.chain)
  const book = useStore((s) => s.book)
  const selected = useStore((s) => s.selected)
  const select = useStore((s) => s.select)
  const wire = useStore((s) => s.wire)
  const logs = useStore((s) => s.logs)
  const ops = useStore((s) => s.ops)
  const tape = useStore((s) => s.tape)
  const spot = useStore((s) => s.spotHistory[s.selected.underlying] ?? NO_POINTS)
  const storeOrders = useStore((s) => s.orders)
  const feed = useStore((s) => s.feed)
  const portfolio = useStore((s) => s.portfolio)
  const liveQuotes = useStore((s) => s.liveQuotes)
  const liveUnderlyings = useStore((s) => s.liveUnderlyings)
  const setOrders = useStore((s) => s.setOrders)
  const queryClient = useQueryClient()

  const [preset, setPreset] = useState<{ side: 'buy' | 'sell'; price: number | null; nonce: number } | null>(null)
  const [toast, setToast] = useState<string | null>(null)
  const [view, setView] = useState<InspectorView>('venue')
  const [orderFilter, setOrderFilter] = useState<string | null>(null)
  const [bottomTab, setBottomTab] = useState<'blotter' | 'positions' | 'chaos' | 'tape'>('blotter')
  const [ticketTab, setTicketTab] = useState<'single' | 'spread'>('single')
  const [error, setError] = useState<string | null>(null)
  const toastTimer = useRef<ReturnType<typeof setTimeout>>(undefined)

  const say = (message: string) => {
    setToast(message)
    clearTimeout(toastTimer.current)
    toastTimer.current = setTimeout(() => setToast(null), 4000)
  }

  const current = underlyings.find((u) => u.symbol === selected.underlying)
  // The daily roll delists passed expiries; a selection that's no longer listed falls back to the nearest one.
  const listedExpiry = selected.expiry && (!current || current.expiries.includes(selected.expiry)) ? selected.expiry : null

  // Live mode: guest session, SignalR subscriptions and history.
  useEffect(() => {
    if (mode !== 'live') return
    let cancelled = false
    ;(async () => {
      try {
        const g = await ensure()
        if (cancelled) return
        await live.start()
        await Promise.all([live.client(g.token), live.ops(), live.chain(useStore.getState().selected.underlying, useStore.getState().selected.expiry)])
        useStore.getState().applyFeed(await api.feed(g.token), true)
        useStore.getState().setPortfolio(await api.positions(g.token))
        const traffic = await api.traffic(g.token, g.clientCompId, 400)
        useStore.getState().addWire(traffic.messages)
        useStore.getState().addLogs(traffic.logs)
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : String(e))
      }
    })()
    return () => {
      cancelled = true
    }
  }, [mode, ensure])

  // Replay mode: play back the recorded session.
  useEffect(() => {
    if (mode !== 'replay') return
    return startReplay()
  }, [mode])

  useEffect(() => {
    if (mode === 'live') void live.chain(selected.underlying, listedExpiry)
  }, [mode, selected.underlying, listedExpiry])

  const ordersQuery = useQuery({
    queryKey: ['orders', guest?.token],
    queryFn: () => api.orders(guest!.token),
    enabled: mode === 'live' && !!guest,
    refetchInterval: 2000,
  })
  useEffect(() => {
    if (ordersQuery.data) setOrders(ordersQuery.data)
  }, [ordersQuery.data, setOrders])
  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined
    const off = live.onOrderEvent(() => {
      clearTimeout(timer)
      timer = setTimeout(() => void queryClient.invalidateQueries({ queryKey: ['orders'] }), 150)
    })
    return () => {
      off()
      clearTimeout(timer)
    }
  }, [queryClient])

  // Until the user picks a contract, default to the at-the-money call (derived, not stored).
  const atmCall = useMemo(() => {
    if (!chain || chain.rows.length === 0) return null
    return chain.rows.reduce((best, r) => (Math.abs(r.strike - chain.spot) < Math.abs(best.strike - chain.spot) ? r : best), chain.rows[0]).call
  }, [chain])
  const contractId = selected.contractId ?? atmCall?.id ?? null
  const atmCallId = atmCall?.id ?? null
  const atmAsk = atmCall?.ask ?? null
  const ticketPreset = useMemo(
    () => preset ?? (selected.contractId === null && atmCallId !== null ? { side: 'buy' as const, price: atmAsk, nonce: atmCallId } : null),
    // The default preset follows the ATM contract, not every ask update.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [preset, selected.contractId, atmCallId],
  )

  useEffect(() => {
    if (mode === 'live' && contractId !== null) void live.book(contractId)
  }, [mode, contractId])

  const quote = useMemo(() => {
    if (!chain || contractId === null) return null
    for (const r of chain.rows) {
      if (r.call?.id === contractId) return r.call
      if (r.put?.id === contractId) return r.put
    }
    return null
  }, [chain, contractId])

  const session = ops?.sessions.find((s) => s.key === guest?.sessionKey)

  const onPick = (p: Pick) => {
    select({ contractId: p.contractId })
    setPreset({ side: p.side, price: p.price, nonce: Date.now() })
  }

  return (
    <div className="mx-auto max-w-[1680px] space-y-3 p-3">
      {/* underlyings + session */}
      <div className="flex flex-wrap items-center gap-2">
        <div className="flex gap-1 overflow-x-auto scroll-thin">
          {(underlyings.length ? underlyings : [{ symbol: 'SPY', price: 0, changePct: 0, name: '', expiries: [] }]).map((u) => (
            <button
              key={u.symbol}
              type="button"
              onClick={() => select({ underlying: u.symbol, expiry: null, contractId: null })}
              className={cn(
                'rounded-lg border px-3 py-1.5 text-left transition-colors',
                selected.underlying === u.symbol ? 'border-accent/40 bg-panel-2' : 'border-line bg-panel hover:border-line-strong',
              )}
            >
              <div className="text-xs font-semibold">{u.symbol}</div>
              <div className="num flex gap-1.5 text-[11px]">
                <span>{px(u.price)}</span>
                <span className={u.changePct >= 0 ? 'text-buy' : 'text-sell'}>{pct(u.changePct)}</span>
              </div>
            </button>
          ))}
        </div>
        <div className="ml-auto flex flex-wrap items-center gap-3 rounded-lg border border-line bg-panel px-3 py-1.5 text-[11.5px]">
          <span className="text-muted">FIX session</span>
          <span className="num font-medium">{guest?.clientCompId ?? (mode === 'replay' ? 'GST-REPLAY' : '…')}</span>
          <span className={cn('rounded px-1.5 py-0.5 font-medium', session?.state === 'Active' ? 'bg-buy-soft text-buy' : 'bg-warn-soft text-warn')}>
            {session?.state ?? (mode === 'replay' ? 'Replay' : 'Connecting')}
          </span>
          {session && (
            <span className="num text-muted" title="Next outbound / next expected inbound MsgSeqNum (venue side)">
              seq out {session.nextSenderSeqNum} · in {session.nextTargetSeqNum} · HB {session.heartBtInt}s
            </span>
          )}
          {mode === 'live' && (
            <button type="button" onClick={() => void reset().then(() => window.location.reload())} className="text-muted underline decoration-line-strong underline-offset-2 hover:text-ink">
              New session
            </button>
          )}
        </div>
      </div>

      {error && <div className="rounded-lg bg-sell-soft px-3 py-2 text-sm text-sell">{error}</div>}

      <div className="grid gap-3 xl:grid-cols-[minmax(0,1fr)_360px]">
        <section className="panel min-w-0">
          <div className="flex items-center justify-between border-b border-line px-3 py-2">
            <h2 className="panel-title">
              {selected.underlying} options · {chain ? expiryLabel(chain.expiry) : ''}
            </h2>
            <span className="num text-[11px] text-muted">
              spot {px(chain?.spot)} · theo = Black-Scholes on a skewed vol surface
            </span>
          </div>
          <ExpiryBar
            expiries={current?.expiries ?? (chain ? [chain.expiry] : [])}
            selected={chain?.expiry ?? selected.expiry}
            asOf={chain?.time ?? null}
            onSelect={(expiry) => select({ expiry, contractId: null })}
          />
          {feed && feed.state !== 'Idle' ? (
            <div className="flex flex-wrap items-center gap-2 border-b border-line px-3 py-1.5 text-[11.5px]">
              <span className="text-muted">Your feed</span>
              <span className="font-medium text-accent">{feed.providerName}</span>
              <span className={feed.state === 'Streaming' ? 'text-buy' : feed.state === 'Error' ? 'text-sell' : 'text-warn'}>{feed.state}</span>
              {liveUnderlyings[selected.underlying] && (
                <span className="num text-ink-2">
                  {selected.underlying} {px(liveUnderlyings[selected.underlying].price)} (sim {px(chain?.spot)})
                </span>
              )}
              <span className="truncate text-muted">{feed.message}</span>
              <Link to="/data" className="ml-auto text-muted underline decoration-line-strong underline-offset-2 hover:text-ink">
                Manage
              </Link>
            </div>
          ) : (
            <div className="border-b border-line px-3 py-1.5 text-[11.5px] text-muted">
              Simulated market.{' '}
              <Link to="/data" className="underline decoration-line-strong underline-offset-2 hover:text-ink">
                Connect a live options feed
              </Link>{' '}
              (SpiderRock, Databento, Polygon.io, Tradier, Alpaca) to see real quotes alongside.
            </div>
          )}
          <ChainTable chain={chain} selectedId={contractId} onPick={onPick} live={feed && feed.state !== 'Idle' ? liveQuotes : undefined} />
        </section>

        <aside className="grid content-start gap-3 md:grid-cols-2 xl:grid-cols-1">
          <section className="panel p-3">
            <div className="mb-3 grid grid-cols-2 gap-1 rounded-lg border border-line p-0.5 text-xs">
              {(['single', 'spread'] as const).map((t) => (
                <button
                  key={t}
                  type="button"
                  data-testid={`ticket-tab-${t}`}
                  onClick={() => setTicketTab(t)}
                  className={cn('rounded-md py-1 font-medium', ticketTab === t ? 'bg-panel-2 text-ink' : 'text-muted hover:text-ink-2')}
                >
                  {t === 'single' ? 'Single option' : 'Spread (multi-leg)'}
                </button>
              ))}
            </div>
            {ticketTab === 'single' ? (
              <OrderTicket quote={quote} book={book} token={guest?.token ?? null} preset={ticketPreset} disabled={mode !== 'live'} onSent={say} />
            ) : (
              <SpreadTicket
                chain={chain}
                anchorStrike={chain?.rows.find((r) => r.call?.id === contractId || r.put?.id === contractId)?.strike ?? null}
                token={guest?.token ?? null}
                disabled={mode !== 'live'}
                onSent={say}
              />
            )}
          </section>
          <section className="panel">
            <div className="border-b border-line px-3 py-2">
              <h2 className="panel-title">Order book</h2>
            </div>
            <div className="py-2">
              <BookLadder book={book} />
            </div>
          </section>
          <section className="panel md:col-span-2 xl:col-span-1">
            <div className="flex items-center justify-between border-b border-line px-3 py-2">
              <h2 className="panel-title">{selected.underlying} spot</h2>
              <span className="num text-xs">{px(current?.price)}</span>
            </div>
            <div className="h-36 px-1">
              <SpotChart points={spot} />
            </div>
          </section>
        </aside>
      </div>

      <div className="grid gap-3 xl:grid-cols-[minmax(0,1fr)_minmax(0,1.1fr)]">
        <section className="panel min-w-0">
          <div className="flex items-center gap-1 border-b border-line px-2 py-1.5">
            {(['blotter', 'positions', 'chaos', 'tape'] as const).map((t) => (
              <button
                key={t}
                type="button"
                onClick={() => setBottomTab(t)}
                className={cn('rounded-md px-2.5 py-1 text-xs font-medium capitalize', bottomTab === t ? 'bg-panel-2 text-ink' : 'text-muted hover:text-ink-2')}
              >
                {t === 'chaos' ? '⚡ Chaos' : t === 'blotter' ? `Blotter (${storeOrders.length})` : t === 'positions' ? `Positions (${portfolio?.positions.length ?? 0})` : 'Time & sales'}
              </button>
            ))}
          </div>
          {bottomTab === 'blotter' && (
            <Blotter rows={storeOrders} token={guest?.token ?? null} selectedClOrdId={orderFilter} onSelect={setOrderFilter} onAction={say} />
          )}
          {bottomTab === 'positions' && <Positions portfolio={portfolio} />}
          {bottomTab === 'chaos' && (
            <ChaosPanel
              token={mode === 'live' ? (guest?.token ?? null) : null}
              compId={guest?.clientCompId ?? null}
              onFired={(a) => setView(a === 'drop-venue' || a === 'disconnect' ? 'both' : 'venue')}
            />
          )}
          {bottomTab === 'tape' && (
            <ul className="num scroll-thin max-h-[360px] overflow-auto text-[12px]">
              {tape.map((t, i) => (
                <li key={`${t.time}${i}`} className="grid grid-cols-[90px_1fr_44px_60px_50px] gap-2 border-b border-line/50 px-3 py-1">
                  <span className="text-muted">{time(t.time).slice(0, 8)}</span>
                  <span className="font-sans">{t.display}</span>
                  <span className="text-[11px] text-muted">{t.exchange ?? ''}</span>
                  <span className={t.side === 'buy' ? 'text-buy' : 'text-sell'}>{px(t.price)}</span>
                  <span className="text-right">{t.quantity}</span>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section className="panel h-[560px] min-w-0 overflow-hidden">
          <Inspector wire={wire} logs={logs} view={view} onViewChange={setView} orderFilter={orderFilter} onClearOrderFilter={() => setOrderFilter(null)} />
        </section>
      </div>

      {toast && (
        <div className="fixed bottom-4 left-1/2 z-40 -translate-x-1/2 rounded-lg border border-line bg-panel-2 px-4 py-2 text-sm shadow-lg" role="status">
          {toast}
        </div>
      )}
    </div>
  )
}
