# 0007. A command queue between the platform and the devices

Status: accepted

## Context

Greenhaus decides what a device should do (run a pump for ten seconds, flip the kill switch) and Pulse has to get it to the
device and report how it went. The device is far away, may be offline, may deliver a message twice through the broker, and
enforces its own safety limits whatever it is told. The contract (`contracts/pulse-device-api.yaml`) defines the HTTP side:
`POST` and `GET` on `/devices/{id}/commands`, the states `queued`, `sent`, `accepted`, `completed`, `rejected`, `failed`,
`expired`, and an event `command.completed.v1`. How a command reaches the device is the MQTT bridge, which comes next.

## Decision

- **A queue in the database, not in memory.** A command is a row. Accepting it means the row is committed, so a restart loses
  nothing and an answer of 202 is a promise. The state is a column and every change of state is one transaction.
- **Idempotent on `commandId`, which the sender picks.** Sending the same command again returns what is stored, in the state
  it is in now, and changes nothing. That is how a sender retries after it lost the answer. The same id for something else
  (another device, another actuator, a different duration) is a `409` with the code `command_id_reused`, because one of the
  two senders is wrong and the safe thing is to say so instead of picking a winner.
- **The state only moves forward and an ending is final.** The device reports `accepted` and then an ending, but the broker
  delivers at least once and not in order, so `completed` can arrive before `accepted`, or twice. A report that does not move
  the command forward is ignored, and a command that is over stays as it ended. A late `completed` for a command that already
  `expired` is ignored too: Pulse told its sender it did not run, and changing that afterwards would be worse than being wrong
  about a pump that did run. The device's own record is the truth for that, and it is a rare edge.
- **The end is announced in the same transaction** (the outbox of [ADR 0006](0006-integration-events-and-outbox.md)): exactly one
  `command.completed.v1` per command, when it reaches `completed`, `rejected`, `failed` or `expired`, and none for the steps
  in between.
- **Expiry is Pulse's job, not only the device's.** A command nobody delivered or nobody accepted before `expiresAt` becomes
  `expired`, and a queued one that is already out of time is never handed over. A command the device accepted is let alone
  until it should long have finished (`expiresAt` + its run time + 60 s), and then fails with the reason `no_result`: it
  may be running, and the report may be on its way back.
- **Pluggable delivery.** `ICommandSender` is what hands a command to the device link. Without a link (`NoDeviceLink`, the
  default until the MQTT bridge exists) commands are queued and expire, which is honest: nothing can deliver them. A worker in
  every instance takes queued rows with `FOR UPDATE SKIP LOCKED`, oldest first, and stops at the first one the link refuses so
  nothing overtakes it. What the device answers goes back in through `CommandStore.ApplyAckAsync`.
- **Checked against what the device declared.** The command has to name an actuator the device has, a run may not be longer than
  that actuator's `maxRunSeconds`, and the expiry has to be in the future. The device would refuse these anyway (safety is
  enforced there, and only there), but telling the sender at once is cheaper than a round trip through a broker. Anything else
  that depends on state Pulse does not know (minimum interval, daily cap, kill switch) is the device's to refuse, and shows up as `rejected`
  with its reason.
- **Only the platform sends commands.** `POST` is for service callers. A user, even the owner, asks Greenhaus, which knows what
  their tier allows. Users can read the commands of their own devices, and a device that is not theirs is a `404`.
- **A retired device ends its commands.** Retiring fails every open command with the reason `device_retired`, in the same
  transaction, and a command cannot be queued on a retired device (`409 invalid_state`). A shared lock on the device row while
  queueing makes the two orders (queue then retire, retire then queue) both come out right, which a test hammers.

## Consequences

- Commands are accepted for a device that is offline. They wait, and either reach it when it is back or expire. The contract
  lists a `device_offline` code. Pulse does not use it yet: until the MQTT bridge reports the device's real connection (the
  last will), "offline" is guessed from telemetry and a wrong guess would refuse a good command.
- Without the bridge every command ends `expired`. That is the intended behaviour of the first step, and the end-to-end
  loop closes when the bridge lands.
- The `sent` state means "handed to the link", not "the device has it". A device that never answers leaves the command `sent`
  until it expires.
- A late result after expiry is dropped, as above.
- The worker polls (500 ms by default). A push from the database would be quicker, but a command is not latency-critical
  and polling needs nothing new.
