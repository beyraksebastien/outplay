using System.Text.RegularExpressions;
using IRSDKSharper;

namespace OutplayOverlay.Telemetry;

/// <summary>
/// Reads iRacing telemetry via shared memory using IRSDKSharper (github.com/mherbold/IRSDKSharper).
/// </summary>
public sealed class IRacingTelemetrySource : ITelemetrySource
{
    private readonly IRacingSdk _sdk = new();
    private bool _connected;

    // --- Track name (session-info YAML), see TryParseTrackNameFromSessionInfo below. ---
    // Parsed once per session (the YAML's WeekendInfo doesn't change mid-session) and cached
    // here; every telemetry tick just reads this field, so there is no per-tick YAML parsing
    // on the hot path.
    private string? _trackName;

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
            _trackName = null; // new connection = possibly a new session/track; re-parse next time
            ConnectionChanged?.Invoke(false);
        };

        // UNVERIFIED, MEDIUM RISK: IRSDKSharper 1.1.9 is assumed to expose an "OnSessionInfo"
        // event fired whenever the session-info YAML block updates (mirrors the raw irsdk C API,
        // which distinguishes the per-tick telemetry var buffer from the much-less-frequently
        // updated session-info string). If this event name/signature is wrong, this is almost
        // certainly a compile error localized to this one line — the fix is changing the event
        // name to whatever IRSDKSharper actually exposes (e.g. it may instead only be readable by
        // polling _sdk.Data.SessionInfoUpdateReady from inside OnTelemetryData). Track name is a
        // "nice to have, not hot-path-critical" field, so if this doesn't compile the safest quick
        // fix is deleting this subscription and instead attempting the parse from inside
        // OnTelemetryData, gated by a bool/counter so it only runs until it first succeeds.
        _sdk.OnSessionInfo += TryParseTrackNameFromSessionInfo;

        _sdk.OnTelemetryData += OnTelemetryData;
    }

    /// <summary>
    /// Extracts WeekendInfo:TrackDisplayName (falling back to TrackName) from the session-info
    /// YAML string. UNVERIFIED, MEDIUM-HIGH RISK, several layered assumptions:
    ///   1. That IRSDKSharper exposes the raw YAML as a string via `_sdk.Data.SessionInfoYaml`
    ///      (or similarly named property) rather than only a pre-parsed object graph. If the real
    ///      property name differs, this needs a one-line fix once verified against the installed
    ///      package version.
    ///   2. That the YAML actually contains a `WeekendInfo:` section with a `TrackDisplayName:`
    ///      field, per the publicly documented irsdk session-info schema (this part is
    ///      well-established across iRacing SDK tooling and is the lowest-risk assumption here).
    ///   3. Regex-based extraction (rather than a full YAML parser) is used deliberately: we only
    ///      need one scalar field out of a large YAML document, and pulling in a YAML parsing
    ///      dependency (or trusting IRSDKSharper's own YAML query API, whose exact method surface
    ///      is unverified) for a single string is more risk than it's worth for v1. This regex
    ///      assumes the field appears as `TrackDisplayName:"Some Track Name"` or
    ///      `TrackDisplayName: Some Track Name` on its own line, which matches every publicly
    ///      documented irsdk session-info sample seen in the wild, but has not been checked
    ///      against a live capture from this exact game build.
    /// Only runs once per connection (guarded by `_trackName is null`) — the session-info YAML is
    /// static for the life of a session, so there is no need to re-parse on every update.
    /// </summary>
    private void TryParseTrackNameFromSessionInfo()
    {
        if (_trackName is not null) return;

        try
        {
            var yaml = _sdk.Data.SessionInfoYaml; // ASSUMPTION: property name/shape, see doc comment above
            if (string.IsNullOrEmpty(yaml)) return;

            var match = Regex.Match(yaml, @"TrackDisplayName:\s*""?([^""\r\n]+)""?");
            if (!match.Success)
            {
                // Fall back to TrackName (shorter/less display-friendly, but more likely present
                // if TrackDisplayName is ever absent for some car/track combo).
                match = Regex.Match(yaml, @"TrackName:\s*""?([^""\r\n]+)""?");
            }

            if (match.Success)
            {
                _trackName = match.Groups[1].Value.Trim();
            }
        }
        catch
        {
            // Missing/renamed property, malformed YAML, whatever — track name is a nice-to-have,
            // never let this take down the telemetry connection.
        }
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
                TrackFlag = TryGetTrackFlag(_sdk),
                TrackName = _trackName,
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

    // --- Track flag (FEATURE 2) ---
    // UNVERIFIED, HIGH RISK: bit values transcribed from the public irsdk C header
    // (irsdk_Flags enum), NOT independently confirmed against IRSDKSharper 1.1.9's actual runtime
    // values or a live iRacing session — same caution level as every other iRacing variable-name
    // guess in this file. If GetInt("SessionFlags") throws (wrong variable name) this falls back
    // to null via the try/catch below, same as the other Try* helpers.
    private const int FlagGreen = 0x00000004;
    private const int FlagYellow = 0x00000008;
    private const int FlagYellowWaving = 0x00000100;
    private const int FlagCaution = 0x00004000;
    private const int FlagCautionWaving = 0x00008000;
    private const int FlagRed = 0x00000010;
    private const int FlagChequered = 0x00000001;
    private const int FlagWhite = 0x00000002;

    /// <summary>
    /// SessionFlags is a bitfield — multiple bits can be set at once (e.g. yellow + yellowWaving
    /// together during a caution period). Priority order below picks the single most "urgent" one
    /// to surface for an announcement, most urgent first:
    ///   Red (full-course stop, highest urgency) > Caution/CautionWaving (slow down, hold
    ///   position) > Yellow/YellowWaving (local caution) > White (final lap / slow vehicle
    ///   warning) > Chequered (session ending — informational, no immediate driving hazard) >
    ///   Green (all-clear / racing resumed).
    /// This ordering is a judgment call (documented per the task brief), not verified against any
    /// iRacing UX convention — flagged as a risk to revisit once tested against a live session
    /// with a real caution period.
    /// </summary>
    private static TrackFlag? TryGetTrackFlag(IRacingSdk sdk)
    {
        try
        {
            var bits = sdk.Data.GetInt("SessionFlags");

            if ((bits & FlagRed) != 0) return TrackFlag.Red;
            if ((bits & (FlagCaution | FlagCautionWaving)) != 0) return TrackFlag.Caution;
            if ((bits & (FlagYellow | FlagYellowWaving)) != 0) return TrackFlag.Yellow;
            if ((bits & FlagWhite) != 0) return TrackFlag.White;
            if ((bits & FlagChequered) != 0) return TrackFlag.Checkered;
            if ((bits & FlagGreen) != 0) return TrackFlag.Green;
            return TrackFlag.Unknown;
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
