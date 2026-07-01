using irsdkSharper;

namespace OutplayOverlay.Telemetry;

/// <summary>
/// Reads iRacing telemetry via shared memory using IRSDKSharper.
/// NOTE: verify exact member names (GetFloat/GetFloatArray) against the installed
/// IRSDKSharper version's API before first build — wrapper APIs shift between releases.
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
            };

            SampleReceived?.Invoke(sample);
        }
        catch
        {
            // A missing telemetry variable (car/session dependent) shouldn't crash the loop.
        }
    }

    public void Dispose()
    {
        _sdk.Stop();
    }
}
