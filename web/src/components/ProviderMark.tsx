import { cn } from '../lib/format'

/**
 * Text wordmarks for data providers. Deliberately not the vendors' logo artwork: the names identify which API an
 * adapter talks to, and no vendor trademark files are bundled.
 */
const styles: Record<string, { text: string; className: string; mark: string }> = {
  spiderrock: { text: 'SPIDERROCK', className: 'tracking-[0.18em] font-bold text-[13px]', mark: 'SR' },
  databento: { text: 'databento', className: 'lowercase font-semibold text-[15px] tracking-tight', mark: 'db' },
  polygon: { text: 'Polygon.io', className: 'font-semibold text-[15px]', mark: 'P' },
  tradier: { text: 'Tradier', className: 'font-bold text-[15px] tracking-tight', mark: 'T' },
  alpaca: { text: 'alpaca', className: 'lowercase font-bold text-[15px]', mark: 'A' },
  demo: { text: 'Demo feed', className: 'font-semibold text-[15px]', mark: '~' },
}

export function ProviderMark({ id, name, color, size = 'md' }: { id: string; name: string; color: string; size?: 'sm' | 'md' }) {
  const s = styles[id] ?? { text: name, className: 'font-semibold text-[15px]', mark: name[0] }
  return (
    <span className="inline-flex items-center gap-2" aria-label={name}>
      <span
        className={cn('num grid shrink-0 place-items-center rounded-md font-bold text-bg', size === 'sm' ? 'size-5 text-[9px]' : 'size-8 text-[11px]')}
        style={{ background: color }}
        aria-hidden
      >
        {s.mark}
      </span>
      <span className={cn(s.className, size === 'sm' && '!text-[12px]')} style={{ color: id === 'spiderrock' ? undefined : color }}>
        {s.text}
      </span>
    </span>
  )
}
