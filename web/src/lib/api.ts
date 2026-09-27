import type { ChaosResult, ConnectInfo, Guest, Me, OrderRow, Ops, RiskLimits, SessionTraffic } from './types'

/**
 * REST goes to the same origin (Vite proxies it in development; Vercel rewrites /api/* to the API in production).
 * SignalR can't go through a Vercel rewrite, so it connects to the API host directly.
 */
export const hubBase: string = import.meta.env.VITE_API_BASE_URL ?? ''

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

async function request<T>(path: string, init: RequestInit & { token?: string } = {}): Promise<T> {
  const headers = new Headers(init.headers)
  if (init.body && !headers.has('content-type')) headers.set('content-type', 'application/json')
  if (init.token) headers.set('authorization', `Bearer ${init.token}`)
  const res = await fetch(path, { ...init, headers })
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
    const res = await fetch('/api/health', { signal, cache: 'no-store' })
    return res.ok
  },
  createGuest: () => request<Guest>('/api/guest', { method: 'POST' }),
  me: (token: string) => request<Me>('/api/me', { token }),
  orders: (token: string) => request<OrderRow[]>('/api/orders', { token }),
  placeOrder: (token: string, body: { contractId: number; side: string; type: string; tif: string; price: number | null; quantity: number }) =>
    request<{ clOrdID: string }>('/api/orders', { method: 'POST', token, body: JSON.stringify(body) }),
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
  connect: (token: string) => request<ConnectInfo>('/api/connect', { method: 'POST', token }),
  metrics: () => request<Ops>('/api/metrics'),
}
