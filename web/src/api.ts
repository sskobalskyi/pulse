import type { Alert, Reading, SeriesPoint } from './types'

export const API_URL = import.meta.env.VITE_API_URL ?? 'http://localhost:5080'

async function get<T>(path: string): Promise<T> {
  const response = await fetch(`${API_URL}${path}`)
  if (!response.ok) throw new Error(`${path} failed with ${response.status}`)
  return response.json() as Promise<T>
}

export const fetchDevices = () => get<Reading[]>('/api/devices')

export const fetchAlerts = (limit = 50) => get<Alert[]>(`/api/alerts?limit=${limit}`)

export const fetchSeries = (deviceId: string, metric: string, minutes: number) =>
  get<SeriesPoint[]>(`/api/devices/${encodeURIComponent(deviceId)}/series?metric=${encodeURIComponent(metric)}&minutes=${minutes}`)
