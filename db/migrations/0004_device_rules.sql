-- Alert rules of one channel of one device. A channel without a rule of its own uses the platform's rules for its metric.
-- Transactional: nothing here touches the continuous aggregate.
BEGIN;

CREATE TABLE device_rules (
    device_id       text             NOT NULL,
    channel_id      text             NOT NULL,
    kind            text             NOT NULL CHECK (kind IN ('threshold', 'no_data')),
    min_value       double precision,
    max_value       double precision,
    margin          double precision CHECK (margin IS NULL OR margin >= 0),
    timeout_seconds integer,
    updated_at      timestamptz      NOT NULL,
    PRIMARY KEY (device_id, channel_id, kind),
    -- A rule can only be about a channel the device declared, and goes when the device or the channel does.
    FOREIGN KEY (device_id, channel_id) REFERENCES device_channels (device_id, channel_id) ON DELETE CASCADE,
    -- The shape of each kind, so a rule that makes no sense cannot be stored whatever wrote it.
    CONSTRAINT ck_device_rules_shape CHECK (
        (kind = 'threshold' AND (min_value IS NOT NULL OR max_value IS NOT NULL) AND timeout_seconds IS NULL)
        OR
        (kind = 'no_data' AND timeout_seconds IS NOT NULL AND timeout_seconds >= 10
            AND min_value IS NULL AND max_value IS NULL AND margin IS NULL))
);

COMMIT;
