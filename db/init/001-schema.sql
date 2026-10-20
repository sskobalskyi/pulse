CREATE EXTENSION IF NOT EXISTS timescaledb;

CREATE TABLE readings (
    device_id text             NOT NULL,
    metric    text             NOT NULL,
    value     double precision NOT NULL,
    ts        timestamptz      NOT NULL
);

SELECT create_hypertable('readings', 'ts', chunk_time_interval => INTERVAL '1 day');
-- A reading is identified by device, metric and timestamp. The unique index makes redelivered Kafka
-- batches harmless (INSERT ... ON CONFLICT DO NOTHING) and still serves per-series range queries.
-- Hypertable unique indexes must include the partitioning column (ts).
CREATE UNIQUE INDEX ux_readings_identity ON readings (device_id, metric, ts DESC);

-- Raw readings are only needed for a short window; aggregates live longer.
SELECT add_retention_policy('readings', INTERVAL '7 days');

CREATE MATERIALIZED VIEW readings_1m
WITH (timescaledb.continuous) AS
SELECT device_id,
       metric,
       time_bucket(INTERVAL '1 minute', ts) AS bucket,
       avg(value) AS avg_value,
       min(value) AS min_value,
       max(value) AS max_value,
       count(*)   AS samples
FROM readings
GROUP BY device_id, metric, bucket
WITH NO DATA;

SELECT add_continuous_aggregate_policy('readings_1m',
    start_offset      => INTERVAL '1 hour',
    end_offset        => INTERVAL '1 minute',
    schedule_interval => INTERVAL '1 minute');

CREATE TABLE alerts (
    id        bigserial PRIMARY KEY,
    device_id text             NOT NULL,
    metric    text             NOT NULL,
    rule      text             NOT NULL,
    value     double precision NOT NULL,
    ts        timestamptz      NOT NULL,
    -- 'Raised' when the rule was breached, 'Resolved' when it cleared past the hysteresis margin.
    state     text             NOT NULL CHECK (state IN ('Raised', 'Resolved')),
    -- 'Threshold': value is the reading. 'NoData': value is how many seconds the series has been silent.
    kind      text             NOT NULL DEFAULT 'Threshold' CHECK (kind IN ('Threshold', 'NoData'))
);
CREATE INDEX ix_alerts_ts ON alerts (ts DESC);
-- The same state change reprocessed after a redelivery must not create a second row.
CREATE UNIQUE INDEX ux_alerts_identity ON alerts (device_id, metric, rule, ts, state);
-- Finds the latest state of every (device, metric, rule) when the evaluator restarts.
CREATE INDEX ix_alerts_key_ts ON alerts (device_id, metric, rule, ts DESC);

-- When each series last reported, by server receive time rather than device time, so a device with a wrong
-- clock cannot hide its silence. One row per (device, metric), upserted by the writer for every batch.
-- Kept in the database, not in memory, so it stays correct when partitions move between API instances.
CREATE TABLE device_last_seen (
    device_id    text        NOT NULL,
    metric       text        NOT NULL,
    last_seen_at timestamptz NOT NULL,
    PRIMARY KEY (device_id, metric)
);
CREATE INDEX ix_device_last_seen_at ON device_last_seen (last_seen_at);
