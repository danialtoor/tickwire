import { NavLink, Outlet } from 'react-router'
import { REPO_URL } from '../lib/config'
import { cn } from '../lib/format'
import { useBackendMode } from '../lib/useBackend'
import { useStore } from '../state/store'


const links = [
  { to: '/trade', label: 'Trader' },
  { to: '/connect', label: 'Connect via FIX' },
  { to: '/data', label: 'Market data' },
  { to: '/analyzer', label: 'Log Analyzer' },
  { to: '/ops', label: 'Ops' },
]

export function Logo() {
  return (
    <span className="flex items-center gap-2 font-semibold tracking-tight">
      <svg viewBox="0 0 32 32" className="size-6" aria-hidden>
        <rect width="32" height="32" rx="7" className="fill-panel-2" />
        <path d="M6 20h5l3-9 4 13 3-8h5" fill="none" className="stroke-accent" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
      Tickwire
    </span>
  )
}

export function Layout() {
  useBackendMode()
  const mode = useStore((s) => s.mode)

  return (
    <div className="flex min-h-full flex-col">
      <header className="sticky top-0 z-30 border-b border-line bg-bg/90 backdrop-blur">
        <div className="mx-auto flex h-12 max-w-[1680px] items-center gap-6 px-4">
          <NavLink to="/" className="shrink-0">
            <Logo />
          </NavLink>
          <nav className="flex min-w-0 flex-1 items-center gap-1 overflow-x-auto text-sm scroll-thin">
            {links.map((l) => (
              <NavLink
                key={l.to}
                to={l.to}
                className={({ isActive }) =>
                  cn('whitespace-nowrap rounded-md px-2.5 py-1.5 transition-colors', isActive ? 'bg-panel-2 text-ink' : 'text-ink-2 hover:text-ink')
                }
              >
                {l.label}
              </NavLink>
            ))}
          </nav>
          <ModePill mode={mode} />
          <a href={REPO_URL} className="hidden text-sm text-ink-2 hover:text-ink sm:block" target="_blank" rel="noreferrer">
            GitHub
          </a>
        </div>
      </header>

      {mode === 'replay' && (
        <div className="border-b border-warn/30 bg-warn-soft px-4 py-2 text-center text-sm text-warn" role="status" data-testid="replay-banner">
          The live backend is unreachable, so you're watching a recorded session. The decoder and log analyzer still run fully in your
          browser.
        </div>
      )}

      <main className="flex-1">
        <Outlet />
      </main>

      <footer className="border-t border-line px-4 py-5 text-center text-xs text-muted">
        Tickwire is a portfolio project. Not affiliated with any trading firm; simulated markets only, no real orders or money.{' '}
        <a href={REPO_URL} className="underline decoration-line-strong underline-offset-2 hover:text-ink-2" target="_blank" rel="noreferrer">
          Source on GitHub
        </a>
      </footer>
    </div>
  )
}

function ModePill({ mode }: { mode: string }) {
  const styles: Record<string, string> = {
    live: 'bg-buy-soft text-buy',
    replay: 'bg-warn-soft text-warn',
    checking: 'bg-panel-2 text-muted',
  }
  const label: Record<string, string> = { live: 'Live', replay: 'Replay', checking: 'Connecting' }
  return (
    <span className={cn('flex shrink-0 items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-medium', styles[mode])}>
      <span className={cn('size-1.5 rounded-full bg-current', mode === 'live' && 'animate-pulse')} />
      {label[mode]}
    </span>
  )
}
