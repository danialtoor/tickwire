export const px = (v: number | null | undefined, digits = 2): string =>
  v === null || v === undefined || Number.isNaN(v) ? '—' : v.toFixed(digits)

export const qty = (v: number | null | undefined): string =>
  v === null || v === undefined ? '—' : v.toLocaleString('en-US', { maximumFractionDigits: 0 })

export const pct = (v: number, digits = 2): string => `${v >= 0 ? '+' : ''}${v.toFixed(digits)}%`

export const usd = (v: number): string => v.toLocaleString('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 })

export const time = (iso: string): string => {
  const d = new Date(iso.endsWith('Z') ? iso : `${iso}Z`)
  return d.toLocaleTimeString('en-US', { hour12: false }) + '.' + String(d.getMilliseconds()).padStart(3, '0')
}

export const expiryLabel = (iso: string): string => {
  const d = new Date(`${iso}T00:00:00Z`)
  return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', timeZone: 'UTC' })
}

export const micros = (us: number): string => (us >= 1000 ? `${(us / 1000).toFixed(2)} ms` : `${us.toFixed(0)} µs`)

export const cn = (...parts: (string | false | null | undefined)[]): string => parts.filter(Boolean).join(' ')
