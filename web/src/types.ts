export interface Reading {
  deviceId: string
  metric: string
  /** The sensor that took it. Absent for a device with one sensor per metric: then the channel is named after the metric. */
  channel?: string | null
  value: number
  timestamp: string
}

export interface Alert {
  deviceId: string
  metric: string
  channel?: string | null
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

/** The channel a reading, alert or series is about. A channel nobody named is the one named after the metric. */
export const channelOf = (x: { metric: string; channel?: string | null }) => x.channel || x.metric

export const seriesKey = (deviceId: string, channel: string) => `${deviceId}|${channel}`

/** A series as the API lists it. `value` is null once its readings have aged out; `silent` means no data for longer than the timeout. */
export interface DeviceStatus {
  deviceId: string
  metric: string
  channel?: string | null
  value: number | null
  timestamp: string | null
  lastSeenAt: string
  silent: boolean
}
