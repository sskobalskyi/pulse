import { useEffect, useRef, useState } from 'react'
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { API_URL, fetchAlerts, fetchDevices } from './api'
import { seriesKey, type Alert, type ConnectionStatus, type Reading } from './types'

const BUFFER_SIZE = 300
const MAX_ALERTS = 50
const RENDER_INTERVAL_MS = 250

export interface LivePoint {
  t: number
  v: number
}

export interface LiveSnapshot {
  status: ConnectionStatus
  latest: ReadonlyMap<string, Reading>
  alerts: readonly Alert[]
  buffers: ReadonlyMap<string, readonly LivePoint[]>
}

/**
 * Subscribes to the SignalR hub and keeps the latest value, a rolling buffer per series and the alert feed.
 * Readings arrive in batches several times a second, so state is mutated in refs and published to React
 * at most every RENDER_INTERVAL_MS instead of re-rendering per message.
 */
export function useLive(): LiveSnapshot {
  const latest = useRef(new Map<string, Reading>())
  const buffers = useRef(new Map<string, LivePoint[]>())
  const alerts = useRef<Alert[]>([])
  const status = useRef<ConnectionStatus>('connecting')
  const dirty = useRef(false)

  const [snapshot, setSnapshot] = useState<LiveSnapshot>({
    status: 'connecting',
    latest: new Map(),
    alerts: [],
    buffers: new Map(),
  })

  useEffect(() => {
    let disposed = false

    const connection = new HubConnectionBuilder()
      .withUrl(`${API_URL}/hubs/live`)
      .withAutomaticReconnect([0, 1000, 3000, 5000, 10000])
      .configureLogging(LogLevel.Warning)
      .build()

    const setStatus = (next: ConnectionStatus) => {
      status.current = next
      dirty.current = true
    }

    connection.on('readings', (batch: Reading[]) => {
      for (const reading of batch) {
        const key = seriesKey(reading.deviceId, reading.metric)
        latest.current.set(key, reading)

        const buffer = buffers.current.get(key) ?? []
        buffer.push({ t: Date.parse(reading.timestamp) / 1000, v: reading.value })
        if (buffer.length > BUFFER_SIZE) buffer.shift()
        buffers.current.set(key, buffer)
      }
      dirty.current = true
    })

    connection.on('alerts', (batch: Alert[]) => {
      alerts.current = [...batch.slice().reverse(), ...alerts.current].slice(0, MAX_ALERTS)
      dirty.current = true
    })

    connection.onreconnecting(() => setStatus('reconnecting'))
    connection.onreconnected(() => setStatus('live'))
    connection.onclose(() => setStatus('offline'))

    const start = async () => {
      try {
        await connection.start()
        if (disposed) return
        setStatus('live')
      } catch {
        if (!disposed) setStatus('offline')
        return
      }

      // Seed with what the backend already has so the page isn't empty before the next batch arrives.
      const [devices, recent] = await Promise.all([fetchDevices().catch(() => []), fetchAlerts().catch(() => [])])
      if (disposed) return
      for (const reading of devices) {
        const key = seriesKey(reading.deviceId, reading.metric)
        if (!latest.current.has(key)) latest.current.set(key, reading)
      }
      if (alerts.current.length === 0) alerts.current = recent
      dirty.current = true
    }
    void start()

    const timer = window.setInterval(() => {
      if (!dirty.current) return
      dirty.current = false
      setSnapshot({
        status: status.current,
        latest: new Map(latest.current),
        alerts: alerts.current,
        buffers: new Map(buffers.current),
      })
    }, RENDER_INTERVAL_MS)

    return () => {
      disposed = true
      window.clearInterval(timer)
      if (connection.state !== HubConnectionState.Disconnected) void connection.stop()
    }
  }, [])

  return snapshot
}
