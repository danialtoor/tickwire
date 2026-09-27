import { useState } from 'react'
import { api } from '../lib/api'
import { cn, px, usd } from '../lib/format'
import type { Book, Quote } from '../lib/types'

interface Props {
  quote: Quote | null
  book: Book | null
  token: string | null
  preset: { side: 'buy' | 'sell'; price: number | null; nonce: number } | null
  disabled?: boolean
  onSent: (message: string) => void
}

type Side = 'buy' | 'sell'
type OrdType = 'limit' | 'market'
type Tif = 'day' | 'ioc' | 'fok'

export function OrderTicket({ quote, book, token, preset, disabled, onSent }: Props) {
  const [side, setSide] = useState<Side>('buy')
  const [quantity, setQuantity] = useState(10)
  const [type, setType] = useState<OrdType>('limit')
  const [tif, setTif] = useState<Tif>('day')
  const [price, setPrice] = useState<string>('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // A click in the chain loads side and price. Applied during render when a new preset arrives (derived state).
  const [appliedNonce, setAppliedNonce] = useState<number | null>(null)
  if (preset && preset.nonce !== appliedNonce) {
    setAppliedNonce(preset.nonce)
    setSide(preset.side)
    if (preset.price !== null) setPrice(preset.price.toFixed(2))
    setError(null)
  }

  const priceNum = Number(price)
  const refPx = type === 'limit' && priceNum > 0 ? priceNum : side === 'buy' ? quote?.ask : quote?.bid
  const notional = refPx ? refPx * quantity * 100 : null
  const offTheo = quote && type === 'limit' && priceNum > 0 ? ((priceNum - quote.theo) / Math.max(quote.theo, 0.01)) * 100 : null

  const submit = async () => {
    if (!quote || !token) return
    setBusy(true)
    setError(null)
    try {
      const res = await api.placeOrder(token, {
        contractId: quote.id,
        side,
        type,
        tif,
        price: type === 'limit' ? priceNum : null,
        quantity,
      })
      onSent(`NewOrderSingle sent · 11=${res.clOrdID}`)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const setMid = () => {
    if (quote?.bid != null && quote.ask != null) setPrice(((quote.bid + quote.ask) / 2).toFixed(2))
  }

  return (
    <div className="space-y-3">
      <div className="flex items-baseline justify-between gap-2">
        <div>
          <div className="text-sm font-semibold">{book?.display ?? 'Pick a contract from the chain'}</div>
          <div className="num text-[11px] text-muted">{quote?.occ ?? '—'}</div>
        </div>
        {quote && (
          <div className="num text-right text-[11px] text-muted">
            theo <span className="text-ink-2">{px(quote.theo)}</span>
            <br />
            IV {(quote.iv * 100).toFixed(1)}% · Δ {quote.delta.toFixed(2)}
          </div>
        )}
      </div>

      <div className="grid grid-cols-2 gap-1 rounded-lg bg-bg p-1">
        {(['buy', 'sell'] as const).map((s) => (
          <button
            key={s}
            type="button"
            onClick={() => setSide(s)}
            className={cn(
              'rounded-md py-1.5 text-sm font-semibold capitalize transition-colors',
              side === s ? (s === 'buy' ? 'bg-buy text-bg' : 'bg-sell text-bg') : 'text-ink-2 hover:text-ink',
            )}
          >
            {s}
          </button>
        ))}
      </div>

      <div className="grid grid-cols-2 gap-2">
        <Field label="Quantity">
          <input
            type="number"
            min={1}
            step={1}
            value={quantity}
            onChange={(e) => setQuantity(Math.max(1, Math.floor(Number(e.target.value) || 1)))}
            className="num w-full rounded-md border border-line bg-bg px-2 py-1.5 text-sm"
            aria-label="Quantity"
          />
        </Field>
        <Field label="Limit price">
          <div className="flex gap-1">
            <input
              type="number"
              step={0.01}
              min={0.01}
              value={type === 'market' ? '' : price}
              placeholder={type === 'market' ? 'Market' : ''}
              disabled={type === 'market'}
              onChange={(e) => setPrice(e.target.value)}
              className="num w-full min-w-0 rounded-md border border-line bg-bg px-2 py-1.5 text-sm disabled:opacity-50"
              aria-label="Limit price"
            />
            <button type="button" onClick={setMid} disabled={type === 'market'} className="rounded-md border border-line px-2 text-xs text-ink-2 hover:text-ink disabled:opacity-40">
              Mid
            </button>
          </div>
        </Field>
        <Field label="Type">
          <Segmented value={type} options={['limit', 'market']} onChange={(v) => setType(v as OrdType)} />
        </Field>
        <Field label="Time in force">
          <Segmented value={tif} options={['day', 'ioc', 'fok']} onChange={(v) => setTif(v as Tif)} />
        </Field>
      </div>

      <div className="num flex justify-between text-[11px] text-muted">
        <span>Notional {notional ? usd(notional) : '—'}</span>
        {offTheo !== null && (
          <span className={cn(Math.abs(offTheo) > 50 && 'text-warn')}>
            {offTheo >= 0 ? '+' : ''}
            {offTheo.toFixed(0)}% vs theo
          </span>
        )}
      </div>

      <button
        type="button"
        data-testid="ticket-submit"
        onClick={submit}
        disabled={!quote || !token || busy || disabled || (type === 'limit' && !(priceNum > 0))}
        className={cn(
          'w-full rounded-lg py-2.5 text-sm font-semibold transition-opacity disabled:cursor-not-allowed disabled:opacity-40',
          side === 'buy' ? 'bg-buy text-bg' : 'bg-sell text-bg',
        )}
      >
        {busy
          ? 'Sending…'
          : type === 'limit' && !(priceNum > 0)
            ? 'Enter a limit price'
            : `${side === 'buy' ? 'Buy' : 'Sell'} ${quantity} ${book ? book.strike + book.right : ''}${type === 'limit' ? ` @ ${priceNum.toFixed(2)}` : ' at market'}`}
      </button>
      {error && <p className="rounded-md bg-sell-soft px-2 py-1.5 text-xs text-sell">{error}</p>}
      <p className="text-[11px] leading-relaxed text-muted">
        Guest limits: 100 contracts per order, $50k notional, ±50% of theo. Try breaking one to see a risk reject in the FIX Inspector.
      </p>
    </div>
  )
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block space-y-1">
      <span className="text-[11px] text-muted">{label}</span>
      {children}
    </label>
  )
}

function Segmented({ value, options, onChange }: { value: string; options: string[]; onChange: (v: string) => void }) {
  return (
    <div className="flex rounded-md border border-line bg-bg p-0.5">
      {options.map((o) => (
        <button
          key={o}
          type="button"
          onClick={() => onChange(o)}
          className={cn('flex-1 rounded px-1 py-1 text-xs uppercase', value === o ? 'bg-panel-2 text-ink' : 'text-muted hover:text-ink-2')}
        >
          {o}
        </button>
      ))}
    </div>
  )
}
