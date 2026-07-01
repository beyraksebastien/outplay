using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OutplayOverlay.Telemetry;

namespace OutplayOverlay;

/// <summary>
/// Region-picker / "Test Region" UI for ScreenDeltaReader. Talks to the shared
/// TelemetryHub.ScreenDelta instance passed in by MainWindow (there's exactly one reader in the
/// app; this window never constructs its own).
/// </summary>
public partial class CalibrationWindow : Window
{
    private readonly ScreenDeltaReader _reader;
    private readonly ScreenRegion _originalRegion;
    private readonly bool _originalEnabled;

    // Guards against a stray DeltaRead firing after we've already finished/timed-out a test,
    // since ScreenDeltaReader's timer keeps polling on its own schedule independent of us.
    private bool _testInProgress;
    private DispatcherTimer? _testTimeoutTimer;

    public CalibrationWindow(ScreenDeltaReader reader)
    {
        InitializeComponent();

        _reader = reader;
        _originalRegion = reader.Region;
        _originalEnabled = reader.IsEnabled;

        XBox.Text = _originalRegion.X.ToString(CultureInfo.InvariantCulture);
        YBox.Text = _originalRegion.Y.ToString(CultureInfo.InvariantCulture);
        WidthBox.Text = _originalRegion.Width.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = _originalRegion.Height.ToString(CultureInfo.InvariantCulture);
    }

    private bool TryReadRegionFromBoxes(out ScreenRegion region)
    {
        region = new ScreenRegion(0, 0, 0, 0);
        if (!int.TryParse(XBox.Text, out var x)) return false;
        if (!int.TryParse(YBox.Text, out var y)) return false;
        if (!int.TryParse(WidthBox.Text, out var w) || w <= 0) return false;
        if (!int.TryParse(HeightBox.Text, out var h) || h <= 0) return false;
        region = new ScreenRegion(x, y, w, h);
        return true;
    }

    private void TestRegionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_testInProgress) return;

        if (!TryReadRegionFromBoxes(out var region))
        {
            TestResultText.Text = "Last test: enter valid whole numbers for X/Y/Width/Height first";
            TestResultText.Foreground = Brushes.OrangeRed;
            return;
        }

        // ScreenDeltaReader's only surface is SetRegion/Enable/Disable + the DeltaRead event —
        // there's no single-shot "read now" method. So a "test" here means: point the reader at
        // the candidate region, make sure its poll loop is running, wait for the next tick's
        // DeltaRead, then immediately undo whatever we changed (region + enabled-state) so
        // Cancel/Close never leaves side effects beyond what Save explicitly commits.
        _reader.SetRegion(region);

        var wasEnabled = _reader.IsEnabled;
        if (!wasEnabled) _reader.Enable();

        _testInProgress = true;
        TestRegionButton.IsEnabled = false;
        TestResultText.Text = "Last test: reading...";
        TestResultText.Foreground = Brushes.Gray;

        void OnRead(ScreenDeltaReading reading)
        {
            Dispatcher.Invoke(() =>
            {
                if (!_testInProgress) return; // already timed out
                FinishTest(wasEnabled, reading);
            });
        }

        _reader.DeltaRead += OnRead;

        // Defensive timeout: the reader polls on its own ~250ms timer, but if it's mid-capture or
        // something throws before ever invoking the event, don't leave the UI stuck on "reading...".
        _testTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _testTimeoutTimer.Tick += (_, _) =>
        {
            if (!_testInProgress) return;
            FinishTest(wasEnabled, null);
        };
        _testTimeoutTimer.Start();

        // Local function capture of OnRead/timer needs cleanup in FinishTest; store via closure.
        _pendingUnsubscribe = () => _reader.DeltaRead -= OnRead;

        void FinishTest(bool restoreEnabled, ScreenDeltaReading? reading)
        {
            _testInProgress = false;
            _testTimeoutTimer?.Stop();
            _testTimeoutTimer = null;
            _pendingUnsubscribe?.Invoke();
            _pendingUnsubscribe = null;

            if (!restoreEnabled) _reader.Disable();

            TestRegionButton.IsEnabled = true;

            if (reading is null)
            {
                TestResultText.Text = "Last test: no reading came back (timed out) — check the region and try again";
                TestResultText.Foreground = Brushes.Gray;
            }
            else if (reading.Success)
            {
                TestResultText.Text = $"Last test: OK — read \"{reading.RawText}\"";
                TestResultText.Foreground = Brushes.LimeGreen;
            }
            else if (string.IsNullOrEmpty(reading.RawText))
            {
                TestResultText.Text = "Last test: no match — nothing recognizable in that region";
                TestResultText.Foreground = Brushes.Gray;
            }
            else
            {
                TestResultText.Text = $"Last test: garbled — raw: \"{reading.RawText}\"";
                TestResultText.Foreground = Brushes.OrangeRed;
            }
        }
    }

    private Action? _pendingUnsubscribe;

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadRegionFromBoxes(out var region))
        {
            TestResultText.Text = "Last test: enter valid whole numbers for X/Y/Width/Height before saving";
            TestResultText.Foreground = Brushes.OrangeRed;
            return;
        }

        _reader.SetRegion(region);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => CancelAndClose();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CancelAndClose();

    private void CancelAndClose()
    {
        // Undo any side effect Test Region left behind (it always restores enabled-state itself,
        // but the region it set via SetRegion(...) needs restoring here since it isn't reverted
        // automatically once the test read completes).
        _reader.SetRegion(_originalRegion);
        if (_reader.IsEnabled != _originalEnabled)
        {
            if (_originalEnabled) _reader.Enable(); else _reader.Disable();
        }
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _pendingUnsubscribe?.Invoke();
        _testTimeoutTimer?.Stop();
        base.OnClosed(e);
    }
}
