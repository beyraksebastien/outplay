using IRSDKSharper;

namespace OutplayOverlay.Telemetry;

/// <summary>
/// Reads iRacing telemetry via shared memory using IRSDKSharper (github.com/mherbold/IRSDKSharper).
/// </summary>
public sealed class IRacingTelemetrySource : ITelemetrySource
{
    private readonly IRacingSdk _sdk = new();
    private bool _connected;

    public event Action<TelemetrySample>? SampleReceived;
    public event Action<bool>? ConnectionChanged;

    public IRacingTelemetrySource()
    {
        _sdk.OnConnected += () =>
        {
            _connected = true;
            ConnectionChanged?.Invoke(true);
        };

        _sdk.OnDisconnected += () =>
        {
            _connected = false;
            ConnectionChanged?.Invoke(false);
        };

        _sdk.OnTelemetryData += OnTelemetryData;
    }

    public void Start() => _sdk.Start();

    public void Stop() => _sdk.Stop();

    private void OnTelemetryData()
    {
        if (!_connected) return;

        try
        {
            var data = _sdk.Data;

            var sample = new TelemetrySample
            {
                Sim = "iRacing",
                TimestampUtc = DateTime.UtcNow,
                Speed = data.GetFloat("Speed") * 3.6f, // m/s -> km/h
                Throttle = data.GetFloat("Throttle"),
                Brake = data.GetFloat("Brake"),
                Steering = data.GetFloat("SteeringWheelAngle"),
                Gear = data.GetInt("Gear"),
                FuelLevel = data.GetFloat("FuelLevel"),
                SlipAngle = data.GetFloat("YawRate"), // approximation; refine with a proper slip-angle calc
                TireTempC = new[]
                {
                    data.GetFloat("LFtempCL"),
                    data.GetFloat("RFtempCL"),
                    data.GetFloat("LRtempCL"),
                    data.GetFloat("RRtempCL"),
                },
                TireWearPct = new[]
                {
                    data.GetFloat("LFwearL"),
                    data.GetFloat("RFwearL"),
                    data.GetFloat("LRwearL"),
                    data.GetFloat("RRwearL"),
                },
                LapDistancePct = data.GetFloat("LapDistPct"),
                CurrentLapTimeSec = data.GetFloat("LapCurrentLapTime"),
                DeltaToBestSec = data.GetFloat("LapDeltaToBest"),
                GapToCarAheadSec = TryComputeGapToCarAhead(_sdk),
                PlayerTireCompound = TryGetPlayerTireCompound(_sdk),
                CarAheadTireCompound = TryGetCarAheadTireCompound(_sdk),
            };

            SampleReceived?.Invoke(sample);
        }
        catch
        {
            // A missing telemetry variable (car/session dependent) shouldn't crash the loop.
        }
    }

    // ---------------------------------------------------------------------
    // UNVERIFIED API SURFACE — see final report for risk ranking.
    //
    // Everything below assumes IRSDKSharper exposes per-car ("CarIdx*") array telemetry
    // variables via an indexed overload: data.GetInt("VarName", carIdx) / data.GetFloat(...).
    // This mirrors how the raw irsdk C API addresses array-valued variables (each iRacing
    // "session" variable has an entryCount, and array members are read by index), but I have
    // NOT confirmed IRSDKSharper 1.1.9 actually surfaces this as an overload rather than e.g.
    // GetIntArray(name) returning int[], or data.GetInt(name)[idx]. If this doesn't compile,
    // the fix is almost certainly just changing the call shape below, not the logic.
    //
    // Also unverified: "PlayerCarIdx" as a live telemetry variable name (it may only be
    // available from session-info YAML, not the per-tick var table) and "CarIdxTireCompound"
    // (iRacing may not broadcast opponents' tire compound at all for most series — this is
    // the single riskiest assumption in this file).
    // ---------------------------------------------------------------------

    private static float? TryComputeGapToCarAhead(IRacingSdk sdk)
    {
        try
        {
            var data = sdk.Data;
            var playerIdx = data.GetInt("PlayerCarIdx"); // ASSUMPTION: see comment block above
            var myEstTime = data.GetFloat("CarIdxEstTime", playerIdx);
            var myPosition = data.GetInt("CarIdxPosition", playerIdx);
            if (myPosition <= 0) return null; // not currently scored/on track

            int? aheadIdx = null;
            var bestGap = int.MaxValue;

            // iRacing supports up to 64 cars per session (CarIdx 0..63).
            for (var i = 0; i < 64; i++)
            {
                if (i == playerIdx) continue;
                var pos = data.GetInt("CarIdxPosition", i);
                if (pos <= 0) continue; // car not in session / not yet classified

                var gapPos = myPosition - pos;
                if (gapPos > 0 && gapPos < bestGap)
                {
                    bestGap = gapPos;
                    aheadIdx = i;
                }
            }

            if (aheadIdx is not int idx) return null;

            var aheadEstTime = data.GetFloat("CarIdxEstTime", idx);

            // CarIdxEstTime is iRacing's estimate of each car's time-to-complete-lap based on
            // current track position; the car further along its lap has the SMALLER value.
            // Gap-to-car-ahead ≈ myEstTime - aheadEstTime. Sign convention is unverified —
            // confirm against live telemetry (should be a small positive number, growing as
            // you fall behind).
            return myEstTime - aheadEstTime;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetPlayerTireCompound(IRacingSdk sdk)
    {
        try
        {
            // ASSUMPTION: "PlayerTireCompound" is not a confirmed iRacing telemetry variable
            // name — iRacing's tire model varies per car/series and compound naming isn't
            // standardized the way F1's C0-C5 slicks are. Surfacing the raw index rather than
            // inventing a compound name, since we can't reliably map index -> tire name without
            // per-car tire-set metadata.
            var compoundIndex = sdk.Data.GetInt("PlayerTireCompound");
            return $"Compound {compoundIndex}";
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetCarAheadTireCompound(IRacingSdk sdk)
    {
        try
        {
            var data = sdk.Data;
            var playerIdx = data.GetInt("PlayerCarIdx");
            var myPosition = data.GetInt("CarIdxPosition", playerIdx);
            if (myPosition <= 0) return null;

            for (var i = 0; i < 64; i++)
            {
                if (i == playerIdx) continue;
                var pos = data.GetInt("CarIdxPosition", i);
                if (pos == myPosition - 1)
                {
                    // ASSUMPTION (high risk): "CarIdxTireCompound" — iRacing very likely does
                    // NOT broadcast opponents' tire compound for most cars/series. This will
                    // probably just throw and fall back to null; left in place in case a
                    // future car/series does expose it, but flagged as the least trustworthy
                    // assumption in this file.
                    var compoundIndex = data.GetInt("CarIdxTireCompound", i);
                    return $"Compound {compoundIndex}";
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _sdk.Stop();
    }
}
