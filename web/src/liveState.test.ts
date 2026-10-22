import { describe, expect, it } from 'vitest'
import { appendPoint, isNewer, mergeAlerts, type LivePoint } from './liveState'
import type { Alert, Reading } from './types'

const reading = (timestamp: string, value = 20): Reading => ({ deviceId: 'dev-1', metric: 'temperature', value, timestamp })

const alert = (overrides: Partial<Alert> = {}): Alert => ({
  deviceId: 'dev-1',
  metric: 'temperature',
  rule: 'temperature outside [-10, 80]',
  state: 'Raised',
  kind: 'Threshold',
  value: 95,
  timestamp: '2026-10-20T12:00:00Z',
  ...overrides,
})

describe('appendPoint', () => {
  it('adds points in increasing time order', () => {
    const buffer: LivePoint[] = []

    expect(appendPoint(buffer, { t: 1, v: 10 }, 5)).toBe(true)
    expect(appendPoint(buffer, { t: 2, v: 11 }, 5)).toBe(true)

    expect(buffer.map((p) => p.t)).toEqual([1, 2])
  })

  it('ignores a repeated point', () => {
    const buffer: LivePoint[] = [{ t: 1, v: 10 }]

    expect(appendPoint(buffer, { t: 1, v: 10 }, 5)).toBe(false)

    expect(buffer).toHaveLength(1)
  })

  it('ignores a point older than the last one, so the series stays sorted', () => {
    const buffer: LivePoint[] = [{ t: 1, v: 10 }, { t: 5, v: 14 }]

    expect(appendPoint(buffer, { t: 3, v: 12 }, 5)).toBe(false)

    expect(buffer.map((p) => p.t)).toEqual([1, 5])
  })

  it('drops the oldest point once the buffer is full', () => {
    const buffer: LivePoint[] = []
    for (let t = 1; t <= 4; t++) appendPoint(buffer, { t, v: t }, 3)

    expect(buffer.map((p) => p.t)).toEqual([2, 3, 4])
  })

  it('does not count a rejected point against the capacity', () => {
    const buffer: LivePoint[] = [{ t: 1, v: 1 }, { t: 2, v: 2 }]

    appendPoint(buffer, { t: 2, v: 2 }, 2)

    expect(buffer.map((p) => p.t)).toEqual([1, 2])
  })

  it('treats a whole redelivered batch as already seen', () => {
    const buffer: LivePoint[] = []
    const batch = [1, 2, 3].map((t) => ({ t, v: t }))

    for (const p of batch) appendPoint(buffer, p, 10)
    for (const p of batch) appendPoint(buffer, p, 10)

    expect(buffer.map((p) => p.t)).toEqual([1, 2, 3])
  })
})

describe('isNewer', () => {
  it('accepts the first reading', () => {
    expect(isNewer(reading('2026-10-20T12:00:00Z'), undefined)).toBe(true)
  })

  it('accepts a later reading and rejects an equal or earlier one', () => {
    const current = reading('2026-10-20T12:00:05Z')

    expect(isNewer(reading('2026-10-20T12:00:06Z'), current)).toBe(true)
    expect(isNewer(reading('2026-10-20T12:00:05Z'), current)).toBe(false)
    expect(isNewer(reading('2026-10-20T12:00:04Z'), current)).toBe(false)
  })

  it('compares instants, not text, so offsets and fractions do not matter', () => {
    const current = reading('2026-10-20T12:00:00.500+00:00')

    expect(isNewer(reading('2026-10-20T15:00:00.400+03:00'), current)).toBe(false)
    expect(isNewer(reading('2026-10-20T15:00:00.600+03:00'), current)).toBe(true)
  })
})

describe('mergeAlerts', () => {
  it('puts a pushed batch at the top, newest first', () => {
    const older = alert({ deviceId: 'dev-a', timestamp: '2026-10-20T12:00:00Z' })
    const a = alert({ deviceId: 'dev-b', timestamp: '2026-10-20T12:01:00Z' })
    const b = alert({ deviceId: 'dev-c', timestamp: '2026-10-20T12:02:00Z' })

    const feed = mergeAlerts([older], [a, b], 10) // a batch arrives oldest first

    expect(feed.map((x) => x.deviceId)).toEqual(['dev-c', 'dev-b', 'dev-a'])
  })

  it('skips an alert the feed already has', () => {
    const existing = [alert()]

    expect(mergeAlerts(existing, [alert()], 10)).toHaveLength(1)
  })

  it('skips repeats inside one batch', () => {
    expect(mergeAlerts([], [alert(), alert()], 10)).toHaveLength(1)
  })

  it('keeps a resolved alert next to its raised one: the state is part of the identity', () => {
    const feed = mergeAlerts([alert({ state: 'Raised' })], [alert({ state: 'Resolved' })], 10)

    expect(feed.map((a) => a.state)).toEqual(['Resolved', 'Raised'])
  })

  it('keeps a later breach of the same rule: the timestamp is part of the identity', () => {
    const feed = mergeAlerts([alert()], [alert({ timestamp: '2026-10-20T12:30:00Z' })], 10)

    expect(feed).toHaveLength(2)
  })

  it('does not mutate its inputs', () => {
    const existing = Object.freeze([alert({ deviceId: 'dev-a', timestamp: '2026-10-20T11:00:00Z' })])
    const pushed = Object.freeze([alert()])

    expect(() => mergeAlerts(existing, pushed, 10)).not.toThrow()
  })

  it('caps the feed length, dropping the oldest', () => {
    const existing = ['1', '2', '3'].map((n) => alert({ deviceId: `old-${n}`, timestamp: `2026-10-20T11:0${n}:00Z` }))

    const feed = mergeAlerts(existing, [alert()], 3)

    expect(feed).toHaveLength(3)
    expect(feed[0].deviceId).toBe('dev-1')
    expect(feed.at(-1)?.deviceId).toBe('old-2')
  })
})
