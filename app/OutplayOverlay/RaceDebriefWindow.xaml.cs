using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OutplayOverlay.Telemetry;

namespace OutplayOverlay;

/// <summary>
/// Post-session debrief popup. Purely a rendering of the SessionSummary handed to it by
/// MainWindow when the notification pill is clicked — this window never touches SessionLogger,
/// TelemetryHub, or SQLite directly.
/// </summary>
public partial class RaceDebriefWindow : Window
{
    // Judgment call (not specified by the design): "tight" vs. "loose" consistency threshold.
    // 0.5s std. dev. sits between SessionSummaryGenerator's own bullet thresholds (its bullets
    // fire at <0.3s "very consistent" and >1.0s "varied a lot"), so 0.5s is a reasonable midpoint
    // for a binary tight/loose color split here without inventing a third bucket.
    private const double ConsistencyTightThresholdSec = 0.5;

    public RaceDebriefWindow(SessionSummary summary)
    {
        InitializeComponent();
        Render(summary);
    }

    private void Render(SessionSummary summary)
    {
        TitleText.Text = $"RACE DEBRIEF — {summary.Sim}";
        SubtitleText.Text =
            $"Session started {summary.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm}   ·   Duration {summary.Duration:hh\\:mm\\:ss}";

        if (summary.LapCount == 0)
        {
            NoLapsText.Visibility = Visibility.Visible;
            ContentPanel.Visibility = Visibility.Collapsed;
            return;
        }

        NoLapsText.Visibility = Visibility.Collapsed;
        ContentPanel.Visibility = Visibility.Visible;

        RenderStatStrip(summary);
        RenderBulletColumn(PositivesPanel, summary.Positives, Brushes.LimeGreen);
        RenderBulletColumn(ImprovementsPanel, summary.Improvements, Brushes.Goldenrod);
        RenderLapTable(summary);
    }

    private void RenderStatStrip(SessionSummary summary)
    {
        if (summary.BestLap is { } best)
        {
            BestLapTimeText.Text = FormatLapTime(best.LapTimeSec);
            BestLapNumberText.Text = $"Lap {best.LapNumber}";
        }

        if (summary.WorstLap is { } worst)
        {
            WorstLapTimeText.Text = FormatLapTime(worst.LapTimeSec);
            WorstLapNumberText.Text = $"Lap {worst.LapNumber}";
        }

        // Consistency and Trend both require 2+ laps to mean anything. LapTimeStdDevSec is
        // already null below 2 laps per SessionSummaryGenerator.ComputeStdDev's own documented
        // behavior, so we key off that nullability directly rather than re-deriving LapCount==1.
        if (summary.LapCount < 2 || summary.LapTimeStdDevSec is not double stdDev)
        {
            ConsistencyText.Text = "--";
            ConsistencyText.Foreground = Brushes.Gray;
            ConsistencySubText.Text = "(need 2+ laps)";

            TrendText.Text = "--";
            TrendText.Foreground = Brushes.Gray;
            return;
        }

        var tight = stdDev < ConsistencyTightThresholdSec;
        ConsistencyText.Text = $"σ {stdDev:F2}s";
        ConsistencyText.Foreground = tight ? Brushes.LimeGreen : Brushes.Goldenrod;
        ConsistencySubText.Text = tight ? "(tight)" : "(loose)";

        switch (summary.Trend)
        {
            case LapTimeTrend.Improving:
                TrendText.Text = "▲ Improving";
                TrendText.Foreground = Brushes.LimeGreen;
                break;
            case LapTimeTrend.Worsening:
                // Diagnostic, not an error state — Goldenrod (not OrangeRed, which this app
                // reserves for actual error/alert states elsewhere, e.g. brake bar / OCR garble).
                TrendText.Text = "▼ Worsening";
                TrendText.Foreground = Brushes.Goldenrod;
                break;
            default:
                TrendText.Text = "▬ Flat";
                TrendText.Foreground = Brushes.Gray;
                break;
        }
    }

    private static void RenderBulletColumn(StackPanel panel, IReadOnlyList<string> items, Brush bulletColor)
    {
        panel.Children.Clear();

        if (items.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Not enough laps for pattern analysis",
                Foreground = Brushes.Gray,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var item in items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            row.Children.Add(new TextBlock
            {
                Text = "● ",
                Foreground = bulletColor,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Top,
            });
            row.Children.Add(new TextBlock
            {
                Text = item,
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 260,
            });
            panel.Children.Add(row);
        }
    }

    private void RenderLapTable(SessionSummary summary)
    {
        LapRowsPanel.Children.Clear();

        var bestLapNumber = summary.BestLap?.LapNumber;

        foreach (var lap in summary.Laps)
        {
            var isBest = bestLapNumber is not null && lap.LapNumber == bestLapNumber;

            var grid = new Grid
            {
                Margin = new Thickness(0, 1, 0, 1),
                Background = isBest ? new SolidColorBrush(Color.FromArgb(0x33, 0x32, 0xCD, 0x32)) : Brushes.Transparent,
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

            var textColor = isBest ? Brushes.LimeGreen : Brushes.White;
            var weight = isBest ? FontWeights.Bold : FontWeights.Normal;

            var gutter = MakeCell(isBest ? "▶" : "", textColor, weight);
            Grid.SetColumn(gutter, 0);

            var lapCell = MakeCell(lap.LapNumber.ToString(), textColor, weight);
            Grid.SetColumn(lapCell, 1);

            var timeCell = MakeCell(FormatLapTime(lap.LapTimeSec), textColor, weight);
            Grid.SetColumn(timeCell, 2);

            TextBlock deltaCell;
            if (isBest)
            {
                deltaCell = MakeCell("★ BEST", Brushes.LimeGreen, FontWeights.Bold);
            }
            else if (lap.DeltaToBestSec is float delta)
            {
                // DeltaToBestSec is the sim's own live delta-to-best telemetry field (passed
                // through verbatim from TelemetrySample via SessionLogger), NOT recomputed
                // against this session's own best lap by SessionSummaryGenerator — so its sign
                // is not guaranteed positive. Format with an explicit sign rather than assuming.
                deltaCell = MakeCell($"{delta:+0.000;-0.000}", textColor, weight);
            }
            else
            {
                deltaCell = MakeCell("--", Brushes.Gray, FontWeights.Normal);
            }
            Grid.SetColumn(deltaCell, 3);

            var bestFlagCell = MakeCell(isBest ? "★" : "", textColor, weight);
            Grid.SetColumn(bestFlagCell, 4);

            var throttleCell = MakeCell($"{lap.AvgThrottle * 100:0}%", textColor, weight);
            Grid.SetColumn(throttleCell, 5);

            var brakeCell = MakeCell($"{lap.AvgBrake * 100:0}%", textColor, weight);
            Grid.SetColumn(brakeCell, 6);

            grid.Children.Add(gutter);
            grid.Children.Add(lapCell);
            grid.Children.Add(timeCell);
            grid.Children.Add(deltaCell);
            grid.Children.Add(bestFlagCell);
            grid.Children.Add(throttleCell);
            grid.Children.Add(brakeCell);

            LapRowsPanel.Children.Add(grid);
        }
    }

    private static TextBlock MakeCell(string text, Brush color, FontWeight weight) => new()
    {
        Text = text,
        Foreground = color,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 11,
        FontWeight = weight,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static string FormatLapTime(float lapTimeSec)
    {
        var minutes = (int)(lapTimeSec / 60);
        var seconds = lapTimeSec - minutes * 60;
        return $"{minutes}:{seconds:00.000}";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
