# DevMetrics
![CI](https://github.com/YOUR_USERNAME/DevMetrics/actions/workflows/dotnet.yml/badge.svg)

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
- REST API with health-check endpoints (`/health`, `/health/live`, `/health/ready`)

---

## Prerequisites

| Dependency       | Version  | Notes                                              |
|------------------|----------|----------------------------------------------------|
| .NET SDK         | 8.0+     | Required for `dotnet run` and `dotnet build`       |
| Git              | Any      | Repos must be valid Git repositories               |

---

## Quick Start

```bash
# 1. Clone the repository
git clone https://github.com/your-org/devmetrics.git
cd devmetrics

# 2. Apply database migrations
dotnet ef database update \
  --project DevMetrics.Infrastructure \
  --startup-project DevMetrics.Api

# 3. Start the API
dotnet run --project DevMetrics.Api

# 4. Open the dashboard
open http://localhost:5000
# Swagger UI: http://localhost:5000/swagger
```

---

## Configuration

All settings live in `appsettings.json`.

### Adding a repository

Via the web dashboard (easiest): open http://localhost:5000, type the absolute path in the **Add Repository** card, and click **Track Repository**.

Via the REST API:
```bash
curl -X POST http://localhost:5000/api/repositories \
  -H "Content-Type: application/json" \
  -d '{"path": "/absolute/path/to/your/repo"}'
```

### Scan schedule

```json
"CronExpressions": {
  "HourlyScan":   "0 * * * *",
  "WeeklyReport": "0 9 * * 1"
}
```

POSIX cron format: `minute hour day-of-month month day-of-week`.

| Expression     | Meaning                       |
|----------------|-------------------------------|
| `0 * * * *`    | Every hour at minute 0        |
| `*/15 * * * *` | Every 15 minutes              |
| `0 9 * * 1`    | Monday at 09:00 UTC           |
| `0 8 * * 1-5`  | Weekdays at 08:00 UTC         |

### Email reports

```json
"Email": {
  "Enabled":                 false,
  "Host":                    "smtp.gmail.com",
  "Port":                    587,
  "Username":                "you@gmail.com",
  "Password":                "app-password",
  "FromAddress":             "devmetrics@yourdomain.com",
  "FromName":                "DevMetrics",
  "UseSsl":                  false,
  "Recipients":              ["team@example.com"],
  "DashboardBaseUrl":        "http://localhost:5000"
}
```

For Gmail, generate an [App Password](https://myaccount.google.com/apppasswords) under **Security → 2-Step Verification → App passwords**. Use `Port: 587` with `UseSsl: false` (STARTTLS).

Supply credentials via environment variables rather than committing them to `appsettings.json`:
```bash
Email__Enabled=true
Email__Username=you@gmail.com
Email__Password=your-app-password
```

---

## API Documentation

Swagger UI is available at **http://localhost:5000/swagger** in Development mode.

| Endpoint                        | Method | Description                                     |
|---------------------------------|--------|-------------------------------------------------|
| `/api/repositories`             | GET    | List all tracked repositories                   |
| `/api/repositories`             | POST   | Add a repository by path                        |
| `/api/repositories/{id}`        | DELETE | Remove a repository and all its history         |
| `/api/dashboard/summary`        | GET    | Aggregated daily stats (default: 14 days)       |
| `/api/dashboard/health`         | GET    | DB connectivity + last scan timestamp           |
| `/api/scan/trigger`             | POST   | Manually trigger an immediate scan (async 202)  |
| `/api/scan/status/{operationId}`| GET    | Poll the status of a triggered scan             |
| `/health`                       | GET    | Combined health (database + git + background)   |
| `/health/live`                  | GET    | Liveness probe (is the process alive?)          |
| `/health/ready`                 | GET    | Readiness probe (can it serve traffic?)         |

**SignalR hub:** `ws://localhost:5000/dashboardHub`

Client events (subscribe with `connection.on(event, handler)`):

| Event                        | Payload                           | When                                |
|------------------------------|-----------------------------------|-------------------------------------|
| `ScanCompleted`              | `ScanResultDto`                   | After every scheduled scan          |
| `DashboardUpdated`           | `DashboardDataDto`                | After a manually triggered scan     |
| `RepositoryActivityDetected` | `{ repositoryPath, repositoryName }` | On `.git` directory change       |

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

---


## Troubleshooting

### SQLite database locked

**Symptom:** `SqliteException: database is locked`

**Cause:** Two processes (e.g., two dotnet run instances) are writing to the same `.db` file simultaneously.

**Fix:** Stop all instances except one. SQLite is a single-writer database — DevMetrics is designed to run as a single process. For multi-instance deployments, migrate to PostgreSQL by replacing the SQLite provider.

### Git repository not found

**Symptom:** `ArgumentException: 'path' does not contain a valid Git repository`

**Cause:** The path passed to **Add Repository** doesn't contain a `.git` folder, or the path is a bare clone.

**Fix:** Verify with `ls /your/path/.git`. Bare clones are supported if the repo root contains `HEAD` and an `objects/` directory directly.

### LibGit2Sharp native library fails to load

**Symptom:** `GitServiceHealthCheck` reports `Unhealthy` immediately after startup.

**Cause:** The `libgit2` native binary for your OS/architecture is missing from the publish output.

**Fix:**
- Ensure you published with `dotnet publish` (not `dotnet build`) — publish copies native binaries.
- If running on ARM64, add `--runtime linux-arm64` to the publish command.

### Emails not sending

**Symptom:** Weekly report isn't arriving; no errors in logs.

**Causes / fixes:**
1. `Email__Enabled` is `false` (the default) — set it to `true`.
2. `Email__Recipients` is empty — add at least one address.
3. Gmail requires an **App Password**, not your account password.
4. Check the application logs for `Email |` prefixed entries for detailed SMTP errors.

### Background service shows Degraded in health check

**Symptom:** `/health` returns `Degraded` for `background-scan`.

**Cause:** The last scan cycle reported `Failed` or `PartialFailure` (e.g., a repository path no longer exists).

**Fix:** Check the logs for `ScanService |` prefixed entries. Remove repositories whose paths are gone: `DELETE /api/repositories/{id}`.

---

## Architecture Overview

```
DevMetrics.Api          → ASP.NET Core Web API + Razor Pages + SignalR Hub
DevMetrics.Application  → MediatR Commands/Queries + Background Services + Email
DevMetrics.Infrastructure → EF Core + SQLite + LibGit2Sharp (GitService)
DevMetrics.Core         → Entities + Interfaces + DTOs (no dependencies)
DevMetrics.Tests        → xUnit + Moq + FluentAssertions + WebApplicationFactory
```

The dependency rule flows strictly inward: Api → Application → Core ← Infrastructure.

---
