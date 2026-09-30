/**
 * Browser port of Tickwire.LogAnalyzer (src/Tickwire.LogAnalyzer/FixLogAnalyzer.cs). Same rule codes and the same
 * sample corpus (samples/expected.json), so the page keeps working when the API is down. Field validation is a
 * subset of the C# data-dictionary check: required tags, enum values, data formats and unknown tags.
 */
import dictionary from '../fix/fix44.json'
import { enumMeaning, fieldName, messageName, normalize, parse, requiredTags, type ParsedMessage } from '../fix/decoder'

export type Severity = 'Info' | 'Warning' | 'Error'

export interface Diagnostic {
  code: string
  severity: Severity
  title: string
  explanation: string
  suggestion: string
  messageIndexes: number[]
  line: number | null
}

export interface LogMessage {
  index: number
  line: number
  raw: string
  parsed: boolean
  parseError: string | null
  senderCompID: string
  targetCompID: string
  seqNum: number
  msgType: string
  msgTypeName: string
  possDup: boolean
  intact: boolean
  integrityDetail: string | null
  summary: string
}

export interface SeqGap {
  expectedSeq: number
  receivedSeq: number
  messageIndex: number
  recovered: boolean
}

export interface SessionFlow {
  senderCompID: string
  targetCompID: string
  messages: number
  firstSeq: number
  lastSeq: number
  gaps: SeqGap[]
  resendRequests: number
  gapFills: number
  logons: number
  logouts: number
  heartBtInt: number | null
}

export interface OrderEvent {
  messageIndex: number
  time: string | null
  msgType: string
  description: string
  execType: string | null
  ordStatus: string | null
  cumQty: number | null
  leavesQty: number | null
  orderQty: number | null
  lastQty: number | null
  lastPx: number | null
  text: string | null
  possDup: boolean
}

export interface OrderLifecycle {
  rootClOrdID: string
  clOrdIDs: string[]
  orderID: string | null
  symbol: string | null
  side: string | null
  orderQty: number | null
  price: number | null
  finalStatus: string | null
  events: OrderEvent[]
}

export interface AnalysisResult {
  lines: number
  messageCount: number
  unparsed: number
  messages: LogMessage[]
  sessions: SessionFlow[]
  orders: OrderLifecycle[]
  diagnostics: Diagnostic[]
  errors: number
  warnings: number
}

const TERMINAL = new Set(['2', '4', '8', 'C', '3'])
const severityRank: Record<Severity, number> = { Error: 2, Warning: 1, Info: 0 }

class DiagnosticSet {
  private items: Diagnostic[] = []
  private index = new Map<string, number>()

  add(code: string, severity: Severity, title: string, explanation: string, suggestion: string, messageIndex: number, line: number | null) {
    const key = `${code}|${title}|${explanation}`
    const i = this.index.get(key)
    if (i !== undefined) {
      this.items[i].messageIndexes.push(messageIndex)
      return
    }
    this.index.set(key, this.items.length)
    this.items.push({ code, severity, title, explanation, suggestion, messageIndexes: [messageIndex], line })
  }

  list(): Diagnostic[] {
    return [...this.items].sort((a, b) => severityRank[b.severity] - severityRank[a.severity] || Math.min(...a.messageIndexes) - Math.min(...b.messageIndexes))
  }
}

const num = (m: ParsedMessage, tag: number): number | null => {
  const v = m.get(tag)
  if (v === undefined || v === '') return null
  const n = Number(v)
  return Number.isFinite(n) ? n : null
}

const fixTime = (v: string | undefined): number | null => {
  if (!v) return null
  const m = /^(\d{4})(\d{2})(\d{2})-(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,9}))?$/.exec(v)
  if (!m) return null
  const ms = m[7] ? Number(m[7].slice(0, 3).padEnd(3, '0')) : 0
  return Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6], ms)
}

const describe = (tag: number, value: string | undefined | null) => (value ? (enumMeaning(tag, value) ?? value) : '?')

// ------------------------------------------------------------------ extraction

interface Extracted {
  messages: LogMessage[]
  parsed: Map<number, ParsedMessage>
  lines: number
}

function extract(text: string): Extracted {
  const messages: LogMessage[] = []
  const parsed = new Map<number, ParsedMessage>()
  const lines = text.replace(/\r\n/g, '\n').split('\n')
  lines.forEach((line, li) => {
    let start = line.indexOf('8=FIX')
    while (start >= 0) {
      let next = line.indexOf('8=FIX', start + 5)
      while (next > 0 && !['\x01', '|', '^', ' ', '\t'].includes(line[next - 1])) next = line.indexOf('8=FIX', next + 5)
      let segment = (next < 0 ? line.slice(start) : line.slice(start, next)).trimEnd()
      // eslint-disable-next-line no-control-regex -- SOH (0x01) is the FIX field delimiter
      const cs = /(?:\x01|\||\^)10=\d{3}(?:\x01|\||\^)?/.exec(segment)
      if (cs) segment = segment.slice(0, cs.index + cs[0].length)
      messages.push(toLogMessage(messages.length, li + 1, segment, parsed))
      start = next
    }
  })
  return { messages, parsed, lines: lines.length }
}

function toLogMessage(index: number, line: number, segment: string, parsedMap: Map<number, ParsedMessage>): LogMessage {
  let wire = normalize(segment)
  if (!wire.endsWith('\x01')) wire += '\x01'
  // eslint-disable-next-line no-control-regex -- SOH (0x01) is the FIX field delimiter
  const raw = wire.replace(/\x01/g, '|')
  const m = parse(wire)
  const structural = m ? structuralError(m) : 'not tag=value data'
  if (!m || structural) {
    return { index, line, raw, parsed: false, parseError: structural, senderCompID: '', targetCompID: '', seqNum: 0, msgType: '?', msgTypeName: 'Unparseable', possDup: false, intact: false, integrityDetail: null, summary: 'Unparseable' }
  }
  parsedMap.set(index, m)
  const parts: string[] = []
  if (m.bodyLengthDeclared !== m.bodyLengthActual) parts.push(`BodyLength(9) says ${m.bodyLengthDeclared} but the body is ${m.bodyLengthActual} bytes`)
  if (m.checksumDeclared !== m.checksumActual)
    parts.push(`CheckSum(10) says ${String(m.checksumDeclared).padStart(3, '0')} but the bytes sum to ${String(m.checksumActual).padStart(3, '0')}`)
  return {
    index,
    line,
    raw,
    parsed: true,
    parseError: null,
    senderCompID: m.sender,
    targetCompID: m.target,
    seqNum: m.seqNum,
    msgType: m.msgType,
    msgTypeName: m.msgTypeName,
    possDup: m.get(43) === 'Y',
    intact: m.intact,
    integrityDetail: parts.length ? parts.join('; ') : null,
    summary: summarize(m),
  }
}

/** The same structural rules the C# parser enforces: 8, 9, 35 first; 10 last; no empty values. */
function structuralError(m: ParsedMessage): string | null {
  const f = m.fields
  if (f[0]?.tag !== 8) return 'MustStartWithBeginString'
  if (f[1]?.tag !== 9 || !/^\d+$/.test(f[1].value)) return 'BodyLengthMustBeSecond'
  if (f[2]?.tag !== 35) return 'MsgTypeMustBeThird'
  if (f[f.length - 1]?.tag !== 10) return 'ChecksumMustBeLast'
  if (f.some((x) => x.value === '')) return 'TagWithoutValue'
  return null
}

// ------------------------------------------------------------------ sessions

function analyzeSessions(messages: LogMessage[], parsed: Map<number, ParsedMessage>, diags: DiagnosticSet): SessionFlow[] {
  const heartBtInts = new Map<string, number>()
  const pair = (a: string, b: string) => (a < b ? `${a}\u0000${b}` : `${b}\u0000${a}`)
  const logonIndexes: number[] = []
  for (const [idx, m] of parsed) {
    if (m.msgType === 'A') {
      logonIndexes.push(idx)
      const hb = num(m, 108)
      if (hb !== null) heartBtInts.set(pair(m.sender, m.target), hb)
    }
  }

  const groups = new Map<string, [number, ParsedMessage][]>()
  for (const [idx, m] of parsed) {
    if (!m.intact) continue
    const key = `${m.sender}\u0000${m.target}`
    if (!groups.has(key)) groups.set(key, [])
    groups.get(key)!.push([idx, m])
  }

  const flows: SessionFlow[] = []
  for (const list of groups.values()) {
    list.sort((a, b) => a[0] - b[0])
    const [sender, target] = [list[0][1].sender, list[0][1].target]
    const hbInt = heartBtInts.get(pair(sender, target)) ?? 0
    let expected: number | null = null
    const gaps: { gap: SeqGap; missing: Set<number> }[] = []
    const seen = new Set<number>()
    let resends = 0
    let gapFills = 0
    let logons = 0
    let logouts = 0
    let prev: ParsedMessage | null = null
    let prevIndex = -1
    let hbReports = 0

    const cover = (seq: number, newSeqNo: number | null) => {
      for (const g of gaps) {
        g.missing.delete(seq)
        if (newSeqNo !== null) for (const s of [...g.missing]) if (s >= seq && s < newSeqNo) g.missing.delete(s)
      }
    }

    for (const [idx, m] of list) {
      const seq = m.seqNum
      const type = m.msgType
      if (type === '2') resends++
      if (type === 'A') logons++
      if (type === '5') logouts++
      const isGapFill = type === '4' && m.get(123) === 'Y'
      if (isGapFill) gapFills++
      const possDup = m.get(43) === 'Y'
      const newSeq = num(m, 36)

      const t1 = fixTime(m.get(52))
      const t0 = prev ? fixTime(prev.get(52)) : null
      if (hbInt > 0 && prev && hbReports < 3 && type !== 'A' && prev.msgType !== '5' && !logonIndexes.some((li) => li > prevIndex && li < idx) && t1 !== null && t0 !== null && (t1 - t0) / 1000 > hbInt * 1.2 + 1 && !possDup) {
        hbReports++
        diags.add(
          'HEARTBEAT_EXCEEDED',
          'Warning',
          'Silence longer than the heartbeat interval',
          `${m.sender} sent nothing for ${((t1 - t0) / 1000).toFixed(1).replace(/\.0$/, '')}s (HeartBtInt is ${hbInt}s). The counterparty should have sent a TestRequest(1) and, without an answer, logged out.`,
          "Check the sender's heartbeat timer and network path; a stalled event loop or GC pause shows up exactly like this.",
          idx,
          messages[idx].line,
        )
      }
      prev = m
      prevIndex = idx

      if (type === 'A' && m.get(141) === 'Y') {
        expected = seq + 1
        seen.clear()
        seen.add(seq)
        continue
      }
      if (type === '4' && !isGapFill) {
        expected = newSeq ?? expected
        continue
      }
      if (expected === null) {
        expected = isGapFill ? newSeq : seq + 1
        seen.add(seq)
        continue
      }
      if (seq === expected) {
        expected = isGapFill ? (newSeq ?? seq + 1) : seq + 1
        seen.add(seq)
        cover(seq, isGapFill ? newSeq : null)
      } else if (seq > expected) {
        const missing = new Set<number>()
        for (let s = expected; s < seq; s++) if (!seen.has(s)) missing.add(s)
        if (missing.size) gaps.push({ gap: { expectedSeq: expected, receivedSeq: seq, messageIndex: idx, recovered: false }, missing })
        expected = isGapFill ? (newSeq ?? seq + 1) : seq + 1
        seen.add(seq)
        cover(seq, isGapFill ? newSeq : null)
      } else if (possDup) {
        seen.add(seq)
        cover(seq, isGapFill ? newSeq : null)
      } else {
        diags.add(
          'SEQ_TOO_LOW',
          'Error',
          'Sequence number went backwards without PossDupFlag',
          `${m.sender} sent ${messageName(type)} with MsgSeqNum ${seq}, but ${expected} was expected and PossDupFlag(43) is not Y. A FIX engine must log out and disconnect on this; it usually means one side lost or reset its sequence numbers.`,
          "Agree a reset: log on with ResetSeqNumFlag(141)=Y, or set the sender's next outbound seq to the expected value. Persist sequence numbers across restarts.",
          idx,
          messages[idx].line,
        )
      }
    }

    for (const { gap, missing } of gaps) {
      const recovered = missing.size === 0
      diags.add(
        recovered ? 'SEQ_GAP_RECOVERED' : 'SEQ_GAP',
        recovered ? 'Info' : 'Error',
        recovered ? 'Sequence gap, recovered by resend' : 'Sequence gap that was never filled',
        recovered
          ? `${sender} jumped from ${gap.expectedSeq} to ${gap.receivedSeq}; the missing messages arrived later as PossDup resends or a GapFill. The session recovered as designed.`
          : `${sender} jumped from ${gap.expectedSeq} to ${gap.receivedSeq} and ${missing.size} message(s) (${formatRange(missing)}) never show up in the log, not even as resends. The receiver should have sent a ResendRequest(2); if it did, the sender didn't answer.`,
        recovered
          ? 'No action needed; this is what ResendRequest/SequenceReset are for.'
          : "Check whether the receiver sent ResendRequest(2) and whether the sender's message store still had those messages. Messages the sender can't resend must be answered with SequenceReset-GapFill.",
        gap.messageIndex,
        messages[gap.messageIndex].line,
      )
    }

    flows.push({
      senderCompID: sender,
      targetCompID: target,
      messages: list.length,
      firstSeq: list[0][1].seqNum,
      lastSeq: list[list.length - 1][1].seqNum,
      gaps: gaps.map((g) => ({ ...g.gap, recovered: g.missing.size === 0 })),
      resendRequests: resends,
      gapFills,
      logons,
      logouts,
      heartBtInt: hbInt || null,
    })
  }
  return flows
}

function formatRange(set: Set<number>): string {
  const s = [...set].sort((a, b) => a - b)
  if (s.length === 0) return 'none'
  if (s.length === 1) return String(s[0])
  if (s[s.length - 1] - s[0] === s.length - 1) return `${s[0]}-${s[s.length - 1]}`
  return s.slice(0, 8).join(', ') + (s.length > 8 ? ', …' : '')
}

// ------------------------------------------------------------------ validation

const fieldDefs = dictionary.fields as unknown as Record<string, [string, string, Record<string, string>?]>

function validate(m: ParsedMessage): { kind: 'missing' | 'invalid'; tag: number; text: string } | null {
  if (!(m.msgType in (dictionary.messages as object))) return { kind: 'invalid', tag: 35, text: `Invalid MsgType '${m.msgType}'` }
  for (const f of m.fields) {
    const def = fieldDefs[f.tag]
    if (!def) {
      if (f.tag < 5000) return { kind: 'invalid', tag: f.tag, text: `Invalid tag number ${f.tag}` }
      continue
    }
    const [name, type, values] = def
    if (!validFormat(type, f.value)) return { kind: 'invalid', tag: f.tag, text: `Incorrect data format for ${name}(${f.tag}): expected ${type}` }
    if (values && !type.startsWith('MULTIPLE') && !(f.value in values)) return { kind: 'invalid', tag: f.tag, text: `Value '${f.value}' is not valid for ${name}(${f.tag})` }
  }
  for (const tag of requiredTags(m.msgType)) {
    if (m.get(tag) === undefined) return { kind: 'missing', tag, text: `Required tag missing: ${fieldName(tag)}(${tag})` }
  }
  return null
}

function validFormat(type: string, v: string): boolean {
  switch (type) {
    case 'INT':
    case 'LENGTH':
    case 'SEQNUM':
    case 'NUMINGROUP':
    case 'DAYOFMONTH':
      return /^-?\d+$/.test(v)
    case 'FLOAT':
    case 'PRICE':
    case 'QTY':
    case 'AMT':
    case 'PERCENTAGE':
    case 'PRICEOFFSET':
      return /^-?(\d+\.?\d*|\.\d+)$/.test(v)
    case 'CHAR':
      return v.length === 1
    case 'BOOLEAN':
      return v === 'Y' || v === 'N'
    case 'UTCTIMESTAMP':
      return fixTime(v) !== null
    case 'LOCALMKTDATE':
    case 'UTCDATEONLY':
      return /^\d{8}$/.test(v)
    default:
      return true
  }
}

function validateMessages(messages: LogMessage[], parsed: Map<number, ParsedMessage>, diags: DiagnosticSet) {
  for (const [idx, m] of [...parsed].sort((a, b) => a[0] - b[0])) {
    if (!m.intact) continue
    const issue = validate(m)
    if (!issue) continue
    if (issue.kind === 'missing') {
      diags.add(
        'MISSING_REQUIRED_TAG',
        'Error',
        `${m.msgTypeName} is missing ${fieldName(issue.tag)}(${issue.tag})`,
        `Message ${idx} (seq ${m.seqNum}) from ${m.sender}: ${issue.text}. The receiver should answer with Reject(3), SessionRejectReason(373)=1, RefTagID(371)=${issue.tag}.`,
        "Populate the field; if it's conditionally required, check the counterparty's Rules of Engagement.",
        idx,
        messages[idx].line,
      )
    } else {
      diags.add(
        'INVALID_FIELD',
        'Warning',
        `Invalid field in ${m.msgTypeName}`,
        `Message ${idx} (seq ${m.seqNum}): ${issue.text}.`,
        "Compare the field against the FIX 4.4 dictionary and the venue's spec; custom tags belong in the user-defined range (5000+).",
        idx,
        messages[idx].line,
      )
    }
  }
}

// ------------------------------------------------------------------ orders

interface Builder {
  root: string
  clOrdIDs: string[]
  orderID: string | null
  symbol: string | null
  side: string | null
  orderQty: number | null
  price: number | null
  lastStatus: string | null
  lastCum: number
  tradeQtySum: number
  hasNewOrder: boolean
  hasReport: boolean
  newOrderIndex: number
  events: OrderEvent[]
}

function event(idx: number, m: ParsedMessage, description: string): OrderEvent {
  const t = fixTime(m.get(52))
  return {
    messageIndex: idx,
    time: t === null ? null : new Date(t).toISOString(),
    msgType: m.msgType,
    description,
    execType: m.get(150) ?? null,
    ordStatus: m.get(39) ?? null,
    cumQty: num(m, 14),
    leavesQty: num(m, 151),
    orderQty: num(m, 38),
    lastQty: num(m, 32),
    lastPx: num(m, 31),
    text: m.get(58) ?? null,
    possDup: m.get(43) === 'Y',
  }
}

function analyzeOrders(messages: LogMessage[], parsed: Map<number, ParsedMessage>, diags: DiagnosticSet): OrderLifecycle[] {
  const byClOrdId = new Map<string, Builder>()
  const byOrderId = new Map<string, Builder>()
  const ordered: Builder[] = []
  const execIds = new Set<string>()
  const get = (cl: string): Builder => {
    let o = byClOrdId.get(cl)
    if (!o) {
      o = { root: cl, clOrdIDs: [cl], orderID: null, symbol: null, side: null, orderQty: null, price: null, lastStatus: null, lastCum: 0, tradeQtySum: 0, hasNewOrder: false, hasReport: false, newOrderIndex: -1, events: [] }
      byClOrdId.set(cl, o)
      ordered.push(o)
    }
    return o
  }

  const effective = (m: ParsedMessage) => (m.get(43) === 'Y' ? (fixTime(m.get(122)) ?? fixTime(m.get(52))) : fixTime(m.get(52))) ?? -Infinity
  const sequence = [...parsed].filter(([, m]) => m.intact).sort((a, b) => effective(a[1]) - effective(b[1]) || a[0] - b[0])

  for (const [idx, m] of sequence) {
    const cl = m.get(11)
    const orig = m.get(41)
    const line = messages[idx].line
    const possDup = m.get(43) === 'Y'
    if ((m.msgType === 'D' || m.msgType === 'AB') && cl) {
      if (possDup && byClOrdId.has(cl)) continue
      const o = get(cl)
      o.hasNewOrder = true
      o.newOrderIndex = idx
      o.symbol = m.get(55) ?? null
      o.side = m.get(54) === '1' ? 'Buy' : m.get(54) === '2' ? 'Sell' : (m.get(54) ?? null)
      o.orderQty = num(m, 38)
      o.price = num(m, 44)
      o.events.push(
        event(
          idx,
          m,
          m.msgType === 'AB'
            ? `NewOrderMultileg ${o.side} ${o.orderQty} ${o.symbol ?? ''} ${m.get(555)}-leg @ ${o.price} net`
            : `NewOrderSingle ${o.side} ${o.orderQty} ${o.symbol ?? ''}${o.price !== null ? ` @ ${o.price}` : ' MKT'}`,
        ),
      )
    } else if ((m.msgType === 'F' || m.msgType === 'G') && cl) {
      const isReplace = m.msgType === 'G'
      let o = orig ? byClOrdId.get(orig) : undefined
      if (!o) {
        diags.add(
          'UNKNOWN_ORDER',
          'Warning',
          `${isReplace ? 'Replace' : 'Cancel'} for a ClOrdID never seen`,
          `Message ${idx}: ${messageName(m.msgType)} refers to OrigClOrdID(41)=${orig ?? '(missing)'}, which no NewOrderSingle or replace in this log used. The venue will answer with OrderCancelReject(9), CxlRejReason(102)=1 (unknown order).`,
          'OrigClOrdID must be the most recent ClOrdID of the order (after a replace, the replace\'s ClOrdID). If the order came from another session or before this log starts, include that log.',
          idx,
          line,
        )
        o = get(orig ?? cl)
      }
      if (!o.clOrdIDs.includes(cl)) o.clOrdIDs.push(cl)
      byClOrdId.set(cl, o)
      o.events.push(event(idx, m, isReplace ? `Replace → ${m.get(38)}${m.get(44) ? ` @ ${m.get(44)}` : ''}` : 'Cancel requested'))
    } else if (m.msgType === '8') {
      const orderId = m.get(37)
      let o = (cl && byClOrdId.get(cl)) || (orig && byClOrdId.get(orig)) || (orderId && byOrderId.get(orderId)) || undefined
      o ??= get(cl ?? orderId ?? `#${idx}`)
      if (cl && !o.clOrdIDs.includes(cl)) {
        o.clOrdIDs.push(cl)
        byClOrdId.set(cl, o)
      }
      if (orderId && orderId !== 'NONE') {
        o.orderID ??= orderId
        byOrderId.set(orderId, o)
      }
      const execId = m.get(17)
      if (execId && execIds.has(execId)) {
        if (!possDup) {
          diags.add(
            'DUPLICATE_EXECID',
            'Warning',
            'ExecID reused',
            `Message ${idx}: ExecID(17)=${execId} was already used and PossDupFlag is not set. Clients de-duplicate fills by ExecID, so this fill may be dropped or double-counted.`,
            'ExecID must be unique per trading day (per FIX 4.4); generate it from a persistent sequence.',
            idx,
            line,
          )
        }
        continue
      }
      if (execId) execIds.add(execId)
      if (m.get(442) === '2') {
        // A leg fill: leg quantity and price, but the strategy's CumQty/LeavesQty. Not a state report.
        o.hasReport = true
        o.events.push(event(idx, m, `Leg fill ${m.get(54) === '1' ? 'buy' : 'sell'} ${m.get(32)} ${m.get(55)} ${m.get(202)} @ ${m.get(31)}`))
        continue
      }
      applyReport(o, idx, m, line, diags)
    } else if (m.msgType === '9') {
      const o = (cl && byClOrdId.get(cl)) || (orig && byClOrdId.get(orig)) || undefined
      o?.events.push(event(idx, m, `Cancel/replace rejected: ${m.get(58) ?? describe(102, m.get(102))}`))
    }
  }

  for (const o of ordered.filter((x) => x.hasNewOrder && !x.hasReport)) {
    const nos = parsed.get(o.newOrderIndex)
    const reject = nos ? [...parsed.values()].find((r) => (r.msgType === '3' || r.msgType === 'j') && num(r, 45) === nos.seqNum && r.sender === nos.target) : undefined
    diags.add(
      'NO_ACK',
      'Warning',
      'Order never acknowledged',
      reject
        ? `ClOrdID ${o.root} got no ExecutionReport: it was rejected before reaching the order book (${messageName(reject.msgType)}: ${reject.get(58)}).`
        : `ClOrdID ${o.root} has no ExecutionReport anywhere in the log. Either the venue never received it (check for a sequence gap or a garbled message right before) or its reports went to another session.`,
      reject
        ? 'Fix the field named in the reject and resend with a new ClOrdID.'
        : "Match the order's MsgSeqNum against the venue's inbound log; send an OrderStatusRequest(H) to ask for its state.",
      o.newOrderIndex,
      messages[o.newOrderIndex].line,
    )
  }

  return ordered
    .filter((o) => o.events.length > 0)
    .map((o) => ({
      rootClOrdID: o.root,
      clOrdIDs: o.clOrdIDs,
      orderID: o.orderID,
      symbol: o.symbol,
      side: o.side,
      orderQty: o.orderQty,
      price: o.price,
      finalStatus: o.lastStatus ? describe(39, o.lastStatus) : null,
      events: o.events,
    }))
}

function applyReport(o: Builder, idx: number, m: ParsedMessage, line: number, diags: DiagnosticSet) {
  o.hasReport = true
  const status = m.get(39) ?? null
  const execType = m.get(150) ?? null
  const cum = num(m, 14)
  const leaves = num(m, 151)
  const qty = num(m, 38) ?? o.orderQty
  const statusName = describe(39, status)
  o.events.push(event(idx, m, `${describe(150, execType)} → ${statusName}`))

  if (execType === '8' || status === '8') {
    diags.add(
      'ORDER_REJECTED',
      'Warning',
      'Order rejected by the venue',
      `ClOrdID ${m.get(11)}: ${m.get(58) ?? 'no Text(58)'}${m.get(103) ? ` (OrdRejReason ${m.get(103)} = ${describe(103, m.get(103))})` : ''}.`,
      'Business reject, not a session problem: fix the order (limits, price, instrument) and send it with a new ClOrdID.',
      idx,
      line,
    )
  }
  if (execType === 'I') return

  if (o.lastStatus && TERMINAL.has(o.lastStatus) && status && !TERMINAL.has(status)) {
    diags.add(
      'STATE_REGRESSION',
      'Error',
      'Order came back to life after a terminal state',
      `ClOrdID ${o.root}: OrdStatus went from ${describe(39, o.lastStatus)} to ${statusName} (message ${idx}). Filled, canceled and rejected are final; clients stop tracking the order and will ignore or mis-book this report.`,
      "Check the venue's order state machine for a race between a fill and a cancel, or for reports sent out of order.",
      idx,
      line,
    )
  }

  if (cum !== null) {
    if (cum < o.lastCum) {
      diags.add(
        'CUMQTY_DECREASED',
        'Error',
        'CumQty went down',
        `ClOrdID ${o.root}: CumQty(14) dropped from ${o.lastCum} to ${cum} at message ${idx}. Executed quantity can only grow (a trade bust uses ExecType H).`,
        'Look for reports sent out of order or a report built from stale order state.',
        idx,
        line,
      )
    }
    o.lastCum = Math.max(o.lastCum, cum)
  }

  if (cum !== null && leaves !== null && qty !== null && status) {
    const terminal = TERMINAL.has(status)
    if (!terminal && cum + leaves !== qty) {
      diags.add(
        'QTY_INVARIANT',
        'Error',
        'CumQty + LeavesQty ≠ OrderQty',
        `ClOrdID ${o.root}, message ${idx}: CumQty ${cum} + LeavesQty ${leaves} = ${cum + leaves}, but OrderQty is ${qty} and the order is still ${statusName}. For a live order the two must add up.`,
        'Recompute LeavesQty as OrderQty − CumQty after every fill and replace.',
        idx,
        line,
      )
    } else if (terminal && leaves !== 0) {
      diags.add(
        'QTY_INVARIANT',
        'Error',
        'Terminal order still has LeavesQty',
        `ClOrdID ${o.root}, message ${idx}: the order is ${statusName} but LeavesQty is ${leaves}. A done order has nothing left to execute.`,
        'Set LeavesQty(151)=0 on Filled, Canceled, Rejected and Expired reports.',
        idx,
        line,
      )
    }
  }

  const last = num(m, 32)
  if ((execType === 'F' || execType === '1' || execType === '2') && last !== null) {
    o.tradeQtySum += last
    if (cum !== null && o.tradeQtySum !== cum) {
      diags.add(
        'FILL_SUM_MISMATCH',
        'Warning',
        "Fills don't add up to CumQty",
        `ClOrdID ${o.root}: LastQty(32) of the trades so far sums to ${o.tradeQtySum}, but this report says CumQty ${cum}. Either a fill report is missing from the log or CumQty is wrong.`,
        'Reconcile by ExecID; request an OrderStatusRequest(H) if a report may have been lost.',
        idx,
        line,
      )
      o.tradeQtySum = cum
    }
  }

  o.lastStatus = status ?? o.lastStatus
  o.orderQty ??= qty
}

// ------------------------------------------------------------------ rejects and logouts

function analyzeRejects(messages: LogMessage[], parsed: Map<number, ParsedMessage>, diags: DiagnosticSet) {
  for (const [idx, m] of [...parsed].sort((a, b) => a[0] - b[0])) {
    const line = messages[idx].line
    if (m.msgType === '3') {
      const reason = m.get(373)
      const reasonName = reason ? describe(373, reason) : 'unspecified'
      const tag = num(m, 371)
      const refType = m.get(372)
      diags.add(
        'SESSION_REJECT',
        'Warning',
        `Session-level Reject: ${reasonName}`,
        `${m.sender} rejected seq ${m.get(45)} (${refType ? messageName(refType) : '?'})${tag !== null ? ` on tag ${tag} (${fieldName(tag)})` : ''}: ${m.get(58) ?? reasonName}. The rejected message was not processed, but its sequence number was consumed.`,
        "Fix the field and resend as a new message (new ClOrdID for orders); don't resend with the old MsgSeqNum.",
        idx,
        line,
      )
    } else if (m.msgType === 'j') {
      const refType = m.get(372)
      diags.add(
        'BUSINESS_REJECT',
        'Warning',
        'BusinessMessageReject',
        `${m.sender} rejected a ${refType ? messageName(refType) : 'message'}: ${m.get(58) ?? describe(380, m.get(380))}.`,
        'The message was valid FIX but the application can\'t handle it; check which message types the counterparty supports.',
        idx,
        line,
      )
    } else if (m.msgType === '5' && m.get(58)) {
      const text = m.get(58)!
      const tooLow = text.toLowerCase().includes('too low')
      diags.add(
        'LOGOUT_REASON',
        tooLow ? 'Error' : 'Info',
        'Logout with a reason',
        `${m.sender} logged out: "${text}".`,
        tooLow
          ? 'Sequence numbers are out of step; reset with ResetSeqNumFlag(141)=Y on the next Logon or fix the stored seq numbers.'
          : 'Read the text; if it names a field or limit, fix that before reconnecting.',
        idx,
        line,
      )
    }
  }
}

function summarize(m: ParsedMessage): string {
  const g = m.get
  switch (m.msgType) {
    case 'D':
      return `${g(54) === '1' ? 'Buy' : 'Sell'} ${g(38)} ${g(55) ?? ''}${g(44) ? ` @ ${g(44)}` : ''} (${g(11)})`
    case '8':
      return `${describe(150, g(150))} ${g(11)} cum ${g(14)} leaves ${g(151)}`
    case 'F':
      return `Cancel ${g(41)}`
    case 'G':
      return `Replace ${g(41)} → ${g(11)}`
    case '2':
      return `Resend ${g(7)}..${g(16)}`
    case '4':
      return `${g(123) === 'Y' ? 'GapFill' : 'Reset'} → ${g(36)}`
    case '5':
      return `Logout${g(58) ? `: ${g(58)}` : ''}`
    case '3':
      return `Reject seq ${g(45)}: ${g(58) ?? ''}`
    default:
      return m.msgTypeName
  }
}

export function analyze(text: string): AnalysisResult {
  const { messages, parsed, lines } = extract(text)
  const diags = new DiagnosticSet()
  for (const m of messages.filter((x) => !x.parsed)) {
    diags.add('UNPARSEABLE', 'Warning', "Text that looks like FIX but can't be parsed", `Line ${m.line}: ${m.parseError}. The receiving engine would drop this silently.`, "Check the log wasn't truncated or re-wrapped, and that each field is tag=value.", m.index, m.line)
  }
  for (const m of messages.filter((x) => x.parsed && !x.intact)) {
    diags.add(
      'GARBLED',
      'Error',
      'Garbled message (BodyLength or CheckSum wrong)',
      `Message ${m.index} (${m.msgTypeName}, seq ${m.seqNum}): ${m.integrityDetail}. FIX engines ignore garbled messages without replying, so the counterparty sees a sequence gap on the next message.`,
      'Look for manual edits, character-set conversion (the checksum is over bytes), or a proxy rewriting fields.',
      m.index,
      m.line,
    )
  }
  const sessions = analyzeSessions(messages, parsed, diags)
  validateMessages(messages, parsed, diags)
  const orders = analyzeOrders(messages, parsed, diags)
  analyzeRejects(messages, parsed, diags)
  const diagnostics = diags.list()
  return {
    lines,
    messageCount: messages.length,
    unparsed: messages.filter((m) => !m.parsed).length,
    messages,
    sessions,
    orders,
    diagnostics,
    errors: diagnostics.filter((d) => d.severity === 'Error').length,
    warnings: diagnostics.filter((d) => d.severity === 'Warning').length,
  }
}
