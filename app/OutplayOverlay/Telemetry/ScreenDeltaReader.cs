using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Tesseract;

namespace OutplayOverlay.Telemetry;

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
    private readonly Timer _timer;
    private bool _enabled;
    private bool _busy;
    private ScreenRegion _region = new(X: 100, Y: 100, Width: 160, Height: 50); // placeholder, must be calibrated

    // Lazily created on first enabled poll, since constructing a TesseractEngine loads trained-data
    // and is too slow to do per-tick or in the constructor of a component that may never be enabled.
    private TesseractEngine? _engine;

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
        _timer = new Timer(_ => Poll(), null, interval, interval);
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

        _engine ??= new TesseractEngine("./tessdata", "eng", EngineMode.Default);
        // Restrict recognition to characters that can appear in a signed 3-decimal delta, to cut
        // down on misreads of stray HUD pixels as garbage characters.
        _engine.SetVariable("tessedit_char_whitelist", "-+0123456789.");

        using var pix = ConvertToPix(preprocessed);
        using var page = _engine.Process(pix, PageSegMode.SingleLine);
        var text = page.GetText()?.Trim() ?? string.Empty;

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
        _engine?.Dispose();
    }
}
