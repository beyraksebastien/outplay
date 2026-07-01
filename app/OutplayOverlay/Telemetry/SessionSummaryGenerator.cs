using Microsoft.Data.Sqlite;

namespace OutplayOverlay.Telemetry;

/// <summary>
/// Lap-level trend across the session. Deliberately coarse — see SessionSummary doc comment.
/// </summary>
public enum LapTimeTrend
{
    Improving,
    Worsening,
    Flat,
}

/// <summary>
/// One completed lap as surfaced to the UI. Mirrors LapRecord but is the public/summary-facing
/// shape (kept separate in case the persisted schema and the UI-facing shape diverge later).
/// </summary>
public sealed record LapSummary
{
    public required int LapNumber { get; init; }
    public required float LapTimeSec { get; init; }
    public float? DeltaToBestSec { get; init; }
    public float AvgThrottle { get; init; }
    public float AvgBrake { get; init; }
}

/// <summary>
/// Deterministic, rule-based post-session debrief (PRD §5.4 Race Debrief, scoped to lap-level
/// data only — no LLM, no corner segmentation, since neither exists yet). Every bullet below is
/// generated from a template against a stat computed from the Laps table; nothing here is
/// free-form text generation. See the report for how much simpler this reads than an
/// LLM-authored debrief would.
/// </summary>
public sealed record SessionSummary
{
    public required long SessionId { get; init; }
    public required string Sim { get; init; }
    public required DateTime StartUtc { get; init; }
    public required DateTime EndUtc { get; init; }
    public TimeSpan Duration => EndUtc - StartUtc;

    public required int LapCount { get; init; }
    public LapSummary? BestLap { get; init; }
    public LapSummary? WorstLap { get; init; }

    /// <summary>Standard deviation of lap times in seconds. Lower = more consistent. Null if
    /// fewer than 2 laps were logged.</summary>
    public double? LapTimeStdDevSec { get; init; }

    public LapTimeTrend Trend { get; init; }

    public required IReadOnlyList<string> Positives { get; init; }
    public required IReadOnlyList<string> Improvements { get; init; }

    public required IReadOnlyList<LapSummary> Laps { get; init; }
}

/// <summary>
/// Builds a SessionSummary from persisted Laps rows for a given session. Pure, synchronous,
/// in-memory computation over a handful of rows (a session is realistically tens of laps, not
/// thousands) — safe to run on SessionLogger's disconnect callback without violating the
/// telemetry hot-path budget (this only ever runs once per session-end, not per sample).
/// </summary>
public static class SessionSummaryGenerator
{
    public static SessionSummary Generate(string connectionString, long sessionId, string sim, DateTime startUtc, DateTime endUtc)
    {
        var laps = LoadLaps(connectionString, sessionId);

        var best = laps.Count > 0 ? laps.MinBy(l => l.LapTimeSec) : null;
        var worst = laps.Count > 0 ? laps.MaxBy(l => l.LapTimeSec) : null;
        var stdDev = ComputeStdDev(laps.Select(l => (double)l.LapTimeSec).ToList());
        var trend = ComputeTrend(laps);

        var positives = new List<string>();
        var improvements = new List<string>();
        BuildBullets(laps, stdDev, trend, positives, improvements);

        return new SessionSummary
        {
            SessionId = sessionId,
            Sim = sim,
            StartUtc = startUtc,
            EndUtc = endUtc,
            LapCount = laps.Count,
            BestLap = best,
            WorstLap = worst,
            LapTimeStdDevSec = stdDev,
            Trend = trend,
            Positives = positives,
            Improvements = improvements,
            Laps = laps,
        };
    }

    /// <summary>Convenience overload: builds a summary for whichever session most recently
    /// ended (EndUtc IS NOT NULL, latest StartUtc). Returns null if no completed session exists.</summary>
    public static SessionSummary? GenerateForMostRecentSession(string? dbPathOverride = null)
    {
        var dbPath = dbPathOverride ?? SessionLogger.GetDefaultDbPath();
        var connectionString = $"Data Source={dbPath}";

        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Sim, StartUtc, EndUtc FROM Sessions
            WHERE EndUtc IS NOT NULL
            ORDER BY StartUtc DESC
            LIMIT 1;
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        var sessionId = reader.GetInt64(0);
        var sim = reader.GetString(1);
        var startUtc = DateTime.Parse(reader.GetString(2)).ToUniversalTime();
        var endUtc = DateTime.Parse(reader.GetString(3)).ToUniversalTime();

        return Generate(connectionString, sessionId, sim, startUtc, endUtc);
    }

    private static List<LapSummary> LoadLaps(string connectionString, long sessionId)
    {
        var laps = new List<LapSummary>();

        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT LapNumber, LapTimeSec, DeltaToBestSec, AvgThrottle, AvgBrake
            FROM Laps WHERE SessionId = $sessionId ORDER BY LapNumber ASC;
            """;
        cmd.Parameters.AddWithValue("$sessionId", sessionId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            laps.Add(new LapSummary
            {
                LapNumber = reader.GetInt32(0),
                LapTimeSec = (float)reader.GetDouble(1),
                DeltaToBestSec = reader.IsDBNull(2) ? null : (float)reader.GetDouble(2),
                AvgThrottle = reader.IsDBNull(3) ? 0f : (float)reader.GetDouble(3),
                AvgBrake = reader.IsDBNull(4) ? 0f : (float)reader.GetDouble(4),
            });
        }

        return laps;
    }

    private static double? ComputeStdDev(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return null;
        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
        return Math.Sqrt(variance);
    }

    /// <summary>First-half vs. second-half average lap time comparison. Simple and explainable
    /// over a proper linear regression for a v1 debrief; a >0.15s swing either way is treated as
    /// a real trend rather than noise.</summary>
    private static LapTimeTrend ComputeTrend(IReadOnlyList<LapSummary> laps)
    {
        if (laps.Count < 4) return LapTimeTrend.Flat; // not enough laps to call a trend meaningfully

        var half = laps.Count / 2;
        var firstHalfAvg = laps.Take(half).Average(l => l.LapTimeSec);
        var secondHalfAvg = laps.Skip(laps.Count - half).Average(l => l.LapTimeSec);
        var delta = secondHalfAvg - firstHalfAvg;

        const float DeadbandSec = 0.15f;
        if (delta < -DeadbandSec) return LapTimeTrend.Improving; // second half faster
        if (delta > DeadbandSec) return LapTimeTrend.Worsening;  // second half slower
        return LapTimeTrend.Flat;
    }

    private static void BuildBullets(
        IReadOnlyList<LapSummary> laps,
        double? stdDev,
        LapTimeTrend trend,
        List<string> positives,
        List<string> improvements)
    {
        if (laps.Count == 0)
        {
            improvements.Add("No completed laps were logged this session.");
            return;
        }

        // --- Trend ---
        switch (trend)
        {
            case LapTimeTrend.Improving:
                positives.Add("Lap times trended faster across the session — the second half was quicker than the first.");
                break;
            case LapTimeTrend.Worsening:
                improvements.Add("Lap times trended slower in the second half of the session — check tire wear or fatigue.");
                break;
            case LapTimeTrend.Flat:
                positives.Add("Lap pace held steady across the session, without a first-half/second-half drop-off.");
                break;
        }

        // --- Consistency (stdDev) ---
        if (stdDev is double sd)
        {
            if (sd < 0.3)
            {
                positives.Add($"Lap times were very consistent this session (std. dev. {sd:F2}s).");
            }
            else if (sd > 1.0)
            {
                improvements.Add($"Lap times varied a lot this session (std. dev. {sd:F2}s) — look for the outlier lap(s) below.");
            }
        }

        // --- Last-3-laps consistency (only meaningful with enough laps) ---
        if (laps.Count >= 6)
        {
            var lastThree = laps.TakeLast(3).Select(l => (double)l.LapTimeSec).ToList();
            var lastThreeStdDev = ComputeStdDev(lastThree);
            if (lastThreeStdDev is double lastSd && stdDev is double overallSd && lastSd < overallSd)
            {
                positives.Add("Your last 3 laps were more consistent than the session average — a strong way to finish.");
            }
        }

        // --- Best/worst lap callout ---
        var best = laps.MinBy(l => l.LapTimeSec)!;
        var worst = laps.MaxBy(l => l.LapTimeSec)!;
        if (laps.Count > 1 && worst.LapNumber != best.LapNumber)
        {
            var gap = worst.LapTimeSec - best.LapTimeSec;
            if (gap > 1.5f)
            {
                improvements.Add($"Lap {worst.LapNumber} was {gap:F2}s slower than your best lap ({best.LapNumber}) — worth reviewing what happened there.");
            }
        }

        if (positives.Count == 0)
        {
            positives.Add("Session completed — keep logging laps to build a consistency baseline.");
        }
    }
}
