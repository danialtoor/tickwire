import { useMemo, useState } from 'react'
import { api } from '../lib/api'
import { cn, px } from '../lib/format'
import type { Chain, Quote } from '../lib/types'

type Template = 'call-vertical' | 'put-vertical' | 'straddle' | 'strangle' | 'call-butterfly'

const templates: { id: Template; label: string; hint: string }[] = [
  { id: 'call-vertical', label: 'Call vertical', hint: 'Buy the strike, sell the next one up' },
  { id: 'put-vertical', label: 'Put vertical', hint: 'Buy the strike, sell the next one down' },
  { id: 'straddle', label: 'Straddle', hint: 'Buy the call and put at the strike' },
  { id: 'strangle', label: 'Strangle', hint: 'Buy the call above and the put below' },
  { id: 'call-butterfly', label: 'Call butterfly', hint: '+1 below, −2 at, +1 above' },
]

interface Leg {
  quote: Quote
  label: string
  ratio: number
  side: 'buy' | 'sell'
}

/** Builds the legs of a template around an anchor strike, using neighbouring strikes in the chain. */
function buildLegs(chain: Chain, anchorStrike: number, t: Template): Leg[] | null {
  const i = chain.rows.findIndex((r) => r.strike === anchorStrike)
  const row = (k: number) => chain.rows[i + k]
  const call = (k: number) => row(k)?.call
  const put = (k: number) => row(k)?.put
  const leg = (q: Quote | null | undefined, kind: 'C' | 'P', k: number, ratio: number, side: 'buy' | 'sell'): Leg | null =>
    q ? { quote: q, label: `${row(k)!.strike}${kind}`, ratio, side } : null
  const all = (legs: (Leg | null)[]) => (legs.every(Boolean) ? (legs as Leg[]) : null)
  if (i < 0) return null
  switch (t) {
    case 'call-vertical':
      return all([leg(call(0), 'C', 0, 1, 'buy'), leg(call(1), 'C', 1, 1, 'sell')])
    case 'put-vertical':
      return all([leg(put(0), 'P', 0, 1, 'buy'), leg(put(-1), 'P', -1, 1, 'sell')])
    case 'straddle':
      return all([leg(call(0), 'C', 0, 1, 'buy'), leg(put(0), 'P', 0, 1, 'buy')])
    case 'strangle':
      return all([leg(call(1), 'C', 1, 1, 'buy'), leg(put(-1), 'P', -1, 1, 'buy')])
    case 'call-butterfly':
      return all([leg(call(-1), 'C', -1, 1, 'buy'), leg(call(0), 'C', 0, 2, 'sell'), leg(call(1), 'C', 1, 1, 'buy')])
  }
}

const round2 = (v: number) => Math.round(v * 100) / 100

export function SpreadTicket({ chain, anchorStrike, token, disabled, onSent }: {
  chain: Chain | null
  anchorStrike: number | null
  token: string | null
  disabled?: boolean
  onSent: (message: string) => void
}) {
  const [template, setTemplate] = useState<Template>('call-vertical')
  const [side, setSide] = useState<'buy' | 'sell'>('buy')
  const [quantity, setQuantity] = useState(1)
  const [tif, setTif] = useState<'day' | 'ioc'>('day')
  const [price, setPrice] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const legs = useMemo(() => (chain && anchorStrike !== null ? buildLegs(chain, anchorStrike, template) : null), [chain, anchorStrike, template])

  // Net prices in the legs' own direction: + for bought legs, − for sold legs.
  const sign = (l: Leg) => (l.side === 'buy' ? 1 : -1)
  const theo = legs ? round2(legs.reduce((s, l) => s + sign(l) * l.ratio * l.quote.theo, 0)) : null
  const mid = legs?.every((l) => l.quote.bid !== null && l.quote.ask !== null)
    ? round2(legs.reduce((s, l) => s + sign(l) * l.ratio * ((l.quote.bid! + l.quote.ask!) / 2), 0))
    : null
  // Natural: what you'd pay buying (buy legs at the ask, sell legs at the bid) or receive selling (the reverse).
  const natural = legs?.every((l) => l.quote.bid !== null && l.quote.ask !== null)
    ? round2(
        legs.reduce((s, l) => {
          const buyingThisLeg = (side === 'buy') === (l.side === 'buy')
          return s + sign(l) * l.ratio * (buyingThisLeg ? l.quote.ask! : l.quote.bid!)
        }, 0),
      )
    : null

  const priceNum = price === '' ? null : Number(price)
  const effectivePrice = priceNum ?? natural

  const submit = async () => {
    if (!legs || !token || effectivePrice === null) return
    setBusy(true)
    setError(null)
    try {
      const res = await api.placeSpread(token, {
        legs: legs.map((l) => ({ contractId: l.quote.id, ratio: l.ratio, side: l.side })),
        side,
        tif,
        price: effectivePrice,
        quantity,
      })
      onSent(`NewOrderMultileg sent · 11=${res.clOrdID}`)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="space-y-3">
      <div className="grid grid-cols-2 gap-1">
        {templates.map((t) => (
          <button
            key={t.id}
            type="button"
            title={t.hint}
            onClick={() => {
              setTemplate(t.id)
              setPrice('')
            }}
            className={cn('rounded-md border px-2 py-1.5 text-left text-xs', template === t.id ? 'border-accent/50 bg-accent/10 text-ink' : 'border-line text-ink-2 hover:text-ink')}
          >
            {t.label}
          </button>
        ))}
      </div>

      {legs ? (
        <table className="num w-full text-[11.5px]">
          <thead className="text-[10px] uppercase tracking-wider text-muted">
            <tr>
              <th className="text-left font-medium">Leg</th>
              <th className="text-right font-medium">Bid</th>
              <th className="text-right font-medium">Ask</th>
            </tr>
          </thead>
          <tbody>
            {legs.map((l) => (
              <tr key={l.quote.id} className="border-t border-line/50">
                <td className={cn('py-1', l.side === 'buy' ? 'text-buy' : 'text-sell')}>
                  {l.side === 'buy' ? '+' : '−'}
                  {l.ratio} {chain?.underlying} {l.label}
                </td>
                <td className="py-1 text-right">{px(l.quote.bid)}</td>
                <td className="py-1 text-right">{px(l.quote.ask)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <p className="text-[12px] text-muted">Pick a strike in the chain with room on both sides for this strategy.</p>
      )}

      <div className="num grid grid-cols-3 gap-2 rounded-lg bg-bg p-2 text-center text-[11px]">
        <div>
          <div className="text-muted">Natural</div>
          <div className="text-ink">{px(natural)}</div>
        </div>
        <div>
          <div className="text-muted">Mid</div>
          <div className="text-ink">{px(mid)}</div>
        </div>
        <div>
          <div className="text-muted">Theo</div>
          <div className="text-ink">{px(theo)}</div>
        </div>
      </div>

      <div className="grid grid-cols-2 gap-1 rounded-lg bg-bg p-1">
        {(['buy', 'sell'] as const).map((s) => (
          <button
            key={s}
            type="button"
            onClick={() => {
              setSide(s)
              setPrice('')
            }}
            className={cn('rounded-md py-1.5 text-sm font-semibold capitalize', side === s ? (s === 'buy' ? 'bg-buy text-bg' : 'bg-sell text-bg') : 'text-ink-2 hover:text-ink')}
          >
            {s} spread
          </button>
        ))}
      </div>

      <div className="grid grid-cols-3 gap-2">
        <label className="block space-y-1">
          <span className="text-[11px] text-muted">Spreads</span>
          <input
            type="number"
            min={1}
            value={quantity}
            onChange={(e) => setQuantity(Math.max(1, Math.floor(Number(e.target.value) || 1)))}
            className="num w-full rounded-md border border-line bg-bg px-2 py-1.5 text-sm"
          />
        </label>
        <label className="block space-y-1">
          <span className="text-[11px] text-muted">Net price</span>
          <input
            type="number"
            step={0.01}
            value={price}
            placeholder={natural !== null ? natural.toFixed(2) : ''}
            onChange={(e) => setPrice(e.target.value)}
            className="num w-full rounded-md border border-line bg-bg px-2 py-1.5 text-sm"
          />
        </label>
        <label className="block space-y-1">
          <span className="text-[11px] text-muted">TIF</span>
          <select value={tif} onChange={(e) => setTif(e.target.value as 'day' | 'ioc')} className="w-full rounded-md border border-line bg-bg px-1 py-1.5 text-sm">
            <option value="day">DAY</option>
            <option value="ioc">IOC</option>
          </select>
        </label>
      </div>

      <button
        type="button"
        data-testid="spread-submit"
        onClick={submit}
        disabled={!legs || !token || busy || disabled || effectivePrice === null}
        className={cn('w-full rounded-lg py-2.5 text-sm font-semibold disabled:cursor-not-allowed disabled:opacity-40', side === 'buy' ? 'bg-buy text-bg' : 'bg-sell text-bg')}
      >
        {busy ? 'Sending…' : `${side === 'buy' ? 'Buy' : 'Sell'} ${quantity} ${templates.find((t) => t.id === template)!.label.toLowerCase()} @ ${px(effectivePrice)} net`}
      </button>
      {error && <p className="rounded-md bg-sell-soft px-2 py-1.5 text-xs text-sell">{error}</p>}
      <p className="text-[11px] leading-relaxed text-muted">
        Sent as one NewOrderMultileg (35=AB). All legs trade together against the outright books, only when the net price reaches your
        limit; the Inspector shows a strategy report (442=3) and a report per leg (442=2).
      </p>
    </div>
  )
}
