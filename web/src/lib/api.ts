import type { ChaosResult, ConnectInfo, FeedSnapshot, FeedStatus, Guest, Me, OrderRow, Ops, PortfolioView, ProviderInfo, RiskLimits, SessionRole, SessionTraffic } from './types'

/**
 * In development everything is same-origin (Vite proxies /api and /hubs to the API). In production the browser talks
 * to the API host directly: SignalR can't go through a Vercel rewrite anyway, and skipping the extra hop removes a
 * failure point. CORS on the API allows the Vercel domains.
 */
export const hubBase: string = import.meta.env.VITE_API_BASE_URL ?? ''
const apiBase = hubBase

export const apiUrl = (path: string): string => apiBase + path

const TRANSIENT = new Set([502, 503, 504])
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms))

/**
 * fetch with retries for transient gateway errors and network failures. Only used for requests that are safe to repeat
 * (GETs, guest creation): an order must never be sent twice. A 503 problem+json from the API itself is not retried.
 */
async function fetchWithRetry(url: string, init: RequestInit, attempts = 3): Promise<Response> {
  for (let i = 1; ; i++) {
    try {
      const res = await fetch(url, init)
      if (!TRANSIENT.has(res.status) || i >= attempts || res.headers.get('content-type')?.includes('problem+json')) return res
    } catch (e) {
      if (i >= attempts || init.signal?.aborted) throw e
    }
    await sleep(300 * i)
  }
}

const TOKEN_KEY = 'tickwire.guest'

export function loadGuest(): Guest | null {
  try {
    const raw = localStorage.getItem(TOKEN_KEY)
    if (!raw) return null
    const guest = JSON.parse(raw) as Guest
    return new Date(guest.expiresAt) > new Date() ? guest : null
  } catch {
    return null
  }
}

export function saveGuest(guest: Guest | null): void {
  try {
    if (guest) localStorage.setItem(TOKEN_KEY, JSON.stringify(guest))
    else localStorage.removeItem(TOKEN_KEY)
  } catch {
    // Private mode or blocked storage: the session simply won't survive a reload.
  }
}

export class ApiError extends Error {
  readonly status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

async function request<T>(path: string, init: RequestInit & { token?: string; retry?: boolean } = {}): Promise<T> {
  const headers = new Headers(init.headers)
  if (init.body && !headers.has('content-type')) headers.set('content-type', 'application/json')
  if (init.token) headers.set('authorization', `Bearer ${init.token}`)
  const retry = init.retry ?? (init.method === undefined || init.method === 'GET')
  const res = retry ? await fetchWithRetry(apiBase + path, { ...init, headers }) : await fetch(apiBase + path, { ...init, headers })
  if (!res.ok) {
    let detail = res.statusText
    try {
      const problem = await res.json()
      detail = problem.detail ?? problem.title ?? JSON.stringify(problem.errors ?? problem)
    } catch {
      // not JSON
    }
    throw new ApiError(res.status, detail)
  }
  if (res.status === 204) return undefined as T
  const text = await res.text()
  return (text ? JSON.parse(text) : undefined) as T
}

export const api = {
  health: async (signal?: AbortSignal) => {
    const res = await fetchWithRetry(`${apiBase}/api/health`, { signal, cache: 'no-store' })
    return res.ok
  },
  createGuest: () => request<Guest>('/api/guest', { method: 'POST', retry: true }),
  me: (token: string) => request<Me>('/api/me', { token }),
  orders: (token: string) => request<OrderRow[]>('/api/orders', { token }),
  positions: (token: string) => request<PortfolioView>('/api/positions', { token }),
  placeOrder: (
    token: string,
    body: { contractId: number; side: string; type: string; tif: string; price: number | null; quantity: number; destination?: string },
  ) =>
    request<{ clOrdID: string }>('/api/orders', { method: 'POST', token, body: JSON.stringify(body) }),
  placeSpread: (
    token: string,
    body: { legs: { contractId: number; ratio: number; side: 'buy' | 'sell' }[]; side: string; tif: string; price: number; quantity: number },
  ) => request<{ clOrdID: string }>('/api/orders/spread', { method: 'POST', token, body: JSON.stringify(body) }),
  cancelOrder: (token: string, orderId: string) => request<{ clOrdID: string }>(`/api/orders/${orderId}/cancel`, { method: 'POST', token }),
  replaceOrder: (token: string, orderId: string, price: number | null, quantity: number) =>
    request<{ clOrdID: string }>(`/api/orders/${orderId}/replace`, { method: 'POST', token, body: JSON.stringify({ price, quantity }) }),
  traffic: (token: string, compId: string, limit = 300) =>
    request<SessionTraffic>(`/api/sessions/${compId}/messages?limit=${limit}`, { token }),
  chaos: (token: string, compId: string, action: string, count?: number) =>
    request<ChaosResult>(`/api/sessions/${compId}/chaos`, { method: 'POST', token, body: JSON.stringify({ action, count }) }),
  resetSession: (token: string, compId: string) => request<void>(`/api/sessions/${compId}/reset`, { method: 'POST', token }),
  updateLimits: (token: string, clientId: string, limits: RiskLimits) =>
    request<RiskLimits>(`/api/clients/${clientId}/limits`, { method: 'PUT', token, body: JSON.stringify(limits) }),
  kill: (token: string, clientId: string, engaged: boolean) =>
    request<{ engaged: boolean; ordersCanceled: number }>(`/api/clients/${clientId}/kill`, { method: 'POST', token, body: JSON.stringify({ engaged }) }),
  connect: (token: string, role: SessionRole = 'trading', version = 'FIX.4.4') =>
    request<ConnectInfo>(`/api/connect?role=${role}&version=${encodeURIComponent(version)}`, { method: 'POST', token }),
  metrics: () => request<Ops>('/api/metrics'),
  providers: () => request<ProviderInfo[]>('/api/feeds/providers'),
  feed: (token: string) => request<FeedSnapshot>('/api/feeds', { token }),
  connectFeed: (token: string, provider: string, credentials: Record<string, string>) =>
    request<FeedStatus>('/api/feeds/connect', { method: 'POST', token, body: JSON.stringify({ provider, credentials }) }),
  disconnectFeed: (token: string) => request<void>('/api/feeds/disconnect', { method: 'POST', token }),
}
