export interface Reading {
  deviceId: string
  metric: string
  value: number
  timestamp: string
}

export interface Alert {
  deviceId: string
  metric: string
  rule: string
  state: 'Raised' | 'Resolved'
  kind: 'Threshold' | 'NoData'
  value: number
  timestamp: string
}

export interface SeriesPoint {
  timestamp: string
  avg: number
  min: number
  max: number
}

export type ConnectionStatus = 'connecting' | 'live' | 'reconnecting' | 'offline'

export const seriesKey = (deviceId: string, metric: string) => `${deviceId}|${metric}`

/** A series as the API lists it. `value` is null once its readings have aged out; `silent` means no data for longer than the timeout. */
export interface DeviceStatus {
  deviceId: string
  metric: string
  value: number | null
  timestamp: string | null
  lastSeenAt: string
  silent: boolean
}
