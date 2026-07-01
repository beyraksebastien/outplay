# EF Core migrations — not generated in this session

This environment (macOS, no .NET SDK/toolchain installed) cannot run `dotnet ef`. No
`Migrations/*.cs` snapshot/migration files exist yet, and none were fabricated by hand — a
hand-written fake migration file would silently drift from whatever EF Core's actual scaffolding
would produce (model snapshot format, designer file, etc.) and is more likely to cause a confusing
failure than a missing-migration error is.

## What you need to do on your Windows machine (or any machine with the .NET 8 SDK) before first deploy

1. Restore packages once: `dotnet restore` from `services/ExpertLapApi/`.
2. Generate the initial migration:
   ```
   dotnet ef migrations add InitialCreate
   ```
   (Requires the `dotnet-ef` global tool: `dotnet tool install --global dotnet-ef` if you don't
   already have it.) This reads `Data/AppDbContext.cs` / `Models/ExpertLap.cs` and generates the
   real `Migrations/*.cs` files plus the model snapshot.
3. Apply it to your Postgres instance:
   ```
   dotnet ef database update --connection "Host=...;Port=5432;Database=...;Username=...;Password=..."
   ```
   or, more commonly for a Render-hosted Postgres instance, set `ConnectionStrings__Default` in
   your shell and run `dotnet ef database update` with no `--connection` override (it'll pick up
   the same configuration binding Program.cs uses).
4. Commit the generated `Migrations/` folder to the repo so future deploys don't need to
   re-generate it — Render's Dockerfile build does NOT run `dotnet ef migrations add` (that
   requires the design-time `dotnet-ef` tool and a live DB connection, neither of which belong in
   a container build step); it only runs `dotnet publish`. Applying migrations against production
   is a separate, deliberate step — see the deployment section of the final report for exactly
   when to run it (once, after first deploy, before real traffic).

## Fallback: hand-written SQL DDL (for review / manual `psql` apply if you don't want to use `dotnet ef database update`)

This mirrors what `dotnet ef migrations add InitialCreate` should produce from the current
`AppDbContext`/`ExpertLap` model as of this writing. Treat this as a reviewable approximation, NOT
a substitute for actually running `dotnet ef migrations add` — EF Core's own migration is the
source of truth once generated (exact column ordering/naming/default-value SQL, and the
`__EFMigrationsHistory` bookkeeping table EF Core needs to track which migrations have been
applied, are not reproduced here).

```sql
CREATE TABLE "ExpertLaps" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "Sim" character varying(64) NOT NULL,
    "Track" character varying(128) NOT NULL,
    "Car" character varying(128) NOT NULL,
    "LapTimeSec" double precision NOT NULL,
    "UploadedAtUtc" timestamp with time zone NOT NULL,
    "SubmitterLabel" character varying(64) NULL,
    "TelemetryTraceJson" text NOT NULL,
    "IsCurrentBest" boolean NOT NULL
);

CREATE INDEX "IX_ExpertLaps_Sim_Track_Car" ON "ExpertLaps" ("Sim", "Track", "Car");
```

If you apply this by hand instead of via `dotnet ef database update`, EF Core will not know a
migration has been "applied" (no `__EFMigrationsHistory` row), so the first real
`dotnet ef database update` run later will try to re-create these objects and fail on
already-exists errors. Prefer the `dotnet ef` path in step 2/3 above; this SQL block exists so a
reviewer without the .NET SDK installed can see the expected shape without executing anything.
