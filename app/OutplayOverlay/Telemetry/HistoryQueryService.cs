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
/// Cross-session trend for a given sim, optionally narrowed to a single track.
///
/// TRACK-COLUMN GAP: CLOSED. Both adapters now populate TelemetrySample.TrackName
/// (IRacingTelemetrySource parses WeekendInfo:TrackDisplayName from session-info YAML;
/// F125TelemetrySource maps PacketSessionData's m_trackId through a lookup table — see each
/// file's doc comments for the unverified assumptions behind those extractions), and
/// SessionLogger persists it to a new nullable Sessions.TrackName column (added via an additive
/// ALTER TABLE guard so existing sessions.db files upgrade in place — see SessionLogger's
/// InitializeSchema doc comment).
///
/// BACKWARD COMPATIBILITY: <paramref name="GetTrend"/>'s new <c>track</c> parameter defaults to
/// null, which preserves the exact old sim-wide behavior (all tracks pooled together) — existing
/// callers (TrendsWindow.xaml.cs calls GetTrend("iRacing") / GetTrend("F1_25") with no other args)
/// keep working unchanged. Passing a non-null track additionally filters to
/// Sessions.TrackName = track, so "your best lap improved" becomes an apples-to-apples comparison
/// for that circuit specifically. Sessions logged before this change (or where the sim/car
/// combination never yielded a parseable track name) have a NULL TrackName and are excluded from
/// any track-filtered query — they still count toward the sim-wide (track = null) view.
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
    /// <param name="track">Optional track name (must match Sessions.TrackName exactly, e.g.
    /// "Spa" or "Silverstone" as extracted by the corresponding adapter). When null (default),
    /// preserves the original sim-wide behavior — all sessions for this sim are pooled regardless
    /// of track, including sessions with a NULL TrackName (pre-upgrade data or a session where the
    /// track name was never successfully parsed). When non-null, only sessions whose TrackName
    /// exactly matches are included; sessions with a NULL TrackName are excluded from a
    /// track-filtered query since we can't confirm they match.</param>
    /// <param name="recentSessionCount">How many of the most recent sessions to include in
    /// RecentSessionBests / the RecentTrend comparison. Default 10 is an arbitrary but reasonable
    /// "last handful of sessions" window; callers needing more history can pass a larger value.</param>
    /// <param name="dbPathOverride">For tests; production callers should omit this and use
    /// SessionLogger's default path.</param>
    public static TrendResult? GetTrend(string sim, string? track = null, int recentSessionCount = 10, string? dbPathOverride = null)
    {
        if (recentSessionCount < 1) recentSessionCount = 1;

        var dbPath = dbPathOverride ?? SessionLogger.GetDefaultDbPath();
        var connectionString = $"Data Source={dbPath}";

        // Only completed sessions (EndUtc IS NOT NULL) count — an in-progress session's laps are
        // still being accumulated and its "best lap" could still improve mid-session, which would
        // make an in-progress session a moving target for a trend computation.
        var sessionBests = LoadSessionBests(connectionString, sim, track);
        if (sessionBests.Count == 0) return null;

        var totalLaps = LoadTotalLapCount(connectionString, sim, track);

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

    private static List<SessionBestLap> LoadSessionBests(string connectionString, string sim, string? track)
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
                AND ($track IS NULL OR Sessions.TrackName = $track)
            GROUP BY Sessions.Id
            ORDER BY Sessions.StartUtc ASC;
            """;
        cmd.Parameters.AddWithValue("$sim", sim);
        cmd.Parameters.AddWithValue("$track", (object?)track ?? DBNull.Value);

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

    private static int LoadTotalLapCount(string connectionString, string sim, string? track)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM Laps
            JOIN Sessions ON Sessions.Id = Laps.SessionId
            WHERE Sessions.Sim = $sim AND Sessions.EndUtc IS NOT NULL
                AND ($track IS NULL OR Sessions.TrackName = $track);
            """;
        cmd.Parameters.AddWithValue("$sim", sim);
        cmd.Parameters.AddWithValue("$track", (object?)track ?? DBNull.Value);
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
