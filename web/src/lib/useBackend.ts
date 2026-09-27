import { useCallback, useEffect } from 'react'
import { useStore } from '../state/store'
import { api, ApiError, loadGuest, saveGuest } from './api'

/** Checks the API once on load; if it is unreachable the app switches to Replay mode and keeps retrying quietly. */
export function useBackendMode(): void {
  const setMode = useStore((s) => s.setMode)
  const mode = useStore((s) => s.mode)

  useEffect(() => {
    let cancelled = false
    const check = async () => {
      const controller = new AbortController()
      const timer = setTimeout(() => controller.abort(), 4000)
      try {
        const ok = await api.health(controller.signal)
        if (!cancelled) setMode(ok ? 'live' : 'replay')
      } catch {
        if (!cancelled) setMode('replay')
      } finally {
        clearTimeout(timer)
      }
    }
    if (mode === 'checking') void check()
    const retry = mode === 'replay' ? setInterval(check, 30_000) : undefined
    return () => {
      cancelled = true
      if (retry) clearInterval(retry)
    }
  }, [mode, setMode])
}

/** Returns the guest session, creating one on first use. A stale token (e.g. after a nightly reset) is replaced. */
export function useGuest() {
  const guest = useStore((s) => s.guest)
  const setGuest = useStore((s) => s.setGuest)
  const mode = useStore((s) => s.mode)

  const ensure = useCallback(async () => {
    const existing = guest ?? loadGuest()
    if (existing) {
      try {
        await api.me(existing.token)
        if (!guest) setGuest(existing)
        return existing
      } catch (e) {
        if (!(e instanceof ApiError) || (e.status !== 401 && e.status !== 404)) throw e
      }
    }
    const created = await api.createGuest()
    saveGuest(created)
    useStore.getState().resetSession()
    setGuest(created)
    return created
  }, [guest, setGuest])

  const reset = useCallback(async () => {
    saveGuest(null)
    setGuest(null)
    useStore.getState().resetSession()
    const created = await api.createGuest()
    saveGuest(created)
    setGuest(created)
    return created
  }, [setGuest])

  return { guest, ensure, reset, live: mode === 'live' }
}
