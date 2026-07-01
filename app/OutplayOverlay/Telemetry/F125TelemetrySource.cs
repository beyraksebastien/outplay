using System.IO;
using System.Net;
using System.Net.Sockets;

namespace OutplayOverlay.Telemetry;

/// <summary>
/// Reads F1 25 telemetry via the game's UDP broadcast (Settings > Telemetry Settings > UDP Telemetry: On).
/// Default port 20777. Parses PacketHeader + PacketCarTelemetryData (packetId == 6) only for this first build —
/// lap time/delta (packetId 2, Lap Data) and session/status packets are follow-up work (PRD §14.6: re-validate
/// the exact packet layout against the shipped F1 25 build before relying on this in production, since EA/
/// Codemasters have changed byte layouts between seasons).
/// </summary>
public sealed class F125TelemetrySource : ITelemetrySource
{
    private const int Port = 20777;
    private const int HeaderSize = 29;
    private const int CarTelemetrySize = 60;
    private const byte CarTelemetryPacketId = 6;

    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private bool _connected;

    public event Action<TelemetrySample>? SampleReceived;
    public event Action<bool>? ConnectionChanged;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _client = new UdpClient(Port);
        _ = ListenLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _client?.Close();
        SetConnected(false);
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        var lastPacketUtc = DateTime.MinValue;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await _client!.ReceiveAsync(token);
                lastPacketUtc = DateTime.UtcNow;
                SetConnected(true);
                TryParsePacket(result.Buffer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Malformed/partial UDP packet — drop it, keep listening.
            }

            // No packets for >2s while the socket is open usually means the game
            // isn't running or telemetry UDP isn't enabled in-game.
            if (DateTime.UtcNow - lastPacketUtc > TimeSpan.FromSeconds(2) && _connected)
            {
                SetConnected(false);
            }
        }
    }

    private void SetConnected(bool connected)
    {
        if (_connected == connected) return;
        _connected = connected;
        ConnectionChanged?.Invoke(connected);
    }

    private void TryParsePacket(byte[] buffer)
    {
        if (buffer.Length < HeaderSize) return;

        using var ms = new MemoryStream(buffer);
        using var reader = new BinaryReader(ms);

        reader.ReadUInt16();           // packetFormat
        reader.ReadByte();             // gameYear
        reader.ReadByte();             // gameMajorVersion
        reader.ReadByte();             // gameMinorVersion
        reader.ReadByte();             // packetVersion
        var packetId = reader.ReadByte();
        reader.ReadUInt64();           // sessionUID
        reader.ReadSingle();           // sessionTime
        reader.ReadUInt32();           // frameIdentifier
        reader.ReadUInt32();           // overallFrameIdentifier
        var playerCarIndex = reader.ReadByte();
        reader.ReadByte();             // secondaryPlayerCarIndex

        if (packetId != CarTelemetryPacketId) return;

        var playerOffset = HeaderSize + playerCarIndex * CarTelemetrySize;
        if (buffer.Length < playerOffset + CarTelemetrySize) return;

        ms.Position = playerOffset;

        var speed = reader.ReadUInt16();          // km/h
        var throttle = reader.ReadSingle();       // 0..1
        var steer = reader.ReadSingle();           // -1..1
        var brake = reader.ReadSingle();           // 0..1
        reader.ReadByte();                         // clutch
        var gear = reader.ReadSByte();              // -1 = reverse, 0 = neutral
        reader.ReadUInt16();                        // engineRPM
        reader.ReadByte();                          // drs
        reader.ReadByte();                          // revLightsPercent
        reader.ReadUInt16();                        // revLightsBitValue
        reader.ReadUInt16(); reader.ReadUInt16(); reader.ReadUInt16(); reader.ReadUInt16(); // brake temps
        var tyreSurfaceTemp = new float[4];
        for (var i = 0; i < 4; i++) tyreSurfaceTemp[i] = reader.ReadByte();
        reader.ReadByte(); reader.ReadByte(); reader.ReadByte(); reader.ReadByte(); // tyre inner temps

        var sample = new TelemetrySample
        {
            Sim = "F1_25",
            TimestampUtc = DateTime.UtcNow,
            Speed = speed,
            Throttle = throttle,
            Brake = brake,
            Steering = steer,
            Gear = gear,
            FuelLevel = 0, // requires Car Status packet (id 7) — not parsed in this first build
            SlipAngle = null, // not exposed by F1 25 telemetry — PRD §13.2
            TireTempC = tyreSurfaceTemp,
            TireWearPct = null, // requires Car Damage packet (id 10) — follow-up work
            LapDistancePct = null, // requires Lap Data packet (id 2) — follow-up work
            CurrentLapTimeSec = null,
            DeltaToBestSec = null,
        };

        SampleReceived?.Invoke(sample);
    }

    public void Dispose()
    {
        Stop();
        _client?.Dispose();
        _cts?.Dispose();
    }
}
