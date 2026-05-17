# DevMetrics
![CI](https://github.com/imann128/DevMetrics/actions/workflows/dotnet.yml/badge.svg)

**DevMetrics** is a self-hosted developer productivity dashboard that tracks local Git activity in real time. It scans your Git repositories, aggregates commit history and diff statistics, and serves the data through a live web dashboard with automatic updates via SignalR.

---

## Features

- Scans local Git repositories on a configurable cron schedule (default: hourly)
- Tracks commits per day, lines added/deleted, and files changed
- Stores history in an embedded SQLite database via EF Core
- Real-time dashboard updates pushed via WebSocket (SignalR)
- Weekly email productivity reports via MailKit/SMTP
- Watches repository `.git` directories for immediate activity detection
- REST API with Swagger UI for programmatic access
- Health-check endpoints (`/health`, `/health/live`, `/health/ready`)

---

## Quick Start

### Option A — Docker (recommended)

**Prerequisites:** [Docker Desktop](https://www.docker.com/products/docker-desktop/)

```bash
# 1. Clone the repository
git clone https://github.com/imann128/DevMetrics.git
cd DevMetrics

# 2. Open docker-compose.yml and add your repo(s) under the volumes section:
#
#    volumes:
#      - devmetrics-data:/app/Data
#      - devmetrics-logs:/app/Logs
#      - D:\Users\YourName\projects\my-repo:/repos/my-repo:ro   ← add this
#
# Use the Windows path on the left, a Linux container path on the right.

# 3. Build and start
docker compose up -d --build

# 4. Open the dashboard
start http://localhost:5000
```

> **Adding repositories in Docker:** Inside the container, only Linux paths exist. If you mounted `D:\Projects\my-repo:/repos/my-repo:ro`, enter `/repos/my-repo` in the dashboard's Add Repository form — not the Windows path.

**Useful Docker commands:**

| Command | Effect |
|---|---|
| `docker compose up -d --build` | Build image and start (or rebuild after code changes) |
| `docker compose stop` | Pause containers — data is preserved |
| `docker compose start` | Resume paused containers |
| `docker compose down` | Remove containers — named volumes (data/logs) are kept |
| `docker compose logs -f` | Stream live logs |
| `docker compose down --volumes` | ⚠ Remove containers **and** all data |

---

### Option B — dotnet run (local)

**Prerequisites:**

| Dependency | Version | Notes |
|---|---|---|
| .NET SDK | 8.0+ | Required for `dotnet run` and `dotnet build` |
| Git | Any | Repos must be valid Git repositories |

```bash
# 1. Clone the repository
git clone https://github.com/imann128/DevMetrics.git
cd DevMetrics

# 2. Run the API (migrations are applied automatically on startup)
dotnet run --project DevMetrics.Api

# 3. Open the dashboard
start http://localhost:5000
# Swagger UI: http://localhost:5000/swagger
```

---

## Configuration

All settings live in `DevMetrics.Api/appsettings.json`. In Docker, any key can be overridden with an environment variable using double-underscore notation (`Email__Username=you@gmail.com`).

### Adding a repository

**Via the web dashboard (easiest):** open http://localhost:5000, type the path in the **Add Repository** card, and click **Track Repository**.

- **dotnet run:** enter the Windows path directly, e.g. `D:\Projects\my-repo`
- **Docker:** enter the container-side path, e.g. `/repos/my-repo` (the right-hand side of your volume mount)

**Via the REST API:**
```bash
curl -X POST http://localhost:5000/api/repositories \
  -H "Content-Type: application/json" \
  -d '{"path": "/repos/my-repo"}'
```

### Scan schedule

```json
"CronExpressions": {
  "HourlyScan":   "0 * * * *",
  "WeeklyReport": "0 9 * * 1"
}
```

POSIX cron format: `minute hour day-of-month month day-of-week`.

| Expression | Meaning |
|---|---|
| `0 * * * *` | Every hour at minute 0 |
| `*/15 * * * *` | Every 15 minutes |
| `0 9 * * 1` | Monday at 09:00 UTC |
| `0 8 * * 1-5` | Weekdays at 08:00 UTC |

### Email reports

```json
"Email": {
  "Enabled":         false,
  "Host":            "smtp.gmail.com",
  "Port":            587,
  "Username":        "you@gmail.com",
  "Password":        "app-password",
  "FromAddress":     "devmetrics@yourdomain.com",
  "FromName":        "DevMetrics",
  "UseSsl":          false,
  "Recipients":      ["team@example.com"],
  "DashboardBaseUrl":"http://localhost:5000"
}
```

For Gmail, generate an [App Password](https://myaccount.google.com/apppasswords) under **Security → 2-Step Verification → App passwords**. Use `Port: 587` with `UseSsl: false` (STARTTLS).

In Docker, supply credentials via environment variables in `docker-compose.yml` rather than editing `appsettings.json`:
```yaml
environment:
  Email__Enabled: "true"
  Email__Username: "you@gmail.com"
  Email__Password: "your-app-password"
```

---

## API Documentation

Swagger UI is available at **http://localhost:5000/swagger** in Development mode.

| Endpoint | Method | Description |
|---|---|---|
| `/api/repositories` | GET | List all tracked repositories |
| `/api/repositories` | POST | Add a repository by path |
| `/api/repositories/{id}` | DELETE | Remove a repository and all its history |
| `/api/dashboard/summary` | GET | Aggregated daily stats (default: 14 days) |
| `/api/dashboard/health` | GET | DB connectivity + last scan timestamp |
| `/api/scan/trigger` | POST | Manually trigger an immediate scan (async 202) |
| `/api/scan/status/{operationId}` | GET | Poll the status of a triggered scan |
| `/health` | GET | Combined health (database + git + background) |
| `/health/live` | GET | Liveness probe (is the process alive?) |
| `/health/ready` | GET | Readiness probe (can it serve traffic?) |

**SignalR hub:** `ws://localhost:5000/dashboardHub`

| Event | Payload | When |
|---|---|---|
| `ScanCompleted` | `ScanResultDto` | After every scheduled scan |
| `DashboardUpdated` | `DashboardDataDto` | After a manually triggered scan |
| `RepositoryActivityDetected` | `{ repositoryPath, repositoryName }` | On `.git` directory change |

---

## Testing

```bash
# Run all tests
dotnet test

# Unit tests only (fast, no DB)
dotnet test --filter "Category!=Integration"

# Integration tests only
dotnet test --filter "FullyQualifiedName~Integration"

# With coverage report
dotnet test \
  --collect:"XPlat Code Coverage" \
  --settings DevMetrics.Tests/DevMetrics.runsettings \
  --results-directory ./TestResults

# Generate HTML coverage report (requires reportgenerator)
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator \
  -reports:"./TestResults/**/coverage.cobertura.xml" \
  -targetdir:"./CoverageReport" \
  -reporttypes:Html
```

---

## EF Core Migrations

```bash
# Create a new migration after changing entities
dotnet ef migrations add YourMigrationName \
  --project DevMetrics.Infrastructure \
  --startup-project DevMetrics.Api

# Apply pending migrations
dotnet ef database update \
  --project DevMetrics.Infrastructure \
  --startup-project DevMetrics.Api

# View applied migrations
dotnet ef migrations list \
  --project DevMetrics.Infrastructure \
  --startup-project DevMetrics.Api
```

> Migrations run automatically on startup — `dotnet ef database update` is only needed if you want to apply them manually before starting the app.

---

## Troubleshooting

### Repository path is not owned by current user (Docker)

**Symptom:** `LibGit2SharpException: repository path '/repos/...' is not owned by current user`

**Cause:** Docker volumes mounted from a Windows host appear as root-owned inside the Linux container. libgit2 (used by LibGit2Sharp) enforces an ownership safety check and refuses to open repos not owned by the running process user.

**Fix:** Already handled in the Dockerfile — `/etc/gitconfig` is written with `safe.directory = *` during the build. If you see this error, rebuild the image: `docker compose up -d --build`.

### SQLite database locked

**Symptom:** `SqliteException: database is locked`

**Cause:** Two processes are writing to the same `.db` file simultaneously.

**Fix:** Stop all instances except one. SQLite is a single-writer database. For multi-instance deployments, migrate to PostgreSQL.

### Git repository not found

**Symptom:** `ArgumentException: '...' does not appear to be a Git repository`

**Cause:** The path doesn't contain a `.git` folder (or `HEAD` + `objects/` for a bare clone).

**Fix:** Verify with `ls /your/path/.git`. In Docker, double-check your volume mount is present in `docker-compose.yml` and use the container-side path in the dashboard.

### LibGit2Sharp native library fails to load

**Symptom:** `GitServiceHealthCheck` reports `Unhealthy` immediately after startup.

**Cause:** The `libgit2` native binary for your OS/architecture is missing from the publish output.

**Fix:** The Dockerfile explicitly restores and publishes for `linux-x64`, which bundles the correct native binary. If running on ARM64, change `--runtime linux-x64` to `--runtime linux-arm64` in the Dockerfile and rebuild.

### Emails not sending

**Symptom:** Weekly report isn't arriving; no errors in logs.

**Fix:**
1. `Email__Enabled` must be `true` (it defaults to `false`).
2. `Email__Recipients` must contain at least one address.
3. Gmail requires an **App Password**, not your account password.
4. Check the logs for `Email |` prefixed entries for detailed SMTP errors.

### Background service shows Degraded in health check

**Symptom:** `/health` returns `Degraded` for `background-scan`.

**Cause:** The last scan cycle reported `Failed` or `PartialFailure` (e.g., a repository path no longer exists).

**Fix:** Check the logs for `Scan |` prefixed entries. Remove stale repositories: `DELETE /api/repositories/{id}`.

---

## Architecture Overview

```
DevMetrics.Api          → ASP.NET Core Web API + Razor Pages + SignalR Hub
DevMetrics.Application  → MediatR Commands/Queries + Background Services + Email
DevMetrics.Infrastructure → EF Core + SQLite + LibGit2Sharp (GitService)
DevMetrics.Core         → Entities + Interfaces + DTOs (no dependencies)
DevMetrics.Tests        → xUnit + Moq + FluentAssertions + WebApplicationFactory
```

The dependency rule flows strictly inward: `Api → Application → Core ← Infrastructure`.

---
