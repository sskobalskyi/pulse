-- A series is identified by device and channel, not by device and metric: a device can have several channels of one
-- metric, for example one soil moisture sensor per zone. Existing data keeps working: its channel is its metric.
--
-- This cannot be one transaction, because the continuous aggregate has to be dropped and created again with a new
-- grouping. So every step is written to be safe to run twice, and a start that finds this half done finishes it.

-- The aggregate groups by metric today. Drop it first so changing the table does not make it track every change.
DROP MATERIALIZED VIEW IF EXISTS readings_1m;

ALTER TABLE readings ADD COLUMN IF NOT EXISTS channel text;
UPDATE readings SET channel = metric WHERE channel IS NULL;
ALTER TABLE readings ALTER COLUMN channel SET NOT NULL;

-- A reading is identified by device, channel and timestamp, so redelivered Kafka batches stay harmless.
-- The channel determines the metric, so the metric does not need to be in the key.
CREATE UNIQUE INDEX IF NOT EXISTS ux_readings_channel_identity ON readings (device_id, channel, ts DESC);
DROP INDEX IF EXISTS ux_readings_identity;

CREATE MATERIALIZED VIEW readings_1m
WITH (timescaledb.continuous) AS
SELECT device_id,
       channel,
       metric,
       time_bucket(INTERVAL '1 minute', ts) AS bucket,
       avg(value) AS avg_value,
       min(value) AS min_value,
       max(value) AS max_value,
       count(*)   AS samples
FROM readings
GROUP BY device_id, channel, metric, bucket
WITH NO DATA;

SELECT add_continuous_aggregate_policy('readings_1m',
    start_offset      => INTERVAL '1 hour',
    end_offset        => INTERVAL '1 minute',
    schedule_interval => INTERVAL '1 minute');

-- Rebuild what the retention window still holds, so charts do not lose their history.
-- It cannot run together with other statements, hence its own command.
-- split
CALL refresh_continuous_aggregate('readings_1m', now() - INTERVAL '7 days', now());
-- split

-- When each channel last reported.
ALTER TABLE device_last_seen ADD COLUMN IF NOT EXISTS channel text;
UPDATE device_last_seen SET channel = metric WHERE channel IS NULL;
ALTER TABLE device_last_seen ALTER COLUMN channel SET NOT NULL;
ALTER TABLE device_last_seen DROP CONSTRAINT IF EXISTS device_last_seen_pkey;
ALTER TABLE device_last_seen ADD PRIMARY KEY (device_id, channel);

-- An alert is about one rule on one channel.
ALTER TABLE alerts ADD COLUMN IF NOT EXISTS channel text;
UPDATE alerts SET channel = metric WHERE channel IS NULL;
ALTER TABLE alerts ALTER COLUMN channel SET NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_alerts_channel_identity ON alerts (device_id, channel, rule, ts, state);
DROP INDEX IF EXISTS ux_alerts_identity;
CREATE INDEX IF NOT EXISTS ix_alerts_channel_key_ts ON alerts (device_id, channel, rule, ts DESC);
DROP INDEX IF EXISTS ix_alerts_key_ts;
