import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { LiveChart } from './components/LiveChart'
import { LOCALES } from './i18n'
import { fetchDevices } from './api'
import { seriesKey, type Alert } from './types'
import { useLive } from './useLive'
import './App.css'

const STATUS_POLL_MS = 5000

interface Row {
  deviceId: string
  metric: string
  value: number | null
  silent: boolean
}

export default function App() {
  const { t, i18n } = useTranslation()
  const { status, latest, alerts, buffers } = useLive()
  const [selected, setSelected] = useState<{ deviceId: string; metric: string } | null>(null)
  const [filter, setFilter] = useState('')

  // The API knows every series that ever reported and whether it went quiet; SignalR keeps the values fresh.
  const { data: statuses } = useQuery({ queryKey: ['devices'], queryFn: fetchDevices, refetchInterval: STATUS_POLL_MS })

  const devices = useMemo(() => {
    const rows = new Map<string, Row>()
    for (const d of statuses ?? []) rows.set(seriesKey(d.deviceId, d.metric), { ...d })
    for (const [key, reading] of latest) {
      rows.set(key, { deviceId: reading.deviceId, metric: reading.metric, value: reading.value, silent: rows.get(key)?.silent ?? false })
    }

    const byDevice = new Map<string, Row[]>()
    for (const row of rows.values()) {
      if (filter && !row.deviceId.includes(filter.toLowerCase())) continue
      byDevice.set(row.deviceId, [...(byDevice.get(row.deviceId) ?? []), row])
    }
    return [...byDevice.entries()].sort(([a], [b]) => a.localeCompare(b))
  }, [statuses, latest, filter])

  const metricLabel = (metric: string) => t(`metrics.${metric}`, { defaultValue: metric })
  const language = LOCALES.find((l) => i18n.resolvedLanguage === l) ?? 'en'

  // A threshold alert carries the reading; a no-data alert carries how long the device was silent.
  const alertDetail = (a: Alert) => {
    if (a.kind === 'Threshold') return t('alerts.value', { value: a.value })
    return a.state === 'Raised' ? t('alerts.noData.silent', { seconds: Math.round(a.value) }) : t('alerts.noData.resumed')
  }

  return (
    <div className="app">
      <header>
        <div>
          <h1>{t('app.title')}</h1>
          <p className="muted">{t('app.subtitle')}</p>
        </div>
        <div className="header-controls">
          <span className={`status status-${status}`} role="status">
            <span className="dot" aria-hidden="true" />
            {t(`status.${status}`)}
          </span>
          <label className="language">
            <span className="sr-only">{t('language')}</span>
            <select value={language} onChange={(e) => void i18n.changeLanguage(e.target.value)}>
              {LOCALES.map((l) => (
                <option key={l} value={l}>
                  {l.toUpperCase()}
                </option>
              ))}
            </select>
          </label>
        </div>
      </header>

      <main>
        <section className="panel devices" aria-labelledby="devices-title">
          <h2 id="devices-title">{t('devices.title')}</h2>
          <input
            type="search"
            placeholder={t('devices.filter')}
            aria-label={t('devices.filter')}
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
          />
          {devices.length === 0 ? (
            <p className="muted">{t('devices.empty')}</p>
          ) : (
            <ul>
              {devices.map(([deviceId, readings]) => (
                <li key={deviceId}>
                  <strong>{deviceId}</strong>
                  <div className="metrics">
                    {readings
                      .slice()
                      .sort((a, b) => a.metric.localeCompare(b.metric))
                      .map((r) => {
                        const isSelected = selected?.deviceId === deviceId && selected.metric === r.metric
                        return (
                          <button
                            key={r.metric}
                            className={`metric${isSelected ? ' selected' : ''}${r.silent ? ' silent' : ''}`}
                            aria-pressed={isSelected}
                            onClick={() => setSelected({ deviceId, metric: r.metric })}
                          >
                            <span className="muted">{metricLabel(r.metric)}</span>
                            <span className="value">{r.value === null ? '—' : r.value.toFixed(1)}</span>
                            {r.silent && <span className="silent-label">{t('devices.silent')}</span>}
                          </button>
                        )
                      })}
                  </div>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section className="panel chart-panel">
          {selected ? (
            <>
              <h2>{t('chart.title', { device: selected.deviceId, metric: metricLabel(selected.metric) })}</h2>
              <LiveChart
                deviceId={selected.deviceId}
                metric={selected.metric}
                live={buffers.get(seriesKey(selected.deviceId, selected.metric)) ?? []}
              />
            </>
          ) : (
            <p className="muted">{t('chart.select')}</p>
          )}
        </section>

        <section className="panel alerts" aria-labelledby="alerts-title">
          <h2 id="alerts-title">{t('alerts.title')}</h2>
          {alerts.length === 0 ? (
            <p className="muted">{t('alerts.empty')}</p>
          ) : (
            <ul>
              {alerts.map((a, i) => (
                <li key={`${a.deviceId}-${a.metric}-${a.timestamp}-${i}`}>
                  <strong>{a.deviceId}</strong> · {metricLabel(a.metric)}{' '}
                  <span className={`badge badge-${a.state.toLowerCase()}`}>{t(`alerts.state.${a.state}`)}</span>
                  <div className="muted">
                    {alertDetail(a)} ·{new Date(a.timestamp).toLocaleTimeString(i18n.language)}
                  </div>
                </li>
              ))}
            </ul>
          )}
        </section>
      </main>
    </div>
  )
}
