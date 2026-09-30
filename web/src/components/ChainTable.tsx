import { cn, px } from '../lib/format'
import type { Chain, ExternalQuote, Quote } from '../lib/types'
import { Flash } from './Flash'

export type Pick = { contractId: number; side: 'buy' | 'sell'; price: number | null }

interface Props {
  chain: Chain | null
  selectedId: number | null
  onPick: (pick: Pick) => void
  /** Quotes from the visitor's own market data feed, keyed by OCC symbol. Adds a "Live" column per side. */
  live?: Record<string, ExternalQuote>
}

/**
 * Calls on the left, puts on the right, strikes in the middle. Clicking an ask loads a buy ticket at that price;
 * clicking a bid loads a sell.
 */
export function ChainTable({ chain, selectedId, onPick, live }: Props) {
  if (!chain) return <div className="p-6 text-sm text-muted">Loading chain…</div>

  const showLive = !!live && Object.keys(live).length > 0
  const asOf = new Date(chain.time.endsWith('Z') ? chain.time : `${chain.time}Z`).getTime()
  const liveFor = (q: Quote | null) => (showLive && q ? live![q.occ] : undefined)
  const span = showLive ? 6 : 5
  const atm = chain.rows.reduce((best, r) => (Math.abs(r.strike - chain.spot) < Math.abs(best - chain.spot) ? r.strike : best), chain.rows[0]?.strike ?? 0)

  return (
    <div className="scroll-thin max-h-[468px] overflow-auto">
      <table className="w-full border-separate border-spacing-0 text-right text-[12.5px]">
        <thead className="sticky top-0 z-10 bg-panel text-[10.5px] uppercase tracking-wider text-muted">
          <tr>
            <th colSpan={span} className="border-b border-line py-1.5 text-center font-semibold text-buy/80">
              Calls
            </th>
            <th className="border-b border-line" />
            <th colSpan={span} className="border-b border-line py-1.5 text-center font-semibold text-sell/80">
              Puts
            </th>
          </tr>
          <tr className="[&>th]:border-b [&>th]:border-line [&>th]:px-2 [&>th]:py-1.5 [&>th]:font-medium">
            <th className="hidden md:table-cell">Δ</th>
            <th className="hidden lg:table-cell">IV</th>
            <th>Theo</th>
            {showLive && <th className="text-accent">Live</th>}
            <th>Bid</th>
            <th>Ask</th>
            <th className="text-center text-ink-2">Strike</th>
            <th>Bid</th>
            <th>Ask</th>
            {showLive && <th className="text-accent">Live</th>}
            <th>Theo</th>
            <th className="hidden lg:table-cell">IV</th>
            <th className="hidden md:table-cell">Δ</th>
          </tr>
        </thead>
        <tbody className="num">
          {chain.rows.map((row) => {
            const itmCall = row.strike < chain.spot
            const isAtm = row.strike === atm
            return (
              <tr key={row.strike} className={cn('group', isAtm && '[&>td]:border-y [&>td]:border-accent/25')}>
                <Side q={row.call} itm={itmCall} selectedId={selectedId} onPick={onPick} mirrored={false} live={liveFor(row.call)} showLive={showLive} asOf={asOf} />
                <td className={cn('bg-panel-2 px-3 text-center font-semibold', isAtm ? 'text-accent' : 'text-ink')}>{row.strike}</td>
                <Side q={row.put} itm={!itmCall && !isAtm} selectedId={selectedId} onPick={onPick} mirrored live={liveFor(row.put)} showLive={showLive} asOf={asOf} />
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

interface SideProps {
  q: Quote | null
  itm: boolean
  selectedId: number | null
  onPick: (p: Pick) => void
  mirrored: boolean
  live?: ExternalQuote
  showLive: boolean
  asOf: number
}

function Side({ q, itm, selectedId, onPick, mirrored, live, showLive, asOf }: SideProps) {
  if (!q) return <td colSpan={showLive ? 6 : 5} />
  const selected = q.id === selectedId
  const cell = cn('px-2 py-[5px]', itm && 'bg-panel-2/60', selected && 'bg-accent/10')
  const bid = (
    <td className={cell}>
      <button
        type="button"
        className="rounded px-1 text-sell hover:bg-sell-soft disabled:opacity-40"
        disabled={q.bid === null}
        onClick={() => onPick({ contractId: q.id, side: 'sell', price: q.bid })}
        title="Sell at bid"
      >
        <Flash value={q.bid}>{px(q.bid)}</Flash>
      </button>
    </td>
  )
  const ask = (
    <td className={cell}>
      <button
        type="button"
        className="rounded px-1 text-buy hover:bg-buy-soft disabled:opacity-40"
        disabled={q.ask === null}
        onClick={() => onPick({ contractId: q.id, side: 'buy', price: q.ask })}
        title="Buy at ask"
      >
        <Flash value={q.ask}>{px(q.ask)}</Flash>
      </button>
    </td>
  )
  const theo = <td className={cn(cell, 'text-ink-2')}>{px(q.theo)}</td>
  const stale = live ? asOf - new Date(live.time).getTime() > 30_000 : false
  const liveCell = showLive ? (
    <td
      className={cn(cell, 'whitespace-nowrap text-accent', stale && 'opacity-50')}
      title={
        live
          ? `${live.provider} · ${live.bidSize ?? '–'} × ${live.askSize ?? '–'}${live.impliedVol != null ? ` · IV ${(live.impliedVol * 100).toFixed(1)}%` : ''}${live.delta != null ? ` · Δ ${live.delta.toFixed(2)}` : ''}${live.last != null ? ` · last ${px(live.last)}` : ''}${stale ? ' · stale' : ''}`
          : 'No quote from your feed yet'
      }
    >
      {live ? `${px(live.bid)} × ${px(live.ask)}` : '—'}
    </td>
  ) : null
  const iv = <td className={cn(cell, 'hidden text-muted lg:table-cell')}>{(q.iv * 100).toFixed(1)}</td>
  const delta = <td className={cn(cell, 'hidden text-muted md:table-cell')}>{q.delta.toFixed(2)}</td>
  return mirrored ? (
    <>
      {bid}
      {ask}
      {liveCell}
      {theo}
      {iv}
      {delta}
    </>
  ) : (
    <>
      {delta}
      {iv}
      {theo}
      {liveCell}
      {bid}
      {ask}
    </>
  )
}
