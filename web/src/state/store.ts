import { create } from 'zustand'
import type { Book, Chain, ExternalQuote, ExternalUnderlying, FeedSnapshot, FeedStatus, Guest, OrderRow, Ops, PortfolioView, SessionLog, TradePrint, Underlying, WireEvent } from '../lib/types'

export type Mode = 'checking' | 'live' | 'replay'

const MAX_WIRE = 1500
const MAX_LOGS = 400
const MAX_TAPE = 60
const MAX_SPOT_POINTS = 2400

export interface SpotPoint {
  time: number
  value: number
}

interface State {
  mode: Mode
  guest: Guest | null
  underlyings: Underlying[]
  spotHistory: Record<string, SpotPoint[]>
  chain: Chain | null
  book: Book | null
  tape: TradePrint[]
  orders: OrderRow[]
  wire: WireEvent[]
  logs: SessionLog[]
  ops: Ops | null
  selected: { underlying: string; expiry: string | null; contractId: number | null }
  feed: FeedStatus | null
  portfolio: PortfolioView | null
  liveQuotes: Record<string, ExternalQuote>
  liveUnderlyings: Record<string, ExternalUnderlying>

  setMode: (mode: Mode) => void
  setGuest: (guest: Guest | null) => void
  setUnderlyings: (u: Underlying[]) => void
  setChain: (c: Chain | null) => void
  setBook: (b: Book | null) => void
  addTape: (prints: TradePrint[]) => void
  setOrders: (orders: OrderRow[]) => void
  addWire: (events: WireEvent[]) => void
  addLogs: (logs: SessionLog[]) => void
  setOps: (ops: Ops) => void
  select: (patch: Partial<State['selected']>) => void
  resetSession: () => void
  applyFeed: (snapshot: FeedSnapshot, replace?: boolean) => void
  setFeedStatus: (status: FeedStatus) => void
  setPortfolio: (p: PortfolioView | null) => void
}

export const useStore = create<State>((set) => ({
  mode: 'checking',
  guest: null,
  underlyings: [],
  spotHistory: {},
  chain: null,
  book: null,
  tape: [],
  orders: [],
  wire: [],
  logs: [],
  ops: null,
  selected: { underlying: 'SPY', expiry: null, contractId: null },
  feed: null,
  portfolio: null,
  liveQuotes: {},
  liveUnderlyings: {},

  setMode: (mode) => set({ mode }),
  setGuest: (guest) => set({ guest }),
  setUnderlyings: (underlyings) =>
    set((s) => {
      const now = Math.floor(Date.now() / 1000)
      const spotHistory = { ...s.spotHistory }
      for (const u of underlyings) {
        const series = spotHistory[u.symbol] ?? []
        const last = series[series.length - 1]
        if (!last || last.time < now) spotHistory[u.symbol] = [...series.slice(-MAX_SPOT_POINTS), { time: now, value: u.price }]
        else if (last.value !== u.price) spotHistory[u.symbol] = [...series.slice(0, -1), { time: now, value: u.price }]
      }
      return { underlyings, spotHistory }
    }),
  setChain: (chain) => set((s) => (chain && s.selected.expiry === null ? { chain, selected: { ...s.selected, expiry: chain.expiry } } : { chain })),
  setBook: (book) => set({ book }),
  addTape: (prints) => set((s) => ({ tape: [...prints, ...s.tape].slice(0, MAX_TAPE) })),
  setOrders: (orders) => set({ orders }),
  addWire: (events) =>
    set((s) => {
      const seen = new Set(s.wire.map((w) => w.id))
      const fresh = events.filter((e) => !seen.has(e.id))
      if (fresh.length === 0) return {}
      const wire = [...s.wire, ...fresh].sort((a, b) => a.id - b.id)
      return { wire: wire.slice(-MAX_WIRE) }
    }),
  addLogs: (logs) =>
    set((s) => {
      const seen = new Set(s.logs.map((l) => l.id))
      const fresh = logs.filter((l) => !seen.has(l.id))
      return fresh.length ? { logs: [...s.logs, ...fresh].sort((a, b) => a.id - b.id).slice(-MAX_LOGS) } : {}
    }),
  setOps: (ops) => set({ ops }),
  select: (patch) => set((s) => ({ selected: { ...s.selected, ...patch } })),
  resetSession: () => set({ orders: [], wire: [], logs: [], feed: null, portfolio: null, liveQuotes: {}, liveUnderlyings: {} }),
  applyFeed: (snapshot, replace = false) =>
    set((s) => {
      const liveQuotes = replace ? {} : { ...s.liveQuotes }
      for (const q of snapshot.quotes) liveQuotes[q.occSymbol] = q
      const liveUnderlyings = { ...(replace ? {} : s.liveUnderlyings) }
      for (const u of snapshot.underlyings) liveUnderlyings[u.symbol] = u
      return { feed: snapshot.status, liveQuotes, liveUnderlyings }
    }),
  setPortfolio: (portfolio) => set({ portfolio }),
  setFeedStatus: (status) => set(status.state === 'Idle' ? { feed: status, liveQuotes: {}, liveUnderlyings: {} } : { feed: status }),
}))
