import { useState } from 'react'
import { api } from '../lib/api'
import { cn, px, qty, time } from '../lib/format'
import type { OrderRow } from '../lib/types'

const statusTone: Record<string, string> = {
  New: 'bg-info-soft text-info',
  PendingNew: 'bg-panel-2 text-ink-2',
  PartiallyFilled: 'bg-warn-soft text-warn',
  Filled: 'bg-buy-soft text-buy',
  Canceled: 'bg-panel-2 text-muted',
  PendingCancel: 'bg-panel-2 text-ink-2',
  PendingReplace: 'bg-panel-2 text-ink-2',
  Rejected: 'bg-sell-soft text-sell',
  Expired: 'bg-panel-2 text-muted',
}

const label = (s: string) => s.replace(/([a-z])([A-Z])/g, '$1 $2')

interface Props {
  rows: OrderRow[]
  token: string | null
  selectedClOrdId: string | null
  onSelect: (clOrdId: string | null) => void
  onAction: (message: string) => void
}

export function Blotter({ rows, token, selectedClOrdId, onSelect, onAction }: Props) {
  const [editing, setEditing] = useState<string | null>(null)
  const [editQty, setEditQty] = useState(0)
  const [editPx, setEditPx] = useState('')

  if (rows.length === 0) {
    return <div className="p-6 text-center text-sm text-muted">No orders yet. Click a bid or ask in the chain to load the ticket.</div>
  }

  const act = async (fn: () => Promise<{ clOrdID: string }>, what: string) => {
    try {
      const res = await fn()
      onAction(`${what} sent · 11=${res.clOrdID}`)
    } catch (e) {
      onAction(`${what} failed: ${e instanceof Error ? e.message : e}`)
    }
  }

  return (
    <div className="scroll-thin max-h-[360px] overflow-auto" data-testid="blotter">
      <table className="w-full text-left text-[12.5px]">
        <thead className="sticky top-0 z-10 bg-panel text-[10.5px] uppercase tracking-wider text-muted">
          <tr className="[&>th]:border-b [&>th]:border-line [&>th]:px-2 [&>th]:py-1.5 [&>th]:font-medium">
            <th className="hidden 2xl:table-cell">Time</th>
            <th>Contract</th>
            <th>Side</th>
            <th className="text-right">Qty</th>
            <th className="text-right">Price</th>
            <th>Filled</th>
            <th className="hidden text-right 2xl:table-cell">Avg</th>
            <th>Status</th>
            <th className="hidden xl:table-cell" title="Exchange the order went to (LastMkt on fills); fees are per contract, negative is a rebate">
              Venue
            </th>
            <th className="hidden 2xl:table-cell" title="What the FIX client rebuilt from the ExecutionReports it received">Client</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {rows.map(({ order: o, clientView, inSync }) => {
            const open = ['New', 'PartiallyFilled', 'PendingNew'].includes(o.status)
            const selected = selectedClOrdId !== null && (o.clOrdID === selectedClOrdId || o.origClOrdID === selectedClOrdId)
            return (
              <tr
                key={o.orderId}
                onClick={() => onSelect(selected ? null : o.clOrdID)}
                className={cn('cursor-pointer border-b border-line/60 hover:bg-panel-2/60', selected && 'bg-accent/8')}
              >
                <td className="num hidden px-2 py-1.5 text-muted 2xl:table-cell">{time(o.created).slice(0, 8)}</td>
                <td className="px-2 py-1.5">
                  <div className="whitespace-nowrap font-medium">{o.display}</div>
                  {o.legs && (
                    <div className="num text-[10.5px] text-ink-2">
                      {o.legs.map((l) => `${l.side === 'Buy' ? '+' : '−'}${l.ratio} ${l.display.split(' ').slice(-2).join('')}`).join('  ')}
                    </div>
                  )}
                  <div className="num text-[10.5px] text-muted" title="ClOrdID(11)">
                    {o.clOrdID}
                  </div>
                </td>
                <td className={cn('px-2 py-1.5 font-semibold', o.side === 'Buy' ? 'text-buy' : 'text-sell')}>
                  {o.side} <span className="text-[10.5px] font-normal text-muted">{o.type === 'Market' ? 'MKT' : o.tif}</span>
                </td>
                <td className="num px-2 py-1.5 text-right">
                  {editing === o.orderId ? (
                    <input
                      type="number"
                      className="w-16 rounded border border-line bg-bg px-1 text-right"
                      value={editQty}
                      onClick={(e) => e.stopPropagation()}
                      onChange={(e) => setEditQty(Number(e.target.value))}
                    />
                  ) : (
                    qty(o.quantity)
                  )}
                </td>
                <td className="num px-2 py-1.5 text-right">
                  {editing === o.orderId ? (
                    <input
                      type="number"
                      step={0.01}
                      className="w-20 rounded border border-line bg-bg px-1 text-right"
                      value={editPx}
                      onClick={(e) => e.stopPropagation()}
                      onChange={(e) => setEditPx(e.target.value)}
                    />
                  ) : (
                    px(o.price)
                  )}
                </td>
                <td className="px-2 py-1.5">
                  <div className="num flex items-center gap-2 text-[11.5px]">
                    <div className="h-1.5 w-10 shrink-0 overflow-hidden rounded-full bg-panel-2">
                      <div className="h-full rounded-full bg-buy" style={{ width: `${(o.cumQty / Math.max(o.quantity, 1)) * 100}%` }} />
                    </div>
                    {qty(o.cumQty)}/{qty(o.quantity)}
                  </div>
                </td>
                <td className="num hidden px-2 py-1.5 text-right 2xl:table-cell">{o.cumQty > 0 ? px(o.avgPx, 3) : '—'}</td>
                <td className="px-2 py-1.5">
                  <span className={cn('whitespace-nowrap rounded px-1.5 py-0.5 text-[11px] font-medium', statusTone[o.status] ?? 'bg-panel-2')} title={o.text ?? undefined}>
                    {label(o.status)}
                  </span>
                  {clientView && !inSync && <span className="ml-1 text-[10.5px] text-warn" title="The FIX client hasn't seen every report yet">…</span>}
                </td>
                <td className="num hidden px-2 py-1.5 text-[11px] xl:table-cell">
                  <span className="text-ink-2">{o.exchange ?? '—'}</span>
                  {o.destination && <span className="ml-1 text-muted" title="Directed with ExDestination(100)">dir</span>}
                  {!!o.fees && (
                    <span className={cn('ml-1.5', o.fees < 0 ? 'text-buy' : 'text-muted')} title="Exchange fees so far">
                      {o.fees < 0 ? '+' : '−'}${Math.abs(o.fees).toFixed(2)}
                    </span>
                  )}
                </td>
                <td className="hidden px-2 py-1.5 text-[11px] 2xl:table-cell">
                  {clientView ? (
                    <span className={inSync ? 'text-buy' : 'text-warn'} title={`${clientView.reportsSeen} ExecutionReports applied`}>
                      {inSync ? '✓ in sync' : '… catching up'}
                    </span>
                  ) : (
                    <span className="text-muted">waiting</span>
                  )}
                </td>
                <td className="whitespace-nowrap px-2 py-1.5 text-right" onClick={(e) => e.stopPropagation()}>
                  {open && token && editing !== o.orderId && (
                    <>
                      {!o.legs && (
                      <button
                        type="button"
                        className="rounded px-1.5 py-0.5 text-[11px] text-ink-2 hover:bg-panel-2 hover:text-ink"
                        onClick={() => {
                          setEditing(o.orderId)
                          setEditQty(o.quantity)
                          setEditPx(o.price?.toFixed(2) ?? '')
                        }}
                      >
                        Modify
                      </button>
                      )}
                      <button
                        type="button"
                        className="rounded px-1.5 py-0.5 text-[11px] text-sell hover:bg-sell-soft"
                        onClick={() => act(() => api.cancelOrder(token, o.orderId), 'OrderCancelRequest')}
                      >
                        Cancel
                      </button>
                    </>
                  )}
                  {editing === o.orderId && token && (
                    <>
                      <button
                        type="button"
                        className="rounded px-1.5 py-0.5 text-[11px] text-accent hover:bg-panel-2"
                        onClick={() => {
                          setEditing(null)
                          void act(() => api.replaceOrder(token, o.orderId, editPx ? Number(editPx) : null, editQty), 'OrderCancelReplaceRequest')
                        }}
                      >
                        Send
                      </button>
                      <button type="button" className="rounded px-1.5 py-0.5 text-[11px] text-muted hover:text-ink" onClick={() => setEditing(null)}>
                        ×
                      </button>
                    </>
                  )}
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}
