using System.IO;
using System.Net;
using System.Net.Sockets;

namespace OutplayOverlay.Telemetry;

/// <summary>
/// Reads F1 25 telemetry via the game's UDP broadcast (Settings > Telemetry Settings > UDP Telemetry: On,
/// UDP Format: 2024 — the 2025 default format is not implemented here). Default port 20777.
/// Parses PacketCarTelemetryData (id 6), PacketLapData (id 2), and PacketCarStatusData (id 7).
///
/// PRD §14.6: re-validate the exact packet layout against the shipped F1 25 build before relying on this
/// in production — EA/Codemasters have changed byte layouts between seasons, and the struct offsets below
/// are transcribed from the public F1 24 UDP telemetry spec (unverified against a live F1 25 capture).
/// See the accompanying report for a full risk ranking of every assumption in this file.
/// </summary>
public sealed class F125TelemetrySource : ITelemetrySource
{
    private const int Port = 20777;
    private const int HeaderSize = 29;
    private const int CarTelemetrySize = 60;
    private const byte CarTelemetryPacketId = 6;

    // --- Lap Data packet (id 2) ---
    // UNVERIFIED: struct layout transcribed from the public F1 24 UDP telemetry spec. The
    // m_deltaToCarInFrontMSPart/MinutesPart split fields were introduced in F1 24 (F1 23 used a
    // single uint16 m_deltaToCarInFrontInMS). If F1 25 changed this again, LapDataSize and/or the
    // field read order below will be wrong — BinaryReader won't throw on wrong-but-in-range
    // values, it'll just silently misread, so this needs to be checked against a real capture.
    private const byte LapDataPacketId = 2;
    private const int LapDataSize = 57; // bytes per car — see field-by-field read in ParseLapData
    private const int MaxCars = 22;

    // --- Car Status packet (id 7) ---
    // UNVERIFIED: same caveat as Lap Data above. m_actualTyreCompound offset (25 bytes into the
    // struct) is derived by summing the preceding fields per the F1 24 spec.
    private const byte CarStatusPacketId = 7;
    private const int CarStatusSize = 55; // bytes per car — see field-by-field read in ParseCarStatus

    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private bool _connected;

    // Optional screen-OCR fallback for DeltaToBestSec (see ScreenDeltaReader). Null/disabled by
    // default — this is a fragile, opt-in side channel, not something F1 25 telemetry depends on.
    // Only the most recent reading is kept, and it is only trusted while Success is true; a failed
    // or stale reading must never leak into DeltaToBestSec (matches the "null = not available"
    // pattern already used for every other optional field on TelemetrySample).
    private readonly ScreenDeltaReader? _screenDeltaReader;
    private float? _lastScreenDeltaSec;
    private bool _lastScreenDeltaSucceeded;

    // Cached cross-packet state. Car Telemetry (id 6) arrives every physics frame and is what
    // actually triggers SampleReceived; Lap Data (id 2) and Car Status (id 7) arrive at a lower
    // rate, so we cache their latest decoded values here and merge them into whichever Car
    // Telemetry sample comes next. All of this is plain field reads/writes — no locking, no
    // blocking I/O — so it stays well under the <50ms hot-path budget.
    private float? _gapToCarAheadSec;
    private string? _playerTireCompound;
    private string? _carAheadTireCompound;
    private float? _currentLapTimeSec;
    private byte[]? _tyreCompoundByCarIndex; // most recent Car Status snapshot, index = car index

    public event Action<TelemetrySample>? SampleReceived;
    public event Action<bool>? ConnectionChanged;

    /// <param name="screenDeltaReader">Optional OCR-based delta-to-best fallback (see
    /// ScreenDeltaReader). Pass null to disable this path entirely; when non-null but not yet
    /// Enabled/calibrated by the frontend, DeltaToBestSec still stays null (safe default) until a
    /// successful reading arrives.</param>
    public F125TelemetrySource(ScreenDeltaReader? screenDeltaReader = null)
    {
        _screenDeltaReader = screenDeltaReader;
        if (_screenDeltaReader is not null)
        {
            _screenDeltaReader.DeltaRead += OnScreenDeltaRead;
        }
    }

    private void OnScreenDeltaRead(ScreenDeltaReading reading)
    {
        // Not locked: single producer (ScreenDeltaReader's own timer thread), single consumer
        // (this field is only read from ParseCarTelemetryAndEmit on the UDP listen loop thread).
        // A torn read of two floats/bools here is a non-issue — worst case is using last tick's
        // reading one poll late, which is already the expected staleness of a 4Hz side channel.
        _lastScreenDeltaSucceeded = reading.Success;
        _lastScreenDeltaSec = reading.Success ? reading.DeltaSec : null;
    }

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

        switch (packetId)
        {
            case LapDataPacketId:
                ParseLapData(buffer, reader, ms, playerCarIndex);
                return;
            case CarStatusPacketId:
                ParseCarStatus(buffer, ms, playerCarIndex);
                return;
            case CarTelemetryPacketId:
                ParseCarTelemetryAndEmit(buffer, reader, ms, playerCarIndex);
                return;
            default:
                return;
        }
    }

    private void ParseCarTelemetryAndEmit(byte[] buffer, BinaryReader reader, MemoryStream ms, byte playerCarIndex)
    {
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
            FuelLevel = 0, // requires Car Status packet fuel fields — not needed for this task, left as-is
            SlipAngle = null, // not exposed by F1 25 telemetry — PRD §13.2
            TireTempC = tyreSurfaceTemp,
            TireWearPct = null, // requires Car Damage packet (id 10) — out of scope for this task
            LapDistancePct = null, // requires track length from the Session packet (id 1) — out of scope
            CurrentLapTimeSec = _currentLapTimeSec,
            // DeltaToBestSec is null for F1 25 by default (see the CODE-CRITIC FIX remark in
            // ParseLapData below for why we stopped fabricating a value from lap-time packets).
            // The one exception: a currently-succeeding OCR read of the in-game HUD delta via
            // ScreenDeltaReader, which is opt-in and treated as "no data" the instant it fails.
            DeltaToBestSec = _lastScreenDeltaSucceeded ? _lastScreenDeltaSec : null,
            GapToCarAheadSec = _gapToCarAheadSec,
            PlayerTireCompound = _playerTireCompound,
            CarAheadTireCompound = _carAheadTireCompound,
        };

        SampleReceived?.Invoke(sample);
    }

    /// <summary>
    /// PacketLapData (id 2). Per-car LapData struct fields read in spec order (F1 24 layout,
    /// UNVERIFIED for F1 25):
    ///   uint32 lastLapTimeInMS, uint32 currentLapTimeInMS,
    ///   uint16 sector1TimeMSPart, uint8 sector1TimeMinutesPart,
    ///   uint16 sector2TimeMSPart, uint8 sector2TimeMinutesPart,
    ///   uint16 deltaToCarInFrontMSPart, uint8 deltaToCarInFrontMinutesPart,
    ///   uint16 deltaToRaceLeaderMSPart, uint8 deltaToRaceLeaderMinutesPart,
    ///   float lapDistance, float totalDistance, float safetyCarDelta,
    ///   uint8 carPosition, uint8 currentLapNum, uint8 pitStatus, uint8 numPitStops,
    ///   uint8 sector, uint8 currentLapInvalid, uint8 penalties, uint8 totalWarnings,
    ///   uint8 cornerCuttingWarnings, uint8 numUnservedDriveThroughPens,
    ///   uint8 numUnservedStopGoPens, uint8 gridPosition, uint8 driverStatus,
    ///   uint8 resultStatus, uint8 pitLaneTimerActive, uint16 pitLaneTimeInLaneInMS,
    ///   uint16 pitStopTimerInMS, uint8 pitStopShouldServePen,
    ///   float speedTrapFastestSpeed, uint8 speedTrapFastestLap  == 57 bytes.
    ///
    /// IMPORTANT (flagged risk): the task brief describes this packet as containing a direct
    /// "personal-best delta" field. The public spec (as far as I can verify without a live
    /// capture) does NOT have such a field — only deltaToCarInFront and deltaToRaceLeader.
    /// DeltaToBestSec below is therefore an approximation (current lap time vs. the best
    /// completed lap time seen so far this session), not a true live delta-to-best like
    /// iRacing's LapDeltaToBest. Getting a real delta-to-best requires stitching in
    /// PacketSessionHistoryData (id 11) for the personal-best lap/sector times — out of scope
    /// for this task; flagged in the report as a follow-up.
    /// </summary>
    private void ParseLapData(byte[] buffer, BinaryReader reader, MemoryStream ms, byte playerCarIndex)
    {
        var playerOffset = HeaderSize + playerCarIndex * LapDataSize;
        if (buffer.Length < playerOffset + LapDataSize) return;

        ms.Position = playerOffset;

        reader.ReadUInt32(); // lastLapTimeInMS — not consumed (see CODE-CRITIC FIX below)
        var currentLapTimeMs = reader.ReadUInt32();
        reader.ReadUInt16(); reader.ReadByte(); // sector1 time
        reader.ReadUInt16(); reader.ReadByte(); // sector2 time
        var deltaCarFrontMsPart = reader.ReadUInt16();
        var deltaCarFrontMinPart = reader.ReadByte();
        reader.ReadUInt16(); reader.ReadByte(); // deltaToRaceLeader
        reader.ReadSingle(); // lapDistance
        reader.ReadSingle(); // totalDistance
        reader.ReadSingle(); // safetyCarDelta
        var carPosition = reader.ReadByte(); // 1-based race position
        // Remaining fields (currentLapNum, pitStatus, ...) not needed for this task — stop reading.

        _currentLapTimeSec = currentLapTimeMs / 1000f;

        // CODE-CRITIC FIX: this used to compute a "_deltaToBestSec" as
        // (currentLapTimeMs - bestMs) / 1000f and feed it straight into
        // TelemetrySample.DeltaToBestSec / CoachEngine's Push/Steady/BackingOff classifier.
        // That value's slope w.r.t. wall-clock time is ~1.0 sec/sec for almost the entire lap
        // (currentLapTimeMs counts up every tick while bestMs is fixed), which is ~20x over
        // CoachEngine's TrendDeadbandSecPerSec (0.05f) — so it would classify as BackingOff
        // continuously, regardless of actual driving quality. It is a monotonic "time since
        // start of lap minus a fixed constant" proxy, not a true relative-pace signal like
        // iRacing's LapDeltaToBest, and CoachEngine's classifier implicitly assumes the latter.
        //
        // Decision: we deliberately do NOT compute or forward a DeltaToBestSec for F1 25 at all
        // (TelemetrySample.DeltaToBestSec is always null for this sim — see
        // ParseCarTelemetryAndEmit). A wrong/fabricated push-hold signal is worse than no
        // signal (PRD §11: "bad live callouts are actively distracting"), and CoachEngine
        // already treats a null DeltaToBestSec as "no signal" (clears history, returns Steady)
        // with zero special-casing required in shared logic — so this keeps the sim-specific
        // quirk isolated entirely to this adapter instead of teaching CoachEngine about F1.
        // (A prior revision also tracked a running best-lap-so-far here as scaffolding for a
        // future PacketSessionHistoryData-based delta-to-best; removed per code-critic review
        // since nothing consumed it — an unused field is a maintenance trap that looks wired up
        // when it isn't. Re-add it if/when that follow-up is actually built.)

        // Gap to car ahead: the game computes this for us per-car (deltaToCarInFront).
        _gapToCarAheadSec = deltaCarFrontMinPart * 60f + deltaCarFrontMsPart / 1000f;

        // Find the car directly ahead on track (position - 1) to look up its cached tire
        // compound from the last Car Status packet. Lap Data doesn't carry compound info
        // itself, so this is a second pass over the buffer to find that car's carPosition.
        if (carPosition > 1 && _tyreCompoundByCarIndex is not null)
        {
            for (var carIdx = 0; carIdx < MaxCars; carIdx++)
            {
                var otherOffset = HeaderSize + carIdx * LapDataSize;
                if (buffer.Length < otherOffset + LapDataSize) break;

                // carPosition for each car lives at the same fixed offset within its LapData
                // struct: 4+4 + (2+1)*2 + (2+1)*2 + 4+4+4 = 32 bytes in (verified against the
                // sequential BinaryReader reads in the primary parse path above: lastLapTimeMs,
                // currentLapTimeMs, sector1(2+1), sector2(2+1), deltaCarFront(2+1),
                // deltaToRaceLeader(2+1), lapDistance, totalDistance, safetyCarDelta, then
                // carPosition — 8+6+6+12 = 32).
                const int carPositionOffsetWithinStruct = 32;
                var otherPos = buffer[otherOffset + carPositionOffsetWithinStruct];

                if (otherPos == carPosition - 1)
                {
                    _carAheadTireCompound = carIdx < _tyreCompoundByCarIndex.Length
                        ? MapTyreCompound(_tyreCompoundByCarIndex[carIdx])
                        : null;
                    break;
                }
            }
        }
        else if (carPosition <= 1)
        {
            _carAheadTireCompound = null; // leading the field — nobody ahead
        }
    }

    /// <summary>
    /// PacketCarStatusData (id 7). Per-car CarStatusData struct, field order per the F1 24 spec
    /// (UNVERIFIED for F1 25):
    ///   uint8 tractionControl, antiLockBrakes, fuelMix, frontBrakeBias, pitLimiterStatus,
    ///   float fuelInTank, fuelCapacity, fuelRemainingLaps,
    ///   uint16 maxRPM, idleRPM, uint8 maxGears, drsAllowed, uint16 drsActivationDistance,
    ///   uint8 actualTyreCompound, ...  == m_actualTyreCompound at byte offset 25 within the struct.
    /// Struct size 55 bytes/car per the same spec.
    /// </summary>
    private void ParseCarStatus(byte[] buffer, MemoryStream ms, byte playerCarIndex)
    {
        const int actualTyreCompoundOffsetWithinStruct = 25;

        _tyreCompoundByCarIndex ??= new byte[MaxCars];

        for (var carIdx = 0; carIdx < MaxCars; carIdx++)
        {
            var offset = HeaderSize + carIdx * CarStatusSize + actualTyreCompoundOffsetWithinStruct;
            if (buffer.Length <= offset) break;
            _tyreCompoundByCarIndex[carIdx] = buffer[offset];
        }

        if (playerCarIndex < _tyreCompoundByCarIndex.Length)
        {
            _playerTireCompound = MapTyreCompound(_tyreCompoundByCarIndex[playerCarIndex]);
        }
    }

    /// <summary>
    /// F1 actualTyreCompound byte codes, per the public F1 24 spec (UNVERIFIED for F1 25 — if a
    /// new compound was added/renumbered this season, unknown codes fall through to "Unknown(n)"
    /// rather than silently mislabeling a tire).
    /// </summary>
    private static string MapTyreCompound(byte code) => code switch
    {
        15 => "C6",
        16 => "C5",
        17 => "C4",
        18 => "C3",
        19 => "C2",
        20 => "C1",
        21 => "C0",
        7 => "Intermediate",
        8 => "Wet",
        _ => $"Unknown({code})",
    };

    public void Dispose()
    {
        Stop();
        _client?.Dispose();
        _cts?.Dispose();
        if (_screenDeltaReader is not null)
        {
            _screenDeltaReader.DeltaRead -= OnScreenDeltaRead;
        }
    }
}
