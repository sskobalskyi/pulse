-- Where a device's online/offline status comes from. Until the MQTT bridge, only telemetry could tell: a device that stops
-- sending is offline. A device that reports its own status over MQTT (online when it connects, offline as the broker's last
-- will) is the better source, and once a device has done so, silence no longer changes its status. A connected device
-- whose sensor has stopped is online and has a no-data alert, which are two different facts.
BEGIN;

ALTER TABLE devices ADD COLUMN connection_source text NOT NULL DEFAULT 'telemetry'
    CHECK (connection_source IN ('telemetry', 'mqtt'));

COMMIT;
