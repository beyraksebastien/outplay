using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OutplayOverlay.Telemetry;
// Disambiguates against System.Drawing.Color/FontFamily/Brushes and
// System.Windows.Forms.HorizontalAlignment, all implicitly in scope project-wide now that
// UseWindowsForms=true is set alongside UseWPF=true.
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using FontFamily = System.Windows.Media.FontFamily;
using Brushes = System.Windows.Media.Brushes;

namespace OutplayOverlay;

/// <summary>
/// Cross-session "View Trends" popup. Pulls from HistoryQueryService on demand (pull-based, not
/// event-driven — same contract HistoryQueryService itself documents) — this window never touches
/// SessionLogger or SQLite directly.
/// </summary>
public partial class TrendsWindow : Window
{
    // Bar chart normalization/coloring: fixed-width Border rectangles, no chart library. Each bar's
    // fill width is (worst - value) / (worst - best) within the RecentSessionBests window being
    // shown (so the fastest lap in the window always renders full-width, the slowest empty). The
    // single fastest bar in the window is LimeGreen; every other bar is White on a Gray track.
    private const double BarMaxWidth = 260;

    private TrendResult? _iracing;
    private TrendResult? _f125;
    private string _selectedSim = "iRacing";

    public TrendsWindow()
    {
        InitializeComponent();

        _iracing = HistoryQueryService.GetTrend("iRacing");
        _f125 = HistoryQueryService.GetTrend("F1_25");

        // Only show the tab strip when both sims actually have history — otherwise there's nothing
        // to switch to, and the strip would just be visual noise.
        if (_iracing is not null && _f125 is not null)
        {
            TabStrip.Visibility = Visibility.Visible;
            _selectedSim = "iRacing";
        }
        else if (_iracing is not null)
        {
            _selectedSim = "iRacing";
        }
        else if (_f125 is not null)
        {
            _selectedSim = "F1_25";
        }
        else
        {
            _selectedSim = "iRacing"; // arbitrary — empty state doesn't care which sim name shows
        }

        Render();
    }

    private void Render()
    {
        var trend = _selectedSim == "iRacing" ? _iracing : _f125;
        var displayName = _selectedSim == "iRacing" ? "iRacing" : "F1 25";
        TitleText.Text = $"TRENDS — {displayName}";

        UpdateTabHighlight();

        if (trend is null)
        {
            EmptyStateText.Visibility = Visibility.Visible;
            EmptyStateText.Text =
                $"No session history yet for {displayName}. Drive a full session and Outplay will start tracking your progress here.";
            ContentPanel.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyStateText.Visibility = Visibility.Collapsed;
        ContentPanel.Visibility = Visibility.Visible;

        SessionsText.Text = trend.SessionCount.ToString();
        LapsText.Text = trend.TotalLapsLogged.ToString();
        BestLapEverText.Text = RaceDebriefWindow.FormatLapTime(trend.BestLapTimeSec);

        var since = trend.ImprovementSinceFirstSessionSec;
        SinceFirstText.Text = $"{since:+0.000;-0.000}s";
        SinceFirstText.Foreground = since > 0 ? Brushes.LimeGreen : (since < 0 ? Brushes.OrangeRed : Brushes.Gray);

        switch (trend.RecentTrend)
        {
            case LapTimeTrend.Improving:
                RecentTrendText.Text = "▲ IMPROVING";
                RecentTrendText.Foreground = Brushes.LimeGreen;
                break;
            case LapTimeTrend.Worsening:
                RecentTrendText.Text = "▼ WORSENING";
                RecentTrendText.Foreground = Brushes.OrangeRed;
                break;
            default:
                RecentTrendText.Text = "▬ FLAT";
                RecentTrendText.Foreground = Brushes.Gray;
                break;
        }

        RenderSessionBars(trend.RecentSessionBests);
    }

    private void RenderSessionBars(IReadOnlyList<SessionBestLap> sessions)
    {
        SessionBarsPanel.Children.Clear();

        if (sessions.Count == 0) return;

        var best = sessions.Min(s => s.BestLapTimeSec);
        var worst = sessions.Max(s => s.BestLapTimeSec);
        var range = worst - best;

        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            var isBestOfWindow = session.BestLapTimeSec == best;

            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BarMaxWidth + 4) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var indexCell = new TextBlock
            {
                Text = (i + 1).ToString(),
                Foreground = isBestOfWindow ? Brushes.LimeGreen : Brushes.Gray,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(indexCell, 0);

            var timeCell = new TextBlock
            {
                Text = RaceDebriefWindow.FormatLapTime(session.BestLapTimeSec),
                Foreground = isBestOfWindow ? Brushes.LimeGreen : Brushes.White,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(timeCell, 1);

            var ratio = range > 0 ? (worst - session.BestLapTimeSec) / range : 1.0;
            var fillWidth = Math.Max(2, ratio * BarMaxWidth);

            var barTrack = new Border
            {
                Width = BarMaxWidth,
                Height = 12,
                Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var barFill = new Border
            {
                Width = fillWidth,
                Height = 12,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = isBestOfWindow ? Brushes.LimeGreen : Brushes.White,
            };
            barTrack.Child = barFill;
            Grid.SetColumn(barTrack, 2);

            var dateCell = new TextBlock
            {
                Text = session.StartUtc.ToLocalTime().ToString("MM/dd"),
                Foreground = Brushes.Gray,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(dateCell, 3);

            row.Children.Add(indexCell);
            row.Children.Add(timeCell);
            row.Children.Add(barTrack);
            row.Children.Add(dateCell);

            SessionBarsPanel.Children.Add(row);
        }
    }

    private void UpdateTabHighlight()
    {
        IRacingTabButton.Foreground = _selectedSim == "iRacing" ? Brushes.White : Brushes.Gray;
        F125TabButton.Foreground = _selectedSim == "F1_25" ? Brushes.White : Brushes.Gray;
    }

    private void IRacingTabButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedSim = "iRacing";
        Render();
    }

    private void F125TabButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedSim = "F1_25";
        Render();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
