using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Tesseract;

// NOTE: both System.Threading and System.Windows.Forms are in scope in this file (the latter is
// required for Screen.PrimaryScreen.Bounds in BuildCandidateRegions), and both namespaces define a
// type named "Timer" (System.Threading.Timer vs. System.Windows.Forms.Timer). A bare "Timer" here
// is genuinely ambiguous (CS0104) and, even if it resolved, System.Windows.Forms.Timer does not
// have the 4-arg (callback, state, dueTime, period) constructor this file relies on and is a
// message-loop timer, not a thread-pool timer - the wrong type entirely for a background polling
// loop. Every use of Timer below is therefore fully qualified as System.Threading.Timer rather
// than relying on using-directive resolution order.

namespace OutplayOverlay.Telemetry;

/// <summary>
/// Result of a one-shot auto-detect scan (see ScreenDeltaReader.AutoDetectRegionAsync). Mirrors
/// the existing "Test Region" calibration flow: this never mutates the reader's own Region — it
/// only reports what it found so the frontend can show the user a preview and let them explicitly
/// accept/Save it (same test-then-save split already used for manual calibration).
/// </summary>
public sealed record AutoDetectResult
{
    public required bool Success { get; init; }

    /// <summary>Non-null only when Success is true. Not applied to the reader automatically.</summary>
    public ScreenRegion? DetectedRegion { get; init; }

    /// <summary>How many candidate rectangles were scanned, success or failure — lets the UI show
    /// "scanned 32 regions" regardless of outcome.</summary>
    public required int CandidatesScanned { get; init; }

    /// <summary>The OCR'd text from the winning candidate (e.g. "-0.234"), so the UI can show the
    /// user what was actually found. Null when Success is false.</summary>
    public string? SampleReading { get; init; }
}

/// <summary>
/// A rectangular region of the screen, in screen (not client-window) pixel coordinates, to
/// capture and OCR. This is inherently per-user (depends on resolution, UI scale, and where the
/// player has positioned/sized F1 25's delta-to-best/rival HUD element) — there is no sane
/// hardcoded default beyond a placeholder, so calibration is mandatory for this feature to work.
/// </summary>
public sealed record ScreenRegion(int X, int Y, int Width, int Height);

/// <summary>
/// One OCR attempt's outcome. Success=false means "treat as no data" — consumers (specifically
/// F125TelemetrySource) must not reuse a prior successful reading when the latest attempt failed;
/// staleness is exactly the kind of silently-wrong signal this whole feature exists to avoid
/// (see the removed DeltaToBestSec proxy in F125TelemetrySource.ParseLapData for why that matters).
/// </summary>
public sealed record ScreenDeltaReading
{
    public required DateTime TimestampUtc { get; init; }
    public required bool Success { get; init; }

    /// <summary>Parsed signed delta in seconds (e.g. -0.234), only meaningful when Success is true.</summary>
    public float? DeltaSec { get; init; }

    /// <summary>Raw OCR text, for diagnostics / a "reading OK vs. recalibrate" UI indicator.</summary>
    public string? RawText { get; init; }
}

/// <summary>
/// Optional, fragile, opt-in fallback data source for F1 25: periodically screen-captures a
/// user-calibrated rectangle (expected to contain F1 25's in-game delta-to-best/rival HUD readout)
/// and OCRs it into a signed decimal. This is a slow side-channel (2-5Hz), NOT part of the hot
/// telemetry path — screen capture + OCR together can easily take tens of ms, far too slow for
/// the <50ms/60Hz budget CoachEngine and the UDP sources run under. Runs on its own timer and
/// only ever talks to the rest of the app via the DeltaRead event.
///
/// Lifecycle from the UI's perspective (see report for full contract):
///   - Region: set via SetRegion(...) once the frontend's region-picker UI has one. Defaults to
///     an arbitrary placeholder rectangle that is very unlikely to be correct for any given user.
///   - Enabled: off by default (safe state). The frontend flips this on only after the user has
///     opted in and (ideally) calibrated a region; nothing stops Enable() before calibration, but
///     reads against the placeholder region will simply keep failing (Success=false), which is
///     the correct default-safe behavior, not a crash.
///   - DeltaRead fires on every poll tick, enabled or not... actually see remarks on Enable()/timer:
///     while disabled, no capture/OCR work happens at all and no events fire.
/// </summary>
public sealed class ScreenDeltaReader : IDisposable
{
    // 2-5Hz per the task brief; 4Hz is a reasonable default balance of responsiveness vs. OCR CPU cost.
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    // Signed decimal like "-0.234", "+1.205", "0.000". Tesseract's digit whitelist below already
    // restricts recognized characters, but the regex is a second line of defense against garbage.
    private static readonly Regex DeltaPattern = new(@"[-+]?\d{1,2}\.\d{3}", RegexOptions.Compiled);

    private readonly object _lock = new();
    private readonly System.Threading.Timer _timer;
    private bool _enabled;
    private bool _busy;
    private ScreenRegion _region = new(X: 100, Y: 100, Width: 160, Height: 50); // placeholder, must be calibrated

    // Lazily created on first enabled poll, since constructing a TesseractEngine loads trained-data
    // and is too slow to do per-tick or in the constructor of a component that may never be enabled.
    private TesseractEngine? _engine;

    // Dedicated lock guarding all access to _engine (creation + SetVariable + Process), separate
    // from _lock above. _lock guards fast, near-instant state (_enabled/_busy/_region) and is held
    // only briefly; _engineLock instead serializes CaptureAndOcr calls, which can each take tens of
    // ms (OCR) and, during an auto-detect scan, run 20-40 of them back to back over a few seconds.
    // Using the same lock for both would mean a single OCR call blocks unrelated fast Region/Enabled
    // reads for its whole duration - a needless latency hit for callers that have nothing to do with
    // the engine. Keeping them separate also avoids the risk of a Dispose() waiting on _lock (it
    // doesn't - see Dispose() - but keeping the two concerns apart makes that easy to keep true).
    private readonly object _engineLock = new();

    public event Action<ScreenDeltaReading>? DeltaRead;

    public ScreenRegion Region
    {
        get { lock (_lock) return _region; }
    }

    public bool IsEnabled
    {
        get { lock (_lock) return _enabled; }
    }

    public ScreenDeltaReader(TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? DefaultPollInterval;
        _timer = new System.Threading.Timer(_ => Poll(), null, interval, interval);
    }

    /// <summary>Called by the frontend's region-picker UI once the user has drawn/confirmed a
    /// rectangle over F1 25's delta HUD element. Coordinates are absolute screen pixels (the same
    /// space CopyFromScreen expects), not window-relative.</summary>
    public void SetRegion(ScreenRegion region)
    {
        lock (_lock) _region = region;
    }

    /// <summary>Turns the capture/OCR loop on. Safe to call before or after SetRegion — if called
    /// before calibration, reads will simply fail (Success=false) against the placeholder region.</summary>
    public void Enable()
    {
        lock (_lock) _enabled = true;
    }

    /// <summary>Turns the capture/OCR loop off (e.g. user unticks "use screen-read delta", or the
    /// selected sim isn't F1 25). No further DeltaRead events fire until Enable() is called again.
    /// Synchronously raises a synthetic Success=false DeltaRead first, so consumers (specifically
    /// F125TelemetrySource, which caches the last successful reading) clear their cached state
    /// through the exact same "failed reading" path they already use — otherwise a stale
    /// last-successful delta would keep being reported as live forever after disabling (the same
    /// class of bug the Success=false convention exists to prevent in the first place).</summary>
    public void Disable()
    {
        lock (_lock)
        {
            if (!_enabled) return;
            _enabled = false;
        }

        DeltaRead?.Invoke(new ScreenDeltaReading
        {
            TimestampUtc = DateTime.UtcNow,
            Success = false,
        });
    }

    private void Poll()
    {
        ScreenRegion region;
        lock (_lock)
        {
            if (!_enabled || _busy) return;
            _busy = true;
            region = _region;
        }

        try
        {
            var reading = CaptureAndRead(region);
            DeltaRead?.Invoke(reading);
        }
        catch
        {
            // Any capture/OCR failure (region off-screen, Tesseract init failure, etc.) is
            // reported as a failed reading, never thrown out of the timer callback.
            DeltaRead?.Invoke(new ScreenDeltaReading
            {
                TimestampUtc = DateTime.UtcNow,
                Success = false,
            });
        }
        finally
        {
            lock (_lock) _busy = false;
        }
    }

    private ScreenDeltaReading CaptureAndRead(ScreenRegion region)
    {
        var (text, _) = CaptureAndOcr(region);

        var match = DeltaPattern.Match(text);
        if (!match.Success || !float.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return new ScreenDeltaReading { TimestampUtc = DateTime.UtcNow, Success = false, RawText = text };
        }

        return new ScreenDeltaReading
        {
            TimestampUtc = DateTime.UtcNow,
            Success = true,
            DeltaSec = value,
            RawText = text,
        };
    }

    /// <summary>Shared capture+OCR core used by both the polling loop (CaptureAndRead) and the
    /// auto-detect scan below. Returns the raw OCR'd text and Tesseract's mean confidence
    /// (0..100; used only as a tiebreaker for auto-detect, see AutoDetectRegionAsync).</summary>
    private (string Text, float Confidence) CaptureAndOcr(ScreenRegion region)
    {
        using var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            // DESIGN CHOICE (flagged in report): CopyFromScreen is the simplest GDI-based way to
            // grab a screen rectangle on Windows and is fine for a 2-5Hz side channel. It reads
            // the composited desktop image, so it will NOT see games running in exclusive
            // fullscreen (only borderless/windowed, where the desktop compositor still owns the
            // frame) — Windows.Graphics.Capture is the modern API that can also capture exclusive
            // fullscreen via DXGI, but is materially more complex to wire up. V1 assumes
            // borderless/windowed F1 25, which is the common recommendation for overlay-based
            // tools anyway.
            g.CopyFromScreen(region.X, region.Y, 0, 0, new Size(region.Width, region.Height));
        }

        using var preprocessed = Preprocess(bitmap);
        using var pix = ConvertToPix(preprocessed);

        // _engine is a single shared TesseractEngine instance, and Tesseract engines are not
        // documented as safe for concurrent use from multiple threads. Poll() (internal timer
        // thread) and AutoDetectRegionAsync (Task.Run thread-pool thread) both funnel through this
        // method, so without this lock a user triggering auto-detect while polling is already
        // enabled could have both call Process() on _engine at the same time - garbage OCR results
        // or a crash in the native Tesseract handle.
        //
        // Tradeoff (expected, not a bug): this serializes ALL engine access behind one lock, so
        // while an auto-detect scan is in flight (20-40 sequential OCR calls, each tens of ms - a
        // few seconds total) the polling loop's Poll() ticks will simply wait their turn and
        // effectively pause for that duration. Auto-detect is a rare, on-demand user action, not
        // something that needs to run concurrently with continuous polling, so this is acceptable.
        // In the far more common case (no auto-detect in flight), this lock is uncontended and
        // costs a negligible amount of time on top of the OCR work itself.
        lock (_engineLock)
        {
            _engine ??= new TesseractEngine("./tessdata", "eng", EngineMode.Default);
            // Restrict recognition to characters that can appear in a signed 3-decimal delta, to
            // cut down on misreads of stray HUD pixels as garbage characters.
            _engine.SetVariable("tessedit_char_whitelist", "-+0123456789.");

            using var page = _engine.Process(pix, PageSegMode.SingleLine);
            var text = page.GetText()?.Trim() ?? string.Empty;

            // UNVERIFIED (low-medium risk): GetMeanConfidence() returning a 0..1 float is the
            // Tesseract .NET wrapper's documented API for this version family; only used here as a
            // ranking tiebreaker for auto-detect, never for pass/fail, so a wrong scale (0..1 vs
            // 0..100) would at worst pick a slightly worse candidate among several valid matches,
            // not silently accept a bad reading.
            float confidence;
            try
            {
                confidence = page.GetMeanConfidence() * 100f;
            }
            catch
            {
                confidence = 0f;
            }

            return (text, confidence);
        }
    }

    /// <summary>
    /// One-shot, on-demand scan for a HUD region likely to contain the delta-time readout — an
    /// alternative to manual X/Y/Width/Height entry in the calibration UI. NOT part of the polling
    /// loop and does not touch _region/_enabled/_busy state; safe to call regardless of whether the
    /// reader is currently enabled. Runs the actual scan on a background thread pool thread via
    /// Task.Run so a caller awaiting this from a WPF UI event handler never blocks the UI thread.
    ///
    /// Never calls SetRegion itself — mirrors the existing "Test Region" flow (test first, then an
    /// explicit user-driven Save/SetRegion call). See report for the perf estimate and the
    /// candidate-grid design tradeoff (coarse corner/edge scan vs. full-screen exhaustive scan).
    /// </summary>
    public Task<AutoDetectResult> AutoDetectRegionAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var candidates = BuildCandidateRegions();

            ScreenRegion? bestRegion = null;
            string? bestText = null;
            var bestConfidence = float.MinValue;
            var scanned = 0;

            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                scanned++;

                string text;
                float confidence;
                try
                {
                    (text, confidence) = CaptureAndOcr(candidate);
                }
                catch
                {
                    // Candidate rectangle off-screen, transient capture failure, etc. — skip it,
                    // same "treat as no data" convention as the polling-loop CaptureAndRead.
                    continue;
                }

                var match = DeltaPattern.Match(text);
                if (!match.Success) continue;

                // Confidence is the primary ranking signal; when Tesseract doesn't return a usable
                // confidence (falls back to 0 above) or ties, prefer the longer matched substring
                // as a cheap proxy for "more of the recognized text was actually the number" —
                // an acceptable fallback per the task brief, not a rigorous quality measure.
                var score = confidence * 1000f + match.Value.Length;
                var bestScore = bestConfidence * 1000f + (bestText is null ? 0 : DeltaPattern.Match(bestText).Value.Length);
                if (bestRegion is null || score > bestScore)
                {
                    bestRegion = candidate;
                    bestText = text;
                    bestConfidence = confidence;
                }
            }

            if (bestRegion is null)
            {
                return new AutoDetectResult { Success = false, CandidatesScanned = scanned };
            }

            return new AutoDetectResult
            {
                Success = true,
                DetectedRegion = bestRegion,
                CandidatesScanned = scanned,
                SampleReading = bestText,
            };
        }, ct);
    }

    /// <summary>
    /// Coarse candidate grid: fixed 160x50 rectangles tiled across the outer 25% margin of the
    /// primary screen (corners + edges), where racing HUDs conventionally place delta/timing
    /// widgets. Deliberately NOT an exhaustive full-screen scan — that would be an order of
    /// magnitude more capture+OCR passes for a part of the screen (dead center) HUD elements
    /// essentially never occupy. See report for the resulting candidate count and perf estimate.
    /// </summary>
    private static List<ScreenRegion> BuildCandidateRegions()
    {
        const int candidateWidth = 160;
        const int candidateHeight = 50;

        // Fixed candidate counts per band (independent of screen resolution) so the scan stays a
        // predictable ~20-40 candidates regardless of monitor size: 5 evenly-spaced positions
        // along the top band and 5 along the bottom band, plus 4 down the left band and 4 down
        // the right band (excluding the four corners, already covered by the top/bottom rows) =
        // 5 + 5 + 4 + 4 = 18, doubled by scanning both an "outer" and "inner" row within each
        // margin band (HUD elements can sit flush against the edge or slightly inset) = 36 total.
        const int perEdgeCount = 5;
        const int perSideCount = 4;

        // UNVERIFIED (low risk): System.Windows.Forms.Screen.PrimaryScreen is non-null on any
        // real Windows desktop session; only null in headless/no-display contexts this app
        // doesn't run in. Bounds is in the same absolute-pixel screen space CopyFromScreen (and
        // ScreenRegion) already use elsewhere in this file.
        var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);

        var marginX = Math.Max(bounds.Width / 4, candidateWidth);
        var marginY = Math.Max(bounds.Height / 4, candidateHeight);

        var regions = new List<ScreenRegion>();
        var seen = new HashSet<(int X, int Y)>();

        void Add(int x, int y)
        {
            x = Math.Clamp(x, bounds.Left, bounds.Right - candidateWidth);
            y = Math.Clamp(y, bounds.Top, bounds.Bottom - candidateHeight);
            if (seen.Add((x, y)))
            {
                regions.Add(new ScreenRegion(x, y, candidateWidth, candidateHeight));
            }
        }

        // Two rows of "top band" candidates: one flush against the top edge, one inset toward
        // the bottom of the top margin (HUD elements are commonly anchored to the very edge or
        // sit just inside a safe-area inset).
        foreach (var y in new[] { bounds.Top, bounds.Top + marginY - candidateHeight })
        {
            for (var i = 0; i < perEdgeCount; i++)
            {
                var x = bounds.Left + i * (bounds.Width - candidateWidth) / (perEdgeCount - 1);
                Add(x, y);
            }
        }

        // Two rows of "bottom band" candidates, mirrored.
        foreach (var y in new[] { bounds.Bottom - candidateHeight, bounds.Bottom - marginY })
        {
            for (var i = 0; i < perEdgeCount; i++)
            {
                var x = bounds.Left + i * (bounds.Width - candidateWidth) / (perEdgeCount - 1);
                Add(x, y);
            }
        }

        // Left/right band candidates, vertically spaced within the middle (non-corner) portion
        // of the screen so they don't duplicate the top/bottom rows above.
        foreach (var x in new[] { bounds.Left, bounds.Left + marginX - candidateWidth, bounds.Right - candidateWidth, bounds.Right - marginX })
        {
            for (var i = 0; i < perSideCount; i++)
            {
                var y = bounds.Top + marginY + i * (bounds.Height - 2 * marginY - candidateHeight) / Math.Max(perSideCount - 1, 1);
                Add(x, y);
            }
        }

        return regions;
    }

    /// <summary>Simple grayscale + contrast-stretch preprocessing to help OCR reliability against
    /// a game HUD (small, often anti-aliased or colored digits over a semi-transparent background).
    /// Deliberately not a real image-processing pipeline — flagged as a v1 risk area in the report.</summary>
    private static Bitmap Preprocess(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var px = source.GetPixel(x, y);
                var gray = (byte)(0.299 * px.R + 0.587 * px.G + 0.114 * px.B);
                // Hard threshold: HUD delta text is normally high-contrast (white/green/red digits
                // on a dark or semi-transparent background). This is a blunt instrument — likely
                // needs tuning once tested against a real F1 25 HUD capture.
                var bw = gray > 140 ? (byte)255 : (byte)0;
                result.SetPixel(x, y, Color.FromArgb(bw, bw, bw));
            }
        }
        return result;
    }

    private static Pix ConvertToPix(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return Pix.LoadFromMemory(stream.ToArray());
    }

    public void Dispose()
    {
        // Timer.Dispose() (parameterless) does NOT block for an in-flight callback, so a Poll()
        // that's mid-capture/OCR on a thread-pool thread could still be touching _engine after we
        // disposed it below — a use-after-dispose race on the native Tesseract handle. The
        // Dispose(WaitHandle) overload signals the handle only once any running callback has
        // finished (and prevents new ones from starting), so waiting on it here guarantees Poll()
        // is fully quiesced before we touch _engine.
        using var disposedEvent = new ManualResetEvent(false);
        _timer.Dispose(disposedEvent);
        disposedEvent.WaitOne();

        // No deadlock risk against _engineLock here: by the time WaitOne() returns, Poll() is
        // fully quiesced (see above), so the only other possible holder of _engineLock at this
        // point is an in-flight AutoDetectRegionAsync scan. Dispose() doesn't wait on any handle
        // owned by AutoDetectRegionAsync (it's a plain lock, not tied to the timer's WaitHandle),
        // so we simply block here until that scan's current CaptureAndOcr call releases the lock,
        // then proceed - no circular wait. Taking the lock also guarantees we never dispose the
        // engine out from under an in-flight auto-detect OCR call.
        lock (_engineLock)
        {
            _engine?.Dispose();
        }
    }
}
