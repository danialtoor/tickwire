import { cn, px } from '../lib/format'
import type { Chain, Quote } from '../lib/types'
import { Flash } from './Flash'

export type Pick = { contractId: number; side: 'buy' | 'sell'; price: number | null }

interface Props {
  chain: Chain | null
  selectedId: number | null
  onPick: (pick: Pick) => void
}

/**
 * Calls on the left, puts on the right, strikes in the middle. Clicking an ask loads a buy ticket at that price;
 * clicking a bid loads a sell.
 */
export function ChainTable({ chain, selectedId, onPick }: Props) {
  if (!chain) return <div className="p-6 text-sm text-muted">Loading chain…</div>

  const atm = chain.rows.reduce((best, r) => (Math.abs(r.strike - chain.spot) < Math.abs(best - chain.spot) ? r.strike : best), chain.rows[0]?.strike ?? 0)

  return (
    <div className="scroll-thin max-h-[468px] overflow-auto">
      <table className="w-full border-separate border-spacing-0 text-right text-[12.5px]">
        <thead className="sticky top-0 z-10 bg-panel text-[10.5px] uppercase tracking-wider text-muted">
          <tr>
            <th colSpan={5} className="border-b border-line py-1.5 text-center font-semibold text-buy/80">
              Calls
            </th>
            <th className="border-b border-line" />
            <th colSpan={5} className="border-b border-line py-1.5 text-center font-semibold text-sell/80">
              Puts
            </th>
          </tr>
          <tr className="[&>th]:border-b [&>th]:border-line [&>th]:px-2 [&>th]:py-1.5 [&>th]:font-medium">
            <th className="hidden md:table-cell">Δ</th>
            <th className="hidden lg:table-cell">IV</th>
            <th>Theo</th>
            <th>Bid</th>
            <th>Ask</th>
            <th className="text-center text-ink-2">Strike</th>
            <th>Bid</th>
            <th>Ask</th>
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
                <Side q={row.call} itm={itmCall} selectedId={selectedId} onPick={onPick} mirrored={false} />
                <td className={cn('bg-panel-2 px-3 text-center font-semibold', isAtm ? 'text-accent' : 'text-ink')}>{row.strike}</td>
                <Side q={row.put} itm={!itmCall && !isAtm} selectedId={selectedId} onPick={onPick} mirrored />
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

function Side({ q, itm, selectedId, onPick, mirrored }: { q: Quote | null; itm: boolean; selectedId: number | null; onPick: (p: Pick) => void; mirrored: boolean }) {
  if (!q) return <td colSpan={5} />
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
  const iv = <td className={cn(cell, 'hidden text-muted lg:table-cell')}>{(q.iv * 100).toFixed(1)}</td>
  const delta = <td className={cn(cell, 'hidden text-muted md:table-cell')}>{q.delta.toFixed(2)}</td>
  return mirrored ? (
    <>
      {bid}
      {ask}
      {theo}
      {iv}
      {delta}
    </>
  ) : (
    <>
      {delta}
      {iv}
      {theo}
      {bid}
      {ask}
    </>
  )
}
