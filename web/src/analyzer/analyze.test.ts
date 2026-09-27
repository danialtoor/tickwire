import { readdirSync, readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import { parse } from '../fix/decoder'
import { analyze } from './analyze'

// The same corpus and expectations the C# analyzer is tested against.
const samples = resolve(process.cwd(), '../samples') // vitest runs from web/
const expected = JSON.parse(readFileSync(join(samples, 'expected.json'), 'utf8')) as Record<string, string[]>

describe('log analyzer parity with the C# implementation', () => {
  for (const file of readdirSync(samples).filter((f) => f.endsWith('.log')).sort()) {
    it(`${file} produces the expected diagnostics`, () => {
      const result = analyze(readFileSync(join(samples, file), 'utf8'))
      const codes = [...new Set(result.diagnostics.map((d) => d.code))].sort()
      expect(codes).toEqual([...expected[file]].sort())
      expect(result.unparsed).toBe(0)
    })
  }

  it('rebuilds the order through a resend', () => {
    const result = analyze(readFileSync(join(samples, '01-gap-recovered-and-unfilled.log'), 'utf8'))
    const order = result.orders.find((o) => o.rootClOrdID === 'ORD-1')!
    expect(order.finalStatus).toBe('Filled')
    expect(order.events.filter((e) => e.execType === 'F').map((e) => e.cumQty)).toEqual([4, 6, 8, 10])
  })
})

describe('decoder', () => {
  it('verifies the reference message', () => {
    const m = parse(
      '8=FIX.4.2|9=178|35=8|49=PHLX|56=PERS|52=20071123-05:30:00.000|11=ATOMNOCCC9990900|20=3|150=E|39=E|55=MSFT|167=CS|54=1|38=15|40=2|44=15|58=PHLX EQUITY TESTING|59=0|47=C|32=0|31=0|151=15|14=0|6=0|10=128|',
    )!
    expect(m.intact).toBe(true)
    expect(m.msgTypeName).toBe('ExecutionReport')
    expect(m.checksumActual).toBe(128)
  })

  it('keeps pipes that are part of a value', () => {
    const m = parse('8=FIX.4.4|9=5|35=0|58=a|b|10=000|')!
    expect(m.get(58)).toBe('a|b')
  })
})
