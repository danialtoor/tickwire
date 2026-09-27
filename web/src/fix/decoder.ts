import dictionary from './fix44.json'

type FieldDef = [name: string, type: string, values?: Record<string, string>]
type MessageDef = [name: string, category: string, required: number[]]

const fields = dictionary.fields as unknown as Record<string, FieldDef>
const messages = dictionary.messages as unknown as Record<string, MessageDef>

const CUSTOM_TAGS: Record<number, string> = { 20001: 'TheoValue', 20002: 'UnderlyingLastPx' }
const HEADER_TAGS = new Set([8, 9, 35, 49, 56, 34, 52, 43, 97, 122, 115, 128, 50, 57, 369, 90, 91, 116, 129, 142, 143, 144, 145, 347, 212, 213, 627, 628, 629, 630])
const TRAILER_TAGS = new Set([10, 93, 89])

export interface RawField {
  tag: number
  value: string
}

export interface DecodedField extends RawField {
  name: string
  meaning?: string
  section: 'header' | 'body' | 'trailer'
}

export interface ParsedMessage {
  fields: RawField[]
  msgType: string
  msgTypeName: string
  seqNum: number
  sender: string
  target: string
  bodyLengthDeclared: number
  bodyLengthActual: number
  checksumDeclared: number
  checksumActual: number
  intact: boolean
  get: (tag: number) => string | undefined
}

export const fieldName = (tag: number): string => fields[tag]?.[0] ?? CUSTOM_TAGS[tag] ?? `Tag${tag}`

export const messageName = (msgType: string): string => messages[msgType]?.[0] ?? `Unknown(${msgType})`

export const isAdminMsgType = (msgType: string): boolean => messages[msgType]?.[1] === 'admin'

export const enumMeaning = (tag: number, value: string): string | undefined => {
  if (tag === 35) return messageName(value)
  const values = fields[tag]?.[2]
  if (!values) return undefined
  if (fields[tag][1].startsWith('MULTIPLE')) return value.split(' ').map((v) => values[v] ?? v).join(', ')
  return values[value]
}

export const requiredTags = (msgType: string): number[] => [...dictionary.headerRequired, ...(messages[msgType]?.[2] ?? [])]

export const isKnownTag = (tag: number): boolean => tag in fields || tag >= 5000

/** Accepts SOH, '|', '^' or literal "<SOH>" delimiters. */
export function normalize(text: string): string {
  return text.trim().replace(/<SOH>/gi, '\x01').replace(/\\x01/gi, '\x01').replace(/[|^](?=\d+=|$)/g, '\x01')
}

/** Parses one message. Returns null when it isn't tag=value data at all. */
export function parse(text: string): ParsedMessage | null {
  const wire = normalize(text)
  const fieldsOut: RawField[] = []
  let pos = 0
  while (pos < wire.length) {
    const eq = wire.indexOf('=', pos)
    if (eq < 0) break
    const tag = Number(wire.slice(pos, eq))
    if (!Number.isInteger(tag) || tag <= 0) return null
    let end = wire.indexOf('\x01', eq + 1)
    if (end < 0) end = wire.length
    fieldsOut.push({ tag, value: wire.slice(eq + 1, end) })
    pos = end + 1
  }

  if (fieldsOut.length < 3 || fieldsOut[0].tag !== 8) return null

  const get = (tag: number) => fieldsOut.find((f) => f.tag === tag)?.value
  const bodyStart = wire.indexOf('\x01', wire.indexOf('\x019=') + 1) + 1
  const checksumAt = wire.lastIndexOf('\x0110=')
  const bodyLengthActual = checksumAt >= 0 ? checksumAt + 1 - bodyStart : -1
  let sum = 0
  const upto = checksumAt >= 0 ? checksumAt + 1 : wire.length
  for (let i = 0; i < upto; i++) sum += wire.charCodeAt(i)
  const checksumActual = sum % 256
  const msgType = get(35) ?? '?'
  const bodyLengthDeclared = Number(get(9) ?? -1)
  const checksumDeclared = Number(get(10) ?? -1)
  return {
    fields: fieldsOut,
    msgType,
    msgTypeName: messageName(msgType),
    seqNum: Number(get(34) ?? 0),
    sender: get(49) ?? '',
    target: get(56) ?? '',
    bodyLengthDeclared,
    bodyLengthActual,
    checksumDeclared,
    checksumActual,
    intact: bodyLengthDeclared === bodyLengthActual && checksumDeclared === checksumActual,
    get,
  }
}

export function decode(message: ParsedMessage): DecodedField[] {
  return message.fields.map((f) => ({
    ...f,
    name: fieldName(f.tag),
    meaning: enumMeaning(f.tag, f.value),
    section: HEADER_TAGS.has(f.tag) ? 'header' : TRAILER_TAGS.has(f.tag) ? 'trailer' : 'body',
  }))
}

/** Human summary for a list row, e.g. "Buy 10 SPY 560C @ 4.25" or "Filled 10 @ 4.25". */
export function summarize(m: ParsedMessage): string {
  const g = m.get
  switch (m.msgType) {
    case 'D': {
      const side = g(54) === '1' ? 'Buy' : g(54) === '2' ? 'Sell' : `Side=${g(54)}`
      const px = g(44) ? ` @ ${g(44)}` : ' MKT'
      return `${side} ${g(38) ?? '?'} ${instrument(m)}${px}`
    }
    case 'F':
      return `Cancel ${g(41)}`
    case 'G':
      return `Replace ${g(41)} → ${g(38)}${g(44) ? ` @ ${g(44)}` : ''}`
    case '8': {
      const exec = enumMeaning(150, g(150) ?? '') ?? g(150)
      const last = g(32) && Number(g(32)) > 0 ? ` ${g(32)} @ ${g(31)}` : ''
      const text = g(58) ? ` — ${g(58)}` : ''
      return `${exec}${last} · cum ${g(14)} leaves ${g(151)}${text}`
    }
    case '9':
      return `Cancel rejected: ${g(58) ?? ''}`
    case '0':
      return g(112) ? `Heartbeat (TestReqID ${g(112)})` : 'Heartbeat'
    case '1':
      return `TestRequest ${g(112)}`
    case '2':
      return `Resend ${g(7)}..${g(16) === '0' ? '∞' : g(16)}`
    case '3':
      return `Reject seq ${g(45)} tag ${g(371) ?? '-'}: ${g(58) ?? enumMeaning(373, g(373) ?? '') ?? ''}`
    case '4':
      return g(123) === 'Y' ? `GapFill → ${g(36)}` : `SequenceReset → ${g(36)}`
    case '5':
      return g(58) ? `Logout: ${g(58)}` : 'Logout'
    case 'A':
      return `Logon HeartBtInt=${g(108)}${g(141) === 'Y' ? ' ResetSeqNum' : ''}`
    case 'j':
      return `Business reject: ${g(58) ?? ''}`
    default:
      return m.msgTypeName
  }
}

function instrument(m: ParsedMessage): string {
  const strike = m.get(202)
  const pc = m.get(201) === '1' ? 'C' : m.get(201) === '0' ? 'P' : ''
  return strike ? `${m.get(55)} ${Number(strike)}${pc}` : (m.get(55) ?? '')
}
