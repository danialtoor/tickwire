// Builds src/fix/fix44.json from the same FIX44.xml the C# engine embeds, so the browser decoder and the
// server agree on names, enum meanings and required fields. Run: npm run gen:dictionary
import { readFileSync, writeFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { XMLParser } from 'fast-xml-parser'

const here = dirname(fileURLToPath(import.meta.url))
const xml = readFileSync(resolve(here, '../../src/Tickwire.Fix/Spec/FIX44.xml'), 'utf8')
const doc = new XMLParser({ ignoreAttributes: false, attributeNamePrefix: '', isArray: (name) => ['field', 'value', 'group', 'component', 'message'].includes(name) }).parse(xml).fix

const pascal = (s) => s.split('_').filter(Boolean).map((p) => p[0].toUpperCase() + p.slice(1).toLowerCase()).join('')

const fields = {}
const nameToTag = {}
for (const f of doc.fields.field) {
  const values = {}
  for (const v of f.value ?? []) values[v.enum] = pascal(v.description ?? v.enum)
  fields[f.number] = Object.keys(values).length ? [f.name, f.type, values] : [f.name, f.type]
  nameToTag[f.name] = Number(f.number)
}

const components = Object.fromEntries((doc.components?.component ?? []).map((c) => [c.name, c]))

function walk(node, required, allowed, requiredPath, inGroup) {
  for (const f of node.field ?? []) {
    const tag = nameToTag[f.name]
    if (!tag) continue
    allowed.add(tag)
    if (!inGroup && requiredPath && f.required === 'Y') required.push(tag)
  }
  for (const g of node.group ?? []) {
    const tag = nameToTag[g.name]
    if (tag) {
      allowed.add(tag)
      if (!inGroup && requiredPath && g.required === 'Y') required.push(tag)
    }
    walk(g, required, allowed, false, true)
  }
  for (const c of node.component ?? []) {
    const comp = components[c.name]
    if (comp) walk(comp, required, allowed, requiredPath && c.required === 'Y', inGroup)
  }
}

const header = []
walk(doc.header, header, new Set(), true, false)
const messages = {}
for (const m of doc.messages.message) {
  const required = []
  walk(m, required, new Set(), true, false)
  messages[m.msgtype] = [m.name, m.msgcat, required]
}

const out = {
  beginString: `FIX.${doc.major}.${doc.minor}`,
  headerRequired: header.filter((t) => ![8, 9, 35].includes(t)),
  fields,
  messages,
}
writeFileSync(resolve(here, '../src/fix/fix44.json'), JSON.stringify(out))
console.log(`fix44.json: ${Object.keys(fields).length} fields, ${Object.keys(messages).length} messages`)
