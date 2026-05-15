# DevMetrics — EF Core Migrations

This folder is managed by `dotnet-ef` and should not be edited by hand.
EF Core generates migration files here when you run the commands below.

---

## Prerequisites

Install the EF Core global tool (once per machine):

```bash
dotnet tool install --global dotnet-ef
```

Or update it if already installed:

```bash
dotnet tool update --global dotnet-ef
```

---

## Creating the initial migration

Run from the **solution root**:

```bash
dotnet ef migrations add InitialCreate \
  --project DevMetrics.Infrastructure \
  --startup-project DevMetrics.Api
```

- `--project` targets the project that contains `AppDbContext` and the `Migrations/` folder.
- `--startup-project` provides the host (for DI and configuration). EF Core
  also discovers `DesignTimeDbContextFactory` in `DevMetrics.Infrastructure`,
  so this works even without a running API.

This generates three files:

| File | Purpose |
|------|---------|
| `Migrations/<timestamp>_InitialCreate.cs` | `Up()` / `Down()` SQL operations |
| `Migrations/<timestamp>_InitialCreate.Designer.cs` | EF Core model snapshot used for diff |
| `Migrations/AppDbContextModelSnapshot.cs` | Current model state — updated on every migration |

---

## Applying migrations to the database

```bash
dotnet ef database update \
  --project DevMetrics.Infrastructure \
  --startup-project DevMetrics.Api
```

Creates `./Data/devmetrics.db` (SQLite file) and applies all pending migrations.

---

## Adding subsequent migrations

After modifying entities in `DevMetrics.Core/Entities/` or the model configuration
in `AppDbContext.OnModelCreating`, run:

```bash
dotnet ef migrations add <MigrationName> \
  --project DevMetrics.Infrastructure \
  --startup-project DevMetrics.Api
```

Then apply:

```bash
dotnet ef database update \
  --project DevMetrics.Infrastructure \
  --startup-project DevMetrics.Api
```

---

## Rolling back a migration

```bash
# Roll back to the previous migration (does NOT delete the migration files)
dotnet ef database update <PreviousMigrationName> \
  --project DevMetrics.Infrastructure \
  --startup-project DevMetrics.Api

# Then remove the broken migration file
dotnet ef migrations remove \
  --project DevMetrics.Infrastructure \
  --startup-project DevMetrics.Api
```

---

## Schema overview (InitialCreate)

| Table | Key constraints |
|-------|----------------|
| `Repositories` | `IX_Repositories_Path` UNIQUE, `IX_Repositories_LastScannedUtc` |
| `Commits` | `IX_Commits_Hash` UNIQUE, `IX_Commits_RepositoryId_DateUtc` composite |
| `DailySummaries` | `IX_DailySummaries_RepositoryId_Date` UNIQUE, `IX_DailySummaries_Date` |

Cascade delete is configured from `Repositories` → `Commits` and `DailySummaries`.
