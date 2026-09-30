// Shapes of the Tickwire API (see /swagger). Kept by hand; they are small.

export interface Quote {
  id: number
  occ: string
  theo: number
  iv: number
  delta: number
  gamma: number
  vega: number
  theta: number
  bid: number | null
  bidSize: number
  ask: number | null
  askSize: number
  last: number | null
  volume: number
}

export interface ChainRow {
  strike: number
  call: Quote | null
  put: Quote | null
}

export interface Chain {
  underlying: string
  expiry: string
  spot: number
  changePct: number
  rows: ChainRow[]
  time: string
}

export interface Underlying {
  symbol: string
  name: string
  price: number
  changePct: number
  expiries: string[]
}

export interface BookLevel {
  price: number
  quantity: number
  orders: number
}

export interface Book {
  contractId: number
  occ: string
  display: string
  underlying: string
  expiry: string
  strike: number
  right: 'C' | 'P'
  theo: number
  iv: number
  last: number | null
  volume: number
  bids: BookLevel[]
  asks: BookLevel[]
  time: string
  /** Each exchange's own top of book; bids/asks are the consolidated depth. */
  venues?: VenueQuote[]
}

export interface VenueQuote {
  exchange: string
  bid: number | null
  bidSize: number
  ask: number | null
  askSize: number
}

export interface TradePrint {
  contractId: number
  display: string
  price: number
  quantity: number
  side: 'buy' | 'sell'
  time: string
  exchange?: string
}

export interface Guest {
  clientId: string
  token: string
  sessionKey: string
  clientCompId: string
  venueCompId: string
  expiresAt: string
}

export interface RiskLimits {
  maxOrderQty: number
  maxNotional: number
  priceBandPct: number
  priceBandMinAbs: number
  maxOpenOrders: number
  allowedUnderlyings: string[] | null
  allowedOrderTypes: string[]
  allowedTimeInForce: string[]
  maxMessagesPerSecond: number
  cancelOnDisconnect: boolean
  maxAbsDelta: number
  maxAbsVega: number
}

export interface SessionConfig {
  id: number
  clientId: string
  beginString: string
  clientCompId: string
  venueCompId: string
  heartBtInt: number
  resetOnLogon: boolean
  transport: string
  enableChaos: boolean
  key: string
}

export interface Me {
  clientId: string
  displayName: string
  isGuest: boolean
  expiresAt: string | null
  killSwitch: boolean
  limits: RiskLimits
  sessions: SessionConfig[]
}

export interface BlotterLeg {
  contractId: number
  display: string
  ratio: number
  side: 'Buy' | 'Sell'
}

export interface BlotterRow {
  orderId: string
  clOrdID: string
  origClOrdID: string | null
  contractId: number
  display: string
  occ: string
  side: 'Buy' | 'Sell'
  type: string
  tif: string
  price: number | null
  quantity: number
  cumQty: number
  leavesQty: number
  avgPx: number
  status: string
  text: string | null
  created: string
  updated: string
  lastExecType: string
  lastQty: number
  lastPx: number
  legs?: BlotterLeg[] | null
  /** Exchange the order went to (SMART for spreads), the ExDestination asked for, and fees so far (negative = rebate). */
  exchange?: string | null
  destination?: string | null
  fees?: number
}

export interface ClientOrderState {
  orderId: string
  clOrdID: string
  ordStatus: string
  cumQty: number
  leavesQty: number
  avgPx: number
  reportsSeen: number
}

export interface OrderRow {
  order: BlotterRow
  clientView: ClientOrderState | null
  inSync: boolean
}

export interface WireEvent {
  id: number
  sessionKey: string
  side: 'venue' | 'client'
  direction: 'in' | 'out'
  time: string
  raw: string
  msgType: string
  msgTypeName: string
  seqNum: number
  disposition: string
  note: string | null
  clOrdID: string | null
  possDup: boolean
}

export interface SessionLog {
  id: number
  sessionKey: string
  side: 'venue' | 'client'
  time: string
  level: 'Info' | 'Warning' | 'Error'
  text: string
}

export interface SessionTraffic {
  key: string
  messages: WireEvent[]
  logs: SessionLog[]
}

export interface SessionSummary {
  key: string
  clientId: string
  clientCompId: string
  venueCompId: string
  transport: string
  state: string
  nextSenderSeqNum: number
  nextTargetSeqNum: number
  heartBtInt: number
  remote: string | null
  chaos: boolean
  isGuest: boolean
  lastReceived: string | null
}

export interface LatencySummary {
  samples: number
  p50: number
  p90: number
  p99: number
  max: number
}

export interface Ops {
  time: string
  uptimeSeconds: number
  msgInPerSec: number
  msgOutPerSec: number
  msgInTotal: number
  msgOutTotal: number
  inSeries: number[]
  outSeries: number[]
  orderToAckMicros: LatencySummary
  ordersAccepted: number
  fills: number
  contractsTraded: number
  cancelRejects: number
  rejectsByReason: Record<string, number>
  sessionsActive: number
  sessionsTotal: number
  sessions: SessionSummary[]
  globalKillSwitch: boolean
  persistence: string
}

export interface ConnectInfo {
  clientId: string
  senderCompID: string
  targetCompID: string
  host: string
  port: number
  heartBtInt: number
  configs: Record<string, string>
  role: SessionRole
  beginString: string
}

export type SessionRole = 'trading' | 'dropcopy'

export interface ChaosResult {
  action: string
  description: string
}

export interface CredentialField {
  name: string
  label: string
  secret: boolean
  placeholder: string | null
  defaultValue: string | null
}

export interface ProviderInfo {
  id: string
  name: string
  tagline: string
  transport: string
  docsUrl: string
  signupUrl: string
  credentials: CredentialField[]
  provides: string[]
  brandColor: string
  verified: boolean
}

export interface ExternalQuote {
  occSymbol: string
  bid: number | null
  ask: number | null
  bidSize: number | null
  askSize: number | null
  last: number | null
  impliedVol: number | null
  delta: number | null
  time: string
  provider: string
}

export interface ExternalUnderlying {
  symbol: string
  price: number
  time: string
  provider: string
}

export interface FeedStatus {
  provider: string | null
  providerName: string | null
  state: 'Idle' | 'Connecting' | 'Streaming' | 'Error' | 'Stopped'
  message: string | null
  startedAt: string | null
  updates: number
  contracts: number
  lastUpdate: string | null
}

export interface FeedSnapshot {
  status: FeedStatus
  quotes: ExternalQuote[]
  underlyings: ExternalUnderlying[]
}

export interface PositionView {
  contract: { id: number; underlying: string; expiry: string; right: string; strike: number; occSymbol: string; display: string }
  quantity: number
  avgCost: number
  mark: number
  marketValue: number
  unrealizedPnl: number
  realizedPnl: number
  delta: number
  gamma: number
  vega: number
  theta: number
}

export interface PortfolioView {
  clientId: string
  positions: PositionView[]
  realizedPnl: number
  unrealizedPnl: number
  totalPnl: number
  delta: number
  gamma: number
  vega: number
  theta: number
  time: string
}
