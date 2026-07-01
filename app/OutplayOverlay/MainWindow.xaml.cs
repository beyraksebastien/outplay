using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OutplayOverlay.Telemetry;

namespace OutplayOverlay;

public partial class MainWindow : Window
{
    private readonly TelemetryHub _hub = new();
    private string? _activeSim;

    public MainWindow()
    {
        InitializeComponent();

        _hub.SampleReceived += OnSample;
        _hub.SourceConnectionChanged += OnConnectionChanged;
        _hub.StartAll();

        Closed += (_, _) => _hub.Dispose();

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

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }
}
