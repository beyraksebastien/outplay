using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OutplayOverlay.Telemetry;

namespace OutplayOverlay;

public partial class MainWindow : Window
{
    private readonly TelemetryHub _hub = new();
    private readonly CoachEngine _coach;
    private readonly SessionLogger _sessionLogger;
    private readonly CornerIntelligenceEngine _cornerIntel;
    private readonly SpeechSynthesizer _speech = new();
    private string? _activeSim;
    private PushHoldState? _lastSpokenState;
    private PushHoldState? _pendingSpokenState;
    private DateTime _pendingSpokenStateSinceUtc;
    private DateTime _lastSpeechUtc = DateTime.MinValue;
    private static readonly TimeSpan SpokenStateDebounce = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan MinTimeBetweenSpeech = TimeSpan.FromSeconds(2);
    private ScreenDeltaReading? _lastScreenDeltaReading;
    private SessionSummary? _latestSummary;
    private SessionCornerReport? _latestCornerReport;

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            // Explicit, even though System.Speech is documented to default here - being explicit
            // avoids depending on that default and makes an audio-routing failure show up as a
            // caught exception (logged below) instead of silent nothing.
            _speech.SetOutputToDefaultAudioDevice();
            _speech.Volume = 100;
            _speech.Rate = 0;

            // Unconditional startup announcement, bypassing all coaching/debounce logic entirely -
            // isolates "is the audio pipeline broken" from "is the coaching state logic broken".
            // If you don't hear this at launch, the problem is System.Speech/Windows audio routing,
            // not anything in CoachEngine or the debounce logic added afterward.
            _speech.SpeakAsync("Testing text to speech");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Speech] Failed to initialize audio output: {ex}");
        }

        _hub.SampleReceived += OnSample;
        _hub.SourceConnectionChanged += OnConnectionChanged;
        _hub.ScreenDelta.DeltaRead += OnScreenDeltaRead;

        _coach = new CoachEngine(_hub);
        _coach.SignalReceived += OnCoachingSignal;

        _sessionLogger = new SessionLogger(_hub);
        _sessionLogger.SummaryReady += OnSummaryReady;

        _cornerIntel = new CornerIntelligenceEngine(_hub);

        _hub.StartAll();

        Closed += (_, _) =>
        {
            _coach.Dispose();
            _sessionLogger.Dispose();
            _cornerIntel.Dispose();
            _hub.Dispose();
            _speech.Dispose();
        };

        // Some games re-grab topmost/focus even in windowed/borderless mode.
        // Note: this cannot beat exclusive Fullscreen — no overlay can render
        // over that, since it bypasses the desktop compositor. Use Borderless
        // or Windowed mode in the game's display settings.
        var reassertTopmost = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        reassertTopmost.Tick += (_, _) =>
        {
            if (!Topmost)
            {
                Topmost = true;
            }
        };
        reassertTopmost.Start();
    }

    private void OnConnectionChanged(string sim, bool connected)
    {
        Dispatcher.Invoke(() =>
        {
            if (connected)
            {
                _activeSim = sim;
                StatusText.Text = $"Connected: {sim}";
                StatusText.Foreground = System.Windows.Media.Brushes.LimeGreen;
            }
            else if (_activeSim == sim)
            {
                _activeSim = null;
                StatusText.Text = "Waiting for sim...";
                StatusText.Foreground = System.Windows.Media.Brushes.Orange;
            }
        });
    }

    private void OnSample(TelemetrySample sample)
    {
        // Only render whichever sim is currently active, in case both adapters
        // are technically running (e.g. iRacing idle in background).
        if (_activeSim != null && sample.Sim != _activeSim) return;

        Dispatcher.Invoke(() =>
        {
            SpeedText.Text = $"{sample.Speed:0} km/h";
            ThrottleBar.Value = sample.Throttle;
            BrakeBar.Value = sample.Brake;
            SteeringBar.Value = sample.Steering;
            GearText.Text = sample.Gear switch
            {
                -1 => "R",
                0 => "N",
                _ => sample.Gear.ToString(),
            };
        });
    }

    private void OnCoachingSignal(CoachingSignal signal)
    {
        Dispatcher.Invoke(() =>
        {
            CoachNoSignalText.Visibility = Visibility.Collapsed;
            CoachPanel.Visibility = Visibility.Visible;

            switch (signal.State)
            {
                case PushHoldState.Push:
                    CoachStateText.Text = "▲▲▲ PUSH";
                    CoachStateText.Foreground = Brushes.LimeGreen;
                    break;
                case PushHoldState.BackingOff:
                    CoachStateText.Text = "▼▼▼ BACKING OFF";
                    CoachStateText.Foreground = Brushes.OrangeRed;
                    break;
                default:
                    CoachStateText.Text = "─ STEADY";
                    CoachStateText.Foreground = Brushes.White;
                    break;
            }

            if (signal.DeltaToBestSec is float delta)
            {
                CoachDeltaText.Text = $"{delta:+0.000;-0.000}s";
                CoachDeltaText.Foreground = delta < 0 ? Brushes.LimeGreen : Brushes.OrangeRed;
            }
            else
            {
                CoachDeltaText.Text = "--";
                CoachDeltaText.Foreground = Brushes.Gray;
            }

            // OCR fallback status tag. Only F1 25 ever wires ScreenDelta into DeltaToBestSec
            // (see F125TelemetrySource), so we only show the tag while F1 25 is the active sim.
            if (!_hub.ScreenDelta.IsEnabled || _activeSim != "F1_25")
            {
                CoachOcrTagText.Visibility = Visibility.Collapsed;
            }
            else if (signal.DeltaToBestSec is null)
            {
                CoachOcrTagText.Visibility = Visibility.Visible;
                CoachOcrTagText.Text = "[OCR: searching...]";
                CoachOcrTagText.Foreground = Brushes.Gray;
                CoachOcrTagText.ToolTip = null;
            }
            else
            {
                CoachOcrTagText.Visibility = Visibility.Visible;
                CoachOcrTagText.Text = "[OCR]";
                CoachOcrTagText.Foreground = Brushes.Gray;
                var raw = _lastScreenDeltaReading?.RawText ?? "--";
                var ts = _lastScreenDeltaReading?.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff") ?? "--";
                CoachOcrTagText.ToolTip = $"raw: \"{raw}\" @ {ts}";
            }

            if (signal.GapToCarAheadSec is float gap)
            {
                CoachGapText.Text = $"{gap:0.0}s";
                CoachGapText.Foreground = Brushes.White;
            }
            else
            {
                CoachGapText.Text = "--";
                CoachGapText.Foreground = Brushes.Gray;
            }

            if (signal.PlayerTireCompound is null && signal.CarAheadTireCompound is null)
            {
                CoachTiresText.Visibility = Visibility.Collapsed;
            }
            else
            {
                CoachTiresText.Visibility = Visibility.Visible;
                var you = signal.PlayerTireCompound ?? "--";
                var ahead = signal.CarAheadTireCompound ?? "--";
                CoachTiresText.Text = $"YOU {you}   ·   AHEAD {ahead}";
            }

            // Voice callout only on a PushHoldState transition that HOLDS for a debounce window,
            // not every tick (CoachEngine fires per telemetry sample, i.e. up to ~60Hz). Without
            // this, a delta hovering right at CoachEngine's push/hold deadband can flip state
            // every single tick, spamming SpeakAsyncCancelAll()+SpeakAsync() fast enough to wedge
            // the underlying SAPI engine into a broken state (observed: voice works once, then
            // never again). A minimum time-between-speech floor is a second, independent guard
            // against the same failure mode even if the debounce window itself is too short for
            // some noisy signal.
            var now = DateTime.UtcNow;

            if (signal.State != _pendingSpokenState)
            {
                _pendingSpokenState = signal.State;
                _pendingSpokenStateSinceUtc = now;
            }
            else if (signal.State != _lastSpokenState
                     && now - _pendingSpokenStateSinceUtc >= SpokenStateDebounce
                     && now - _lastSpeechUtc >= MinTimeBetweenSpeech)
            {
                _lastSpokenState = signal.State;
                _lastSpeechUtc = now;
                var phrase = signal.State switch
                {
                    PushHoldState.Push => "Push",
                    PushHoldState.BackingOff => "Backing off",
                    _ => "Steady",
                };
                try
                {
                    _speech.SpeakAsyncCancelAll();
                    _speech.SpeakAsync(phrase);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Speech] SpeakAsync('{phrase}') failed: {ex}");
                }
            }
        });
    }

    private void OnScreenDeltaRead(ScreenDeltaReading reading)
    {
        Dispatcher.Invoke(() =>
        {
            _lastScreenDeltaReading = reading;
        });
    }

    private void OnSummaryReady(SessionSummary summary)
    {
        // Persists until clicked or superseded by a new summary (no auto-hide timer) — a new
        // summary here simply overwrites the previous one and keeps the pill visible/re-shown.
        Dispatcher.Invoke(() =>
        {
            _latestSummary = summary;
            // Built at the same moment the summary is captured, so the debrief's SEGMENTS section
            // reflects the session that just ended (BuildSessionReport is a cheap in-memory
            // aggregation over CornerIntelligenceEngine's already-accumulated completed laps).
            _latestCornerReport = _cornerIntel.BuildSessionReport();
            DebriefPill.Visibility = Visibility.Visible;
        });
    }

    private void DebriefPill_Click(object sender, MouseButtonEventArgs e)
    {
        if (_latestSummary is null) return;
        DebriefPill.Visibility = Visibility.Collapsed;
        var debrief = new RaceDebriefWindow(_latestSummary, _latestCornerReport) { Owner = this };
        debrief.Show();
    }

    private void TrendsButton_Click(object sender, RoutedEventArgs e)
    {
        var trends = new TrendsWindow { Owner = this };
        trends.Show();
    }

    private void GearButton_Click(object sender, RoutedEventArgs e)
    {
        // Initialize the flyout's checkbox/region display from current reader state every time
        // it opens, since the user (or a calibration save) may have changed things since last open.
        OcrEnabledCheck.IsChecked = _hub.ScreenDelta.IsEnabled;
        UpdateRegionText();
        GearPopup.IsOpen = GearButton.IsChecked == true;
    }

    private void GearPopup_Closed(object? sender, EventArgs e)
    {
        GearButton.IsChecked = false;
    }

    private void UpdateRegionText()
    {
        var r = _hub.ScreenDelta.Region;
        OcrRegionText.Text = $"Region: {r.X},{r.Y} {r.Width}x{r.Height}";
    }

    private void OcrEnabledCheck_Checked(object sender, RoutedEventArgs e) => _hub.ScreenDelta.Enable();

    private void OcrEnabledCheck_Unchecked(object sender, RoutedEventArgs e) => _hub.ScreenDelta.Disable();

    private void CalibrateButton_Click(object sender, RoutedEventArgs e)
    {
        GearPopup.IsOpen = false;
        var calibration = new CalibrationWindow(_hub.ScreenDelta) { Owner = this };
        calibration.ShowDialog();
        UpdateRegionText();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }
}
