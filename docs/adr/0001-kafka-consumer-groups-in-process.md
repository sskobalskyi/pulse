# 0001. Stream processors as Kafka consumer groups inside the API process

Status: accepted

## Context

Every reading has to be stored, evaluated against alert rules and pushed to browsers. These three jobs have different failure modes and speeds: a slow database must not delay live updates, and a rule bug must not block storage.

## Decision

Publish each reading once to a Kafka topic (`pulse.readings`, keyed by device id) and give each job its own consumer group: `pulse-writer`, `pulse-live`, `pulse-alerts`. Each runs as a `BackgroundService` derived from one `ReadingsConsumer` base class, inside the API process.

The base class reads in small batches (up to 500 messages or 250 ms), calls the handler, then commits offsets. A failing batch is not committed and is redelivered.

## Consequences

- The three jobs are isolated: each tracks its own offsets and lags independently.
- Per-device ordering holds because the message key is the device id.
- Delivery is at-least-once. Writes are not yet idempotent, see the trade-offs in the README.
- Scaling out is adding API instances: Kafka splits partitions between instances of the same group. Moving a processor to its own service needs only a new host project that references the same base class.
- Running in-process means processors and HTTP handling share resources. Acceptable for a demo, and the reason this decision is recorded.
