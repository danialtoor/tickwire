import { px, qty } from '../lib/format'
import type { Book } from '../lib/types'

/** Five levels each side; bar length is quantity relative to the largest level shown. */
export function BookLadder({ book }: { book: Book | null }) {
  if (!book) return <div className="p-4 text-sm text-muted">Select a contract to see its order book.</div>
  const max = Math.max(1, ...book.bids.map((l) => l.quantity), ...book.asks.map((l) => l.quantity))
  const asks = [...book.asks].reverse()
  const spread = book.asks[0] && book.bids[0] ? book.asks[0].price - book.bids[0].price : null

  return (
    <div className="num text-[12px]">
      <div className="grid grid-cols-[1fr_auto_1fr] px-2 pb-1 text-[10.5px] uppercase tracking-wider text-muted">
        <span>Orders</span>
        <span>Price</span>
        <span className="text-right">Size</span>
      </div>
      {asks.map((l) => (
        <Row key={`a${l.price}`} price={l.price} quantity={l.quantity} orders={l.orders} width={l.quantity / max} tone="sell" />
      ))}
      <div className="my-1 flex items-center justify-between border-y border-line px-2 py-1 text-[11px] text-muted">
        <span>theo {px(book.theo)}</span>
        <span>spread {px(spread)}</span>
        <span>last {px(book.last)}</span>
      </div>
      {book.bids.map((l) => (
        <Row key={`b${l.price}`} price={l.price} quantity={l.quantity} orders={l.orders} width={l.quantity / max} tone="buy" />
      ))}
      {book.bids.length === 0 && <div className="px-2 py-1 text-muted">No bids</div>}
      <div className="mt-2 px-2 text-[11px] text-muted">Volume {qty(book.volume)}</div>
    </div>
  )
}

function Row({ price, quantity, orders, width, tone }: { price: number; quantity: number; orders: number; width: number; tone: 'buy' | 'sell' }) {
  return (
    <div className="relative grid grid-cols-[1fr_auto_1fr] px-2 py-[3px]">
      <div
        className={`absolute inset-y-0.5 right-0 rounded-sm ${tone === 'buy' ? 'bg-buy/12' : 'bg-sell/12'}`}
        style={{ width: `${Math.max(4, width * 100)}%` }}
      />
      <span className="relative text-muted">{orders}</span>
      <span className={`relative font-medium ${tone === 'buy' ? 'text-buy' : 'text-sell'}`}>{px(price)}</span>
      <span className="relative text-right">{qty(quantity)}</span>
    </div>
  )
}
