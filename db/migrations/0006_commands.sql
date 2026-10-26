-- Commands: what Greenhaus asks a device to do (run a pump, flip the kill switch), the state each one is in, and the
-- result. Transactional: nothing here touches the continuous aggregate.
BEGIN;

CREATE TABLE commands (
    -- Chosen by the sender, which is what makes sending the same command twice harmless.
    command_id       uuid        PRIMARY KEY,
    device_id        text        NOT NULL REFERENCES devices (id),
    type             text        NOT NULL CHECK (type IN ('run_actuator', 'stop_actuator', 'set_kill_switch')),
    actuator_id      text        NOT NULL,
    duration_seconds integer     CHECK (duration_seconds IS NULL OR duration_seconds >= 1),
    enabled          boolean,
    status           text        NOT NULL DEFAULT 'queued'
                                 CHECK (status IN ('queued', 'sent', 'accepted', 'completed', 'rejected', 'failed', 'expired')),
    reason           text,
    -- After this moment the device must not execute the command, and Pulse stops trying to deliver it.
    expires_at       timestamptz NOT NULL,
    created_at       timestamptz NOT NULL,
    finished_at      timestamptz,
    -- A command that is over has a time it ended, and one that is not has none.
    CONSTRAINT commands_finished_matches_status CHECK (
        (status IN ('completed', 'rejected', 'failed', 'expired')) = (finished_at IS NOT NULL)),
    CONSTRAINT commands_shape CHECK (
        (type = 'run_actuator' AND duration_seconds IS NOT NULL)
        OR (type = 'set_kill_switch' AND enabled IS NOT NULL)
        OR type = 'stop_actuator')
);

-- "Recent commands of a device", newest first.
CREATE INDEX ix_commands_device ON commands (device_id, created_at DESC);

-- The worker only ever looks at commands that are not over: to deliver them and to let them expire.
CREATE INDEX ix_commands_open ON commands (created_at) WHERE finished_at IS NULL;

COMMIT;
