import { accessToken } from './auth'
import type { Alert, DeviceStatus, SeriesPoint } from './types'

export const API_URL = import.meta.env.VITE_API_URL ?? 'http://localhost:5080'

async function get<T>(path: string): Promise<T> {
  const token = await accessToken()
  const response = await fetch(`${API_URL}${path}`, token ? { headers: { Authorization: `Bearer ${token}` } } : undefined)
  if (!response.ok) throw new Error(`${path} failed with ${response.status}`)
  return response.json() as Promise<T>
}

export const fetchDevices = () => get<DeviceStatus[]>('/api/devices')

export const fetchAlerts = (limit = 50) => get<Alert[]>(`/api/alerts?limit=${limit}`)

// The endpoint's `metric` parameter is the channel id. A channel nobody named is the one named after its metric,
// which is why this also worked before channels existed.
export const fetchSeries = (deviceId: string, channel: string, minutes: number) =>
  get<SeriesPoint[]>(`/api/devices/${encodeURIComponent(deviceId)}/series?metric=${encodeURIComponent(channel)}&minutes=${minutes}`)
