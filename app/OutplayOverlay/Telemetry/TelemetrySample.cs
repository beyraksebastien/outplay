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
}
