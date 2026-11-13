# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Read `AGENTS.md` first. It points to the binding rules in `AI-rules/AGENTS.md` (data ownership, Clean Architecture, outbox/consumer rules, Docker rules, done checklist), and `AI-rules/api-guidelines.md`. Docs and most comments are in Vietnamese. Reply to the user in Vietnamese.

## Commands

```powershell
dotnet build Tourbooking.sln                                    # if DLLs are locked by the IDE, add -m:1
dotnet test tests/Tourbooking.Tests/Tourbooking.Tests.csproj           # the only test project
dotnet test tests/Tourbooking.Tests/Tourbooking.Tests.csproj --filter "FullyQualifiedName~AdminNotificationFeedTests"   # one class
dotnet test tests/Tourbooking.Tests/Tourbooking.Tests.csproj --filter "FullyQualifiedName~AdminNotificationFeedTests.OnlyActionableEventsReachTheAdminBell"
```

- Tests marked `[ExternalServiceFact]` are skipped unless `TOURBOOKING_EXTERNAL_TESTS=1` is set together with `TOURBOOKING_URL`, `RABBITMQ_*`, `TEST_SQL_BOOKING`, `TEST_*_MONGO`, and so on.
- These external tests create real accounts and bookings, so run them only against an isolated stack. That means the `test` profile in `compose.hybrid.yaml`, or `tests/compose.hybrid-verification.yaml`, never `tourbooking-live`.
- The `tests/*.mjs` files are Node load and resilience scripts.

### Running the stack

The live Compose project is `tourbooking-live`, set by `HYBRID_PROJECT_NAME` in `.env`.

```powershell
.\scripts\Initialize-Environment.ps1          # only adds missing secrets to .env; never overwrites
.\scripts\Start-Hybrid.ps1 -AdminPorts        # add -Monitoring for Prometheus :9090 / Grafana :3000
docker compose -p tourbooking-live -f compose.hybrid.yaml config --quiet
.\scripts\Test-Hybrid.ps1                     # smoke checks; other Test-*.ps1 cover isolation/resilience/monitoring
.\scripts\Backup.ps1                          # SQL .bak + mongodump + key/image volumes -> backups/hybrid-<ts>
.\scripts\Restore.ps1 -BackupDirectory backups\hybrid-<ts> -Drill   # non-destructive verification
```

- The site is reachable only through Kong at http://localhost:8080. App services publish no host ports.
- `compose.admin.yaml` (loaded by `-AdminPorts`) binds these to loopback only:
  - SQL Server: `127.0.0.1,14333`
  - MongoDB: `27018`
  - RabbitMQ: `5672` / `15672`
- All SQL data lives in the SQL Server container. The Windows SQL Express setup was removed on 2026-11-13; its last `TourBookingDB` backup is in `backups/sqlexpress-final-*`.
- `Register-BackupTask.ps1` has registered the Windows scheduled task "Tourbooking Hybrid Backup", which runs `Backup.ps1` daily at 02:00 and keeps 14 days of backups.
- Rebuild one service with `docker compose -p tourbooking-live -f compose.hybrid.yaml up -d --build <svc>`. For identity, catalog, booking and customer, the image is built by the matching `<svc>-init` service, which also runs the migration.

## Architecture

Request path: Kong 3.9 (declarative `infrastructure/kong/kong.hybrid.yml`, DB-less) → either the API backends directly, or `web-router` (YARP, `services/Gateway`) → the MVC backends. Services call each other at `http://<service>:8080`, authenticated with an `X-Service-Key` header. All services sit on one Docker network. There is no HAProxy or Nginx. The project runs locally only: there is no cloud target, and Docker Swarm should not be introduced.

There are five backends. Each backend hosts several logical modules:

| Backend (folder) | Modules | Data |
|---|---|---|
| identity (`services/Identity`) | Accounts, Review | `IdentityDB` |
| catalog (`services/Catalog`) | Tours, Reservations, Reporting | `CatalogDB` |
| booking (`services/Booking`, project `BookingPlatform`) | Booking, Payment | `BookingDB` |
| customer (`services/Customer`, project `CustomerExperience`) | Support | `CustomerDB` + Mongo `ChatDB.conversations` |
| notification (`services/Notification`, minimal API) | Notifications | Mongo `NotificationDB` |

### Module layout and loading

- Each module lives at `Modules/<M>/{Domain,Application,Infrastructure,Web}`.
- The backend host project is only a composition root. It references the module `Web` projects with `IsConsolidatedHost=true` and calls `TourbookingHost.RunAsync(args, "<backend>", "<Module>", ...)` from `Shared/Hosting`.
- `Shared/Routing/ServiceBoundary.cs` is the single map of which backend owns which module. It also maps shared MVC controller actions (for example `Admin/Dashboard`) to their owner, via `ActionOwners`.
- Adding or moving a module requires changes in three places:
  1. `ServiceBoundary`
  2. the `Services__<module>` environment variable in compose
  3. the schema grants in `infrastructure/sql/provision.sql`
- MVC namespaces (`Tourbooking.Controllers` and so on) and the legacy MVC/API routes must stay unchanged.

### Shared projects

| Project | Contents |
|---|---|
| `Shared/Contracts` | DTOs and `IntegrationEvent` |
| `Shared/Hosting` | Host bootstrap and the internal reservation endpoints |
| `Shared/Messaging` | RabbitMQ topology |
| `Shared/Persistence` | The shared historical `ApplicationDbContext` and migrations, `ServiceData`, `OutboxDispatcher` |
| `Shared/Presentation` | Shared Razor views and layouts, including `_AdminNotificationBell` |
| `Shared/Routing` | `ServiceBoundary` |

`Shared/wwwroot/` holds the shared static assets: `lib/` (bootstrap and jquery), `js/site.js`, `favicon.ico`, and the seed images referenced by `TourbookingHost` demo tours, the login background and the payment QR. The root `Dockerfile` and each `services/*/Dockerfile` copy a per-service subset of these assets in the `service-assets` stage. At runtime, `/app/wwwroot/images` is the `hybrid-images` volume, which also holds admin uploads, so uploaded images are never in git.

### Data ownership

- There is one SQL Server instance with one database per backend.
- Each database has an owned schema `<svc>` and a read-only replica schema `<svc>_read`. The `_read` schema is filled over internal HTTP and is eventually consistent.
- Each runtime logs in as `<svc>_app`, which has grants only on its own schemas. The `sql-provision` job creates the databases, logins and grants.
- Never query another service's database or schema. Call its API or read your own `_read` copy instead.

### Booking flow

1. Catalog reserves seats atomically using `sp_getapplock` and an idempotent RequestId.
2. Booking stores the booking and payment in one local transaction.
3. A saga handles confirm, cancel and reschedule.
4. No SQL transaction is held open across an HTTP call.

### Events

- Business data and outbox rows are written in the same transaction.
- `OutboxDispatcher` claims a lease under a SQL lock, publishes with broker confirms, then marks the row sent.
- Delivery is at-least-once, so consumers must be idempotent. `NotificationConsumer` uses EventId as the Mongo `_id` and has retry and a DLQ.
- The admin bell polls `GET /api/notifications/admin/summary`. The feed filter is in `services/Notification/.../Application/AdminFeed.cs`.

### Notification tests

Notification also has an EF/SQLite `NotificationDb` implementation of `INotificationStore`. It is used by the tests. Keep it in sync with `MongoNotificationStore`.

## Operational rules

- **Never touch live data:** no `docker compose down -v`, no deleting the `hybrid-*` volumes, no destructive migrations. Run `scripts/Backup.ps1` before any risky data work.
- **SQLite is gone:** the old SQLite stack was removed on 2026-11-13 and its data was verified to be in SQL Server and MongoDB. Do not reintroduce it.
- **Don't print secrets:** never echo secrets from `.env`. Tell the user which key to read instead.
- **PowerShell 5.1 native arguments:** Windows PowerShell 5.1 mangles embedded `"` in native-command arguments. `scripts/BackupCommon.ps1` (`Invoke-Docker`) shows the workaround. Reuse its helpers (`Invoke-Sql`, `Invoke-Mongo`, `Get-ServiceContainer`) for container work.

## Docs

| Doc | Covers |
|---|---|
| `docs/architecture.md` | Architecture and fault tolerance |
| `docs/operations.md` | Running and deploying the stack |
| `docs/backup-restore.md` | Backup and restore |
| `docs/monitoring.md` | Monitoring |
| `docs/verification.md` | What has been verified and known limits |
| `contracts/api-spec.md` | API and MVC contracts |
