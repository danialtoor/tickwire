import { useState, type ReactNode } from 'react'

/** Re-mounts its content with a brief green/red background whenever the numeric value changes. */
export function Flash({ value, children, className }: { value: number | null | undefined; children: ReactNode; className?: string }) {
  const [state, setState] = useState({ prev: value, dir: '' as '' | 'up' | 'down', version: 0 })
  if (value !== state.prev) {
    // Derived from the previous render's value (React's recommended alternative to reading refs during render).
    const dir = value != null && state.prev != null ? (value > state.prev ? 'up' : 'down') : ''
    setState({ prev: value, dir, version: state.version + 1 })
  }
  return (
    <span key={state.version} className={`${state.dir ? `flash-${state.dir}` : ''} ${className ?? ''} rounded-sm px-0.5`}>
      {children}
    </span>
  )
}
