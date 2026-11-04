using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LifeReplay.Analysis;
using LifeReplay.Core;

namespace LifeReplay.Viewer;

/// <summary>The right-hand Summary panel: the list of views, switching between them, and shared helpers.</summary>
public partial class MainWindow
{
    private AnalysisService? _analysis;
    private int _summaryVersion;

    private AnalysisService Analysis => _analysis ??= new AnalysisService(_paths, _store);

    /// <summary>
    /// The Summary views, in tab order. To add one: a RadioButton in MainWindow.xaml, a file in Summary/ with its
    /// Render method, and one line here. To remove one: delete all three.
    /// </summary>
    private (RadioButton Button, Func<Task> Render)[] SummaryViews =>
    [
        (ModeApps, () => { RenderApps(); return Task.CompletedTask; }),     // AppsView.cs
        (ModeTopics, RenderTopicsAsync),                                   // TopicsView.cs
        (ModePlan, RenderPlanAsync),                                       // PlanView.cs
        (ModePhone, () => { RenderPhone(); return Task.CompletedTask; }),  // PhoneView.cs
        (ModeCoach, RenderCoachAsync),                                     // CoachView.cs
    ];

    private void InitSummaryModes()
    {
        foreach (var (button, _) in SummaryViews) button.Checked += (_, _) => UpdateSummary();
        SummaryButton.Unchecked += (_, _) => _analysis?.Unload(); // free the model's memory when the panel closes
        Closed += (_, _) => { _analysis?.Dispose(); _todoWatcher?.Dispose(); };
        WatchTodo();
    }

    private void UpdateSummary()
    {
        if (_timeline == null || SummaryPanel.Visibility != Visibility.Visible) return;
        var view = SummaryViews.FirstOrDefault(v => v.Button.IsChecked == true);
        _ = (view.Render ?? SummaryViews[0].Render)();
    }

    // ------------------------------------------------------------------ helpers

    private Brush Muted => (Brush)FindResource("Muted");
    private Brush Accent => (Brush)FindResource("Accent");

    private TextBlock Text(string s, double size = 13, bool bold = false, Brush? color = null, Thickness? margin = null) => new()
    {
        Text = s, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = color ?? (Brush)FindResource("Fg"),
        TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0, 1, 0, 1),
    };

    private UIElement Row(string left, string right, double size = 12, Brush? leftColor = null, Thickness? margin = null, Action? onClick = null, string? tooltip = null)
    {
        var row = new DockPanel { Margin = margin ?? new Thickness(10, 1, 0, 1), Background = Brushes.Transparent, ToolTip = tooltip ?? left };
        var r = new TextBlock { Text = right, Foreground = Muted, FontSize = size, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(r, Dock.Right);
        row.Children.Add(r);
        row.Children.Add(new TextBlock { Text = left, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = leftColor ?? (Brush)FindResource("Fg") });
        if (onClick != null) { row.Cursor = Cursors.Hand; row.MouseLeftButtonUp += (_, _) => onClick(); }
        return row;
    }

    private Border Bar(double fraction, Brush color, double width = 370) => new()
    {
        Height = 5, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 4),
        Width = Math.Max(2, width * Math.Clamp(fraction, 0, 1)), Background = color,
    };

    private void SeekToItem(TitleItem item)
    {
        var first = _timeline?.Focus.FirstOrDefault(f => FocusSummary.AppName(f.Process) == item.App && FocusSummary.ItemName(f.Process, f.Title) == item.Item);
        if (first != null) Seek(first.StartUtcMs);
    }

    /// <summary>Shown in Topics/Plan when the model hasn't been downloaded yet.</summary>
    private bool EnsureModelUi()
    {
        if (Analysis.Model.IsInstalled) return true;
        SummarySubtitle.Text = "";
        SummaryList.Children.Add(Text("Topics and plan matching use a small language model that runs on this PC (no internet needed after the download).", 13));
        SummaryList.Children.Add(Text($"One-time download: about {ModelStore.DownloadBytes / 1_000_000} MB from Hugging Face (paraphrase-multilingual-MiniLM-L12-v2, Korean + English). Uses about 250 MB of memory while this panel is open.", 12, color: Muted, margin: new Thickness(0, 6, 0, 10)));
        var progress = Text("", 12, color: Muted);
        var button = new Button { Content = "Download topic model", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 6, 14, 6) };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                var p = new Progress<double>(x => progress.Text = $"Downloading… {x:P0}");
                await Task.Run(() => Analysis.Model.InstallAsync(p));
                progress.Text = "Installed.";
                UpdateSummary();
            }
            catch (Exception ex)
            {
                Log.Error("Model download failed", ex);
                progress.Text = "Download failed: " + ex.Message;
                button.IsEnabled = true;
            }
        };
        SummaryList.Children.Add(button);
        SummaryList.Children.Add(progress);
        return false;
    }
}
