import { AreaSeries, ColorType, createChart, type IChartApi, type ISeriesApi, type UTCTimestamp } from 'lightweight-charts'
import { useEffect, useRef } from 'react'
import type { SpotPoint } from '../state/store'

const cssVar = (name: string) => getComputedStyle(document.documentElement).getPropertyValue(name).trim() || '#888'

export function SpotChart({ points }: { points: SpotPoint[] }) {
  const host = useRef<HTMLDivElement>(null)
  const chart = useRef<IChartApi | null>(null)
  const series = useRef<ISeriesApi<'Area'> | null>(null)

  useEffect(() => {
    if (!host.current) return
    const accent = cssVar('--color-accent')
    const c = createChart(host.current, {
      autoSize: true,
      layout: { background: { type: ColorType.Solid, color: 'transparent' }, textColor: cssVar('--color-muted'), fontFamily: 'JetBrains Mono', fontSize: 10, attributionLogo: false },
      grid: { vertLines: { visible: false }, horzLines: { color: cssVar('--color-line') } },
      rightPriceScale: { borderVisible: false },
      timeScale: { borderVisible: false, timeVisible: true, secondsVisible: true },
      crosshair: { horzLine: { labelBackgroundColor: cssVar('--color-panel-2') }, vertLine: { labelBackgroundColor: cssVar('--color-panel-2') } },
      handleScroll: false,
      handleScale: false,
    })
    series.current = c.addSeries(AreaSeries, {
      lineColor: accent,
      topColor: accent.length === 7 ? `${accent}47` : accent, // hex + ~28% alpha
      bottomColor: accent.length === 7 ? `${accent}00` : 'transparent',
      lineWidth: 2,
      priceLineVisible: false,
    })
    chart.current = c
    return () => {
      c.remove()
      chart.current = null
      series.current = null
    }
  }, [])

  useEffect(() => {
    series.current?.setData(points.map((p) => ({ time: p.time as UTCTimestamp, value: p.value })))
    chart.current?.timeScale().fitContent()
  }, [points])

  return <div ref={host} className="h-full min-h-[140px] w-full" />
}
