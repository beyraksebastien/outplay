using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OutplayOverlay.Telemetry;
// Disambiguates against System.Drawing.Color/Brush/Brushes/FontFamily and
// System.Windows.Forms.Orientation, all implicitly in scope project-wide now that
// UseWindowsForms=true is set alongside UseWPF=true.
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using FontFamily = System.Windows.Media.FontFamily;

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

    // SEGMENTS heat colors (iRacing case, higher-confidence DeltaToBest signal): four buckets by
    // TimeLossSec, gray-to-OrangeRed gradient — darker/neutral for negligible loss, saturating to
    // OrangeRed for the worst bucket. Documented here since these thresholds/colors are a judgment
    // call, not something CornerIntelligenceEngine itself specifies.
    private static readonly (double MaxSec, Brush Brush)[] SegmentHeatBuckets =
    {
        (0.05, new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44))),   // <0.05s lost
        (0.15, new SolidColorBrush(Color.FromRgb(0x8A, 0x6D, 0x3A))),   // 0.05-0.15s
        (0.30, new SolidColorBrush(Color.FromRgb(0xC1, 0x5A, 0x2E))),   // 0.15-0.30s
        (double.MaxValue, new SolidColorBrush(Color.FromRgb(0xE0, 0x38, 0x1A))), // >0.30s
    };

    public RaceDebriefWindow(SessionSummary summary, SessionCornerReport? cornerReport = null)
    {
        InitializeComponent();
        Render(summary, cornerReport);
    }

    private void Render(SessionSummary summary, SessionCornerReport? cornerReport)
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
        RenderSegments(summary, cornerReport);
        RenderBulletColumn(PositivesPanel, summary.Positives, Brushes.LimeGreen);
        RenderBulletColumn(ImprovementsPanel, summary.Improvements, Brushes.Goldenrod);
        RenderLapTable(summary);
    }

    /// <summary>
    /// SEGMENTS section: distance-based, approximate corner breakdown from CornerIntelligenceEngine.
    /// F1_25 gets a visibly lower-confidence rendering (muted/dashed cells, amber disclaimer) since
    /// its DeltaToBestSec only updates once per lap — see SegmentStats.TimeLossSec's doc comment.
    /// </summary>
    private void RenderSegments(SessionSummary summary, SessionCornerReport? cornerReport)
    {
        var isF125 = summary.Sim == "F1_25";
        F125DisclaimerText.Visibility = isF125 ? Visibility.Visible : Visibility.Collapsed;

        if (cornerReport is null || cornerReport.AggregatedBySegment.Count == 0)
        {
            WorstSegmentText.Text = "Worst segment: -- (not enough data)";
            WorstSegmentText.Foreground = Brushes.Gray;
            SegmentCellsPanel.Children.Clear();
            LegendPanel.Visibility = Visibility.Collapsed;
            return;
        }

        LegendPanel.Visibility = isF125 ? Visibility.Collapsed : Visibility.Visible;

        if (cornerReport.WorstSegmentIndex is int worstIndex)
        {
            var worstStats = cornerReport.AggregatedBySegment.First(s => s.SegmentIndex == worstIndex);
            var lossText = worstStats.TimeLossSec is float loss ? $"{loss:F2}s" : "--";
            WorstSegmentText.Text = isF125
                ? $"Worst segment: #{worstIndex + 1} (approximate) · lap-boundary segment, low confidence"
                : $"Worst segment: #{worstIndex + 1} · lost {lossText} on average";
            WorstSegmentText.Foreground = isF125 ? Brushes.Goldenrod : Brushes.White;
        }
        else
        {
            WorstSegmentText.Text = "Worst segment: -- (not enough data)";
            WorstSegmentText.Foreground = Brushes.Gray;
        }

        SegmentCellsPanel.Children.Clear();
        for (var i = 0; i < CornerIntelligenceEngine.SegmentCount; i++)
        {
            var index = i;
            var stats = cornerReport.AggregatedBySegment.FirstOrDefault(s => s.SegmentIndex == index);

            var cell = new Border
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(2, 0, 2, 0),
            };

            if (isF125)
            {
                // Muted/dashed treatment: lighter border, gray fill, no heat color — visibly lower
                // confidence than the iRacing case, per the approved mockup.
                cell.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
                cell.BorderBrush = Brushes.Gray;
                cell.BorderThickness = new Thickness(1);
            }
            else
            {
                cell.Background = stats is not null ? BucketBrush(stats.TimeLossSec) : new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
            }

            SegmentCellsPanel.Children.Add(cell);
        }
    }

    private static Brush BucketBrush(float? timeLossSec)
    {
        if (timeLossSec is not float loss || loss < 0) loss = 0;
        foreach (var (maxSec, brush) in SegmentHeatBuckets)
        {
            if (loss < maxSec) return brush;
        }
        return SegmentHeatBuckets[^1].Brush;
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

    /// <summary>Internal (not private) so other windows in this app — e.g. TrendsWindow — can
    /// reuse the exact same m:ss.fff formatting instead of reimplementing it.</summary>
    internal static string FormatLapTime(float lapTimeSec)
    {
        var minutes = (int)(lapTimeSec / 60);
        var seconds = lapTimeSec - minutes * 60;
        return $"{minutes}:{seconds:00.000}";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
