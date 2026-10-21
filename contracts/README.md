# Contracts

Pulse implements and is tested against the contracts that describe how it fits into the wider system. They are
owned by the `greenhaus` repository (`contracts/`). The copies here exist so this repository builds, tests and runs
on its own. They are verbatim, never edited here: a change belongs in `greenhaus` first and is then copied over.

| File | Source in `greenhaus` | Copied from commit |
| --- | --- | --- |
| `pulse-device-api.yaml` | `contracts/openapi/pulse-device-api.yaml` | `4853219` (2026-10-21) |

`dotnet test` validates real responses against `pulse-device-api.yaml` (`tests/Pulse.Tests/OpenApiContract.cs`):
the status code must be one the contract documents and the body must match its schema. A test also lists the
operations that are implemented and the ones that are not yet, so the gap is explicit and shrinks as work lands.
