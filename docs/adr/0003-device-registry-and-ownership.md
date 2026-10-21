# 0003. Device registry, claim tokens and authentication

Status: accepted

## Context

Pulse used to learn about a device when its first reading arrived, had no notion of who owned it, and had no way to
take a device out of service: a retired device stayed "silent" for ever. A product built on Pulse needs to know a
device before it sends data, hand it to one owner, and let it go. Pulse has to stay generic: it knows about devices,
channels and owners, not about what they are used for. The HTTP surface is defined by `contracts/pulse-device-api.yaml`.

## Decision

**Lifecycle.** A device moves one way: `allocated`, `shipped`, `claimed`, `active`, `retired`. The rules are in
`DeviceLifecycle` and every change is one conditional statement or one transaction, so racing callers cannot both win
(eight concurrent claims of one device give exactly one winner, which a test checks).
Retiring is allowed from any state and is repeatable; shipping is repeatable so a retried call from an at-least-once
event handler is harmless. The first reading of a claimed device makes it `active`.

**Claim tokens.** The token is long and random and printed on the device. Only its SHA-256 is stored, and the row is
locked while it is compared in constant time. A token works once (the hash is erased on claim), expires, and is erased
when the device is retired. Wrong tokens and expired ones are told apart, but only after the token matched, so an
"expired" answer is never given to a guesser. A plain hash is enough because the token has plenty of entropy. A slow
password hash would only protect guessable secrets. Attempts are rate limited per device and per caller.

**Retiring.** Retiring forgets the device's last-seen rows, so the no-data monitor resolves any open no-data alert and
stops watching it. The writer never creates last-seen rows for a retired device, so readings still queued in Kafka
cannot revive it. Ingestion refuses data from a retired device with 409.

**Authentication.** JWT bearer tokens from Keycloak, switched by `Auth:Enabled` (off by default so the standalone demo
needs no identity provider). A *service* caller (a client-credentials token with the `pulse-service` role) registers,
ships, claims and retires devices and sees all of them. A *user* sees only devices whose `ownerId` is their subject, and
a device that is not theirs answers 404 rather than 403, so its existence is not revealed. The settings are read when
used, not at startup, which is what lets tests and compose switch them.

**Time.** Everything about "recently" (last seen, online, silent) uses the database clock, so the three can never
disagree. Application time is used only for lifecycle timestamps and token expiry, through `TimeProvider` so tests can
control it.

## Consequences

- The dashboard endpoints (`/api/devices`, `/api/alerts`, series) and the live SignalR hub follow the same rule as the
  registry. A user connection joins a group named after its subject, a service connection joins the service group, and
  `LiveFanout` splits every push of readings or alerts by the owner of the device it is about, looked up per batch so a
  claim takes effect on the very next push. A device nobody has claimed reaches service callers only. With
  authentication off, a push goes to everybody as before.
- Claim attempt counters live in memory of one instance. With several instances the real limit is multiplied.
- Credential hashes are stored and never interpreted. What verifies them (the MQTT broker) arrives later and decides
  the format.
- A registered device whose metric is outside the platform's metric list is rejected, but telemetry for unregistered
  devices, including the demo's `humidity` and `battery`, is still accepted: the registry does not gate ingestion.
