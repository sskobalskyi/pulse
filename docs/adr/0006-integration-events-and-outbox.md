# 0006. Integration events through a transactional outbox

Status: accepted

## Context

Other systems need to know what happens in Pulse: an alert was raised, a device went offline, a device was retired. And Pulse
needs to learn one thing from outside: that a device was packed for an order, so it can be registered. The contract
(`contracts/integration-events.yaml`) defines these as events on RabbitMQ. The hard part is not sending a message. It is
that a change to the database and the message announcing it are two different systems, and one can succeed without the other.

## Decision

- **The outbox.** An event is inserted into an `outbox` table in the same transaction as the change it describes: the alert
  rows, the retirement, the status. A relay publishes the rows afterwards and marks them. Publishing after the commit
  loses the event if the process dies in between. Publishing before it announces something that then fails to commit. With
  the outbox the change and its announcement are one decision, and the broker being away only delays the second half.
- **At least once, in order.** The relay publishes oldest first and stops at the first failure, so an event never overtakes
  an older one. It marks a row only after the broker confirmed it (publisher confirms on a persistent message). If the
  process dies between the confirm and the mark, the message is sent again with the same `messageId`. That is the
  guarantee the contract already asks consumers for: they deduplicate on `messageId`.
- **Several instances.** Every instance runs the relay. Rows are selected with `FOR UPDATE SKIP LOCKED` in a transaction that
  lasts through the publish, so an instance never publishes a row another is holding, and nothing goes out twice because of
  concurrency. A batch is at most 100 rows so a round cannot hold locks for long.
- **Announced once.** An alert that is stored again (a Kafka batch redelivered) hits the unique key, returns no row, and
  writes no event. A resolved alert copies the `alertId` of the raised alert it ends, so a consumer can tie the two.
- **Device status.** Without the broker's last will, "offline" is derived from the no-data timeout and "online" from the next
  reading. The last status reported is stored on the device, so a restart does not report it again and each change is reported
  once. A device is online while any of its channels is within its own timeout. Devices that are not in service, or never
  reported, are not announced.
- **Consuming.** `device.allocated.v1` registers the device. Registration is idempotent on the device id, which is what makes a
  duplicate harmless, so there is no inbox table. A message that can never succeed goes to a dead-letter queue instead of
  being retried for ever, and a transient failure is retried after a pause. A bad message does not block the ones behind it.
- **Off by default.** `Events:Enabled` is false, nothing is written to the outbox and nothing connects to a broker, so Pulse
  still runs on its own. The settings are read when used, like the other switches.
- **Trace context.** The trace of the change is stored with the event and sent as `traceparent`, and the consumer continues
  the trace of a message it receives.

## Alternatives considered

- **Publish straight from the handler.** The simplest, and it can lose an event or announce a change that did not happen.
  Fine for a demo, wrong for a platform other systems build on.
- **Change data capture (Debezium) on the tables.** No outbox code, but a second system to run and events that are row
  changes rather than business events in the shape the contract defines.
- **An inbox table for the consumer.** Standard practice when handling is not idempotent. Registration is, so it would be
  a table that guards nothing.
- **Publish to Kafka instead.** Pulse already has it, but the contract and the other projects use RabbitMQ.

## Consequences

- Events can arrive twice. Consumers must deduplicate on `messageId`. The contract says so.
- The outbox is a table that grows until its rows are removed. Published rows older than a week are deleted every ten
  minutes, and the number waiting is a metric (`pulse.outbox.pending`), which is what to alert on if the broker is away.
- A device's status event can be late by up to a check interval, and its "offline" by the no-data timeout. The MQTT bridge
  will add the last will, which is immediate.
- A second message for a device that is already registered is ignored, the first wins. Changing a registered device's
  capabilities is not something this event does.
