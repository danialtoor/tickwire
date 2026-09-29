import { useEffect, useMemo, useState } from 'react'
import { analyze, type AnalysisResult, type Diagnostic } from '../analyzer/analyze'
import { apiUrl } from '../lib/api'
import { cn } from '../lib/format'
import { useStore } from '../state/store'

interface Sample {
  file: string
  description: string
}

type Engine = 'server' | 'browser'

const severityTone: Record<string, string> = {
  Error: 'border-sell/40 bg-sell-soft text-sell',
  Warning: 'border-warn/40 bg-warn-soft text-warn',
  Info: 'border-info/40 bg-info-soft text-info',
}

export default function Analyzer() {
  const mode = useStore((s) => s.mode)
  const [text, setText] = useState('')
  const [samples, setSamples] = useState<Sample[]>([])
  const [result, setResult] = useState<AnalysisResult | null>(null)
  const [engineUsed, setEngineUsed] = useState<Engine | null>(null)
  const [engine, setEngine] = useState<Engine>('server')
  const [focus, setFocus] = useState<Diagnostic | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    fetch('/samples/index.json')
      .then((r) => r.json())
      .then(setSamples)
      .catch(() => setSamples([]))
  }, [])

  const run = async (input: string, preferred: Engine = engine) => {
    setError(null)
    setFocus(null)
    if (!input.trim()) return
    const useServer = preferred === 'server' && mode === 'live'
    setBusy(true)
    try {
      if (useServer) {
        const res = await fetch(apiUrl('/api/analyzer'), { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ text: input }) })
        if (!res.ok) throw new Error((await res.json().catch(() => null))?.detail ?? res.statusText)
        const data = await res.json()
        setResult(fromServer(data))
        setEngineUsed('server')
      } else {
        setResult(analyze(input))
        setEngineUsed('browser')
      }
    } catch (e) {
      // The browser engine is the fallback when the API is down or rejects the request.
      setResult(analyze(input))
      setEngineUsed('browser')
      setError(`Server analysis failed (${e instanceof Error ? e.message : e}); showing the in-browser result.`)
    } finally {
      setBusy(false)
    }
  }

  const loadSample = async (s: Sample) => {
    const body = await (await fetch(`/samples/${s.file}`)).text()
    setText(body)
    await run(body)
  }

  const onFile = async (file: File | undefined) => {
    if (!file) return
    const body = await file.text()
    setText(body)
    await run(body)
  }

  const highlighted = useMemo(() => new Set(focus?.messageIndexes ?? []), [focus])

  return (
    <div className="mx-auto max-w-[1400px] space-y-5 px-4 py-8">
      <header className="space-y-2">
        <h1 className="text-2xl font-semibold">FIX Log Analyzer</h1>
        <p className="max-w-3xl text-sm leading-relaxed text-ink-2">
          Paste a FIX log in any common shape: raw SOH, <code className="num">|</code> or <code className="num">^</code> delimited, or QuickFIX
          log lines with timestamps. The analyzer rebuilds each session's sequence timeline and each order's lifecycle, then explains what went
          wrong the way a support engineer would. The same engine ships as a CLI: <code className="num text-accent">tickwire fixlog analyze file.log</code>.
        </p>
      </header>

      <section className="panel space-y-3 p-4">
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-xs text-muted">Broken logs to try:</span>
          {samples.map((s) => (
            <button key={s.file} type="button" title={s.description} onClick={() => loadSample(s)} className="num rounded-md border border-line bg-bg px-2 py-1 text-[11.5px] text-ink-2 hover:border-line-strong hover:text-ink">
              {s.file.replace(/^\d+-/, '').replace('.log', '')}
            </button>
          ))}
        </div>
        <textarea
          value={text}
          onChange={(e) => setText(e.target.value)}
          spellCheck={false}
          placeholder="8=FIX.4.4|9=...|35=D|49=CLIENT|56=TICKWIRE|34=2|..."
          className="num scroll-thin h-44 w-full resize-y rounded-lg border border-line bg-bg p-3 text-[11.5px] leading-relaxed text-ink-2"
          aria-label="FIX log"
        />
        <div className="flex flex-wrap items-center gap-3">
          <button type="button" onClick={() => run(text)} disabled={busy || !text.trim()} className="rounded-lg bg-accent px-4 py-2 text-sm font-semibold text-accent-ink disabled:opacity-40">
            {busy ? 'Analyzing…' : 'Analyze'}
          </button>
          <label className="cursor-pointer rounded-lg border border-line px-3 py-2 text-sm text-ink-2 hover:text-ink">
            Upload log
            <input type="file" accept=".log,.txt,.fix" className="hidden" onChange={(e) => onFile(e.target.files?.[0])} />
          </label>
          <div className="ml-auto flex items-center gap-2 text-xs text-muted">
            Engine
            <div className="flex rounded-md border border-line bg-bg p-0.5">
              {(['server', 'browser'] as const).map((e) => (
                <button
                  key={e}
                  type="button"
                  onClick={() => setEngine(e)}
                  disabled={e === 'server' && mode !== 'live'}
                  className={cn('rounded px-2 py-0.5 disabled:opacity-40', engine === e ? 'bg-panel-2 text-ink' : 'hover:text-ink-2')}
                >
                  {e === 'server' ? 'C# (server)' : 'TypeScript (browser)'}
                </button>
              ))}
            </div>
          </div>
        </div>
        {error && <p className="text-xs text-warn">{error}</p>}
      </section>

      {result && (
        <>
          <div className="grid grid-cols-2 gap-3 md:grid-cols-5">
            <Tile label="Messages" value={result.messageCount} />
            <Tile label="Sessions (directions)" value={result.sessions.length} />
            <Tile label="Orders" value={result.orders.length} />
            <Tile label="Errors" value={result.errors} tone={result.errors ? 'text-sell' : 'text-buy'} />
            <Tile label="Warnings" value={result.warnings} tone={result.warnings ? 'text-warn' : 'text-buy'} />
          </div>
          <p className="text-[11px] text-muted">Analyzed by the {engineUsed === 'server' ? 'C# engine on the server' : 'TypeScript engine in your browser'}.</p>

          <div className="grid gap-4 lg:grid-cols-[minmax(0,1.1fr)_minmax(0,1fr)]">
            <section className="space-y-2">
              <h2 className="panel-title">Findings</h2>
              {result.diagnostics.length === 0 && <div className="panel p-4 text-sm text-buy">No problems found. Clean session.</div>}
              {result.diagnostics.map((d, i) => (
                <button
                  key={i}
                  type="button"
                  onClick={() => setFocus(focus === d ? null : d)}
                  className={cn('panel block w-full p-4 text-left transition-colors hover:border-line-strong', focus === d && 'border-accent/50')}
                >
                  <div className="flex flex-wrap items-center gap-2">
                    <span className={cn('rounded border px-1.5 py-0.5 text-[10.5px] font-semibold uppercase', severityTone[d.severity])}>{d.severity}</span>
                    <span className="num text-[11px] text-muted">{d.code}</span>
                    <span className="num ml-auto text-[11px] text-muted">
                      line {d.line} · {d.messageIndexes.length} message{d.messageIndexes.length > 1 ? 's' : ''}
                    </span>
                  </div>
                  <div className="mt-1.5 font-medium">{d.title}</div>
                  <p className="mt-1 text-[13px] leading-relaxed text-ink-2">{d.explanation}</p>
                  <p className="mt-2 text-[12.5px] leading-relaxed text-ink">
                    <span className="text-accent">Fix: </span>
                    {d.suggestion}
                  </p>
                </button>
              ))}
            </section>

            <section className="space-y-4">
              <div className="panel">
                <h2 className="panel-title border-b border-line px-4 py-2">Sessions</h2>
                <ul className="divide-y divide-line/60 text-[12.5px]">
                  {result.sessions.map((s) => (
                    <li key={`${s.senderCompID}>${s.targetCompID}`} className="px-4 py-2">
                      <div className="num font-medium">
                        {s.senderCompID} → {s.targetCompID}
                      </div>
                      <div className="num mt-0.5 text-[11.5px] text-muted">
                        {s.messages} msgs · seq {s.firstSeq}…{s.lastSeq} · {s.resendRequests} resend req · {s.gapFills} gap fill{s.heartBtInt ? ` · HB ${s.heartBtInt}s` : ''}
                      </div>
                      {s.gaps.length > 0 && (
                        <div className="mt-1 flex flex-wrap gap-1">
                          {s.gaps.map((g, i) => (
                            <span key={i} className={cn('num rounded px-1.5 py-0.5 text-[10.5px]', g.recovered ? 'bg-info-soft text-info' : 'bg-sell-soft text-sell')}>
                              gap {g.expectedSeq}→{g.receivedSeq} {g.recovered ? 'recovered' : 'unfilled'}
                            </span>
                          ))}
                        </div>
                      )}
                    </li>
                  ))}
                </ul>
              </div>

              <div className="panel">
                <h2 className="panel-title border-b border-line px-4 py-2">Order lifecycles</h2>
                <ul className="divide-y divide-line/60">
                  {result.orders.map((o) => (
                    <li key={o.rootClOrdID} className="px-4 py-3">
                      <div className="flex flex-wrap items-baseline gap-2 text-[12.5px]">
                        <span className="num font-semibold">{o.rootClOrdID}</span>
                        <span className="text-ink-2">
                          {o.side} {o.orderQty} {o.symbol} {o.price !== null ? `@ ${o.price}` : ''}
                        </span>
                        <span className="ml-auto rounded bg-panel-2 px-1.5 py-0.5 text-[11px]">{o.finalStatus ?? 'no report'}</span>
                      </div>
                      {o.clOrdIDs.length > 1 && <div className="num mt-0.5 text-[11px] text-muted">chain {o.clOrdIDs.join(' → ')}</div>}
                      <ol className="mt-2 space-y-1 border-l border-line pl-3">
                        {o.events.map((e) => (
                          <li key={e.messageIndex} className={cn('num text-[11.5px]', highlighted.has(e.messageIndex) ? 'text-accent' : 'text-ink-2')}>
                            <span className="text-muted">#{e.messageIndex}</span> {e.description}
                            {e.cumQty !== null && (
                              <span className="text-muted">
                                {' '}
                                · cum {e.cumQty} leaves {e.leavesQty}
                              </span>
                            )}
                            {e.possDup && <span className="ml-1 text-warn">PossDup</span>}
                          </li>
                        ))}
                      </ol>
                    </li>
                  ))}
                </ul>
              </div>
            </section>
          </div>

          <section className="panel overflow-hidden">
            <h2 className="panel-title border-b border-line px-4 py-2">Messages</h2>
            <div className="scroll-thin max-h-[420px] overflow-auto">
              <table className="w-full text-left text-[11.5px]">
                <thead className="sticky top-0 bg-panel text-[10px] uppercase tracking-wider text-muted">
                  <tr className="[&>th]:border-b [&>th]:border-line [&>th]:px-3 [&>th]:py-1.5">
                    <th>#</th>
                    <th>Line</th>
                    <th>From → to</th>
                    <th className="text-right">Seq</th>
                    <th>Type</th>
                    <th>Summary</th>
                  </tr>
                </thead>
                <tbody className="num">
                  {result.messages.map((m) => (
                    <tr key={m.index} className={cn('border-b border-line/40', highlighted.has(m.index) && 'bg-accent/10', !m.intact && 'text-sell')}>
                      <td className="px-3 py-1 text-muted">{m.index}</td>
                      <td className="px-3 py-1 text-muted">{m.line}</td>
                      <td className="px-3 py-1">
                        {m.senderCompID} → {m.targetCompID}
                      </td>
                      <td className="px-3 py-1 text-right">{m.seqNum}</td>
                      <td className="px-3 py-1">
                        {m.msgTypeName}
                        {m.possDup && <span className="ml-1 text-warn">43=Y</span>}
                      </td>
                      <td className="px-3 py-1 font-sans text-ink-2">{m.intact ? m.summary : (m.integrityDetail ?? m.parseError)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>
        </>
      )}
    </div>
  )
}

function Tile({ label, value, tone }: { label: string; value: number; tone?: string }) {
  return (
    <div className="panel px-4 py-3">
      <div className="text-[11px] text-muted">{label}</div>
      <div className={cn('num mt-1 text-2xl font-semibold', tone)}>{value}</div>
    </div>
  )
}

/** The server returns the same shape with PascalCase enums serialized as strings; normalize the few that differ. */
function fromServer(data: AnalysisResult & { diagnostics: (Diagnostic & { severity: string })[] }): AnalysisResult {
  return {
    ...data,
    errors: data.diagnostics.filter((d) => d.severity === 'Error').length,
    warnings: data.diagnostics.filter((d) => d.severity === 'Warning').length,
  }
}
