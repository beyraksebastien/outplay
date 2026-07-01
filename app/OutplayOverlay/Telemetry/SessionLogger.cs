using System.IO;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace OutplayOverlay.Telemetry;

/// <summary>
/// One completed driving session: from sim-connect to sim-disconnect (or app shutdown).
/// Mirrors PRD §8.1's Session/Lap entities, scaled down to what v1 actually needs — see
/// SessionLogger's doc comment for why TelemetrySample/CornerScore/Insight tables are NOT built
/// here.
/// </summary>
public sealed record LapRecord
{
    public int LapNumber { get; init; }
    public float LapTimeSec { get; init; }
    public float? DeltaToBestSec { get; init; }
    public float AvgThrottle { get; init; }
    public float AvgBrake { get; init; }
}

/// <summary>
/// Persists telemetry sessions to a local SQLite database and raises a session-summary event
/// once a session ends.
///
/// HOT-PATH / I-O DECOUPLING: TelemetryHub.SampleReceived handlers must stay under ~50ms
/// (PRD §8.3), and SQLite file I/O (opening a connection, INSERT/UPDATE) does not belong in that
/// call stack. <see cref="OnSample"/> therefore never touches SQLite: it only updates a small
/// in-memory accumulator under <see cref="_lock"/>, and when a lap/session boundary is detected
/// it enqueues an immutable "write job" (<see cref="StartSessionJob"/>/<see cref="LapJob"/>/
/// <see cref="EndSessionJob"/>) onto an unbounded, non-blocking <see cref="Channel{T}"/>. A single
/// dedicated background task (started in the constructor, see <see cref="RunWriterLoopAsync"/>)
/// is the ONLY code path that ever opens a SqliteConnection or runs ExecuteNonQuery/ExecuteScalar;
/// it drains the channel off the telemetry thread entirely. The accumulator lock is never held
/// while a SQLite call is in flight — enqueuing a job is an in-memory, non-blocking TryWrite.
///
/// Because the real SQLite session id doesn't exist until the writer task has actually inserted
/// the Sessions row, <see cref="_currentSessionId"/> is a locally-generated <see cref="Guid"/>
/// ("logical" session id) rather than the SQLite row id. The writer task keeps its own
/// Guid -&gt; real-row-id map (touched only by that one task, so no locking needed there either)
/// and resolves it when processing subsequent Lap/EndSession jobs for the same logical session.
///
/// <see cref="SummaryReady"/> is only raised from inside the writer loop, AFTER the corresponding
/// EndSessionJob's UPDATE has been executed (SQLite auto-commits each ExecuteNonQuery, so by the
/// time <see cref="SessionSummaryGenerator.Generate"/> reads the Laps/Sessions rows back, they are
/// durably committed) — never optimistically before that write lands.
///
/// GRANULARITY DECISION: samples are NOT stored individually. At 60Hz, a 20-minute session is
/// ~72,000 rows of raw telemetry — that's a lot of SQLite writes for a v1 whose only consumer
/// (SessionSummaryGenerator) needs per-lap aggregates, not per-sample traces. Instead this class
/// maintains a small in-memory running accumulator per lap (sum of throttle/brake samples seen,
/// count) and enqueues ONE row-worth of data per lap when a lap boundary is detected. If a future
/// feature (e.g. brake-trace overlay, PRD §5.6 Ghost Racing) needs raw per-sample data, that's a
/// distinct, larger storage decision explicitly deferred — flagged, not silently dropped.
///
/// LAP BOUNDARY DETECTION (unverified, flagged as risk): neither sim adapter exposes an explicit
/// "lap completed" event. iRacing's F125TelemetrySource-equivalent samples update
/// CurrentLapTimeSec every tick; iRacing's own LapCurrentLapTime and F1 25's derived
/// currentLapTimeMs both reset to a small value at the start of a new lap. This class detects a
/// lap boundary by watching for CurrentLapTimeSec to DECREASE by more than a small tolerance
/// versus the previous sample (i.e. it wrapped back to ~0), and records the previous
/// (now-completed) lap's time as the last non-decreasing CurrentLapTimeSec value seen. This is a
/// heuristic, not a sim-confirmed "lap complete" signal — see risk notes in the final report,
/// especially around pit-lane resets, invalid laps, and session-start noise.
/// </summary>
public sealed class SessionLogger : IDisposable
{
    private readonly TelemetryHub _hub;
    private readonly string _dbPath;
    private readonly object _lock = new();

    private readonly Channel<WriteJob> _writeChannel = Channel.CreateUnbounded<WriteJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _writerTask;

    private Guid? _currentSessionId;
    private string? _currentSim;
    private DateTime _sessionStartUtc;

    private int _lapNumber;
    private float _lastLapTimeSec;
    private float _throttleSum;
    private float _brakeSum;
    private int _sampleCount;
    private float? _lastDeltaToBest;

    /// <summary>Raised once a session ends (sim disconnect or Dispose) and its summary has been
    /// computed from durably-committed rows. Raised from the background writer task/thread, not
    /// the telemetry thread and not necessarily the UI thread — subscribers must marshal to the
    /// UI dispatcher themselves if they touch UI state (MainWindow's current subscriber already
    /// does this via Dispatcher.Invoke, so this is not a new requirement, just calling it out).
    /// </summary>
    public event Action<SessionSummary>? SummaryReady;

    private abstract record WriteJob;
    private sealed record StartSessionJob(Guid LogicalSessionId, string Sim, DateTime StartUtc) : WriteJob;
    private sealed record LapJob(Guid LogicalSessionId, LapRecord Lap) : WriteJob;
    private sealed record EndSessionJob(Guid LogicalSessionId, string Sim, DateTime StartUtc, DateTime EndUtc) : WriteJob;

    public SessionLogger(TelemetryHub hub, string? dbPathOverride = null)
    {
        _hub = hub;
        _dbPath = dbPathOverride ?? GetDefaultDbPath();

        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        InitializeSchema();

        _writerTask = Task.Run(RunWriterLoopAsync);

        _hub.SourceConnectionChanged += OnConnectionChanged;
        _hub.SampleReceived += OnSample;
    }

    public static string GetDefaultDbPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "OutplayOverlay", "sessions.db");
    }

    private string ConnectionString => $"Data Source={_dbPath}";

    private void InitializeSchema()
    {
        // Runs once, synchronously, on construction (typically app startup, not the telemetry hot
        // path) — deliberately not routed through the write queue.
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        // Simple, additive schema for v1. No migration framework — if this schema needs to
        // change later, a real migration step (e.g. checking a schema_version table) should be
        // added then. For now "CREATE TABLE IF NOT EXISTS" is sufficient since the shape hasn't
        // shipped to users yet.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Sessions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Sim TEXT NOT NULL,
                StartUtc TEXT NOT NULL,
                EndUtc TEXT
            );

            CREATE TABLE IF NOT EXISTS Laps (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId INTEGER NOT NULL REFERENCES Sessions(Id),
                LapNumber INTEGER NOT NULL,
                LapTimeSec REAL NOT NULL,
                DeltaToBestSec REAL,
                AvgThrottle REAL,
                AvgBrake REAL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private void OnConnectionChanged(string simName, bool connected)
    {
        lock (_lock)
        {
            if (connected)
            {
                StartSessionLocked(simName);
            }
            else if (_currentSessionId is not null && _currentSim == simName)
            {
                EndSessionLocked();
            }
        }
    }

    // NOTE: everything below with a "Locked" suffix is called from within `lock (_lock)` (either
    // directly, or from OnSample which already holds it). None of these methods perform SQLite
    // I/O themselves anymore — they only touch the in-memory accumulator and enqueue immutable
    // job records via a non-blocking Channel.TryWrite. The actual disk I/O lives exclusively in
    // RunWriterLoopAsync / ExecuteXxx below, which run on the dedicated writer task and never
    // take `_lock`.

    private void StartSessionLocked(string simName)
    {
        // If a session is already open (e.g. reconnect blip), close it out first rather than
        // silently orphaning it.
        if (_currentSessionId is not null)
        {
            EndSessionLocked();
        }

        _sessionStartUtc = DateTime.UtcNow;
        _currentSim = simName;
        _currentSessionId = Guid.NewGuid();
        ResetLapAccumulator();

        _writeChannel.Writer.TryWrite(new StartSessionJob(_currentSessionId.Value, simName, _sessionStartUtc));
    }

    private void EndSessionLocked()
    {
        if (_currentSessionId is not Guid logicalId) return;

        // Flush whatever partial lap was in progress as a final (possibly incomplete) lap only
        // if it has meaningful data — an in-progress lap with 0 samples isn't worth a row.
        if (_sampleCount > 0 && _lastLapTimeSec > 0)
        {
            EnqueueLapLocked(logicalId);
        }

        var endUtc = DateTime.UtcNow;
        var sim = _currentSim ?? "Unknown";
        var startUtc = _sessionStartUtc;

        _writeChannel.Writer.TryWrite(new EndSessionJob(logicalId, sim, startUtc, endUtc));

        _currentSessionId = null;
        _currentSim = null;
    }

    private void ResetLapAccumulator()
    {
        _lapNumber = 0;
        _lastLapTimeSec = 0;
        _throttleSum = 0;
        _brakeSum = 0;
        _sampleCount = 0;
        _lastDeltaToBest = null;
    }

    private void OnSample(TelemetrySample sample)
    {
        lock (_lock)
        {
            if (_currentSessionId is not Guid logicalId) return; // no open session (shouldn't normally happen)

            if (sample.CurrentLapTimeSec is not float lapTime) return;

            // Lap boundary heuristic: a meaningful drop means the sim rolled over to a new lap.
            // 0.5s tolerance absorbs telemetry jitter/out-of-order samples without treating a
            // real new-lap reset as noise.
            if (_sampleCount > 0 && lapTime < _lastLapTimeSec - 0.5f)
            {
                EnqueueLapLocked(logicalId);
                ResetLapAccumulatorKeepSessionLocked();
            }

            _lastLapTimeSec = lapTime;
            _throttleSum += sample.Throttle;
            _brakeSum += sample.Brake;
            _sampleCount++;
            if (sample.DeltaToBestSec is float delta) _lastDeltaToBest = delta;
        }
        // No SQLite connection, no disk I/O, and `_lock` has already been released by the time
        // this method returns — the only "work" done above besides arithmetic is a non-blocking
        // Channel<T>.TryWrite (pure in-memory enqueue), so this handler cannot stall on a slow
        // disk / antivirus scan / SQLite lock contention.
    }

    private void ResetLapAccumulatorKeepSessionLocked()
    {
        _throttleSum = 0;
        _brakeSum = 0;
        _sampleCount = 0;
        _lastDeltaToBest = null;
    }

    /// <summary>Builds the LapRecord for the just-completed lap from the in-memory accumulator
    /// and enqueues it (non-blocking). Must be called while holding <see cref="_lock"/>.</summary>
    private void EnqueueLapLocked(Guid logicalSessionId)
    {
        _lapNumber++;
        var avgThrottle = _sampleCount > 0 ? _throttleSum / _sampleCount : 0f;
        var avgBrake = _sampleCount > 0 ? _brakeSum / _sampleCount : 0f;

        var lap = new LapRecord
        {
            LapNumber = _lapNumber,
            LapTimeSec = _lastLapTimeSec,
            DeltaToBestSec = _lastDeltaToBest,
            AvgThrottle = avgThrottle,
            AvgBrake = avgBrake,
        };

        _writeChannel.Writer.TryWrite(new LapJob(logicalSessionId, lap));
    }

    // ---- Background writer: the only code in this class that ever touches SQLite. ----

    private async Task RunWriterLoopAsync()
    {
        // Owned exclusively by this task — a plain Dictionary is safe here because there is only
        // ever one reader on this unbounded Channel (SingleReader = true above).
        var logicalToRealSessionId = new Dictionary<Guid, long>();

        await foreach (var job in _writeChannel.Reader.ReadAllAsync())
        {
            try
            {
                switch (job)
                {
                    case StartSessionJob start:
                        logicalToRealSessionId[start.LogicalSessionId] = ExecuteStartSession(start.Sim, start.StartUtc);
                        break;

                    case LapJob lapJob:
                        if (logicalToRealSessionId.TryGetValue(lapJob.LogicalSessionId, out var sessionIdForLap))
                        {
                            ExecuteCommitLap(sessionIdForLap, lapJob.Lap);
                        }
                        break;

                    case EndSessionJob end:
                        if (logicalToRealSessionId.TryGetValue(end.LogicalSessionId, out var sessionIdForEnd))
                        {
                            ExecuteEndSession(sessionIdForEnd, end.EndUtc);
                            logicalToRealSessionId.Remove(end.LogicalSessionId);

                            // Summary generation reads back from SQLite; ExecuteEndSession above
                            // has already returned (SQLite auto-commits each ExecuteNonQuery), so
                            // this reads durably-committed rows — SummaryReady is only raised
                            // after that, never optimistically before the write lands.
                            var summary = SessionSummaryGenerator.Generate(
                                ConnectionString, sessionIdForEnd, end.Sim, end.StartUtc, end.EndUtc);
                            SummaryReady?.Invoke(summary);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                // A single bad write must not kill the writer loop — that would silently stop
                // *all* future persistence for the rest of the app's lifetime. Best-effort
                // logging only; there's no user-facing surface for storage errors in v1
                // (flagged as an unverified/deferred gap, not fixed here since it's out of scope
                // for this change).
                System.Diagnostics.Debug.WriteLine($"SessionLogger: write job failed: {ex}");
            }
        }
    }

    private long ExecuteStartSession(string simName, DateTime startUtc)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Sessions (Sim, StartUtc) VALUES ($sim, $start); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$sim", simName);
        cmd.Parameters.AddWithValue("$start", startUtc.ToString("o"));
        return (long)cmd.ExecuteScalar()!;
    }

    private void ExecuteCommitLap(long sessionId, LapRecord lap)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Laps (SessionId, LapNumber, LapTimeSec, DeltaToBestSec, AvgThrottle, AvgBrake)
            VALUES ($sessionId, $lapNumber, $lapTime, $delta, $avgThrottle, $avgBrake);
            """;
        cmd.Parameters.AddWithValue("$sessionId", sessionId);
        cmd.Parameters.AddWithValue("$lapNumber", lap.LapNumber);
        cmd.Parameters.AddWithValue("$lapTime", lap.LapTimeSec);
        cmd.Parameters.AddWithValue("$delta", (object?)lap.DeltaToBestSec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$avgThrottle", lap.AvgThrottle);
        cmd.Parameters.AddWithValue("$avgBrake", lap.AvgBrake);
        cmd.ExecuteNonQuery();
    }

    private void ExecuteEndSession(long sessionId, DateTime endUtc)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Sessions SET EndUtc = $end WHERE Id = $id";
        cmd.Parameters.AddWithValue("$end", endUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _hub.SampleReceived -= OnSample;
        _hub.SourceConnectionChanged -= OnConnectionChanged;

        lock (_lock)
        {
            // App closing mid-session: close out whatever session is open so it isn't orphaned
            // with a null EndUtc forever. This only enqueues the final EndSessionJob (and any
            // trailing partial-lap job ahead of it) — it does not itself touch SQLite.
            if (_currentSessionId is not null)
            {
                EndSessionLocked();
            }
        }

        // Stop accepting new work, then block (Dispose is a shutdown path, not the telemetry hot
        // path — a bounded wait here is the intentional trade-off) until the writer task has
        // drained everything already queued, most importantly the session-end write enqueued
        // just above. Without this wait, the process could exit before that final UPDATE lands,
        // leaving the session's EndUtc permanently NULL — the exact regression this fix must not
        // introduce.
        _writeChannel.Writer.Complete();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex)
        {
            System.Diagnostics.Debug.WriteLine($"SessionLogger: writer task faulted during shutdown: {ex}");
        }
    }
}
