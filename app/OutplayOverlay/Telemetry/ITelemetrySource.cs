namespace OutplayOverlay.Telemetry;

public interface ITelemetrySource : IDisposable
{
    event Action<TelemetrySample>? SampleReceived;
    event Action<bool>? ConnectionChanged; // true = connected to sim

    void Start();
    void Stop();
}
