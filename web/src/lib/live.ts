import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useStore } from '../state/store'
import { hubBase } from './api'
import type { Book, Chain, FeedSnapshot, FeedStatus, Ops, PortfolioView, SessionLog, TradePrint, Underlying, WireEvent } from './types'

type Listener = () => void

/**
 * One SignalR connection for the whole app. Components declare what they want (chain, book, client, ops) and the
 * connection re-subscribes after every reconnect.
 */
class LiveConnection {
  private connection: HubConnection | null = null
  private starting: Promise<void> | null = null
  private wants = { chain: null as [string, string] | null, book: null as number | null, client: null as string | null, ops: false }
  private orderListeners = new Set<Listener>()
  readonly state = { connected: false }

  onOrderEvent(listener: Listener): () => void {
    this.orderListeners.add(listener)
    return () => this.orderListeners.delete(listener)
  }

  async start(): Promise<void> {
    if (this.connection && this.connection.state !== HubConnectionState.Disconnected) return this.starting ?? Promise.resolve()
    const store = useStore.getState
    const conn = new HubConnectionBuilder()
      .withUrl(`${hubBase}/hubs/live`)
      .withAutomaticReconnect([0, 1000, 2000, 5000, 10000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build()

    conn.on('chain', (c: Chain | null) => {
      const want = this.wants.chain
      if (c && want && c.underlying === want[0] && c.expiry === want[1]) store().setChain(c)
    })
    conn.on('book', (b: Book | null) => {
      if (b && b.contractId === this.wants.book) store().setBook(b)
    })
    conn.on('underlyings', (u: Underlying[]) => store().setUnderlyings(u))
    conn.on('tape', (t: TradePrint[]) => store().addTape(t))
    conn.on('wire', (w: WireEvent) => store().addWire([w]))
    conn.on('sessionLog', (l: SessionLog) => store().addLogs([l]))
    conn.on('order', () => this.orderListeners.forEach((l) => l()))
    conn.on('cancelReject', () => this.orderListeners.forEach((l) => l()))
    conn.on('ops', (o: Ops) => store().setOps(o))
    conn.on('feedQuotes', (f: FeedSnapshot) => store().applyFeed(f))
    conn.on('feedStatus', (f: FeedStatus) => store().setFeedStatus(f))
    conn.on('portfolio', (p: PortfolioView) => store().setPortfolio(p))
    conn.onreconnected(() => {
      this.state.connected = true
      void this.resubscribe()
    })
    conn.onclose(() => (this.state.connected = false))

    this.connection = conn
    this.starting = conn.start().then(async () => {
      this.state.connected = true
      await this.resubscribe()
    })
    return this.starting
  }

  async stop(): Promise<void> {
    await this.connection?.stop()
    this.connection = null
    this.starting = null
  }

  async chain(underlying: string, expiry: string | null): Promise<void> {
    await this.start()
    const chain = await this.connection!.invoke<Chain | null>('SubscribeChain', underlying, expiry ?? '')
    if (chain) {
      this.wants.chain = [chain.underlying, chain.expiry]
      useStore.getState().setChain(chain)
    }
  }

  async book(contractId: number): Promise<void> {
    this.wants.book = contractId
    await this.start()
    const book = await this.connection!.invoke<Book | null>('SubscribeBook', contractId)
    if (book) useStore.getState().setBook(book)
  }

  async client(token: string): Promise<boolean> {
    this.wants.client = token
    await this.start()
    return this.connection!.invoke<boolean>('SubscribeClient', token)
  }

  async ops(): Promise<void> {
    this.wants.ops = true
    await this.start()
    await this.connection!.invoke('SubscribeOps')
  }

  private async resubscribe(): Promise<void> {
    const c = this.connection
    if (!c) return
    await c.invoke('SubscribeMarket')
    if (this.wants.chain) await c.invoke('SubscribeChain', this.wants.chain[0], this.wants.chain[1])
    if (this.wants.book !== null) await c.invoke('SubscribeBook', this.wants.book)
    if (this.wants.client) await c.invoke('SubscribeClient', this.wants.client)
    if (this.wants.ops) await c.invoke('SubscribeOps')
  }
}

export const live = new LiveConnection()
