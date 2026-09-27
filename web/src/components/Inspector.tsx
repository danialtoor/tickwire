import { useEffect, useMemo, useRef, useState } from 'react'
import { decode, isAdminMsgType, parse, summarize, type DecodedField } from '../fix/decoder'
import { cn, time } from '../lib/format'
import type { SessionLog, WireEvent } from '../lib/types'

export type InspectorView = 'venue' | 'client' | 'both'

interface Props {
  wire: WireEvent[]
  logs: SessionLog[]
  view: InspectorView
  onViewChange: (v: InspectorView) => void
  orderFilter: string | null
  onClearOrderFilter: () => void
}

type Row = { kind: 'wire'; id: number; event: WireEvent } | { kind: 'log'; id: number; log: SessionLog }

const dispositionStyle: Record<string, { label: string; className: string }> = {
  DroppedByFault: { label: 'dropped', className: 'bg-sell-soft text-sell line-through' },
  CorruptedByFault: { label: 'corrupted', className: 'bg-sell-soft text-sell' },
  Garbled: { label: 'garbled · ignored', className: 'bg-sell-soft text-sell' },
  Rejected: { label: 'rejected', className: 'bg-sell-soft text-sell' },
  Queued: { label: 'queued (gap)', className: 'bg-info-soft text-info' },
  Resent: { label: 'resent', className: 'bg-warn-soft text-warn' },
  Duplicate: { label: 'duplicate · ignored', className: 'bg-panel-2 text-muted' },
  StoredWhileOffline: { label: 'stored (offline)', className: 'bg-panel-2 text-ink-2' },
}

function typeTone(msgType: string): string {
  if (msgType === '3' || msgType === 'j' || msgType === '9') return 'bg-sell-soft text-sell'
  if (msgType === '8') return 'bg-accent/12 text-accent'
  if (msgType === 'D' || msgType === 'F' || msgType === 'G' || msgType === 'H') return 'bg-info-soft text-info'
  if (msgType === '2' || msgType === '4') return 'bg-warn-soft text-warn'
  if (isAdminMsgType(msgType)) return 'bg-admin-soft text-admin'
  return 'bg-panel-2 text-ink-2'
}

export function Inspector({ wire, logs, view, onViewChange, orderFilter, onClearOrderFilter }: Props) {
  const [showHeartbeats, setShowHeartbeats] = useState(false)
  const [follow, setFollow] = useState(true)
  const [selectedId, setSelectedId] = useState<number | null>(null)
  const listRef = useRef<HTMLDivElement>(null)

  const rows = useMemo<Row[]>(() => {
    const matchesSide = (side: string) => view === 'both' || side === view
    const w: Row[] = wire
      .filter((e) => matchesSide(e.side))
      .filter((e) => showHeartbeats || e.msgType !== '0' || e.disposition !== 'Normal')
      .filter((e) => !orderFilter || (e.clOrdID !== null && (e.clOrdID === orderFilter || e.raw.includes(`|41=${orderFilter}|`))))
      .map((event) => ({ kind: 'wire', id: event.id, event }))
    const l: Row[] = orderFilter ? [] : logs.filter((x) => matchesSide(x.side)).map((log) => ({ kind: 'log', id: log.id, log }))
    return [...w, ...l].sort((a, b) => a.id - b.id).slice(-500)
  }, [wire, logs, view, showHeartbeats, orderFilter])

  useEffect(() => {
    if (follow && listRef.current) listRef.current.scrollTop = listRef.current.scrollHeight
  }, [rows, follow])

  // With nothing picked, show the newest non-heartbeat message so the detail pane is never empty.
  const fallback = useMemo(() => {
    for (let i = rows.length - 1; i >= 0; i--) {
      const r = rows[i]
      if (r.kind === 'wire' && r.event.msgType !== '0') return r.event
    }
    return null
  }, [rows])
  const selected = wire.find((w) => w.id === selectedId) ?? fallback

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b border-line px-3 py-2">
        <h2 className="panel-title mr-1">FIX Inspector</h2>
        <div className="flex rounded-md border border-line bg-bg p-0.5 text-[11px]">
          {(['venue', 'client', 'both'] as const).map((v) => (
            <button
              key={v}
              type="button"
              onClick={() => onViewChange(v)}
              className={cn('rounded px-2 py-0.5 capitalize', view === v ? 'bg-panel-2 text-ink' : 'text-muted hover:text-ink-2')}
              title={v === 'venue' ? "The gateway's side of the session" : v === 'client' ? "The trader's FIX client" : 'Both ends'}
            >
              {v === 'venue' ? 'Venue view' : v === 'client' ? 'Client view' : 'Both'}
            </button>
          ))}
        </div>
        <Toggle label="Heartbeats" value={showHeartbeats} onChange={setShowHeartbeats} />
        <Toggle label="Follow" value={follow} onChange={setFollow} />
        {orderFilter && (
          <button type="button" onClick={onClearOrderFilter} className="rounded-full bg-accent/12 px-2 py-0.5 text-[11px] text-accent">
            11={orderFilter} ×
          </button>
        )}
        <span className="ml-auto text-[11px] text-muted">{rows.length} rows</span>
      </div>

      <div className="grid min-h-0 flex-1 grid-cols-1 lg:grid-cols-[minmax(0,1.25fr)_minmax(0,1fr)]">
        <div ref={listRef} className="scroll-thin min-h-[220px] overflow-auto border-line lg:border-r" onWheel={() => setFollow(false)}>
          {rows.length === 0 && <div className="p-6 text-center text-sm text-muted">Waiting for traffic…</div>}
          <ul className="num text-[11.5px]">
            {rows.map((r) =>
              r.kind === 'log' ? (
                <LogRow key={`l${r.id}`} log={r.log} view={view} />
              ) : (
                <WireRow key={`w${r.id}`} e={r.event} view={view} selected={r.id === selected?.id} onSelect={() => setSelectedId(r.id)} />
              ),
            )}
          </ul>
        </div>
        <div className="scroll-thin min-h-[220px] overflow-auto">
          {selected ? <Detail event={selected} /> : <div className="p-6 text-sm text-muted">Select a message to decode it tag by tag.</div>}
        </div>
      </div>
    </div>
  )
}

function Toggle({ label, value, onChange }: { label: string; value: boolean; onChange: (v: boolean) => void }) {
  return (
    <label className="flex cursor-pointer items-center gap-1.5 text-[11px] text-ink-2">
      <input type="checkbox" checked={value} onChange={(e) => onChange(e.target.checked)} className="accent-[var(--color-accent)]" />
      {label}
    </label>
  )
}

function WireRow({ e, view, selected, onSelect }: { e: WireEvent; view: InspectorView; selected: boolean; onSelect: () => void }) {
  const parsed = useMemo(() => parse(e.raw), [e.raw])
  const d = dispositionStyle[e.disposition]
  const arrow = e.direction === 'out' ? '→' : '←'
  const who = view === 'both' ? (e.side === 'venue' ? 'V' : 'C') : ''
  return (
    <li
      onClick={onSelect}
      className={cn(
        'flash-new grid cursor-pointer grid-cols-[74px_28px_40px_minmax(0,1fr)] items-center gap-1.5 border-b border-line/50 px-2 py-1 hover:bg-panel-2/70',
        selected && 'bg-panel-2',
        e.disposition === 'DroppedByFault' && 'opacity-70',
      )}
    >
      <span className="text-muted">{time(e.time).slice(0, 12)}</span>
      <span className={cn('text-center', e.direction === 'out' ? 'text-ink-2' : 'text-accent')} title={e.direction === 'out' ? 'sent' : 'received'}>
        {who}
        {arrow}
      </span>
      <span className="text-right text-muted" title="MsgSeqNum(34)">
        {e.seqNum}
      </span>
      <span className="flex min-w-0 items-center gap-1.5">
        <span className={cn('shrink-0 rounded px-1.5 py-px text-[10.5px] font-medium', typeTone(e.msgType))}>{e.msgTypeName}</span>
        {e.possDup && <span className="shrink-0 rounded bg-warn-soft px-1 py-px text-[10px] text-warn">43=Y</span>}
        {d && <span className={cn('shrink-0 rounded px-1 py-px text-[10px]', d.className)}>{d.label}</span>}
        <span className="truncate font-sans text-ink-2">{parsed ? summarize(parsed) : e.note}</span>
      </span>
    </li>
  )
}

function LogRow({ log, view }: { log: SessionLog; view: InspectorView }) {
  const tone = log.level === 'Error' ? 'text-sell' : log.level === 'Warning' ? 'text-warn' : 'text-muted'
  return (
    <li className={cn('border-b border-line/50 px-2 py-1 font-sans text-[11.5px] italic', tone)}>
      <span className="num not-italic text-muted">{time(log.time).slice(0, 12)}</span>{' '}
      {view === 'both' && <span className="not-italic">[{log.side}] </span>}
      {log.text}
    </li>
  )
}

function Detail({ event }: { event: WireEvent }) {
  const parsed = parse(event.raw)
  if (!parsed) return <pre className="num whitespace-pre-wrap break-all p-3 text-[11px] text-ink-2">{event.raw}</pre>
  const fields = decode(parsed)
  return (
    <div className="space-y-3 p-3">
      <div className="flex flex-wrap items-baseline gap-2">
        <span className={cn('rounded px-1.5 py-0.5 text-xs font-semibold', typeTone(parsed.msgType))}>
          35={parsed.msgType} {parsed.msgTypeName}
        </span>
        <span className="num text-xs text-muted">
          34={parsed.seqNum} · {parsed.sender} → {parsed.target}
        </span>
      </div>
      {event.note && <p className="text-xs text-warn">{event.note}</p>}
      <div className="num flex gap-3 text-[11px]">
        <Check ok={parsed.bodyLengthDeclared === parsed.bodyLengthActual} label={`BodyLength ${parsed.bodyLengthDeclared}`} detail={`counted ${parsed.bodyLengthActual}`} />
        <Check ok={parsed.checksumDeclared === parsed.checksumActual} label={`CheckSum ${String(parsed.checksumDeclared).padStart(3, '0')}`} detail={`computed ${String(parsed.checksumActual).padStart(3, '0')}`} />
      </div>
      <RawLine fields={fields} />
      <table className="w-full text-[11.5px]">
        <thead className="text-left text-[10px] uppercase tracking-wider text-muted">
          <tr>
            <th className="w-12 py-1">Tag</th>
            <th className="py-1">Field</th>
            <th className="py-1">Value</th>
          </tr>
        </thead>
        <tbody>
          {fields.map((f, i) => (
            <tr key={i} className={cn('border-t border-line/50', f.section !== 'body' && 'text-ink-2')}>
              <td className="num py-1 text-muted">{f.tag}</td>
              <td className="py-1">{f.name}</td>
              <td className="num py-1 break-all">
                {f.value}
                {f.meaning && <span className="ml-1.5 font-sans text-accent">{f.meaning}</span>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function RawLine({ fields }: { fields: DecodedField[] }) {
  return (
    <p className="num break-all rounded-md bg-bg p-2 text-[11px] leading-relaxed">
      {fields.map((f, i) => (
        <span key={i}>
          <span className="text-info">{f.tag}</span>
          <span className="text-muted">=</span>
          <span className={f.section === 'body' ? 'text-ink' : 'text-ink-2'}>{f.value}</span>
          <span className="text-line-strong">|</span>
        </span>
      ))}
    </p>
  )
}

function Check({ ok, label, detail }: { ok: boolean; label: string; detail: string }) {
  return (
    <span className={ok ? 'text-buy' : 'text-sell'} title={detail}>
      {ok ? '✓' : '✗'} {label}
      {!ok && <span className="text-muted"> ({detail})</span>}
    </span>
  )
}
