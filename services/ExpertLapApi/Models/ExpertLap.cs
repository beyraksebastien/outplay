namespace ExpertLapApi.Models;

/// <summary>
/// One minimal per-sample telemetry point for corner-level comparison coaching. Deliberately NOT
/// the desktop app's full TelemetrySample (see OutplayOverlay/Telemetry/TelemetrySample.cs) — this
/// only carries what CornerIntelligenceEngine's segment-based comparison actually needs to line up
/// an expert lap against the driver's own lap: distance-around-lap plus the four core driver
/// inputs/outcomes. No tire temps, no flags, no opponent context — keeping the wire schema small
/// matters here because a full lap trace at any reasonable sample rate is already a non-trivial
/// payload with no auth to rate-limit by user (see EndpointFilters/UploadRateLimiting notes in
/// Program.cs).
/// </summary>
public sealed class TelemetryTraceSample
{
    /// <summary>0..1 (or 0..100 — pick one and be consistent; this API expects 0..1 to match
    /// TelemetrySample.LapDistancePct's convention in the desktop app, see IRacingTelemetrySource /
    /// F125TelemetrySource) position around the lap.</summary>
    public double DistancePct { get; set; }

    /// <summary>km/h, matching TelemetrySample.Speed's unit convention.</summary>
    public double Speed { get; set; }

    /// <summary>0..1.</summary>
    public double Throttle { get; set; }

    /// <summary>0..1.</summary>
    public double Brake { get; set; }

    /// <summary>-1..1, matching TelemetrySample.Steering's convention.</summary>
    public double Steering { get; set; }
}

/// <summary>
/// One uploaded fast lap's full telemetry trace for a specific sim+track+car combo.
///
/// STORAGE DECISION (v1, documented, deliberately simple): only the current fastest lap per
/// sim+track+car combo is retained as the served "best" lap. A slower upload for a combo that
/// already has a faster lap is still accepted and stored (so a submitter isn't confusingly
/// rejected and the row exists for future auditing), but it is marked IsCurrentBest = false and
/// is never returned by GET /api/laps/best. This avoids building a "keep N best per combo" or full
/// history/versioning system for v1 — see the API report for the explicit list of what's NOT
/// built (no moderation, no lap history endpoint, no accounts).
/// </summary>
public sealed class ExpertLap
{
    public Guid Id { get; set; }

    public required string Sim { get; set; }       // "iRacing" | "F1_25" — free text in v1, not an enum/FK,
                                                     // to avoid coupling this service to the desktop app's
                                                     // sim naming decisions; matched by exact string.
    public required string Track { get; set; }
    public required string Car { get; set; }

    public required double LapTimeSec { get; set; }

    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Free-text optional nickname — there is no account/identity system in this v1
    /// ("open, no accounts" per the task brief), so this is purely cosmetic display text, never
    /// used for auth/ownership/rate-limiting.</summary>
    public string? SubmitterLabel { get; set; }

    /// <summary>The per-sample telemetry trace, serialized as a JSON array and stored as a plain
    /// `text` column (not Postgres `jsonb` — no jsonb-specific querying is configured or used) —
    /// see TelemetryTraceSample. Stored as raw JSON text rather than a normalized child table: a
    /// lap trace is read/written as one atomic blob, never queried sample-by-sample server-side,
    /// so a relational per-sample table would add join/write overhead for no query benefit in v1.</summary>
    public required string TelemetryTraceJson { get; set; }

    /// <summary>True only for the single current-fastest row per (Sim, Track, Car) — see the class
    /// doc comment's storage decision. Indexed alongside (Sim, Track, Car) so
    /// GET /api/laps/best's lookup is a simple indexed equality query.</summary>
    public bool IsCurrentBest { get; set; }
}
