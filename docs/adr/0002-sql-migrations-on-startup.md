# 0002. Plain SQL migrations, applied by the API on startup

Status: accepted

## Context

Until now the schema was one script mounted into the database container's init folder. It only runs when the
database volume is empty, so every schema change meant `docker compose down -v` and losing the data. The device
registry adds tables and later changes will add more, so the schema needs to evolve in place. The tests also need the
same schema as the real stack, not a copy that can drift.

## Decision

- Schema changes are numbered SQL files in `db/migrations` (`0001_initial.sql`, `0002_device_registry.sql`, ...).
- They are embedded in `Pulse.Api` and applied by `SchemaMigrator` when the API starts, before any consumer or
  monitor. The ones that have run are recorded in `schema_migrations`.
- A Postgres advisory lock serialises several instances starting at once, so each script runs exactly once.
- Scripts run as written, **without** a wrapping transaction. TimescaleDB statements such as a continuous aggregate
  cannot run inside one. A script that does not need that wraps itself in `BEGIN` and `COMMIT`, as 0002 does.
- A line that reads `-- split` ends one command and starts the next. A multi-statement command goes to the database in a
  single round trip, and TimescaleDB refuses some calls inside it (`refresh_continuous_aggregate` is one), so a script
  that needs them marks where the commands break. Most scripts never use it.
- A database created by the old init script is **adopted**: `0001_initial` is recorded as applied instead of run
  again, and only the newer scripts run. A database older than that is refused with the instruction to recreate it.
- The tests use the same `SchemaMigrator`, so they run against exactly what the API creates.

## Alternatives considered

- **EF Core migrations, DbUp, Flyway.** All would work. The project talks to the database through Dapper and Npgsql
  and has no EF model to generate migrations from, and the scripts are mostly TimescaleDB-specific SQL. About a
  hundred lines of migrator avoids a dependency and keeps the SQL readable as written.
- **Keep the init script and recreate the database on every change.** Fine for a demo, wrong once data matters.

## Consequences

- A script must never be edited after it has been released. A change is a new file.
- A script that fails half way and is not wrapped in a transaction leaves a partial change and no journal entry. The
  next start runs it again, so such scripts should be written to be repeatable, or kept small and wrapped.
- There is no down migration. Rolling back means restoring a backup.
- The API needs permission to alter the schema. Splitting that into a separate migration job is the usual next step
  if the API's database user ever has to be restricted.
