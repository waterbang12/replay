using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LifeReplay.Analysis;
using LifeReplay.Core;

namespace LifeReplay.Viewer;

/// <summary>Summary → Apps: time per app, with the tabs / documents under each.</summary>
public partial class MainWindow
{
    // ------------------------------------------------------------------ time per app / tab

    private void RenderApps()
    {
        if (_timeline == null) return;
        var apps = FocusSummary.Summarize(_timeline.Focus, _timeline.DayStartMs, _timeline.DayEndMs);
        long total = apps.Sum(a => a.DurationMs);
        SummaryTitle.Text = $"Time per app · {_day:ddd, MMM d}";
        SummarySubtitle.Text = apps.Count == 0
            ? "No window titles logged for this day. Logging starts when the recorder runs this version (Settings → Privacy)."
            : $"{TimeUtil.FormatDuration(total)} of active use. Idle time and pauses are not counted. Click an item to jump to it.";
        SummaryList.Children.Clear();
        var muted = (System.Windows.Media.Brush)FindResource("Muted");
        foreach (var app in apps)
        {
            var header = new DockPanel { Margin = new Thickness(0, 10, 0, 2) };
            var dur = new TextBlock { Text = TimeUtil.FormatDuration(app.DurationMs), FontWeight = FontWeights.SemiBold };
            DockPanel.SetDock(dur, Dock.Right);
            header.Children.Add(dur);
            header.Children.Add(new TextBlock { Text = app.App, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            SummaryList.Children.Add(header);
            // Share bar
            SummaryList.Children.Add(new Border
            {
                Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4),
                Width = Math.Max(2, 330 * app.DurationMs / (double)Math.Max(1, total)), Background = (System.Windows.Media.Brush)FindResource("Accent"),
            });
            foreach (var item in app.Items.Take(12))
            {
                var row = new DockPanel { Margin = new Thickness(10, 1, 0, 1), Cursor = Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent,
                                          ToolTip = item.Item };
                var d = new TextBlock { Text = TimeUtil.FormatDuration(item.DurationMs), Foreground = muted, FontSize = 12 };
                DockPanel.SetDock(d, Dock.Right);
                row.Children.Add(d);
                row.Children.Add(new TextBlock { Text = item.Item, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) });
                var appName = app.App;
                var itemName = item.Item;
                row.MouseLeftButtonUp += (_, _) =>
                {
                    var first = _timeline.Focus.FirstOrDefault(f => FocusSummary.AppName(f.Process) == appName && FocusSummary.ItemName(f.Process, f.Title) == itemName);
                    if (first != null) Seek(first.StartUtcMs);
                };
                SummaryList.Children.Add(row);
            }
            if (app.Items.Count > 12)
                SummaryList.Children.Add(new TextBlock { Text = $"+ {app.Items.Count - 12} more", Foreground = muted, FontSize = 12, Margin = new Thickness(10, 1, 0, 1) });
        }
    }
}
