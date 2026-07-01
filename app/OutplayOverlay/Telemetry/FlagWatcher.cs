namespace OutplayOverlay.Telemetry;

/// <summary>
/// Normalized track flag state, common across sims. Ordered roughly by announcement urgency
/// (see FlagWatcher's priority-order remarks / the report for why Red/Caution/Yellow outrank
/// White/Green/Checkered) — the numeric order here is NOT relied on by any comparison in this
/// file, it's just documentation-by-placement.
/// </summary>
public enum TrackFlag
{
    Unknown,
    Green,
    White,
    Yellow,
    Caution,
    Red,
    Checkered,
}

/// <summary>
/// One flag-state transition. This is the only type the UI/voice layer needs to know about to
/// wire up an announcement — mirrors CoachingSignal's "one record, consumer doesn't touch engine
/// internals" shape.
/// </summary>
public sealed record TrackFlagChanged
{
    public required DateTime TimestampUtc { get; init; }
    public required TrackFlag Flag { get; init; }

    /// <summary>The flag state immediately before this transition. Null on the very first
    /// non-Unknown flag observed this session (no prior state to compare against).</summary>
    public TrackFlag? PreviousFlag { get; init; }

    /// <summary>Which sim this reading came from ("iRacing" | "F1_25"), in case the UI wants to
    /// vary phrasing (mirrors TelemetrySample.Sim).</summary>
    public required string Sim { get; init; }
}

/// <summary>
/// Watches TelemetryHub.SampleReceived for track-flag transitions (green/yellow/red/etc.) and
/// raises FlagChanged only when the derived state actually changes — same "only announce
/// transitions" principle already established for CoachEngine's Push/Steady/BackingOff voice
/// callouts. Deliberately does NOT do any debouncing of its own: this class reports every genuine
/// transition immediately; the existing debounce-before-speaking window (SpokenStateDebounce in
/// MainWindow.xaml.cs) is where transient/flickery readings get filtered before they reach TTS,
/// and that's where flag-change debouncing should live too, to reuse the exact same
/// battle-tested pattern rather than duplicating debounce logic here.
///
/// No LLM calls, no blocking I/O: this subscribes directly to the hot-path SampleReceived event,
/// but per-sample work is just an equality check against the last-seen enum value plus event
/// dispatch, no per-sample allocation beyond one small record on an actual transition (not every
/// tick) — well under the telemetry hot-path budget.
/// </summary>
public sealed class FlagWatcher : IDisposable
{
    private readonly TelemetryHub _hub;
    private TrackFlag? _lastFlag;

    public event Action<TrackFlagChanged>? FlagChanged;

    public FlagWatcher(TelemetryHub hub)
    {
        _hub = hub;
        _hub.SampleReceived += OnSample;
    }

    private void OnSample(TelemetrySample sample)
    {
        // Null TrackFlag means "not available this tick" (e.g. F1 25 hasn't received a Session
        // packet yet, or the game briefly reported no valid marshal zones) — not a real
        // transition to Unknown. Skipping these ticks keeps a single missed packet from firing a
        // spurious announce-and-revert, while still catching genuine flag changes on the next
        // tick that does carry a value.
        if (sample.TrackFlag is not TrackFlag flag) return;
        if (_lastFlag == flag) return;

        var previous = _lastFlag;
        _lastFlag = flag;

        FlagChanged?.Invoke(new TrackFlagChanged
        {
            TimestampUtc = sample.TimestampUtc,
            Flag = flag,
            PreviousFlag = previous,
            Sim = sample.Sim,
        });
    }

    public void Dispose()
    {
        _hub.SampleReceived -= OnSample;
    }
}
