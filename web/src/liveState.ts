import { channelOf, type Alert, type Reading } from './types'

export interface LivePoint {
  t: number
  v: number
}

/**
 * Kafka delivers at least once, so after a rebalance or a failed commit the same readings can be pushed to the
 * browser again. The server cannot know what a given browser already has, so the client drops repeats itself.
 * A chart also needs strictly increasing x values, which an old or repeated point would break.
 */

/** Appends the point unless it is not newer than the last one. Returns whether it was added. */
export function appendPoint(buffer: LivePoint[], point: LivePoint, max: number): boolean {
  const last = buffer.at(-1)
  if (last && point.t <= last.t) return false

  buffer.push(point)
  if (buffer.length > max) buffer.shift()
  return true
}

/** A redelivered or late reading must not replace a newer value. */
export function isNewer(incoming: Reading, current: Reading | undefined): boolean {
  return !current || Date.parse(incoming.timestamp) > Date.parse(current.timestamp)
}

const alertId = (a: Alert) => `${a.deviceId}|${channelOf(a)}|${a.metric}|${a.rule}|${a.state}|${a.timestamp}`

/**
 * Puts a pushed batch at the top of the feed, newest first, skipping alerts the feed already has.
 * The same state change is identical on redelivery, so device, channel, metric, rule, state and timestamp identify it.
 */
export function mergeAlerts(existing: readonly Alert[], pushed: readonly Alert[], max: number): Alert[] {
  const seen = new Set(existing.map(alertId))
  const fresh: Alert[] = []

  for (const alert of pushed.toReversed()) {
    const id = alertId(alert)
    if (seen.has(id)) continue
    seen.add(id)
    fresh.push(alert)
  }

  return [...fresh, ...existing].slice(0, max)
}
