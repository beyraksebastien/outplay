using Microsoft.Data.Sqlite;

namespace OutplayOverlay.Telemetry;

/// <summary>One historical session's best lap, as surfaced to a trend view.</summary>
public sealed record SessionBestLap
{
    public required long SessionId { get; init; }
    public required DateTime StartUtc { get; init; }
    public required float BestLapTimeSec { get; init; }
}

/// <summary>
/// Cross-session trend for a given sim. TRACK-COLUMN GAP (flagged, not resolved in this change):
/// neither SessionLogger's Sessions table nor TelemetrySample carries a track/circuit identifier
/// today — iRacing's adapter never parses session-info YAML (where track name would live) and F1
/// 25's adapter never parses the Session packet's m_trackId (only trackLength was added here, for
/// Feature 1's LapDistancePct). Adding a real per-track column is therefore NOT the "cheap wiring
/// fix" it might look like: it needs a new sim-specific extraction path in each adapter plus a
/// schema/migration decision (SessionLogger's schema comment already says there's no migration
/// framework yet, "for now CREATE TABLE IF NOT EXISTS is sufficient since the shape hasn't shipped
/// to users yet" — adding a nullable Track column later, once one adapter fills it in, is exactly
/// the kind of additive change that comment anticipates). Given that, this is intentionally scoped
/// as a v1 limitation: trends below are SIM-WIDE (all tracks pooled together for that sim), not
/// per-track. If the user only ever practices one track per sim this is a distinction without a
/// difference; if they hop tracks, "your best lap improved" is comparing apples across circuits,
/// which is misleading — surfacing this in the UI (e.g. "trends are sim-wide until per-track
/// tracking ships") is a frontend follow-up, not something this query layer can silently fix.
/// </summary>
public sealed record TrendResult
{
    public required string Sim { get; init; }
    public required int SessionCount { get; init; }
    public required int TotalLapsLogged { get; init; }

    /// <summary>Best (lowest) lap time across every historical session for this sim.</summary>
    public required float BestLapTimeSec { get; init; }

    /// <summary>Best lap time from the earliest session on record for this sim.</summary>
    public required float FirstSessionBestLapTimeSec { get; init; }

    /// <summary>FirstSessionBestLapTimeSec - BestLapTimeSec. Positive = improved since the first
    /// session; negative would mean the earliest session's best has never been beaten (unusual,
    /// but not impossible for a brand-new/rusty return to a sim).</summary>
    public required float ImprovementSinceFirstSessionSec { get; init; }

    /// <summary>Simple recent-trend read over the last <c>recentSessionCount</c> sessions'
    /// best-lap times (earliest vs. latest in that window) — reuses SessionSummaryGenerator's
    /// deadband convention (0.15s) rather than inventing a second one.</summary>
    public required LapTimeTrend RecentTrend { get; init; }

    /// <summary>Per-session best laps, oldest first, capped to the most recent
    /// <c>recentSessionCount</c> sessions requested — for a UI to plot as a simple line/sparkline.</summary>
    public required IReadOnlyList<SessionBestLap> RecentSessionBests { get; init; }
}

/// <summary>
/// Pull-based (not event-driven) query API over SessionLogger's existing SQLite sessions.db, for
/// a UI "View Trends" button to call on demand. Deliberately mirrors
/// SessionSummaryGenerator.GenerateForMostRecentSession's shape: plain synchronous SQLite reads
/// over a handful/dozens of rows, invoked only when the user asks for it — NOT subscribed to
/// TelemetryHub, NOT on the telemetry hot path. No background writer/queue needed here since this
/// class never writes to the database, only reads.
/// </summary>
public static class HistoryQueryService
{
    private const float TrendDeadbandSec = 0.15f; // matches SessionSummaryGenerator.ComputeTrend

    /// <summary>
    /// Computes a cross-session trend for the given sim ("iRacing" or "F1_25", matching the
    /// Sessions.Sim values SessionLogger writes). Returns null if no completed session with at
    /// least one logged lap exists yet for that sim — same "not enough data" convention as
    /// SessionSummaryGenerator returning null laps/using deadbands elsewhere.
    /// </summary>
    /// <param name="sim">Sim name exactly as SessionLogger records it (e.g. "iRacing", "F1_25").</param>
    /// <param name="recentSessionCount">How many of the most recent sessions to include in
    /// RecentSessionBests / the RecentTrend comparison. Default 10 is an arbitrary but reasonable
    /// "last handful of sessions" window; callers needing more history can pass a larger value.</param>
    /// <param name="dbPathOverride">For tests; production callers should omit this and use
    /// SessionLogger's default path.</param>
    public static TrendResult? GetTrend(string sim, int recentSessionCount = 10, string? dbPathOverride = null)
    {
        if (recentSessionCount < 1) recentSessionCount = 1;

        var dbPath = dbPathOverride ?? SessionLogger.GetDefaultDbPath();
        var connectionString = $"Data Source={dbPath}";

        // Only completed sessions (EndUtc IS NOT NULL) count — an in-progress session's laps are
        // still being accumulated and its "best lap" could still improve mid-session, which would
        // make an in-progress session a moving target for a trend computation.
        var sessionBests = LoadSessionBests(connectionString, sim);
        if (sessionBests.Count == 0) return null;

        var totalLaps = LoadTotalLapCount(connectionString, sim);

        var overallBest = sessionBests.Min(s => s.BestLapTimeSec);
        var firstSessionBest = sessionBests[0].BestLapTimeSec; // list is ordered oldest-first
        var improvement = firstSessionBest - overallBest;

        var recentWindow = sessionBests.TakeLast(recentSessionCount).ToList();
        var recentTrend = ComputeRecentTrend(recentWindow);

        return new TrendResult
        {
            Sim = sim,
            SessionCount = sessionBests.Count,
            TotalLapsLogged = totalLaps,
            BestLapTimeSec = overallBest,
            FirstSessionBestLapTimeSec = firstSessionBest,
            ImprovementSinceFirstSessionSec = improvement,
            RecentTrend = recentTrend,
            RecentSessionBests = recentWindow,
        };
    }

    private static List<SessionBestLap> LoadSessionBests(string connectionString, string sim)
    {
        var results = new List<SessionBestLap>();

        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Sessions.Id, Sessions.StartUtc, MIN(Laps.LapTimeSec)
            FROM Sessions
            JOIN Laps ON Laps.SessionId = Sessions.Id
            WHERE Sessions.Sim = $sim AND Sessions.EndUtc IS NOT NULL
            GROUP BY Sessions.Id
            ORDER BY Sessions.StartUtc ASC;
            """;
        cmd.Parameters.AddWithValue("$sim", sim);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SessionBestLap
            {
                SessionId = reader.GetInt64(0),
                StartUtc = DateTime.Parse(reader.GetString(1)).ToUniversalTime(),
                BestLapTimeSec = (float)reader.GetDouble(2),
            });
        }

        return results;
    }

    private static int LoadTotalLapCount(string connectionString, string sim)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM Laps
            JOIN Sessions ON Sessions.Id = Laps.SessionId
            WHERE Sessions.Sim = $sim AND Sessions.EndUtc IS NOT NULL;
            """;
        cmd.Parameters.AddWithValue("$sim", sim);
        return (int)(long)cmd.ExecuteScalar()!;
    }

    /// <summary>Earliest-vs-latest best lap in the recent window, deadbanded the same way
    /// SessionSummaryGenerator.ComputeTrend deadbands first-half-vs-second-half lap times.</summary>
    private static LapTimeTrend ComputeRecentTrend(IReadOnlyList<SessionBestLap> recentWindow)
    {
        if (recentWindow.Count < 2) return LapTimeTrend.Flat; // not enough sessions to call a trend

        var earliest = recentWindow[0].BestLapTimeSec;
        var latest = recentWindow[^1].BestLapTimeSec;
        var delta = latest - earliest;

        if (delta < -TrendDeadbandSec) return LapTimeTrend.Improving; // latest session's best is faster
        if (delta > TrendDeadbandSec) return LapTimeTrend.Worsening;  // latest session's best is slower
        return LapTimeTrend.Flat;
    }
}
