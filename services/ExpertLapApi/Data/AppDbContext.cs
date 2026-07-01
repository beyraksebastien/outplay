using ExpertLapApi.Models;
using Microsoft.EntityFrameworkCore;

namespace ExpertLapApi.Data;

/// <summary>
/// EF Core context over Postgres (Npgsql). A real hosted, multi-writer database — chosen over
/// SQLite (which the desktop app correctly uses for its local single-user log, see
/// OutplayOverlay/Telemetry/SessionLogger.cs's own comment on that choice) because this service is
/// a shared, concurrently-written backend, which is exactly the case SQLite's single-writer model
/// is wrong for.
/// </summary>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<ExpertLap> ExpertLaps => Set<ExpertLap>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExpertLap>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Sim).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Track).HasMaxLength(128).IsRequired();
            entity.Property(e => e.Car).HasMaxLength(128).IsRequired();
            entity.Property(e => e.SubmitterLabel).HasMaxLength(64);

            // Composite index for the combo lookups both GET endpoints need
            // (best-for-combo, and "which combos have any lap").
            entity.HasIndex(e => new { e.Sim, e.Track, e.Car });

            // Partial-index-style lookup for "the current best for this combo" — Postgres
            // supports a real partial index (WHERE "IsCurrentBest"); EF Core's fluent API for
            // filtered indexes requires HasFilter with the exact SQL Postgres expects, so this is
            // left as a plain (non-unique) index for v1 simplicity rather than risking a subtly
            // wrong filter expression that only breaks at migration-apply time. See deployment
            // notes: with expected v1 traffic this is not a performance concern.
        });
    }
}
