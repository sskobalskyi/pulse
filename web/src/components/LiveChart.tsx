import { useEffect, useRef } from 'react'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import { useQuery } from '@tanstack/react-query'
import { fetchSeries } from '../api'
import type { LivePoint } from '../liveState'

interface Props {
  deviceId: string
  metric: string
  live: readonly LivePoint[]
}

const HISTORY_MINUTES = 5

/** Live points win; history only fills the time before the first live point so the chart isn't empty on select. */
function merge(history: LivePoint[], live: readonly LivePoint[]): uPlot.AlignedData {
  const cutoff = live.length > 0 ? live[0].t : Number.POSITIVE_INFINITY
  const points = [...history.filter((p) => p.t < cutoff), ...live]
  return [points.map((p) => p.t), points.map((p) => p.v)]
}

export function LiveChart({ deviceId, metric, live }: Props) {
  const host = useRef<HTMLDivElement>(null)
  const plot = useRef<uPlot | null>(null)

  const { data: history = [] } = useQuery({
    queryKey: ['series', deviceId, metric],
    queryFn: async () => (await fetchSeries(deviceId, metric, HISTORY_MINUTES)).map((p) => ({ t: Date.parse(p.timestamp) / 1000, v: p.avg })),
    staleTime: Number.POSITIVE_INFINITY,
  })

  useEffect(() => {
    if (!host.current) return
    const style = getComputedStyle(host.current)
    const accent = style.getPropertyValue('--primary').trim() || '#2f6fed'
    const grid = style.getPropertyValue('--border').trim() || '#8884'
    const text = style.getPropertyValue('--muted-foreground').trim() || '#888'

    const axis = { stroke: text, grid: { stroke: grid, width: 1 }, ticks: { stroke: grid, width: 1 } }
    plot.current = new uPlot(
      {
        width: host.current.clientWidth,
        height: 320,
        legend: { show: false },
        cursor: { drag: { x: false, y: false } },
        scales: { x: { time: true } },
        axes: [axis, axis],
        series: [{}, { stroke: accent, width: 2, points: { show: false } }],
      },
      [[], []],
      host.current,
    )

    const observer = new ResizeObserver(() => {
      if (host.current && plot.current) plot.current.setSize({ width: host.current.clientWidth, height: 320 })
    })
    observer.observe(host.current)

    return () => {
      observer.disconnect()
      plot.current?.destroy()
      plot.current = null
    }
  }, [deviceId, metric])

  useEffect(() => {
    plot.current?.setData(merge(history, live))
  }, [history, live])

  return <div ref={host} className="chart w-full" role="img" aria-label={`${deviceId} ${metric}`} />
}
