import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { LiveChart } from './components/LiveChart'
import { LOCALES } from './i18n'
import { fetchDevices } from './api'
import { channelOf, seriesKey, type Alert, type ConnectionStatus } from './types'
import { useLive } from './useLive'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Separator } from '@/components/ui/separator'
import { cn } from '@/lib/utils'

const STATUS_POLL_MS = 5000

const STATUS_DOT: Record<ConnectionStatus, string> = {
  connecting: 'bg-amber-500',
  live: 'bg-emerald-500',
  reconnecting: 'bg-amber-500',
  offline: 'bg-muted-foreground',
}

interface Row {
  deviceId: string
  channel: string
  metric: string
  value: number | null
  silent: boolean
}

interface Props {
  /** Who is signed in, when sign-in is on. */
  userName?: string
  onSignOut?: () => void
}

export default function Dashboard({ userName, onSignOut }: Props) {
  const { t, i18n } = useTranslation()
  const { status, latest, alerts, buffers } = useLive()
  const [selected, setSelected] = useState<{ deviceId: string; channel: string; metric: string } | null>(null)
  const [filter, setFilter] = useState('')

  // The API knows every series that ever reported and whether it went quiet; SignalR keeps the values fresh.
  const { data: statuses } = useQuery({ queryKey: ['devices'], queryFn: fetchDevices, refetchInterval: STATUS_POLL_MS })

  const devices = useMemo(() => {
    const rows = new Map<string, Row>()
    for (const d of statuses ?? []) rows.set(seriesKey(d.deviceId, channelOf(d)), { ...d, channel: channelOf(d) })
    for (const [key, reading] of latest) {
      rows.set(key, { deviceId: reading.deviceId, channel: channelOf(reading), metric: reading.metric, value: reading.value, silent: rows.get(key)?.silent ?? false })
    }

    const byDevice = new Map<string, Row[]>()
    for (const row of rows.values()) {
      if (filter && !row.deviceId.includes(filter.toLowerCase())) continue
      byDevice.set(row.deviceId, [...(byDevice.get(row.deviceId) ?? []), row])
    }
    return [...byDevice.entries()].sort(([a], [b]) => a.localeCompare(b))
  }, [statuses, latest, filter])

  const metricLabel = (metric: string) => t(`metrics.${metric}`, { defaultValue: metric })
  // A device with one sensor per metric is labelled by the metric. Several sensors of one kind need their own names.
  const channelLabel = (channel: string, metric: string) =>
    channel === metric ? metricLabel(metric) : `${channel} · ${metricLabel(metric)}`
  const language = LOCALES.find((l) => i18n.resolvedLanguage === l) ?? 'en'

  // A threshold alert carries the reading; a no-data alert carries how long the device was silent.
  const alertDetail = (a: Alert) => {
    if (a.kind === 'Threshold') return t('alerts.value', { value: a.value })
    return a.state === 'Raised' ? t('alerts.noData.silent', { seconds: Math.round(a.value) }) : t('alerts.noData.resumed')
  }

  return (
    <div className="mx-auto flex max-w-[1400px] flex-col gap-4 p-4">
      <header className="flex items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">{t('app.title')}</h1>
          <p className="text-sm text-muted-foreground">{t('app.subtitle')}</p>
        </div>
        <div className="flex items-center gap-3">
          <Badge variant="outline" role="status" className="gap-1.5">
            <span className={cn('size-2 rounded-full', STATUS_DOT[status])} aria-hidden="true" />
            {t(`status.${status}`)}
          </Badge>
          {userName && (
            <span className="text-sm text-muted-foreground">{t('auth.signedInAs', { name: userName })}</span>
          )}
          <Select value={language} onValueChange={(l) => void i18n.changeLanguage(l as string)}>
            <SelectTrigger size="sm" aria-label={t('language')}>
              <SelectValue>{language.toUpperCase()}</SelectValue>
            </SelectTrigger>
            <SelectContent>
              {LOCALES.map((l) => (
                <SelectItem key={l} value={l}>
                  {l.toUpperCase()}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          {onSignOut && (
            <Button variant="outline" size="sm" onClick={onSignOut}>
              {t('auth.signOut')}
            </Button>
          )}
        </div>
      </header>

      <main className="grid items-start gap-4 lg:grid-cols-[320px_1fr_320px]">
        <Card aria-labelledby="devices-title">
          <CardHeader>
            <h2 id="devices-title" className="font-heading text-base font-medium">
              {t('devices.title')}
            </h2>
          </CardHeader>
          <CardContent className="flex max-h-[80vh] flex-col gap-3 overflow-y-auto">
            <Input
              type="search"
              placeholder={t('devices.filter')}
              aria-label={t('devices.filter')}
              value={filter}
              onChange={(e) => setFilter(e.target.value)}
            />
            {devices.length === 0 ? (
              <p className="text-sm text-muted-foreground">{t('devices.empty')}</p>
            ) : (
              <ul className="flex flex-col gap-3">
                {devices.map(([deviceId, readings], n) => (
                  <li key={deviceId} className="flex flex-col gap-2">
                    {n > 0 && <Separator />}
                    <strong className="text-sm">{deviceId}</strong>
                    <div className="flex flex-wrap gap-2">
                      {readings
                        .slice()
                        .sort((a, b) => a.channel.localeCompare(b.channel))
                        .map((r) => {
                          const isSelected = selected?.deviceId === deviceId && selected.channel === r.channel
                          return (
                            <Button
                              key={r.channel}
                              variant={isSelected ? 'default' : 'outline'}
                              className={cn('h-auto flex-col items-start gap-0.5 px-3 py-2', r.silent && 'opacity-60')}
                              aria-pressed={isSelected}
                              onClick={() => setSelected({ deviceId, channel: r.channel, metric: r.metric })}
                            >
                              <span className="text-xs font-normal opacity-70">{channelLabel(r.channel, r.metric)}</span>
                              <span className="text-lg font-semibold tabular-nums">{r.value === null ? '—' : r.value.toFixed(1)}</span>
                              {r.silent && <span className="text-xs font-normal text-amber-600 dark:text-amber-400">{t('devices.silent')}</span>}
                            </Button>
                          )
                        })}
                    </div>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            {selected && (
              <h2 className="font-heading text-base font-medium">
                {t('chart.title', { device: selected.deviceId, metric: channelLabel(selected.channel, selected.metric) })}
              </h2>
            )}
          </CardHeader>
          <CardContent>
            {selected ? (
              <LiveChart
                deviceId={selected.deviceId}
                channel={selected.channel}
                live={buffers.get(seriesKey(selected.deviceId, selected.channel)) ?? []}
              />
            ) : (
              <p className="text-sm text-muted-foreground">{t('chart.select')}</p>
            )}
          </CardContent>
        </Card>

        <Card aria-labelledby="alerts-title">
          <CardHeader>
            <h2 id="alerts-title" className="font-heading text-base font-medium">
              {t('alerts.title')}
            </h2>
          </CardHeader>
          <CardContent className="max-h-[80vh] overflow-y-auto">
            {alerts.length === 0 ? (
              <p className="text-sm text-muted-foreground">{t('alerts.empty')}</p>
            ) : (
              <ul className="flex flex-col gap-3">
                {alerts.map((a, i) => (
                  <li key={`${a.deviceId}-${channelOf(a)}-${a.metric}-${a.timestamp}-${i}`} className="flex flex-col gap-1 text-sm">
                    <div className="flex items-center justify-between gap-2">
                      <span>
                        <strong>{a.deviceId}</strong> · {channelLabel(channelOf(a), a.metric)}
                      </span>
                      <Badge variant={a.state === 'Raised' ? 'destructive' : 'secondary'}>{t(`alerts.state.${a.state}`)}</Badge>
                    </div>
                    <span className="text-muted-foreground">
                      {alertDetail(a)} · {new Date(a.timestamp).toLocaleTimeString(i18n.language)}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>
      </main>
    </div>
  )
}
