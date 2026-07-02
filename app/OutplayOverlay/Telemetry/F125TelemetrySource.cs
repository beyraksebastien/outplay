using System.IO;
using System.Net;
using System.Net.Sockets;

namespace OutplayOverlay.Telemetry;

/// <summary>
/// Reads F1 25 telemetry via the game's UDP broadcast (Settings > Telemetry Settings > UDP Telemetry: On,
/// UDP Format: 2024 — the 2025 default format is not implemented here). Default port 20777.
/// Parses PacketCarTelemetryData (id 6), PacketLapData (id 2), PacketCarStatusData (id 7),
/// PacketSessionData (id 1, trackLength only), and PacketSessionHistoryData (id 11, best-lap time
/// only) — the last of these is what powers a real (end-of-lap, not continuous) DeltaToBestSec;
/// see ParseSessionHistory's doc comment for the full design rationale.
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

    // --- Session packet (id 1) ---
    // UNVERIFIED (but low risk relative to the other packets here): only used to grab
    // m_trackLength, which is one of the first few fields in the struct per the public F1 24/25
    // spec: uint8 weather, int8 trackTemperature, int8 airTemperature, uint8 totalLaps,
    // uint16 trackLength, ... We stop reading immediately after trackLength since nothing else
    // in this packet is needed. Low field-count-before-target keeps the mis-offset risk small
    // compared to e.g. Session History below.
    private const byte SessionPacketId = 1;

    // --- Session packet (id 1) marshal zones (FEATURE 2, track flag) ---
    // UNVERIFIED, HIGH RISK: struct layout transcribed from the public F1 24 UDP telemetry spec,
    // for the fields BETWEEN trackLength and m_marshalZones — this is a much deeper/riskier
    // offset chain than the trackLength read above (12 single/double-byte fields precede the
    // array here, vs. 4 before trackLength), so a single wrong field size anywhere in that chain
    // silently misreads every zone. See ParseSession for the full field list read in order.
    private const int MarshalZoneSize = 5; // float m_zoneStart (4 bytes) + sbyte m_zoneFlag (1 byte)
    private const int MaxMarshalZones = 21; // fixed array size per the F1 24/25 spec

    // --- Session History packet (id 11) ---
    // UNVERIFIED, HIGH RISK: struct layout transcribed from the public F1 24 UDP telemetry spec
    // (F1 25 not independently confirmed). Per the spec:
    //   uint8 carIdx, uint8 numLaps, uint8 numTyreStints,
    //   uint8 bestLapTimeLapNum, uint8 bestSector1LapNum, uint8 bestSector2LapNum, uint8 bestSector3LapNum,
    //   LapHistoryData lapHistoryData[100]  (14 bytes each: uint32 lapTimeInMS,
    //       uint16 sector1TimeMSPart, uint8 sector1TimeMinutesPart,
    //       uint16 sector2TimeMSPart, uint8 sector2TimeMinutesPart,
    //       uint16 sector3TimeMSPart, uint8 sector3TimeMinutesPart, uint8 lapValidBitFlags),
    //   TyreStintHistoryData tyreStintsHistoryData[8] (3 bytes each — not read here).
    // Unlike Lap Data/Car Status/Car Telemetry, this packet carries data for ONE car per packet
    // (cycled by the game across frames), identified by its own m_carIdx field — NOT indexed by
    // playerCarIndex from the shared header. If m_carIdx doesn't match the player's car this
    // tick, the packet is simply skipped (their turn will come around again).
    private const byte SessionHistoryPacketId = 11;
    private const int LapHistoryEntrySize = 14;
    private const int MaxLapHistoryEntries = 100;

    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private bool _connected;

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

    // --- Real (if coarse) delta-to-best state, see Feature 1 remarks on ParseSessionHistory /
    // ParseLapData / the "end-of-lap delta" design decision below. All plain field reads/writes
    // on the UDP listen-loop thread — no locking, no I/O, stays well under the hot-path budget.
    private float? _trackLengthM;              // from Session packet (id 1), for LapDistancePct
    private uint? _bestLapTimeMs;              // player's best completed lap this session, from Session History (id 11)
    private uint? _lastSeenLastLapTimeMs;      // most recent Lap Data "lastLapTimeInMS" value seen (used to detect a new completed lap)
    private float? _endOfLapDeltaToBestSec;    // held constant between lap completions; see design note
    private float? _lapDistancePctF1;          // derived from lapDistance / trackLength, F1 25 only

    // --- Track flag (FEATURE 2), from PacketSessionData (id 1) marshal zones — see ParseSession. ---
    private TrackFlag? _trackFlagF1;

    // --- Track name, from PacketSessionData (id 1) m_trackId — see ParseSession / MapTrackId. ---
    private string? _trackNameF1;

    public event Action<TelemetrySample>? SampleReceived;
    public event Action<bool>? ConnectionChanged;

    public void Start()
    {
        _cts = new CancellationTokenSource();

        try
        {
            _client = new UdpClient(Port);
        }
        catch (SocketException ex)
        {
            // Binding UDP port 20777 fails if another process already holds it - most commonly a
            // leftover instance of this same app still running from a prior crash/interrupted run.
            // This must NOT crash the whole app (it did: this constructor-time failure previously
            // propagated all the way up through MainWindow's constructor as an unhandled
            // XamlParseException, killing the process before the overlay window ever appeared,
            // even though iRacing's adapter was perfectly fine and could have worked on its own).
            // Degrade instead: log it, leave this source permanently "not connected", and let
            // TelemetryHub/MainWindow keep working with whatever sources DID start successfully.
            System.Diagnostics.Debug.WriteLine(
                $"[F125TelemetrySource] Failed to bind UDP port {Port}: {ex.Message}. " +
                "F1 25 telemetry will be unavailable this run (another process likely already " +
                "holds this port - check for a leftover OutplayOverlay.exe in Task Manager).");
            _client = null;
            return;
        }

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
            case SessionPacketId:
                ParseSession(buffer, reader, ms);
                return;
            case LapDataPacketId:
                ParseLapData(buffer, reader, ms, playerCarIndex);
                return;
            case CarStatusPacketId:
                ParseCarStatus(buffer, ms, playerCarIndex);
                return;
            case SessionHistoryPacketId:
                ParseSessionHistory(buffer, reader, ms, playerCarIndex);
                return;
            case CarTelemetryPacketId:
                ParseCarTelemetryAndEmit(buffer, reader, ms, playerCarIndex);
                return;
            default:
                return;
        }
    }

    /// <summary>
    /// PacketSessionData (id 1). Only reads m_trackLength (uint16, metres) — needed to turn F1
    /// 25's lapDistance (metres into the current lap, from Lap Data) into a 0..1 LapDistancePct
    /// comparable to iRacing's LapDistPct. UNVERIFIED field order/offsets against a live F1 25
    /// capture, but this is one of the lowest-risk offsets in this file (only 4 single-byte
    /// fields precede it).
    /// </summary>
    private void ParseSession(byte[] buffer, BinaryReader reader, MemoryStream ms)
    {
        var offset = HeaderSize;
        if (buffer.Length < offset + 6) return;

        ms.Position = offset;
        reader.ReadByte();  // weather
        reader.ReadSByte(); // trackTemperature
        reader.ReadSByte(); // airTemperature
        reader.ReadByte();  // totalLaps
        var trackLength = reader.ReadUInt16(); // metres

        _trackLengthM = trackLength > 0 ? trackLength : null;

        // FEATURE 2 — continue reading past trackLength to reach m_marshalZones. Per the F1 24/25
        // spec (UNVERIFIED, see MarshalZoneSize/MaxMarshalZones comment above), the fields between
        // trackLength and the marshal zone array are, in order:
        //   int8 trackId, uint8 formula, uint16 sessionTimeLeft, uint16 sessionDuration,
        //   uint8 pitSpeedLimit, uint8 gamePaused, uint8 isSpectating, uint8 spectatorCarIndex,
        //   uint8 sliProNativeSupport, uint8 numMarshalZones,
        //   MarshalZone marshalZones[21] (float m_zoneStart, sbyte m_zoneFlag).
        // 12 bytes of fixed fields between trackLength and numMarshalZones's own byte (trackId 1 +
        // formula 1 + sessionTimeLeft 2 + sessionDuration 2 + pitSpeedLimit 1 + gamePaused 1 +
        // isSpectating 1 + spectatorCarIndex 1 + sliProNativeSupport 1 = 11, then numMarshalZones
        // itself is the 12th byte read below).
        var marshalZonesHeaderOffset = offset + 6; // right after trackLength
        if (buffer.Length < marshalZonesHeaderOffset + 12)
        {
            _trackFlagF1 = null; // not enough bytes to safely reach numMarshalZones — leave unknown
            return;
        }

        ms.Position = marshalZonesHeaderOffset;
        var trackId = reader.ReadSByte(); // m_trackId — see MapTrackId below (FEATURE: track name)
        _trackNameF1 = MapTrackId(trackId);
        reader.ReadByte();   // formula
        reader.ReadUInt16(); // sessionTimeLeft
        reader.ReadUInt16(); // sessionDuration
        reader.ReadByte();   // pitSpeedLimit
        reader.ReadByte();   // gamePaused
        reader.ReadByte();   // isSpectating
        reader.ReadByte();   // spectatorCarIndex
        reader.ReadByte();   // sliProNativeSupport
        var numMarshalZones = reader.ReadByte();

        if (numMarshalZones > MaxMarshalZones)
        {
            // Out-of-range value strongly suggests a mis-parsed offset above — bail rather than
            // read garbage as if it were zone flags.
            _trackFlagF1 = null;
            return;
        }

        var marshalZonesArrayStart = marshalZonesHeaderOffset + 12;
        if (buffer.Length < marshalZonesArrayStart + numMarshalZones * MarshalZoneSize)
        {
            _trackFlagF1 = null;
            return;
        }

        // FEATURE 2 DESIGN NOTE (v1 simplification, documented tradeoff): F1 25 reports flag state
        // per marshal zone (a segment of track), not one global flag. Correlating the player's
        // current LapDistancePct to "which zone am I in right now" would need the zones sorted by
        // m_zoneStart and the player's position tested against zone boundaries — doable, but adds
        // another layer of unverified assumptions (zone ordering, exact meaning of m_zoneStart as
        // a fraction of lap vs. track, whether zones wrap across the start/finish line) on top of
        // the already-shaky struct-offset chain above. V1 instead reports the MOST SEVERE flag
        // present across ANY zone — a "track-wide" signal, not "the zone you're currently in".
        // This means a yellow flag on the opposite side of the track will be announced even if
        // your local zone is green, which is a real limitation but a safe-by-default one (drivers
        // are told "caution somewhere on track", never told "all clear" while a zone elsewhere is
        // yellow) and is a reasonable v1 scope call rather than silently guessing at zone geometry.
        ms.Position = marshalZonesArrayStart;
        var mostSevere = TrackFlag.Unknown;
        var foundAny = false;
        for (var i = 0; i < numMarshalZones; i++)
        {
            reader.ReadSingle();     // m_zoneStart — unused in this v1 track-wide simplification
            var zoneFlag = reader.ReadSByte(); // -1 invalid/unknown, 0 none, 1 green, 2 blue, 3 yellow, 4 red

            var mapped = zoneFlag switch
            {
                4 => TrackFlag.Red,
                3 => TrackFlag.Yellow,
                1 => TrackFlag.Green,
                // 2 (blue) is a "car being lapped" warning to an individual driver, not a track
                // condition flag, and 0/-1 mean no flag/unknown for this zone — none of these
                // should override a more severe flag already found in an earlier zone.
                _ => (TrackFlag?)null,
            };

            if (mapped is not TrackFlag flag) continue;
            foundAny = true;
            if (Severity(flag) > Severity(mostSevere)) mostSevere = flag;
        }

        _trackFlagF1 = foundAny ? mostSevere : null;
    }

    /// <summary>
    /// F1 m_trackId -&gt; display name, per the publicly circulated F1 24 UDP telemetry spec.
    /// UNVERIFIED, HIGH RISK — flagged explicitly per the task brief: this table has NOT been
    /// checked against a live F1 25 capture, and EA/Codemasters have reordered/renumbered track
    /// IDs between game years before (e.g. when new circuits are added mid-list). A wrong mapping
    /// here silently mislabels the track rather than failing loudly, since any in-range sbyte
    /// value maps to *some* name. Unknown/negative values (e.g. -1 = no track loaded yet) fall
    /// through to null rather than a fabricated name.
    /// </summary>
    private static string? MapTrackId(sbyte trackId) => trackId switch
    {
        0 => "Melbourne",
        1 => "Paul Ricard",
        2 => "Shanghai",
        3 => "Sakhir (Bahrain)",
        4 => "Catalunya",
        5 => "Monaco",
        6 => "Montreal",
        7 => "Silverstone",
        9 => "Hockenheim",
        10 => "Hungaroring",
        11 => "Spa",
        12 => "Monza",
        13 => "Singapore",
        14 => "Suzuka",
        15 => "Abu Dhabi",
        16 => "Texas (COTA)",
        17 => "Brazil (Interlagos)",
        18 => "Austria",
        19 => "Sochi",
        20 => "Mexico",
        21 => "Baku",
        26 => "Zandvoort",
        27 => "Imola",
        29 => "Jeddah",
        30 => "Miami",
        31 => "Las Vegas",
        32 => "Losail (Qatar)",
        _ => null, // unknown/unmapped id (includes -1 "no track loaded yet") — do not guess
    };

    /// <summary>Severity ranking used only to pick the worst flag across marshal zones in
    /// ParseSession — Red > Yellow > Green (F1 25's zoneFlag codes don't distinguish
    /// caution/white/chequered the way iRacing's SessionFlags bitfield does, so this is a smaller
    /// scale than IRacingTelemetrySource's priority order).</summary>
    private static int Severity(TrackFlag flag) => flag switch
    {
        TrackFlag.Red => 3,
        TrackFlag.Yellow => 2,
        TrackFlag.Green => 1,
        _ => 0,
    };

    /// <summary>
    /// PacketSessionHistoryData (id 11). See the field-layout comment on
    /// <see cref="SessionHistoryPacketId"/> above for the transcribed (UNVERIFIED) struct.
    ///
    /// This packet is per-car (m_carIdx identifies which car it describes), not indexed by the
    /// shared header's playerCarIndex, so we bail out if it's not currently describing the
    /// player's car.
    ///
    /// FEATURE 1 DESIGN NOTE — what this actually gives us: m_bestLapTimeLapNum is a 1-based lap
    /// number (0 = "no valid best lap yet this session") indexing into m_lapHistoryData for the
    /// player's best completed lap. We only need that lap's total m_lapTimeInMS — we deliberately
    /// do NOT attempt to reconstruct sector splits or do distance-interpolated math here (see the
    /// end-of-lap-only design note on ParseLapData below for why).
    /// </summary>
    private void ParseSessionHistory(byte[] buffer, BinaryReader reader, MemoryStream ms, byte playerCarIndex)
    {
        var bodyOffset = HeaderSize;
        // Minimum bytes needed to read the fixed header fields of this packet.
        if (buffer.Length < bodyOffset + 7) return;

        ms.Position = bodyOffset;
        var carIdx = reader.ReadByte();
        if (carIdx != playerCarIndex) return; // this packet describes a different car's history this tick

        reader.ReadByte(); // numLaps
        reader.ReadByte(); // numTyreStints
        var bestLapTimeLapNum = reader.ReadByte(); // 1-based, 0 = no valid best lap yet
        reader.ReadByte(); // bestSector1LapNum
        reader.ReadByte(); // bestSector2LapNum
        reader.ReadByte(); // bestSector3LapNum

        if (bestLapTimeLapNum == 0) return; // no best lap yet this session — leave _bestLapTimeMs as-is (null)
        if (bestLapTimeLapNum > MaxLapHistoryEntries) return; // defensive: out-of-range would indicate a mis-parsed offset

        var lapHistoryArrayStart = bodyOffset + 7;
        var entryOffset = lapHistoryArrayStart + (bestLapTimeLapNum - 1) * LapHistoryEntrySize;
        if (buffer.Length < entryOffset + 4) return;

        ms.Position = entryOffset;
        var bestLapTimeMs = reader.ReadUInt32(); // m_lapTimeInMS for the best lap — no need to read sector fields

        if (bestLapTimeMs > 0)
        {
            _bestLapTimeMs = bestLapTimeMs;
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
            LapDistancePct = _lapDistancePctF1, // now wired from Lap Data lapDistance / Session trackLength
            CurrentLapTimeSec = _currentLapTimeSec,
            // Real (if coarse) delta-to-best: updates once per completed lap (see ParseLapData),
            // held constant until the next lap completes. Null until a valid best lap this session
            // exists. The screen-OCR fallback that previously overrode this with a finer-grained
            // signal has been removed.
            DeltaToBestSec = _endOfLapDeltaToBestSec,
            GapToCarAheadSec = _gapToCarAheadSec,
            PlayerTireCompound = _playerTireCompound,
            CarAheadTireCompound = _carAheadTireCompound,
            TrackFlag = _trackFlagF1,
            TrackName = _trackNameF1,
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
    /// IMPORTANT (flagged risk, updated for Feature 1): this packet does NOT contain a direct
    /// "personal-best delta" field — only deltaToCarInFront and deltaToRaceLeader. A genuine
    /// delta-to-best now comes from combining this packet's lastLapTimeInMS (the just-completed
    /// lap's total time) with the player's best-lap time cached from PacketSessionHistoryData
    /// (id 11, see ParseSessionHistory) — see the end-of-lap design note further down in this
    /// method for exactly what that does and does not give us.
    /// </summary>
    private void ParseLapData(byte[] buffer, BinaryReader reader, MemoryStream ms, byte playerCarIndex)
    {
        var playerOffset = HeaderSize + playerCarIndex * LapDataSize;
        if (buffer.Length < playerOffset + LapDataSize) return;

        ms.Position = playerOffset;

        var lastLapTimeMs = reader.ReadUInt32(); // now consumed — see Feature 1 note below
        var currentLapTimeMs = reader.ReadUInt32();
        reader.ReadUInt16(); reader.ReadByte(); // sector1 time
        reader.ReadUInt16(); reader.ReadByte(); // sector2 time
        var deltaCarFrontMsPart = reader.ReadUInt16();
        var deltaCarFrontMinPart = reader.ReadByte();
        reader.ReadUInt16(); reader.ReadByte(); // deltaToRaceLeader
        var lapDistance = reader.ReadSingle(); // metres into current lap (can be negative pre-start-line)
        reader.ReadSingle(); // totalDistance
        reader.ReadSingle(); // safetyCarDelta
        var carPosition = reader.ReadByte(); // 1-based race position
        // Remaining fields (currentLapNum, pitStatus, ...) not needed for this task — stop reading.

        _currentLapTimeSec = currentLapTimeMs / 1000f;

        // LapDistancePct wiring (was previously hardcoded null — "requires track length from the
        // Session packet", which packet id 1 now supplies via ParseSession). Only meaningful once
        // a Session packet has arrived (trackLength > 0); F1 25 sends Session packets at a lower
        // rate than Lap Data, so there can be a brief startup window where this stays null.
        _lapDistancePctF1 = _trackLengthM is float trackLength && trackLength > 0
            ? Math.Clamp(lapDistance / trackLength, 0f, 1f)
            : null;

        // FEATURE 1 — end-of-lap delta-to-best (real telemetry, not fabricated):
        // lastLapTimeInMS holds the PREVIOUS completed lap's total time and only changes value
        // once per lap (the instant a new lap is registered by the game) — it is not a per-tick
        // counter like currentLapTimeInMS, so comparing it to the cached best-lap time from
        // Session History (id 11) gives a genuine (if coarse) "how did that lap compare to my
        // best" signal, not the monotonic proxy that was rejected before (see the CODE-CRITIC FIX
        // note further down, kept for history).
        //
        // HONEST LIMITATION (deliberate v1 scope, not a hidden gap): this updates ONLY once per
        // lap, at the moment the new lap is registered, and holds constant for the entire next
        // lap. It is NOT a continuously-updating mid-lap delta like iRacing's LapDeltaToBest —
        // sub-lap distance-relative comparison against the best lap would require sector-boundary
        // interpolation math this packet set doesn't give us cheaply. A driver only really learns
        // "how did that lap compare" at the line; this mirrors that, nothing more.
        if (lastLapTimeMs > 0 && lastLapTimeMs != _lastSeenLastLapTimeMs && _bestLapTimeMs is uint bestMs)
        {
            _endOfLapDeltaToBestSec = (lastLapTimeMs - bestMs) / 1000f;
        }
        _lastSeenLastLapTimeMs = lastLapTimeMs;

        // CODE-CRITIC FIX (history, kept for context): this used to compute a "_deltaToBestSec"
        // as (currentLapTimeMs - bestMs) / 1000f and feed it straight into
        // TelemetrySample.DeltaToBestSec / CoachEngine's Push/Steady/BackingOff classifier. That
        // value's slope w.r.t. wall-clock time is ~1.0 sec/sec for almost the entire lap
        // (currentLapTimeMs counts up every tick while bestMs is fixed), which is ~20x over
        // CoachEngine's TrendDeadbandSecPerSec (0.05f) — so it would classify as BackingOff
        // continuously, regardless of actual driving quality. That was reverted to null.
        //
        // The end-of-lap _endOfLapDeltaToBestSec computed above does NOT have this problem: it's
        // a step function (only changes once, at the lap boundary, then holds perfectly flat for
        // the rest of the lap), so its slope is 0 for ~an entire lap and CoachEngine's trend
        // classifier will correctly read it as Steady rather than continuous BackingOff. It is
        // real telemetry-derived data, not a fabricated proxy — see the Feature 1 report for the
        // honest characterization of its coarseness (one update per lap, not continuous).

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
    }
}
