import { useStore } from '../state/store'
import type { Book, Chain, OrderRow, Ops, SessionLog, TradePrint, Underlying, WireEvent } from '../lib/types'

/**
 * A recorded session (scripts/record-replay.ts captures one from a live server). Each frame is what SignalR or
 * REST delivered, with its offset from the start of the recording.
 */
export interface ReplayFile {
  recordedAt: string
  guest: { clientCompId: string; sessionKey: string }
  frames: ReplayFrame[]
}

export type ReplayFrame =
  | { t: number; type: 'underlyings'; payload: Underlying[] }
  | { t: number; type: 'chain'; payload: Chain }
  | { t: number; type: 'book'; payload: Book }
  | { t: number; type: 'tape'; payload: TradePrint[] }
  | { t: number; type: 'wire'; payload: WireEvent }
  | { t: number; type: 'log'; payload: SessionLog }
  | { t: number; type: 'orders'; payload: OrderRow[] }
  | { t: number; type: 'ops'; payload: Ops }

/** Plays the bundled recording in a loop. Returns a stop function. */
export function startReplay(url = '/replays/demo.json'): () => void {
  let stopped = false
  let timer: ReturnType<typeof setTimeout> | undefined

  const run = async () => {
    let file: ReplayFile
    try {
      const res = await fetch(url)
      file = (await res.json()) as ReplayFile
    } catch {
      return
    }
    if (stopped || file.frames.length === 0) return
    const s = useStore.getState()
    s.setGuest({ clientId: 'replay', token: '', sessionKey: file.guest.sessionKey, clientCompId: file.guest.clientCompId, venueCompId: 'TICKWIRE', expiresAt: new Date(Date.now() + 864e5).toISOString() })

    const duration = file.frames[file.frames.length - 1].t
    let loop = 0
    const playFrom = (index: number, loopStart: number) => {
      if (stopped) return
      if (index >= file.frames.length) {
        loop++
        useStore.getState().resetSession()
        timer = setTimeout(() => playFrom(0, performance.now()), 2500)
        return
      }
      const frame = file.frames[index]
      const due = loopStart + frame.t - performance.now()
      timer = setTimeout(() => {
        apply(frame, loop * (duration + 10_000_000))
        playFrom(index + 1, loopStart)
      }, Math.max(0, due))
    }
    playFrom(0, performance.now())
  }

  void run()
  return () => {
    stopped = true
    clearTimeout(timer)
  }
}

function apply(frame: ReplayFrame, idOffset: number): void {
  const s = useStore.getState()
  switch (frame.type) {
    case 'underlyings':
      s.setUnderlyings(frame.payload)
      break
    case 'chain':
      if (frame.payload.underlying === s.selected.underlying) s.setChain(frame.payload)
      break
    case 'book':
      s.setBook(frame.payload)
      if (s.selected.contractId === null) s.select({ contractId: frame.payload.contractId })
      break
    case 'tape':
      s.addTape(frame.payload)
      break
    case 'wire':
      s.addWire([{ ...frame.payload, id: frame.payload.id + idOffset }])
      break
    case 'log':
      s.addLogs([{ ...frame.payload, id: frame.payload.id + idOffset }])
      break
    case 'orders':
      s.setOrders(frame.payload)
      break
    case 'ops':
      s.setOps(frame.payload)
      break
  }
}
