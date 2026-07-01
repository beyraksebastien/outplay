namespace OutplayOverlay.Telemetry;

/// <summary>
/// Common normalized schema across sims (matches TelemetrySample in the PRD data model).
/// F1 25 does not populate SlipAngle or per-tire ThermalTemp — see PRD §13.2.
/// </summary>
public sealed record TelemetrySample
{
    public required string Sim { get; init; } // "iRacing" | "F1_25"
    public required DateTime TimestampUtc { get; init; }

    public float Speed { get; init; }        // km/h
    public float Throttle { get; init; }      // 0..1
    public float Brake { get; init; }         // 0..1
    public float Steering { get; init; }      // -1..1
    public int Gear { get; init; }
    public float FuelLevel { get; init; }     // liters (or normalized if sim doesn't report absolute)

    public float? SlipAngle { get; init; }    // iRacing only
    public float[]? TireTempC { get; init; }  // iRacing only, [FL, FR, RL, RR]
    public float[]? TireWearPct { get; init; } // both sims, [FL, FR, RL, RR]

    public float? LapDistancePct { get; init; } // 0..1 around current lap
    public float? CurrentLapTimeSec { get; init; }
    public float? DeltaToBestSec { get; init; }

    // --- Coaching-support fields (opponent/tire context) ---
    // Populated at the adapter boundary so CoachEngine and scoring logic never need to know
    // sim-specific quirks. Null means "not available this tick" (session just started, var
    // not exposed by this sim/car, no car ahead on track, etc.) — consumers must treat these
    // as optional.
    public float? GapToCarAheadSec { get; init; }       // both sims, seconds, positive = car ahead is that far ahead
    public string? PlayerTireCompound { get; init; }    // both sims
    public string? CarAheadTireCompound { get; init; }  // both sims

    /// <summary>Current track flag state, derived at the adapter boundary from each sim's
    /// sim-specific representation (iRacing's SessionFlags bitfield; F1 25's per-marshal-zone
    /// m_zoneFlag array) — see FlagWatcher.cs for the TrackFlag enum and IRacingTelemetrySource /
    /// F125TelemetrySource for the derivation. Null means "not available this tick" (e.g. no
    /// Session packet received yet for F1 25), not "green" — consumers must not assume a default.</summary>
    public TrackFlag? TrackFlag { get; init; }
}
