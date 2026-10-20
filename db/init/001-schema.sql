CREATE EXTENSION IF NOT EXISTS timescaledb;

CREATE TABLE readings (
    device_id text             NOT NULL,
    metric    text             NOT NULL,
    value     double precision NOT NULL,
    ts        timestamptz      NOT NULL
);

SELECT create_hypertable('readings', 'ts', chunk_time_interval => INTERVAL '1 day');
CREATE INDEX ix_readings_device_metric_ts ON readings (device_id, metric, ts DESC);

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
    ts        timestamptz      NOT NULL
);
CREATE INDEX ix_alerts_ts ON alerts (ts DESC);
