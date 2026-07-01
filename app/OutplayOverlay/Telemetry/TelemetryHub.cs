namespace OutplayOverlay.Telemetry;

/// <summary>
/// Owns both sim adapters. Only one sim is realistically running at a time,
/// so the hub just forwards whichever source is currently emitting samples.
/// </summary>
public sealed class TelemetryHub : IDisposable
{
    private readonly List<ITelemetrySource> _sources;

    public event Action<TelemetrySample>? SampleReceived;
    public event Action<string, bool>? SourceConnectionChanged; // (simName, connected)

    public TelemetryHub()
    {
        _sources = new List<ITelemetrySource>
        {
            new IRacingTelemetrySource(),
            new F125TelemetrySource(),
        };

        foreach (var source in _sources)
        {
            source.SampleReceived += s => SampleReceived?.Invoke(s);
            source.ConnectionChanged += connected =>
            {
                var name = source is IRacingTelemetrySource ? "iRacing" : "F1_25";
                SourceConnectionChanged?.Invoke(name, connected);
            };
        }
    }

    public void StartAll()
    {
        foreach (var source in _sources) source.Start();
    }

    public void Dispose()
    {
        foreach (var source in _sources) source.Dispose();
    }
}
