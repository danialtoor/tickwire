import { cn, expiryLabel } from '../lib/format'

interface Props {
  expiries: string[]
  selected: string | null
  /** Server time of the latest chain snapshot, used for days-to-expiry (keeps render pure). */
  asOf: string | null
  onSelect: (expiry: string) => void
}

const DAY = 86_400_000

/** Monthly options expire on the third Friday of the month; everything else listed here is a weekly. */
const isMonthly = (iso: string) => {
  const d = new Date(`${iso}T00:00:00Z`)
  return d.getUTCDay() === 5 && d.getUTCDate() >= 15 && d.getUTCDate() <= 21
}

export function ExpiryBar({ expiries, selected, asOf, onSelect }: Props) {
  const now = asOf ? new Date(asOf.endsWith('Z') ? asOf : `${asOf}Z`).getTime() : null
  const today = now === null ? null : Math.floor(now / DAY) * DAY

  const move = (delta: number) => {
    const i = expiries.indexOf(selected ?? '')
    const next = expiries[Math.min(expiries.length - 1, Math.max(0, i + delta))]
    if (next && next !== selected) onSelect(next)
  }

  return (
    <div
      role="tablist"
      aria-label="Expiry"
      className="scroll-thin flex gap-1 overflow-x-auto border-b border-line px-2 py-1.5"
      onKeyDown={(e) => {
        if (e.key === 'ArrowRight') move(1)
        if (e.key === 'ArrowLeft') move(-1)
      }}
    >
      {expiries.map((e) => {
        const active = e === selected
        const dte = today === null ? null : Math.round((new Date(`${e}T00:00:00Z`).getTime() - today) / DAY)
        return (
          <button
            key={e}
            type="button"
            role="tab"
            aria-selected={active}
            tabIndex={active ? 0 : -1}
            data-testid="expiry-tab"
            onClick={() => onSelect(e)}
            className={cn(
              'flex shrink-0 flex-col items-start rounded-md border px-3 py-1 text-left transition-colors',
              active ? 'border-accent/50 bg-accent/10 text-ink' : 'border-transparent text-ink-2 hover:bg-panel-2 hover:text-ink',
            )}
          >
            <span className="flex items-center gap-1.5 text-[12.5px] font-semibold">
              {expiryLabel(e)}
              {isMonthly(e) && <span className="rounded bg-panel-2 px-1 text-[9.5px] font-medium text-muted" title="Monthly (third Friday)">M</span>}
            </span>
            <span className={cn('num text-[10.5px]', active ? 'text-accent' : 'text-muted')}>{dte === null ? '—' : `${dte}d`}</span>
          </button>
        )
      })}
    </div>
  )
}
