-- Device registry: identity, lifecycle, ownership, channels and capabilities.
-- Wrapped in one transaction so a failure leaves nothing half applied (0001 cannot be, it creates a continuous aggregate).
BEGIN;

CREATE TABLE devices (
    id                     text        PRIMARY KEY,
    model                  text        NOT NULL,
    state                  text        NOT NULL CHECK (state IN ('allocated', 'shipped', 'claimed', 'active', 'retired')),
    owner_id               text,
    firmware               text,
    -- Salted hash of the device's broker password. The plaintext never reaches Pulse.
    credential_hash        text        NOT NULL,
    -- SHA-256 of the one-time claim token. Cleared once the device is claimed.
    claim_token_hash       text,
    claim_token_expires_at timestamptz,
    -- Declared by the device: [{ id, kind, hardLimits, thermalCutoff }]
    actuators              jsonb       NOT NULL DEFAULT '[]'::jsonb,
    registered_at          timestamptz NOT NULL,
    shipped_at             timestamptz,
    claimed_at             timestamptz,
    activated_at           timestamptz,
    retired_at             timestamptz,
    -- A claimed or active device always has an owner.
    CONSTRAINT ck_devices_owner CHECK (state NOT IN ('claimed', 'active') OR owner_id IS NOT NULL)
);

CREATE INDEX ix_devices_owner ON devices (owner_id) WHERE owner_id IS NOT NULL;
CREATE INDEX ix_devices_state ON devices (state);

-- Sensor channels. A device can expose several channels of one metric, for example one soil moisture channel per zone.
CREATE TABLE device_channels (
    device_id     text             NOT NULL REFERENCES devices (id) ON DELETE CASCADE,
    channel_id    text             NOT NULL,
    position      integer          NOT NULL,
    metric        text             NOT NULL,
    label         text,
    unit          text,
    dry_raw       double precision,
    wet_raw       double precision,
    calibrated_at timestamptz,
    PRIMARY KEY (device_id, channel_id)
);

COMMIT;
