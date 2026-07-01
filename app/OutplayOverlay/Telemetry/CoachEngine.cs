namespace OutplayOverlay.Telemetry;

/// <summary>
/// Push/hold state derived from the trend of DeltaToBestSec over the last ~0.75s.
/// </summary>
public enum PushHoldState
{
    Push,
    Steady,
    BackingOff,
}

/// <summary>
/// One coaching update, emitted per incoming TelemetrySample. This is the only type the UI layer
/// needs to know about — see the accompanying report for field-by-field meaning.
/// </summary>
public sealed record CoachingSignal
{
    public required DateTime TimestampUtc { get; init; }
    public required PushHoldState State { get; init; }

    /// <summary>Current lap's delta to the driver's best lap, in seconds (sim-reported for
    /// iRacing; an approximation vs. best completed lap time for F1 25 — see F125TelemetrySource
    /// remarks). Null if not available yet this session.</summary>
    public float? DeltaToBestSec { get; init; }

    /// <summary>Time gap to the car directly ahead on track, in seconds. Null if unknown (no
    /// car ahead, just started session, telemetry var not available).</summary>
    public float? GapToCarAheadSec { get; init; }

    /// <summary>Player's current tire compound, as a display string (e.g. "C3", "Wet",
    /// "Compound 2" for iRacing where compound naming isn't standardized). Null if unknown.</summary>
    public string? PlayerTireCompound { get; init; }

    /// <summary>Tire compound of the car directly ahead. Null if unknown/no car ahead.</summary>
    public string? CarAheadTireCompound { get; init; }
}

/// <summary>
/// Deterministic, rule-based race coaching signal derived from live TelemetrySample data.
/// No LLM calls, no blocking I/O — this runs on the telemetry hot path (subscribes directly to
/// TelemetryHub.SampleReceived) and must stay cheap; per-sample work here is a handful of float
/// comparisons and a small fixed-size queue, well under the <50ms budget.
///
/// MainWindow (owned by the frontend engineer) should construct this alongside its TelemetryHub
/// and subscribe to SignalReceived — CoachEngine doesn't know about or touch any UI code.
/// </summary>
public sealed class CoachEngine : IDisposable
{
    // How far back we look to compute the delta-to-best trend.
    private const double TrendWindowSec = 0.75;

    // Minimum slope (seconds of delta change per second of real time) required to call it
    // Push/BackingOff instead of Steady. This is an initial guess to avoid flicker on telemetry
    // noise — tune once real Push/Steady/BackingOff transitions are observed on track.
    private const float TrendDeadbandSecPerSec = 0.05f;

    private readonly TelemetryHub _hub;
    private readonly Queue<(DateTime TimeUtc, float Delta)> _deltaHistory = new();

    // OnSample can be invoked concurrently: IRacingTelemetrySource fires on IRSDKSharper's own
    // callback thread, and F125TelemetrySource fires from its UDP ListenLoopAsync thread-pool
    // continuation — both funnel into this same CoachEngine instance via TelemetryHub. This lock
    // guards all reads/mutations of _deltaHistory. Sample volume here is low enough (one Enqueue
    // + a handful of comparisons per sample) that a plain lock is sufficient — no need for a
    // lock-free structure.
    private readonly object _lock = new();

    public event Action<CoachingSignal>? SignalReceived;

    public CoachEngine(TelemetryHub hub)
    {
        _hub = hub;
        _hub.SampleReceived += OnSample;
    }

    private void OnSample(TelemetrySample sample)
    {
        var state = ClassifyPushHold(sample);

        var signal = new CoachingSignal
        {
            TimestampUtc = sample.TimestampUtc,
            State = state,
            DeltaToBestSec = sample.DeltaToBestSec,
            GapToCarAheadSec = sample.GapToCarAheadSec,
            PlayerTireCompound = sample.PlayerTireCompound,
            CarAheadTireCompound = sample.CarAheadTireCompound,
        };

        SignalReceived?.Invoke(signal);
    }

    private PushHoldState ClassifyPushHold(TelemetrySample sample)
    {
        if (sample.DeltaToBestSec is not float delta)
        {
            lock (_lock)
            {
                _deltaHistory.Clear();
            }
            return PushHoldState.Steady;
        }

        DateTime oldestTime;
        float oldestDelta;
        int historyCount;

        lock (_lock)
        {
            _deltaHistory.Enqueue((sample.TimestampUtc, delta));

            while (_deltaHistory.Count > 0 &&
                   (sample.TimestampUtc - _deltaHistory.Peek().TimeUtc).TotalSeconds > TrendWindowSec)
            {
                _deltaHistory.Dequeue();
            }

            historyCount = _deltaHistory.Count;
            if (historyCount >= 2)
            {
                (oldestTime, oldestDelta) = _deltaHistory.Peek();
            }
            else
            {
                oldestTime = default;
                oldestDelta = default;
            }
        }

        if (historyCount < 2) return PushHoldState.Steady;

        var elapsedSec = (sample.TimestampUtc - oldestTime).TotalSeconds;
        if (elapsedSec < 0.1) return PushHoldState.Steady; // not enough time spread to trust the slope

        var slope = (delta - oldestDelta) / (float)elapsedSec;

        // DeltaToBest shrinking over time (getting more negative / less positive) == gaining
        // time on the best lap == push. Growing == losing time == back off.
        if (slope < -TrendDeadbandSecPerSec) return PushHoldState.Push;
        if (slope > TrendDeadbandSecPerSec) return PushHoldState.BackingOff;
        return PushHoldState.Steady;
    }

    public void Dispose()
    {
        _hub.SampleReceived -= OnSample;
    }
}
