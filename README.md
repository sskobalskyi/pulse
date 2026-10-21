# Pulse

[![CI](https://github.com/sskobalskyi/pulse/actions/workflows/ci.yml/badge.svg)](https://github.com/sskobalskyi/pulse/actions/workflows/ci.yml)

Real-time device telemetry platform. A simulator emits readings, an ingestion API publishes them to Kafka, independent stream processors persist them to TimescaleDB, evaluate alert rules and push live updates to a React dashboard over SignalR.

![Pulse dashboard](web/e2e/screenshots/dashboard-en.png)

## What it demonstrates

- **Event streaming:** Kafka (Redpanda) topic keyed by device id, so one device always maps to one partition and keeps its ordering.
- **Consumer groups:** three independent groups (`pulse-writer`, `pulse-live`, `pulse-alerts`) read the same topic. Offsets are committed after a batch is handled (at-least-once delivery), batches are written with one idempotent `INSERT ... ON CONFLICT DO NOTHING`.
- **Alerting with hysteresis:** threshold rules have a margin. A breach raises one alert, and a second "resolved" alert is recorded only when the value comes back past the margin, so a value hovering around a threshold does not flap. State is rebuilt from the database on restart.
- **No-data alerts:** a sensor that stops reporting raises a "no data for 60s" alert and resolves it when data resumes. A silent device sends nothing, so no Kafka consumer can notice it. The writer upserts `device_last_seen` (by server receive time) and a `SilenceMonitor` polls it on a timer. The device list is built from the same table, so a silent series stays on the dashboard, flagged "no data" with its last known value, instead of disappearing after 10 minutes. With several API instances only the one holding a Postgres advisory lock evaluates in each round.
- **Time-series modelling:** TimescaleDB hypertable, a 1-minute continuous aggregate, a 7-day retention policy on raw data. Short chart windows read raw rows, longer ones read the aggregate.
- **Real-time push:** SignalR hub with a throttled client store (state lives in refs and is published to React at most every 250 ms).
- **Observability:** OpenTelemetry traces across HTTP and Kafka (context in message headers, span links for batches), metrics including per-partition consumer lag, and correlated logs. See [Observability](#observability).
- **Device registry:** devices are registered before they send data and move through `allocated`, `shipped`, `claimed`, `active`, `retired`. Claiming uses a one-time, expiring token that is stored only as a hash and is rate limited. Retiring a device stops its no-data alerts. See [Device registry and authentication](#device-registry-and-authentication).
- **Authentication:** Keycloak JWTs behind a switch. Service callers manage the registry, users see only the devices they own.
- **Schema migrations:** plain SQL files applied by the API on startup, with an existing database adopted rather than rebuilt ([ADR 0002](docs/adr/0002-sql-migrations-on-startup.md)).
- **Contract tests:** responses are validated against `contracts/pulse-device-api.yaml`, the OpenAPI document that the rest of the system is built against.
- **i18n:** `en` (default) and `uk`, react-i18next with browser language detection.
- **Tests:** xUnit for the alert rules and tracker, Testcontainers tests for the database layer and for the whole backend pipeline (HTTP ingest, Kafka, stream processors, TimescaleDB and SignalR in one process against real containers), and Playwright E2E for the dashboard in both locales.

## Architecture

```mermaid
flowchart LR
    SIM[Simulator<br/>20 devices x 3 metrics] -->|POST /api/telemetry| API[Ingestion API<br/>ASP.NET Core]
    API -->|key = deviceId| K[(Kafka topic<br/>pulse.readings<br/>3 partitions)]
    K --> W[pulse-writer<br/>group]
    K --> L[pulse-live<br/>group]
    K --> A[pulse-alerts<br/>group]
    W -->|COPY| TS[(TimescaleDB<br/>readings, readings_1m)]
    A -->|breaches| TS
    A -->|alerts| HUB
    L --> HUB[SignalR hub<br/>/hubs/live]
    HUB --> WEB[React dashboard<br/>uPlot]
    WEB -->|history| API
    API --> TS
```

The processors run as `BackgroundService`s inside the API process to keep the demo small. Because each is its own consumer group, any of them can be moved into a separate service without touching the others (see [ADR 0001](docs/adr/0001-kafka-consumer-groups-in-process.md)).

## Run it

Requirements: Docker. Everything runs locally.

```bash
docker compose up --build
```

- Dashboard: http://localhost:8080
- API: http://localhost:5080 (`GET /health`, `GET /api/devices` (every series that ever reported, with its latest value and a `silent` flag), `GET /api/alerts`, `GET /api/devices/{id}/series?metric=temperature&minutes=5`, `POST /api/telemetry`)
- Device registry: `http://localhost:5080/api/pulse/v2/devices` and below, see [below](#device-registry-and-authentication)

### Development

```bash
docker compose up -d redpanda timescaledb     # infrastructure only
dotnet run --project src/Pulse.Api --urls http://localhost:5080
dotnet run --project src/Pulse.Simulator
cd web && npm install && npm run dev          # http://localhost:5173
```

The schema script only runs on an empty database volume. After pulling schema changes, reset it with `docker compose down -v`.

### Tests

```bash
dotnet test                                    # unit, database and pipeline tests (the last two need Docker)
cd web && npx playwright install chromium     # once
cd web && npx playwright test                  # E2E, needs the stack running (PULSE_WEB_URL overrides the URL)
docker compose --profile loadtest run --rm k6  # load smoke test, needs the stack running
```

### Load smoke test

[`k6/smoke.js`](k6/smoke.js) runs for 30 seconds against the running stack: 20 requests per second of 50 readings each (1,000 readings per second) while a second scenario reads `/api/devices` and a series like a dashboard would. It is a regression gate that fits in CI, not a benchmark. The run fails (exit code 99) unless:

- fewer than 1% of requests fail, and 99% of checks pass
- p95 of ingestion is under 500 ms, and p95 of the read endpoints under 1 s
- no iteration is dropped, meaning the arrival rate was actually reached
- **every test device shows up in `/api/devices` after the run**. The API answers 202 as soon as Kafka has the message, so a broken consumer or database would not show in request latency. With TimescaleDB stopped the ingestion checks still pass and only this one fails (0 of 20 devices delivered).

Typical local result: ingest p95 about 12 ms, devices p95 about 40 ms, 20 of 20 devices delivered. The rates can be changed per run, for example `docker compose --profile loadtest run --rm -e INGEST_RATE=100 -e DURATION=60s k6`. Use `--no-deps` when the stack is already up, otherwise compose starts its dependencies too.

## Device registry and authentication

The registry follows [`contracts/pulse-device-api.yaml`](contracts/pulse-device-api.yaml) and lives under `/api/pulse/v2`.
Pulse stays generic: it knows devices, channels and owners, not what they are used for.

```mermaid
stateDiagram-v2
    [*] --> allocated: POST /devices
    allocated --> shipped: POST /devices/{id}/ship
    shipped --> claimed: POST /devices/{id}/claim (token)
    claimed --> active: first reading
    allocated --> retired: POST /devices/{id}/retire
    shipped --> retired
    claimed --> retired
    active --> retired
    retired --> [*]
```

| Operation | Who | Notes |
| --- | --- | --- |
| `POST /devices` | service | Registers a device with its capability manifest. Repeating it changes nothing and returns 200. |
| `POST /devices/{id}/ship` | service | Makes it claimable. Repeating it is accepted. |
| `POST /devices/{id}/claim` | service | Binds it to `ownerId`. Needs the one-time token, which works once and expires. At most 5 attempts a minute per device and 30 per caller, then 429 with `Retry-After`. |
| `POST /devices/{id}/retire` | service | Stops its no-data alerts, erases an unused token, refuses its telemetry (409). |
| `GET /devices`, `GET /devices/{id}` | user or service | A user sees only their own devices. A device that is not theirs answers 404. |
| `PUT /devices/{id}/channels/{channelId}/calibration` | user or service | Stores the dry and wet raw values of a channel. |

Errors are `application/problem+json` with a stable `code` (`invalid_state`, `token_invalid`, `token_expired`, `rate_limited`,
`validation_failed`, ...) to switch on. Rules, commands and series from the same contract are not implemented yet.

### Running with authentication

Authentication is off by default, so the demo needs nothing else. To turn it on with a local Keycloak:

```bash
docker compose -f docker-compose.yml -f docker-compose.auth.yml up --build
```

Keycloak is on http://localhost:8180 (admin / admin) with a `pulse` realm: users `alice` and `bob` (password equal to the name),
and a service client `pulse-service` (secret `pulse-dev-secret`) whose role `pulse-service` makes it a service caller.

```bash
KC=http://localhost:8180/realms/pulse/protocol/openid-connect/token
SERVICE=$(curl -s $KC -d grant_type=client_credentials -d client_id=pulse-service -d client_secret=pulse-dev-secret | jq -r .access_token)
ALICE=$(curl -s $KC -d grant_type=password -d client_id=pulse-web -d username=alice -d password=alice | jq -r .access_token)

curl -s localhost:5080/api/pulse/v2/devices -H "Authorization: Bearer $SERVICE"   # every device
curl -s localhost:5080/api/pulse/v2/devices -H "Authorization: Bearer $ALICE"     # only alice's
```

The simulator gets its own service token. The dashboard has no login yet, so with authentication on it shows nothing.

## Observability

The API and the simulator are instrumented with OpenTelemetry and export over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. To look at it, start the stack with the extra compose file, which adds the .NET Aspire Dashboard:

```bash
docker compose -f docker-compose.yml -f docker-compose.observability.yml up --build
```

Dashboard: http://localhost:18888 (no login, local use only, telemetry is kept in memory).

![Traces in the Aspire Dashboard](docs/images/aspire-traces.png)

**Traces.** Every simulator tick is a trace that continues into the API (`pulse.readings publish`). The trace context travels in the Kafka message headers (`traceparent`). A consumer batch mixes messages from many requests, so each batch is its own trace (`pulse-writer process`, `pulse-live process`, `pulse-alerts process`) with span links back to the producer spans (capped at 10 per batch). Npgsql adds the database spans underneath.

![A consumer batch trace](docs/images/aspire-trace-writer.png)

**Metrics** (meter `Pulse`, plus ASP.NET Core, Kestrel, Npgsql and .NET runtime):

| Metric | Meaning |
| --- | --- |
| `pulse.readings.ingested` | readings accepted and published to Kafka |
| `pulse.ingest.rejected` | rejected batches, tagged by reason |
| `pulse.consumer.batch.size` / `.duration` | per consumer group, duration also by outcome |
| `pulse.consumer.failures` | batches that failed and will be redelivered |
| `pulse.consumer.lag` | messages behind the end of each partition, per group and partition |
| `pulse.alerts` | alert state changes, by state and metric |
| `pulse.signalr.connections` | browsers connected to the live hub |
| `pulse.simulator.readings.sent` / `.send.failures` | simulator side |

**Logs** go through the same exporter, so each log line carries the trace id of the span it was written in.

The Aspire Dashboard is a viewer for local development. A full Grafana, Prometheus and Tempo stack is out of scope here.

## Configuration

| Setting | Default | Notes |
| --- | --- | --- |
| `Kafka:BootstrapServers` | `localhost:19092` | `redpanda:9092` inside compose |
| `ConnectionStrings:Timescale` | `localhost:5433` | |
| `Alerts:Rules` | temperature `[-10, 80]` ±2, humidity `[5, 95]` ±3, battery `>= 10` ±5 | threshold rules with a hysteresis margin, see `AlertRulesOptions` |
| `Simulator:Devices` / `IntervalMs` | 20 / 1000 | |
| `Alerts:NoData:Timeout` / `CheckInterval` / `Enabled` | `00:01:00` / `00:00:10` / `true` | how long a series may stay silent, how often the monitor looks (detection is late by up to this much), and an off switch |
| `Simulator:AnomalyChance` / `AnomalyMinTicks` / `AnomalyMaxTicks` | 0.002 / 5 / 15 | chance per reading that a device starts a fault, and how many readings the fault lasts |
| `Simulator:DropoutChance` / `DropoutMinTicks` / `DropoutMaxTicks` | 0.0002 / 90 / 150 | chance per reading that a sensor goes quiet, and for how many ticks (longer than the no-data timeout, so it is noticed) |
| `Auth:Enabled` | `false` | turns JWT authentication on. `Auth:Authority` is then required. |
| `Auth:Authority` / `Issuer` / `Audience` | unset | the identity provider. `Issuer` is for when tokens carry a different address than the one used for discovery (Docker). An empty `Audience` skips the check. |
| `Auth:ServiceRole` / `RequireHttpsMetadata` | `pulse-service` / `true` | the role that makes a caller a service. Keep the second one true outside local use. |
| `Registry:Claim:PerDevicePerMinute` / `PerCallerPerMinute` | 5 / 30 | claim attempts allowed, counted in this instance |
| `Simulator:Auth:TokenUrl` / `ClientId` / `ClientSecret` | unset | when set, the simulator authenticates with client credentials |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | unset | when set, traces, metrics and logs are exported over OTLP |

## Trade-offs

- Processors share the API process. Simple to run, but a slow consumer competes with request handling for CPU. Splitting them out is a hosting change, not a design change.
- Alert state (raised or not) per device, metric and rule is kept in memory by one `AlertTracker` per instance. A batch only advances it after its alerts are stored, so a failed write is evaluated again on redelivery. On startup the state is rebuilt from the latest alert of each key in the database. With several API instances, partitions moving between them can still repeat a "raised" alert once, because each instance only knows what it saw or what was stored when it started. A threshold alert that is raised when a device goes silent stays raised until the device reports again, and a "no data" alert is raised next to it.
- The no-data monitor runs in every API instance, but a Postgres advisory lock lets only one of them evaluate in a given round, and the leader reads what is already raised from the database each time, so a change of leader loses nothing. The cost is one extra connection held briefly per check. If the leader dies mid-round the lock goes with its session and another instance takes over on its next tick. The threshold alert tracker is still per instance (see the first bullet).
- Silence is tracked per (device, metric). A device that is taken out of service is retired through the registry, which stops its no-data alerts. A device that was never registered and simply stops sending still stays silent until someone retires it, which means registering it first.
- Delivery is at-least-once, so a batch can be redelivered. Writes are idempotent: a reading is identified by `(device_id, metric, ts)` and alerts by `(device_id, metric, rule, ts, state)`, both enforced by unique indexes and `ON CONFLICT DO NOTHING`. The catch is that two genuinely different values for the same device, metric and timestamp collapse into the first one. Live SignalR pushes are not deduplicated, so a redelivery can briefly repeat a point in an open browser.
- With authentication on, the older endpoints (`/api/devices`, `/api/alerts`, series) and the live SignalR hub need a caller but are **not yet limited to the caller's own devices**: a user could see other users' data there. Only the registry is scoped so far. Scoping them is the next change, so do not share an instance with several users until then.
- Claim attempt counters are in memory of one instance, so with several instances the real limit is multiplied by their number.
- Telemetry for a device that was never registered is still accepted, so the standalone demo works as before. The registry does not gate ingestion, except that a retired device is refused.

## Layout

```
src/Pulse.Api         ingestion, stream processors, SignalR hub, query endpoints, device registry, authentication
src/Pulse.Core        shared contracts and alert rules
src/Pulse.Simulator   device simulator
tests/Pulse.Tests     unit tests
web/                  React + TypeScript dashboard, Playwright E2E
db/migrations         numbered SQL migrations, applied on startup
contracts/            the OpenAPI contract this API implements (copied from the greenhaus repo)
auth/                 Keycloak realm for the authentication override
docs/adr              architecture decision records
```
