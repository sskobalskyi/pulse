-- Integration events: the transactional outbox, the id that ties an alert's raised and resolved rows together, and the
-- connection status a device was last reported in. Transactional: nothing here touches the continuous aggregate.
BEGIN;

-- Events are written here in the same transaction as the change they describe, then published to RabbitMQ by a background
-- service. Publishing after the commit would lose an event if the process died in between; publishing before would announce
-- something that then fails to commit. The outbox makes the two one decision.
CREATE TABLE outbox (
    id           bigserial   PRIMARY KEY,
    message_id   uuid        NOT NULL UNIQUE,
    routing_key  text        NOT NULL,
    payload      jsonb       NOT NULL,
    -- The trace the change belonged to, so a consumer's span continues it.
    traceparent  text,
    occurred_at  timestamptz NOT NULL,
    published_at timestamptz,
    attempts     integer     NOT NULL DEFAULT 0
);

-- The publisher only ever looks for what has not gone out yet.
CREATE INDEX ix_outbox_unpublished ON outbox (id) WHERE published_at IS NULL;

-- An alert has an id, and its resolved row carries the id of the raised row it ends. Rows from before this existed get an
-- id each, so an old resolved row is not tied to its raised one.
ALTER TABLE alerts ADD COLUMN alert_id uuid NOT NULL DEFAULT gen_random_uuid();

-- The last connection status Pulse reported for a device, so a restart does not report it again and a change is reported once.
ALTER TABLE devices ADD COLUMN reported_status text NOT NULL DEFAULT 'unknown'
    CHECK (reported_status IN ('unknown', 'online', 'offline'));

COMMIT;
