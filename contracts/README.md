# Contracts

Pulse implements and is tested against the contracts that describe how it fits into the wider system. They are
owned by the `greenhaus` repository (`contracts/`). The copies here exist so this repository builds, tests and runs
on its own. They are verbatim, never edited here: a change belongs in `greenhaus` first and is then copied over.

| File | Source in `greenhaus` | Copied from commit |
| --- | --- | --- |
| `integration-events.yaml` | `contracts/asyncapi/integration-events.yaml` | `12671fb` (2026-10-22), `orderId` of `device.claimed.v1` became optional (greenhaus#8) |
| `pulse-device-api.yaml` | `contracts/openapi/pulse-device-api.yaml` | `22dd015` (2026-10-26), with the notes on who sends commands and what the refusals are (greenhaus#36) |
| `device-mqtt.yaml` | `contracts/asyncapi/device-mqtt.yaml` | `22dd015` (2026-10-26), the topics devices speak on |

`dotnet test` validates the payload and headers of the events Pulse publishes, and the one it consumes, against `integration-events.yaml`, the commands Pulse sends to devices against `device-mqtt.yaml`, and real responses against `pulse-device-api.yaml` (`tests/Pulse.Tests/OpenApiContract.cs`):
the status code must be one the contract documents and the body must match its schema. A test also lists the
operations that are implemented and the ones that are not yet, so the gap is explicit and shrinks as work lands.
