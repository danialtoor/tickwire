// Records a scripted session from a running API into web/public/replays/demo.json, which the site plays back when
// the backend is unreachable. Run from web/ (it uses web's @microsoft/signalr dependency):
//   npm run record:replay -- http://localhost:8080
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { writeFileSync } from 'node:fs'
import { resolve } from 'node:path'

const api = (process.argv[2] ?? 'http://localhost:8080').replace(/\/$/, '')
const out = resolve(process.cwd(), 'public/replays/demo.json')
const frames: { t: number; type: string; payload: unknown }[] = []
const t0 = Date.now()
const record = (type: string, payload: unknown): void => {
  frames.push({ t: Date.now() - t0, type, payload })
}
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms))

async function http<T>(path: string, init: RequestInit & { token?: string } = {}): Promise<T> {
  const headers: Record<string, string> = { 'content-type': 'application/json' }
  if (init.token) headers.authorization = `Bearer ${init.token}`
  const res = await fetch(api + path, { ...init, headers })
  if (!res.ok) throw new Error(`${path}: ${res.status} ${await res.text()}`)
  const text = await res.text()
  return (text ? JSON.parse(text) : undefined) as T
}

interface Guest {
  token: string
  clientCompId: string
  sessionKey: string
}

async function main() {
  const guest = await http<Guest>('/api/guest', { method: 'POST' })
  const hub = new HubConnectionBuilder().withUrl(`${api}/hubs/live`).configureLogging(LogLevel.Warning).build()

  // Throttle the heavy streams so the file stays small: chain 1/s, book 2/s, underlyings and ops 1/s.
  const last: Record<string, number> = {}
  const throttled = (type: string, everyMs: number, payload: unknown) => {
    const now = Date.now()
    if (now - (last[type] ?? 0) >= everyMs) {
      last[type] = now
      record(type, payload)
    }
  }
  hub.on('chain', (c) => throttled('chain', 1000, c))
  hub.on('book', (b) => throttled('book', 500, b))
  hub.on('underlyings', (u) => throttled('underlyings', 1000, u))
  hub.on('tape', (t) => record('tape', t))
  hub.on('wire', (w) => record('wire', w))
  hub.on('sessionLog', (l) => record('log', l))
  for (const ignored of ['order', 'cancelReject', 'sessionState']) hub.on(ignored, () => {}) // orders are polled below
  hub.on('ops', (o) => throttled('ops', 1000, { ...o, sessions: o.sessions.filter((s: { key: string }) => s.key === guest.sessionKey) }))

  await hub.start()
  await hub.invoke('SubscribeMarket')
  await hub.invoke('SubscribeOps')
  await hub.invoke('SubscribeClient', guest.token)
  const chain = await hub.invoke('SubscribeChain', 'SPY', '')
  record('chain', chain)
  const atm = chain.rows.reduce((b: { strike: number }, r: { strike: number }) => (Math.abs(r.strike - chain.spot) < Math.abs(b.strike - chain.spot) ? r : b))
  const call = atm.call
  const put = atm.put
  record('book', await hub.invoke('SubscribeBook', call.id))

  let lastOrders = ''
  const pollOrders = setInterval(async () => {
    const rows = await http<unknown[]>('/api/orders', { token: guest.token }).catch(() => null)
    const json = JSON.stringify(rows)
    if (rows && json !== lastOrders) {
      lastOrders = json
      record('orders', rows)
    }
  }, 700)

  const order = (contractId: number, side: string, price: number | null, quantity: number, tif = 'day') =>
    http<{ clOrdID: string }>('/api/orders', { method: 'POST', token: guest.token, body: JSON.stringify({ contractId, side, type: price ? 'limit' : 'market', tif, price, quantity }) })
  const chaos = (action: string, count?: number) =>
    http('/api/sessions/' + guest.clientCompId + '/chaos', { method: 'POST', token: guest.token, body: JSON.stringify({ action, count }) })

  console.log('recording…')
  await sleep(3000)
  await order(call.id, 'buy', call.ask, 30) // sweeps the makers, then fills over the next ticks
  await sleep(4000)
  await order(put.id, 'sell', Math.round((put.ask + 0.05) * 100) / 100, 10) // rests above the ask
  await sleep(3000)
  await chaos('drop-venue', 3)
  await order(call.id, 'buy', Math.round((call.bid - 0.05) * 100) / 100, 5)
  await sleep(1500)
  await order(call.id, 'buy', Math.round((call.bid - 0.10) * 100) / 100, 5)
  await sleep(5000)
  await order(call.id, 'buy', 99.0, 1) // fat-finger: rejected by the price band
  await sleep(3000)
  await chaos('seq-gap', 2)
  await sleep(4000)
  await chaos('corrupt-checksum')
  await sleep(4000)
  const open = await http<{ order: { orderId: string; status: string } }[]>('/api/orders', { token: guest.token })
  const resting = open.find((r) => r.order.status === 'New')
  if (resting) await http(`/api/orders/${resting.order.orderId}/cancel`, { method: 'POST', token: guest.token })
  await sleep(4000)
  await chaos('invalid-field')
  await sleep(6000)

  clearInterval(pollOrders)
  await hub.stop()
  const file = { recordedAt: new Date().toISOString(), guest: { clientCompId: guest.clientCompId, sessionKey: guest.sessionKey }, frames }
  writeFileSync(out, JSON.stringify(file))
  console.log(`wrote ${frames.length} frames (${Math.round(JSON.stringify(file).length / 1024)} KB) to ${out}`)
}

main().catch((e) => {
  console.error(e)
  process.exit(1)
})
