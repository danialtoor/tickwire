import { cn, px } from '../lib/format'
import type { PortfolioView } from '../lib/types'

const money = (v: number) => `${v < 0 ? '−' : ''}$${Math.abs(v).toLocaleString('en-US', { maximumFractionDigits: 0 })}`
const tone = (v: number) => (v > 0 ? 'text-buy' : v < 0 ? 'text-sell' : 'text-ink-2')
const num = (v: number, d = 0) => v.toLocaleString('en-US', { maximumFractionDigits: d, minimumFractionDigits: d })

export function Positions({ portfolio }: { portfolio: PortfolioView | null }) {
  if (!portfolio || (portfolio.positions.length === 0 && portfolio.realizedPnl === 0)) {
    return <div className="p-6 text-center text-sm text-muted">No positions yet. Fills show up here with P&amp;L and greeks.</div>
  }
  const p = portfolio
  return (
    <div className="space-y-3 p-3" data-testid="positions">
      <div className="num grid grid-cols-3 gap-2 sm:grid-cols-7">
        <Tile label="Total P&L" value={money(p.totalPnl)} className={tone(p.totalPnl)} />
        <Tile label="Realized" value={money(p.realizedPnl)} className={tone(p.realizedPnl)} />
        <Tile label="Unrealized" value={money(p.unrealizedPnl)} className={tone(p.unrealizedPnl)} />
        <Tile label="Δ shares" value={num(p.delta)} title="Net delta in shares of the underlying" />
        <Tile label="Γ /$1" value={num(p.gamma, 1)} title="Change in delta (shares) per $1 move" />
        <Tile label="Vega /pt" value={money(p.vega)} title="P&L per 1 vol point" />
        <Tile label="Θ /day" value={money(p.theta)} className={tone(p.theta)} title="P&L per day from time decay" />
      </div>
      <div className="scroll-thin max-h-[260px] overflow-auto">
        <table className="num w-full text-right text-[12px]">
          <thead className="sticky top-0 bg-panel text-[10.5px] uppercase tracking-wider text-muted">
            <tr className="[&>th]:border-b [&>th]:border-line [&>th]:px-2 [&>th]:py-1.5 [&>th]:font-medium">
              <th className="text-left">Contract</th>
              <th>Qty</th>
              <th>Avg</th>
              <th>Mark</th>
              <th>Unreal.</th>
              <th>Real.</th>
              <th>Δ</th>
              <th className="hidden md:table-cell">Vega</th>
              <th className="hidden md:table-cell">Θ</th>
            </tr>
          </thead>
          <tbody>
            {p.positions.map((pos) => (
              <tr key={pos.contract.id} className="border-b border-line/50">
                <td className="whitespace-nowrap px-2 py-1 text-left font-sans">{pos.contract.display}</td>
                <td className={cn('px-2 py-1', pos.quantity > 0 ? 'text-buy' : pos.quantity < 0 ? 'text-sell' : 'text-muted')}>{pos.quantity}</td>
                <td className="px-2 py-1">{px(pos.avgCost)}</td>
                <td className="px-2 py-1">{px(pos.mark)}</td>
                <td className={cn('px-2 py-1', tone(pos.unrealizedPnl))}>{money(pos.unrealizedPnl)}</td>
                <td className={cn('px-2 py-1', tone(pos.realizedPnl))}>{money(pos.realizedPnl)}</td>
                <td className="px-2 py-1">{num(pos.delta)}</td>
                <td className="hidden px-2 py-1 md:table-cell">{money(pos.vega)}</td>
                <td className="hidden px-2 py-1 md:table-cell">{money(pos.theta)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="text-[11px] text-muted">
        Marked at mid (theo when one side is missing). Delta and vega count toward your risk limits: orders that would push the portfolio
        past them are rejected. Expired positions settle at intrinsic value.
      </p>
    </div>
  )
}

function Tile({ label, value, className, title }: { label: string; value: string; className?: string; title?: string }) {
  return (
    <div className="rounded-lg bg-bg px-2 py-1.5" title={title}>
      <div className="font-sans text-[10.5px] text-muted">{label}</div>
      <div className={cn('text-[13px] font-semibold', className)}>{value}</div>
    </div>
  )
}
