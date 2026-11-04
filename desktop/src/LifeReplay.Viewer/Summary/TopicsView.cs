using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LifeReplay.Analysis;
using LifeReplay.Core;

namespace LifeReplay.Viewer;

/// <summary>Summary → Topics: windows sorted into Progress / Not progress / Unsorted, then grouped by meaning.</summary>
public partial class MainWindow
{
    // ------------------------------------------------------------------ topics

    private async Task RenderTopicsAsync()
    {
        int version = ++_summaryVersion;
        SummaryList.Children.Clear();
        SummaryTitle.Text = $"Topics · {_day:ddd, MMM d}";
        if (!EnsureModelUi()) return;
        var tl = _timeline!;
        SummarySubtitle.Text = "Grouping windows by meaning…";
        List<Topic> topics;
        try { topics = await Task.Run(() => Analysis.Topics(tl.Focus, tl.DayStartMs, tl.DayEndMs)); }
        catch (Exception ex) { Log.Error("Topic analysis failed", ex); SummarySubtitle.Text = "Topic analysis failed: " + ex.Message; return; }
        if (version != _summaryVersion) return; // a newer render started
        long total = topics.Sum(t => t.DurationMs);
        SummarySubtitle.Text = topics.Count == 0 ? "No window titles logged for this day."
            : $"{TimeUtil.FormatDuration(total)} of active use, sorted into progress and not progress, then grouped by meaning. " +
              "Click a heading to open it. Wrong? Open a topic and use \"Move to\"; similar windows follow.";
        if (topics.Count == 0) return;

        // One bar for the whole day: progress / not progress / unsorted.
        var split = new Grid { Height = 8, Margin = new Thickness(0, 4, 0, 8) };
        foreach (var kind in ProgressKinds.All)
        {
            long ms = topics.Where(t => t.Category == kind).Sum(t => t.DurationMs);
            if (ms <= 0) continue;
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ms, GridUnitType.Star) });
            var part = new Border { Background = KindBrush(kind), ToolTip = $"{ProgressKinds.Label(kind)}: {TimeUtil.FormatDuration(ms)}" };
            Grid.SetColumn(part, split.ColumnDefinitions.Count - 1);
            split.Children.Add(part);
        }
        SummaryList.Children.Add(split);

        foreach (var kind in ProgressKinds.All)
        {
            var mine = topics.Where(t => t.Category == kind).ToList();
            if (mine.Count == 0) continue;
            long ms = mine.Sum(t => t.DurationMs);
            var body = Collapsible($"kind:{kind}", ProgressKinds.Label(kind), $"{TimeUtil.FormatDuration(ms)} · {ms / (double)Math.Max(1, total):P0}",
                15, KindBrush(kind), new Thickness(0, 8, 0, 2), 0);
            foreach (var t in mine)
            {
                var tb = Collapsible($"topic:{kind}:{t.Label}", t.Label, TimeUtil.FormatDuration(t.DurationMs), 13, null, new Thickness(0, 3, 0, 0), 14, body);
                tb.Children.Add(Bar(t.DurationMs / (double)Math.Max(1, ms), KindBrush(kind), 340));
                foreach (var i in t.Items.Take(15))
                {
                    var row = Row($"{i.Item}  ·  {i.App}", TimeUtil.FormatDuration(i.DurationMs), onClick: () => SeekToItem(i), tooltip: $"{i.App}: {i.Item}\nRight-click to move this window");
                    ((FrameworkElement)row).ContextMenu = MoveMenu([i], kind);
                    tb.Children.Add(row);
                }
                if (t.Items.Count > 15) tb.Children.Add(Text($"+ {t.Items.Count - 15} more windows", 12, color: Muted, margin: new Thickness(10, 1, 0, 1)));
                var move = new WrapPanel { Margin = new Thickness(10, 3, 0, 6) };
                move.Children.Add(Text(t.Items.Count == 1 ? "Move to:" : $"Move all {t.Items.Count} to:", 11.5, color: Muted, margin: new Thickness(0, 3, 6, 0)));
                foreach (var target in ProgressKinds.All.Where(k => k != kind))
                {
                    var b = new Button { Content = ProgressKinds.Label(target), FontSize = 11.5, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 4, 0) };
                    var items = t.Items;
                    b.Click += (_, _) => MoveWindows(items, target);
                    move.Children.Add(b);
                }
                tb.Children.Add(move);
            }
        }
    }

    /// <summary>Open/closed state of the Topics headings, kept while the viewer runs.</summary>
    private readonly HashSet<string> _openHeadings = [];

    /// <summary>A clickable ▸ heading; returns the body panel (hidden until opened). Added to <paramref name="parent"/> or the summary list.</summary>
    private StackPanel Collapsible(string key, string title, string right, double size, Brush? color, Thickness margin, double indent, Panel? parent = null)
    {
        parent ??= SummaryList;
        bool open = _openHeadings.Contains(key);
        var body = new StackPanel { Margin = new Thickness(indent + 6, 0, 0, 0), Visibility = open ? Visibility.Visible : Visibility.Collapsed };
        var arrow = new TextBlock { Text = open ? "▾" : "▸", FontSize = size, Width = 16, Foreground = color ?? (Brush)FindResource("Fg") };
        var header = new DockPanel { Margin = new Thickness(indent + margin.Left, margin.Top, 0, margin.Bottom), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = title };
        var r = new TextBlock { Text = right, Foreground = Muted, FontSize = size - 1, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(arrow, Dock.Left);
        DockPanel.SetDock(r, Dock.Right);
        header.Children.Add(arrow);
        header.Children.Add(r);
        header.Children.Add(new TextBlock { Text = title, FontSize = size, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = color ?? (Brush)FindResource("Fg") });
        header.MouseLeftButtonUp += (_, _) =>
        {
            bool nowOpen = body.Visibility != Visibility.Visible;
            body.Visibility = nowOpen ? Visibility.Visible : Visibility.Collapsed;
            arrow.Text = nowOpen ? "▾" : "▸";
            if (nowOpen) _openHeadings.Add(key); else _openHeadings.Remove(key);
        };
        parent.Children.Add(header);
        parent.Children.Add(body);
        return body;
    }

    private ContextMenu MoveMenu(IReadOnlyList<TitleItem> items, string current)
    {
        var menu = new ContextMenu();
        foreach (var target in ProgressKinds.All.Where(k => k != current))
        {
            var mi = new MenuItem { Header = $"Move to {ProgressKinds.Label(target)}" };
            mi.Click += (_, _) => MoveWindows(items, target);
            menu.Items.Add(mi);
        }
        return menu;
    }

    private void MoveWindows(IReadOnlyList<TitleItem> items, string kind)
    {
        try
        {
            _store.SetTopicKinds(items.Select(i => i.Text), kind);
            _openHeadings.Add($"kind:{kind}");
            UpdateSummary();
        }
        catch (Exception ex) { Log.Error("Couldn't save the topic choice", ex); SummarySubtitle.Text = "Couldn't save: " + ex.Message; }
    }

    private static Brush KindBrush(string kind) => kind switch
    {
        ProgressKinds.Progress => new SolidColorBrush(Color.FromRgb(0x4C, 0xC3, 0x6E)),
        ProgressKinds.NotProgress => new SolidColorBrush(Color.FromRgb(0xE8, 0x6A, 0x5A)),
        _ => new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x94)),
    };
}
