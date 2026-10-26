# 0008. A bridge between Pulse and the devices over MQTT

Status: accepted

## Context

Devices are small controllers that sit next to a plant. They are behind a home router, they come and go, and they run
their own safety checks. The contract for how they talk to the platform is `contracts/device-mqtt.yaml`: each device
publishes under `greenhaus/v1/devices/{deviceId}/` (a manifest, a reading per channel, its state, its status) and receives
commands on `.../commands`, which it answers on `.../commands/ack`. The broker is EMQX, and it publishes a device's last
will as `offline` when the connection is lost without a goodbye. [ADR 0007](0007-command-queue.md) built the command queue
with a seam for delivery. This is what plugs into it.

## Decision

- **One bridge, in the API process.** `MqttBridge` is a hosted service holding one MQTT 5 client. It listens, hands what it
  hears to `DeviceMessageHandler`, and is the `ICommandSender` that publishes commands. It reconnects by itself, and commands
  stay queued while it cannot. It is off unless `Mqtt:Enabled` is set, like events, so Pulse still runs alone.
- **The handler is separate from the connection.** `DeviceMessageHandler` takes a topic and a payload. Everything that
  decides what a message means lives there and in `DeviceTopics` and `DeviceMessages` (pure code in `Pulse.Core`), so it is
  tested with a topic and some bytes, and the whole path is tested with a real broker.
- **Readings go through the same door as HTTP ones.** A reading on `telemetry/{channelId}` becomes a `TelemetryReading` whose
  metric is the one the device declared for that channel, and goes through `IngestService`, which the HTTP endpoint now uses
  too: plausible range, not retired, fits the declared channels, then Kafka. There is one set of rules, so a device cannot get
  past a check by choosing MQTT. A reading for an unknown device or channel is dropped.
- **Drop, count, carry on.** A payload that is not what the contract describes, a reading for a channel that does not exist, an
  answer to a command that was never sent: each is counted in `pulse.mqtt.messages` with the reason and dropped. A faulty device
  must not be able to hurt the bridge, and there is nobody to send an error to. An exception (the database is away) is logged
  and the message is lost. That is acceptable for a reading among many, and for an answer to a command it ends in `failed` with
  `no_result`, never in a wrong `completed`.
- **Answers are the other half of the queue.** An ack goes to `CommandStore.ApplyAckAsync`, which already ignores repeats and
  things that are older than what is known, so the broker delivering at least once is harmless. A device can say `accepted` or an
  ending; `queued` and `sent` are Pulse's alone.
- **The broker enforces expiry too.** A command is published with MQTT 5 message expiry equal to the time it has left, so a
  device that comes back after the command ran out of time is not handed it. The device checks `expiresAt` as well, and Pulse
  ends the command as `expired`.
- **Status comes from the device, once it says anything.** A device that publishes its status is online or offline as it says,
  and the last will is how Pulse learns the device is gone, which is the `last_will` cause of `device.status.v1`. From the first
  status message on, a device is no longer judged by its silence (`devices.connection_source`): a device that is connected but
  whose sensor stopped is online and has a no-data alert, which are two different facts. A device that never publishes a status keeps
  the telemetry-based behaviour, so the standalone demo and the simulator are unchanged.
- **Several instances share a subscription.** The bridge subscribes with `$share/pulse/...`, so the broker gives each message to
  one instance. Setting `Mqtt:ClientId` gives the bridge a session the broker keeps while it is away, so answers sent meanwhile are
  waiting when it is back. Without it the bridge starts clean.
- **The manifest is read for the firmware only.** What the device can do is what it was registered with, from the allocation. A
  device that declares something else is not believed.

- **Pulse answers the broker's questions.** EMQX's HTTP authentication and authorization call `POST /mqtt/auth` and `/mqtt/acl`, which only the
  broker may call (a shared secret). Pulse is the one that holds each device's `credentialHash`, so it verifies the password the device
  presents (PBKDF2-HMAC-SHA256, the format the allocation writes, checked in constant time, a few at a time so a flood of attempts cannot
  starve the API) and knows whether the device is retired. An unknown device costs the same as a wrong password. A device may publish
  under its own id (but not on `commands`) and subscribe to its own `commands`, and nothing else, which is what keeps one device from
  hearing or faking another. The bridge's account is a superuser, so the bridge is not asked topic by topic. The other way, the broker
  reading Pulse's database, would have tied the broker to Pulse's schema.

## Consequences

- **A device that is retired stays connected** until it next connects, when it is refused. Kicking it at once needs the broker's management API.
- **Nothing throttles guessing** beyond the broker's own protection and the slowness of the hash. The credential is 32 random bytes, so
  guessing it is not a realistic attack, but the endpoint would be the place to add a limit.

- **Pulse trusts the topic, so the broker must not.** The device id in the topic is taken as who is talking. Keeping that true is the
  broker's job, and the broker asks Pulse (see above). Without `docker-compose.mqtt-auth.yml` the local broker accepts any client.
- **Actuator state is not stored.** A device publishes `state` (kill switch, actuators), and Pulse does not keep it yet: the
  answer to a command says what a sender needs. It will be stored when something reads it.
- **Readings can be lost** when the database is away at the wrong moment. HTTP ingestion has a retry in the caller, MQTT
  readings are QoS 0 or 1 from a device that sends the next one shortly. A reading is not a command.
- **Messages are handled one at a time per connection.** Plenty for a home's worth of devices; if one bridge ever has to take a
  fleet, the shared subscription is how to add instances.
