# 0004. A series is a device and a channel, not a device and a metric

Status: accepted

## Context

Until now a series was identified by device and metric, so a device could have one temperature, one humidity and so
on. A pot controller with two soil moisture sensors (one per zone) needs two series of the same metric. Alerts,
no-data detection and charts have to tell them apart, and a device that is not shown with the sensor that is silent
is not much use. The Pulse device contract already says it: a series is keyed by device, metric and channel.

## Decision

- Every reading, last-seen row and alert carries a **channel**. The readings table is unique on
  `(device_id, channel, ts)`, since the channel determines the metric. The 1-minute aggregate groups by channel.
- **A reading with no channel is on the channel named after its metric.** Existing senders keep working unchanged and
  existing rows are migrated that way (`channel = metric`). Nothing in the wire format is required to change.
- A **registered** device declared its channels, so its readings must fit them. `ChannelResolver` is a pure function: a
  reading with no channel gets the only channel of its metric, an ambiguous or unknown one is refused with a code, and
  one bad reading refuses the batch. A device nobody registered is not checked, which is what keeps the demo running.
- Readings of cataloged metrics are checked against a plausible range (`MetricCatalog`). Metrics outside the catalog
  are not judged, so nothing that worked yesterday stops working.
- `AlertKey` and `SilentSeries` carry the channel, and alert state is kept per channel. Secondary constructors default
  the channel to the metric, so code that never heard of channels is unchanged.

## The migration

`0003_channels.sql` has to drop and recreate the continuous aggregate, because its grouping cannot be altered, so it cannot
be a transaction. Every step is written to be safe to run twice, and the script is split so that
`refresh_continuous_aggregate`, which TimescaleDB refuses to run together with other statements, is its own command
(see ADR 0002). It back-fills `channel = metric` with plain `UPDATE`s and rebuilds the aggregate for the retention
window. On a database with 62 000 readings this took a few seconds. On a large one the `UPDATE` of the readings is the
part to watch, and the retention policy (7 days) is what keeps it bounded.

## Alternatives considered

- **Keep (device, metric) and add a zone label.** Smaller change, but then two zones are one series with a label nobody
  keys on, and every query would have to remember to filter by it.
- **Make the channel required.** Cleaner, and it would break every existing sender at once. Defaulting it costs one
  rule that is written down here.
- **Store the metric only on the channel (registry) and drop it from readings.** Saves a column but makes every read a
  join and breaks devices that were never registered. The column is cheap.

## Consequences

- The dashboard still draws one button per metric, so two channels of one metric on one device look like one until
  the dashboard learns about channels. The API and storage keep them apart already.
- Calibration is stored per channel but not applied to readings yet. A sensor has to send values in the metric's unit.
- The ingestion path does two more indexed queries per batch (retired devices, declared channels). The k6 smoke test
  measured about 2 ms more at the 95th percentile. If that grows, the declared channels are the thing to cache.
