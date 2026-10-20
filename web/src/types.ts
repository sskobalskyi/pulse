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
