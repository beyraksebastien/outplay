namespace OutplayOverlay.Telemetry;

/// <summary>
/// Aggregate stats + time-loss/gain estimate for one distance-based lap segment. Same instance
/// shape is used both for "this segment, still being driven" (running, partial averages) and for
/// a fully-completed segment in a finished lap — callers distinguish via which event they got it
/// from (see CornerIntelligenceEngine.LiveSegmentUpdated vs. LapCompleted).
/// </summary>
public sealed record SegmentStats
{
    /// <summary>0-based index into the lap's N equal-length distance segments.</summary>
    public required int SegmentIndex { get; init; }

    public float AvgSpeed { get; init; }
    public float AvgBrake { get; init; }
    public float AvgThrottle { get; init; }
    public int SampleCount { get; init; }

    /// <summary>Estimated time lost (positive) or gained (negative) vs. the best lap over this
    /// segment, in seconds. Computed as (DeltaToBest at segment exit) - (DeltaToBest at segment
    /// entry) — i.e. how much the gap to best grew/shrank while driving this segment. Null if
    /// DeltaToBestSec wasn't available at either endpoint (session just started, no best lap yet,
    /// sim doesn't report it this tick).
    ///
    /// GRANULARITY CAVEAT (flagged, not hidden): this is only as fine-grained as the underlying
    /// DeltaToBestSec signal. iRacing's LapDeltaToBest updates continuously, so this is a
    /// meaningful per-segment estimate. F1 25's DeltaToBestSec (see F125TelemetrySource, Feature 1
    /// of this same change) only updates ONCE PER LAP at the lap boundary and holds flat
    /// otherwise — so for F1 25, TimeLossSec will read ~0 for every segment except whichever one
    /// happens to contain the lap-boundary tick, where the entire lap's delta swing lands. This is
    /// an honest reflection of a real telemetry-richness gap between the two sims, not a bug in
    /// this engine — do not "fix" it here by fabricating interpolated sub-lap values for F1 25.
    /// </summary>
    public float? TimeLossSec { get; init; }
}

/// <summary>Which segment is currently being driven, and its running (partial) stats so far.</summary>
public sealed record LiveSegmentStatus
{
    public required int LapNumber { get; init; }
    public required SegmentStats Current { get; init; }
}

/// <summary>One completed lap's full set of per-segment stats, in segment order.</summary>
public sealed record LapSegmentReport
{
    public required int LapNumber { get; init; }
    public required IReadOnlyList<SegmentStats> Segments { get; init; }
}

/// <summary>
/// Session-level aggregate across all completed laps, for a post-session summary ("you lost the
/// most time in segment 7"). Segment stats here are averaged across every lap that had data for
/// that segment index.
/// </summary>
public sealed record SessionCornerReport
{
    public required IReadOnlyList<LapSegmentReport> Laps { get; init; }
    public required IReadOnlyList<SegmentStats> AggregatedBySegment { get; init; }

    /// <summary>Segment index with the highest average TimeLossSec, or null if no segment has a
    /// non-null TimeLossSec anywhere in the session (e.g. no best lap exists yet, or DeltaToBest
    /// was never available).</summary>
    public int? WorstSegmentIndex { get; init; }
}

/// <summary>
/// Corner Intelligence v1: distance-based auto-segmentation (PRD §5.2), not manual per-track
/// corner maps. Divides each lap into <see cref="SegmentCount"/> equal-length segments by
/// LapDistancePct (0..1) — works for ANY track with no authored per-track data, at the cost of
/// segment boundaries not lining up exactly with actual corner apexes/exits. That trade-off is the
/// whole point of choosing this over manual maps for v1: it ships day one for every track.
///
/// SEGMENT COUNT: 12. Chosen as a middle ground — enough granularity to be more useful than "one
/// score for the whole lap" and roughly in the range of how many corners a typical circuit has
/// (most real tracks have somewhere around 10-20), without being so fine that a single lap's worth
/// of samples per segment gets too sparse to average meaningfully at typical sample rates. This is
/// a judgment call with no telemetry-derived justification — a future version could make it
/// track-length-aware or corner-count-aware once real corner data exists.
///
/// HOT-PATH DISCIPLINE: this class subscribes directly to TelemetryHub.SampleReceived, same as
/// CoachEngine. Per-sample work is a handful of float accumulations and comparisons against a
/// small in-memory list — no SQLite, no disk I/O, no LLM calls. It does NOT persist anything
/// itself; SessionSummaryGenerator/SessionLogger's existing background-writer pattern is the model
/// to follow if/when session-level corner data needs to be persisted (deliberately out of scope
/// here — see the report's follow-up note).
///
/// LAP BOUNDARY DETECTION: reuses the same heuristic as SessionLogger (LapDistancePct wrapping
/// from ~1 back to ~0, rather than watching CurrentLapTimeSec) since LapDistancePct is exactly the
/// signal this class already needs per-sample. A large negative jump in LapDistancePct (current
/// pct less than the previous sample's pct by more than half a lap) is treated as a new lap
/// starting. This is a heuristic, not a sim-confirmed lap-complete event — same caveat SessionLogger
/// already carries for its own (different) heuristic.
/// </summary>
public sealed class CornerIntelligenceEngine : IDisposable
{
    public const int SegmentCount = 12;

    private readonly TelemetryHub _hub;
    private readonly object _lock = new();

    private int _lapNumber;
    private float? _lastLapDistancePct;
    private int _currentSegmentIndex;

    private float _speedSum;
    private float _brakeSum;
    private float _throttleSum;
    private int _sampleCount;
    private float? _entryDeltaToBestSec;
    private float? _lastDeltaToBestSec;

    private readonly List<SegmentStats> _currentLapSegments = new();
    private readonly List<LapSegmentReport> _completedLaps = new();

    /// <summary>Fires on every sample once LapDistancePct is available (so, only for sims/adapters
    /// that populate it — currently iRacing always, F1 25 once a Session packet with a valid
    /// trackLength has arrived; see F125TelemetrySource's LapDistancePct wiring). Fires on the
    /// telemetry thread — same threading contract as CoachEngine.SignalReceived.</summary>
    public event Action<LiveSegmentStatus>? LiveSegmentUpdated;

    /// <summary>Fires once a lap boundary is detected, with the full segment breakdown for the
    /// lap that just finished.</summary>
    public event Action<LapSegmentReport>? LapCompleted;

    public CornerIntelligenceEngine(TelemetryHub hub)
    {
        _hub = hub;
        _hub.SourceConnectionChanged += OnConnectionChanged;
        _hub.SampleReceived += OnSample;
    }

    /// <summary>
    /// Mirrors SessionLogger.OnConnectionChanged / StartSessionLocked's reset-on-CONNECT timing:
    /// SessionLogger itself starts a brand-new logical session (and resets ITS OWN lap
    /// accumulator via ResetLapAccumulator) the instant SourceConnectionChanged fires with
    /// connected == true — not on the first sample after that. So "connect" IS the session-start
    /// boundary both classes agree on; resetting our own per-session state (_completedLaps and
    /// the in-progress lap/segment accumulator) at the same instant keeps the two 1:1 aligned.
    ///
    /// Reset-on-connect (not reset-on-disconnect) is deliberate: BuildSessionReport() is only
    /// ever called from MainWindow's OnSummaryReady handler, which fires from SessionLogger's
    /// SummaryReady event — itself only raised after SessionLogger's EndSessionJob has been
    /// durably written, i.e. strictly AFTER disconnect. If this class reset on disconnect
    /// instead, there would be a race between "we cleared _completedLaps for the ending
    /// session" and "MainWindow hasn't yet read the report for that same session" — reset-on-
    /// disconnect risks wiping the very data BuildSessionReport() is about to be asked for.
    /// Reset-on-connect has no such window: _completedLaps holds session N's laps from the
    /// moment session N starts until session N+1 starts, and OnSummaryReady for session N always
    /// fires while we're still in that window (it fires at N's end, strictly before N+1's
    /// connect can happen). So by the time BuildSessionReport() runs, it only ever contains
    /// exactly session N's laps — never blended with N-1, never prematurely emptied for N+1.
    /// </summary>
    private void OnConnectionChanged(string simName, bool connected)
    {
        if (!connected) return;

        lock (_lock)
        {
            _completedLaps.Clear();
            _currentLapSegments.Clear();
            ResetSegmentAccumulatorLocked();
            _lapNumber = 0;
            _lastLapDistancePct = null;
            _currentSegmentIndex = 0;
            _entryDeltaToBestSec = null;
            _lastDeltaToBestSec = null;
        }
    }

    /// <summary>
    /// Explicit reset hook, in case a caller ever needs to force a fresh session boundary outside
    /// of the normal SourceConnectionChanged path (e.g. a future "discard this session" UI
    /// action). Same effect as the connect-triggered reset in <see cref="OnConnectionChanged"/>;
    /// exposed publicly because that one is private and event-driven only.
    /// </summary>
    public void ResetSession()
    {
        lock (_lock)
        {
            _completedLaps.Clear();
            _currentLapSegments.Clear();
            ResetSegmentAccumulatorLocked();
            _lapNumber = 0;
            _lastLapDistancePct = null;
            _currentSegmentIndex = 0;
            _entryDeltaToBestSec = null;
            _lastDeltaToBestSec = null;
        }
    }

    private void OnSample(TelemetrySample sample)
    {
        if (sample.LapDistancePct is not float pct)
        {
            // No distance signal this tick (e.g. F1 25 before its first Session packet arrives).
            // Nothing to segment against — skip, don't guess.
            return;
        }

        LiveSegmentStatus? liveUpdate = null;
        LapSegmentReport? completedLap = null;

        lock (_lock)
        {
            // Lap-boundary heuristic: a large backward jump (more than half a lap) means we
            // wrapped from ~1.0 back to ~0.0 — a new lap started.
            if (_lastLapDistancePct is float prevPct && pct < prevPct - 0.5f)
            {
                FinalizeCurrentSegmentLocked();
                completedLap = new LapSegmentReport
                {
                    LapNumber = _lapNumber,
                    Segments = _currentLapSegments.ToList(),
                };
                _completedLaps.Add(completedLap);

                _lapNumber++;
                _currentLapSegments.Clear();
                ResetSegmentAccumulatorLocked();
                _currentSegmentIndex = 0;
                _entryDeltaToBestSec = sample.DeltaToBestSec;
            }

            var targetSegment = Math.Clamp((int)(pct * SegmentCount), 0, SegmentCount - 1);
            if (targetSegment != _currentSegmentIndex)
            {
                FinalizeCurrentSegmentLocked();
                _currentSegmentIndex = targetSegment;
                ResetSegmentAccumulatorLocked();
                _entryDeltaToBestSec = sample.DeltaToBestSec;
            }

            _speedSum += sample.Speed;
            _brakeSum += sample.Brake;
            _throttleSum += sample.Throttle;
            _sampleCount++;
            if (sample.DeltaToBestSec is float d) _lastDeltaToBestSec = d;

            _lastLapDistancePct = pct;

            liveUpdate = new LiveSegmentStatus
            {
                LapNumber = _lapNumber,
                Current = BuildRunningStatsLocked(),
            };
        }

        if (liveUpdate is not null) LiveSegmentUpdated?.Invoke(liveUpdate);
        if (completedLap is not null) LapCompleted?.Invoke(completedLap);
    }

    /// <summary>Must be called while holding <see cref="_lock"/>. Pushes the just-finished
    /// segment's averaged stats onto the current lap's segment list, if any samples were
    /// accumulated for it.</summary>
    private void FinalizeCurrentSegmentLocked()
    {
        if (_sampleCount == 0) return;

        _currentLapSegments.Add(new SegmentStats
        {
            SegmentIndex = _currentSegmentIndex,
            AvgSpeed = _speedSum / _sampleCount,
            AvgBrake = _brakeSum / _sampleCount,
            AvgThrottle = _throttleSum / _sampleCount,
            SampleCount = _sampleCount,
            TimeLossSec = (_entryDeltaToBestSec is float entry && _lastDeltaToBestSec is float exit)
                ? exit - entry
                : null,
        });
    }

    private void ResetSegmentAccumulatorLocked()
    {
        _speedSum = 0;
        _brakeSum = 0;
        _throttleSum = 0;
        _sampleCount = 0;
    }

    private SegmentStats BuildRunningStatsLocked()
    {
        var count = Math.Max(_sampleCount, 1);
        return new SegmentStats
        {
            SegmentIndex = _currentSegmentIndex,
            AvgSpeed = _speedSum / count,
            AvgBrake = _brakeSum / count,
            AvgThrottle = _throttleSum / count,
            SampleCount = _sampleCount,
            TimeLossSec = (_entryDeltaToBestSec is float entry && _lastDeltaToBestSec is float latest)
                ? latest - entry
                : null,
        };
    }

    /// <summary>
    /// Builds a session-level report from every lap completed so far (does not include the
    /// in-progress lap). Intended to be called at session-end time (mirrors
    /// SessionSummaryGenerator.Generate's "called once, not per-sample" usage), e.g. from the same
    /// place MainWindow currently calls SessionSummaryGenerator, so a future SessionSummary could
    /// be extended with corner-level bullets. This is pure in-memory aggregation, cheap enough to
    /// call synchronously.
    /// </summary>
    public SessionCornerReport BuildSessionReport()
    {
        List<LapSegmentReport> laps;
        lock (_lock)
        {
            laps = _completedLaps.ToList();
        }

        var bySegment = new List<SegmentStats>();
        for (var i = 0; i < SegmentCount; i++)
        {
            var segmentIndex = i;
            var samples = laps.SelectMany(l => l.Segments).Where(s => s.SegmentIndex == segmentIndex).ToList();
            if (samples.Count == 0) continue;

            var lossValues = samples.Where(s => s.TimeLossSec is not null).Select(s => s.TimeLossSec!.Value).ToList();

            bySegment.Add(new SegmentStats
            {
                SegmentIndex = segmentIndex,
                AvgSpeed = samples.Average(s => s.AvgSpeed),
                AvgBrake = samples.Average(s => s.AvgBrake),
                AvgThrottle = samples.Average(s => s.AvgThrottle),
                SampleCount = samples.Sum(s => s.SampleCount),
                TimeLossSec = lossValues.Count > 0 ? lossValues.Average() : null,
            });
        }

        var worst = bySegment.Where(s => s.TimeLossSec is not null)
            .OrderByDescending(s => s.TimeLossSec)
            .FirstOrDefault();

        return new SessionCornerReport
        {
            Laps = laps,
            AggregatedBySegment = bySegment,
            WorstSegmentIndex = worst?.SegmentIndex,
        };
    }

    public void Dispose()
    {
        _hub.SampleReceived -= OnSample;
        _hub.SourceConnectionChanged -= OnConnectionChanged;
    }
}
