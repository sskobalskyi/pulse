// Load smoke test for the ingestion path.
//
//   docker compose --profile loadtest run --rm k6
//
// It sends readings at a fixed rate while a second scenario reads like a dashboard would, then checks that the
// readings really came out the other end (Kafka, writer, TimescaleDB) and not just that the API said 202.
// Rates are modest on purpose: this is a regression gate that fits in CI, not a benchmark.
import http from 'k6/http'
import { check, sleep } from 'k6'
import { Rate } from 'k6/metrics'

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5080'
const INGEST_RATE = Number(__ENV.INGEST_RATE || 20) // requests per second
const BATCH_SIZE = Number(__ENV.BATCH_SIZE || 50) // readings per request
const DURATION = __ENV.DURATION || '30s'
const DEVICES = 20

// Share of the test devices that appeared in the API after the run. Must be 100%.
const pipelineDelivery = new Rate('pipeline_delivery')

export const options = {
  scenarios: {
    ingest: {
      executor: 'constant-arrival-rate',
      rate: INGEST_RATE,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: 20,
      maxVUs: 100,
      exec: 'ingest',
    },
    dashboard: {
      executor: 'constant-arrival-rate',
      rate: 2,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: 2,
      maxVUs: 10,
      exec: 'dashboard',
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    checks: ['rate>0.99'],
    'http_req_duration{endpoint:ingest}': ['p(95)<500'],
    'http_req_duration{endpoint:devices}': ['p(95)<1000'],
    'http_req_duration{endpoint:series}': ['p(95)<1000'],
    pipeline_delivery: ['rate==1'],
    // k6 has no built-in "iterations dropped" gate: a dropped iteration means the target rate was not reached.
    dropped_iterations: ['count==0'],
  },
}

export function setup() {
  const health = http.get(`${BASE_URL}/health`)
  if (health.status !== 200) throw new Error(`API at ${BASE_URL} is not healthy (HTTP ${health.status})`)

  // A fresh prefix per run, so devices from earlier runs cannot make the final check pass.
  return { run: Date.now().toString(36) }
}

const device = (run, n) => `k6-${run}-${String(n).padStart(2, '0')}`

export function ingest(data) {
  const now = new Date().toISOString()
  const deviceId = device(data.run, (__VU * 7 + __ITER) % DEVICES)

  // Values stay well inside every alert rule, so the run does not depend on, or disturb, alerting.
  const batch = Array.from({ length: BATCH_SIZE }, (_, i) => ({
    deviceId,
    metric: 'temperature',
    value: 20 + Math.random() * 5,
    timestamp: new Date(Date.parse(now) + i).toISOString(), // distinct timestamps: readings are unique per (device, metric, ts)
  }))

  const response = http.post(`${BASE_URL}/api/telemetry`, JSON.stringify(batch), {
    headers: { 'Content-Type': 'application/json' },
    tags: { endpoint: 'ingest' },
  })
  check(response, { 'ingest accepted (202)': (r) => r.status === 202 })
}

export function dashboard(data) {
  const devices = http.get(`${BASE_URL}/api/devices`, { tags: { endpoint: 'devices' } })
  check(devices, { 'devices ok (200)': (r) => r.status === 200 })

  const series = http.get(`${BASE_URL}/api/devices/${device(data.run, 0)}/series?metric=temperature&minutes=5`, {
    tags: { endpoint: 'series' },
  })
  check(series, { 'series ok (200)': (r) => r.status === 200 })
}

export function teardown(data) {
  // The consumers are asynchronous: give the writer a moment to drain, then look at what actually arrived.
  sleep(8)

  const response = http.get(`${BASE_URL}/api/devices`, { tags: { endpoint: 'devices' } })
  // If the API cannot answer, nothing counts as delivered and the gate fails, instead of the script crashing.
  const seen = new Set(
    response.status === 200
      ? response.json().filter((d) => d.deviceId.startsWith(`k6-${data.run}-`)).map((d) => d.deviceId)
      : [],
  )

  for (let n = 0; n < DEVICES; n++) pipelineDelivery.add(seen.has(device(data.run, n)))
  console.log(`delivered devices: ${seen.size}/${DEVICES}`)
}
