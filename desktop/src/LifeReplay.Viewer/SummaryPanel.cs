using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LifeReplay.Analysis;
using LifeReplay.Core;

namespace LifeReplay.Viewer;

/// <summary>The right-hand Summary panel: Apps (time per app/tab), Topics (grouped by meaning), Plan (todo list).</summary>
public partial class MainWindow
{
    private AnalysisService? _analysis;
    private int _summaryVersion;
    private FileSystemWatcher? _todoWatcher;

    private AnalysisService Analysis => _analysis ??= new AnalysisService(_paths, _store);

    private string TodoPathFile => Path.Combine(_paths.DataDir, "todo-path.txt");

    private string TodoPath
    {
        get
        {
            try { if (File.Exists(TodoPathFile) && File.ReadAllText(TodoPathFile).Trim() is { Length: > 0 } p) return p; } catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LifeReplay", "todo.md");
        }
        set { File.WriteAllText(TodoPathFile, value); WatchTodo(); }
    }

    private void InitSummaryModes()
    {
        ModeApps.Checked += (_, _) => UpdateSummary();
        ModeTopics.Checked += (_, _) => UpdateSummary();
        ModePlan.Checked += (_, _) => UpdateSummary();
        ModePhone.Checked += (_, _) => UpdateSummary();
        ModeCoach.Checked += (_, _) => UpdateSummary();
        SummaryButton.Unchecked += (_, _) => _analysis?.Unload(); // free the model's memory when the panel closes
        Closed += (_, _) => { _analysis?.Dispose(); _todoWatcher?.Dispose(); };
        WatchTodo();
    }

    private void WatchTodo()
    {
        _todoWatcher?.Dispose();
        _todoWatcher = null;
        var dir = Path.GetDirectoryName(TodoPath);
        if (dir == null || !Directory.Exists(dir)) return;
        _todoWatcher = new FileSystemWatcher(dir, Path.GetFileName(TodoPath)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
        _todoWatcher.Changed += (_, _) => Dispatcher.BeginInvoke(() => { if (ModePlan.IsChecked == true) UpdateSummary(); });
        _todoWatcher.EnableRaisingEvents = true;
    }

    private void UpdateSummary()
    {
        if (_timeline == null || SummaryPanel.Visibility != Visibility.Visible) return;
        if (ModeTopics.IsChecked == true) _ = RenderTopicsAsync();
        else if (ModePlan.IsChecked == true) _ = RenderPlanAsync();
        else if (ModePhone.IsChecked == true) RenderPhone();
        else if (ModeCoach.IsChecked == true) _ = RenderCoachAsync();
        else RenderApps();
    }

    // ------------------------------------------------------------------ iPhone

    /// <summary>iPhone app time (from Shortcuts events) next to Windows time for the same day.</summary>
    private void RenderPhone()
    {
        if (_timeline == null) return;
        long from = _timeline.DayStartMs, to = Math.Min(_timeline.DayEndMs, Math.Max(_timeline.NowMs, from));
        var sessions = _timeline.PhoneSessions;
        var totals = AppSessions.Totals(sessions, from, _timeline.DayEndMs);
        long phone = totals.Sum(t => t.TotalMs), inferred = totals.Sum(t => t.InferredMs);
        long windows = FocusSummary.Summarize(_timeline.Focus, from, _timeline.DayEndMs).Sum(a => a.DurationMs);
        long both = AppSessions.OverlapMs(sessions, _timeline.Focus, from, _timeline.DayEndMs);
        var (syncText, stale) = TimelineControl.PhoneSyncText(_timeline);

        SummaryTitle.Text = $"iPhone apps · {_day:ddd, MMM d}";
        SummarySubtitle.Text = _timeline.PhoneStatus.Count == 0
            ? "No iPhone events received yet. Set up the Shortcuts automations (tray → iPhone app tracking; guide: docs/iphone-shortcuts.md)."
            : "From Shortcuts \"App is opened / closed\" automations. Only apps you set up are tracked. Click a row to jump there.";
        SummaryList.Children.Clear();

        SummaryList.Children.Add(Text((stale ? "⚠ " : "") + syncText + (stale ? " — the iPhone hasn't sent events for a while (offline, Tailscale off, or automations disabled)" : ""),
            12, color: stale ? Brushes.Goldenrod : Muted, margin: new Thickness(0, 0, 0, 8)));

        // Combined Windows + iPhone view
        SummaryList.Children.Add(Text("Windows + iPhone", 13, bold: true));
        SummaryList.Children.Add(Row("🖥 Windows (active)", TimeUtil.FormatDuration(windows)));
        SummaryList.Children.Add(Row("📱 iPhone (tracked apps)", TimeUtil.FormatDuration(phone) + (inferred > 0 ? $"  (≈{TimeUtil.FormatDuration(inferred)} inferred)" : ""),
            tooltip: "Inferred = one of the two events was missing; the end was taken from the next app, or the start from the previous event (≤ 30 min)."));
        SummaryList.Children.Add(Row("Both at the same time", TimeUtil.FormatDuration(both), tooltip: "Phone in use while the PC also logged active use"));
        SummaryList.Children.Add(Row("Total screen time (either)", TimeUtil.FormatDuration(windows + phone - both), margin: new Thickness(10, 1, 0, 12)));

        if (totals.Count == 0)
        {
            SummaryList.Children.Add(Text("No iPhone app sessions on this day.", 12, color: Muted));
            return;
        }
        SummaryList.Children.Add(Text("Per app", 13, bold: true));
        long max = Math.Max(1, totals.Max(t => t.TotalMs));
        foreach (var t in totals)
        {
            string right = TimeUtil.FormatDuration(t.TotalMs) + (t.InferredMs > 0 ? " ≈" : "");
            if (t.UnknownSessions > 0) right += $"  +{t.UnknownSessions} incomplete";
            var first = sessions.FirstOrDefault(s => s.App == t.App && s.EndUtcMs >= from);
            SummaryList.Children.Add(Row(t.App, right, 13, onClick: first == null ? null : () => Seek(Math.Max(from, first.StartUtcMs)),
                tooltip: $"{t.App}: {t.Sessions} sessions · known {TimeUtil.FormatDuration(t.KnownMs)} · inferred {TimeUtil.FormatDuration(t.InferredMs)}" +
                         (t.UnknownSessions > 0 ? $" · {t.UnknownSessions} with unknown duration (not counted)" : "")));
            SummaryList.Children.Add(Bar(t.TotalMs / (double)max, Accent));
        }

        var incomplete = sessions.Where(s => s.Confidence != SessionConfidence.Complete).ToList();
        if (incomplete.Count == 0) return;
        SummaryList.Children.Add(Text($"Missing events ({incomplete.Count})", 13, bold: true, margin: new Thickness(0, 12, 0, 2)));
        SummaryList.Children.Add(Text("Sessions where an \"opened\" or \"closed\" event never arrived. Often a Shortcuts automation that didn't run or isn't set up for both events.", 11.5, color: Muted, margin: new Thickness(0, 0, 0, 4)));
        foreach (var s in incomplete.Take(30))
        {
            var s1 = s;
            SummaryList.Children.Add(Row($"{TimeUtil.ToLocal(s.StartUtcMs):HH:mm}  {s.App}", TimelineControl.ConfidenceText(s.Confidence), 12,
                onClick: () => Seek(Math.Max(from, s1.StartUtcMs)), tooltip: TimelineControl.DescribePhoneSession(s)));
        }
        if (incomplete.Count > 30) SummaryList.Children.Add(Text($"+ {incomplete.Count - 30} more", 12, color: Muted, margin: new Thickness(10, 1, 0, 1)));
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
            : $"{TimeUtil.FormatDuration(total)} of active use. YouTube and Social are fixed groups; everything else is grouped automatically by meaning. Click to jump.";
        if (topics.Count > 0)
        {
            // Category totals: YouTube / Social / Other
            var totals = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
            foreach (var cat in new[] { FixedCategories.YouTube, FixedCategories.Social, FixedCategories.Other })
            {
                long ms = topics.Where(t => t.Category == cat).Sum(t => t.DurationMs);
                var box = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x36)), CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 6, 6),
                    Child = new StackPanel { Children = { Text(cat, 11.5, color: Muted, margin: new Thickness(0)), Text(TimeUtil.FormatDuration(ms), 15, bold: true, margin: new Thickness(0)) } },
                };
                totals.Children.Add(box);
            }
            SummaryList.Children.Add(totals);
        }
        foreach (var t in topics)
        {
            var header = (DockPanel)Row(t.Label, TimeUtil.FormatDuration(t.DurationMs), 13.5, margin: new Thickness(0, 10, 0, 0), onClick: () => SeekToItem(t.Items[0]));
            ((TextBlock)header.Children[1]).FontWeight = FontWeights.SemiBold;
            SummaryList.Children.Add(header);
            SummaryList.Children.Add(Bar(t.DurationMs / (double)Math.Max(1, total), Accent));
            foreach (var i in t.Items.Take(6))
                SummaryList.Children.Add(Row($"{i.Item}  ·  {i.App}", TimeUtil.FormatDuration(i.DurationMs), onClick: () => SeekToItem(i), tooltip: $"{i.App}: {i.Item}"));
            if (t.Items.Count > 6) SummaryList.Children.Add(Text($"+ {t.Items.Count - 6} more windows", 12, color: Muted, margin: new Thickness(10, 1, 0, 1)));
        }
    }

    // ------------------------------------------------------------------ plan

    private async Task RenderPlanAsync()
    {
        int version = ++_summaryVersion;
        SummaryList.Children.Clear();
        SummaryTitle.Text = $"Plan · {_day:ddd, MMM d}";
        var path = TodoPath;

        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        var open = new Button { Content = "Open todo list", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 4) };
        open.Click += (_, _) => OpenTodo();
        var change = new Button { Content = "Use another file…", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 4) };
        change.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt|All files|*.*", FileName = Path.GetFileName(path), InitialDirectory = Path.GetDirectoryName(path) };
            if (dlg.ShowDialog(this) == true) { TodoPath = dlg.FileName; UpdateSummary(); }
        };
        buttons.Children.Add(open);
        buttons.Children.Add(change);
        SummaryList.Children.Add(buttons);
        SummaryList.Children.Add(Text(path, 11, color: Muted, margin: new Thickness(0, 0, 0, 8)));

        if (!File.Exists(path))
        {
            SummarySubtitle.Text = "No todo list yet.";
            SummaryList.Children.Add(Text("Click \"Open todo list\" to create one. Write tasks as Markdown checkboxes with a planned time, e.g. \"- [ ] Read domain theory paper ~1h\". LifeReplay matches your windows to tasks by meaning, so keywords are optional.", 12));
            return;
        }
        if (!EnsureModelUi()) return;

        TodoPlan plan;
        try { plan = TodoPlan.Parse(await File.ReadAllTextAsync(path), _day); }
        catch (Exception ex) { SummarySubtitle.Text = "Couldn't read the todo list: " + ex.Message; return; }
        if (plan.Tasks.Count == 0)
        {
            SummarySubtitle.Text = $"No tasks for {_day:yyyy-MM-dd} in the todo list.";
            SummaryList.Children.Add(Text($"Add a \"# {_day:yyyy-MM-dd}\" section (or \"# Every day\") with \"- [ ] task ~1h\" lines.", 12));
            return;
        }

        var tl = _timeline!;
        var (defStart, defEnd) = DefaultWorkingHours();
        SummarySubtitle.Text = "Matching windows to tasks…";
        PlanStatus s;
        try { s = await Task.Run(() => Analysis.Plan(plan, tl.Focus, tl.DayStartMs, tl.DayEndMs, TimeUtil.NowMs(), defStart, defEnd)); }
        catch (Exception ex) { Log.Error("Plan analysis failed", ex); SummarySubtitle.Text = "Plan analysis failed: " + ex.Message; return; }
        if (version != _summaryVersion) return;

        // Overall status
        bool today = _day == DateOnly.FromDateTime(DateTime.Now);
        string verdict = "";
        Brush verdictColor = Muted;
        if (s.AheadMs is { } ahead)
        {
            bool isAhead = ahead >= 0;
            verdictColor = isAhead ? new SolidColorBrush(Color.FromRgb(0x4C, 0xC3, 0x6E)) : new SolidColorBrush(Color.FromRgb(0xE8, 0x6A, 0x5A));
            verdict = today && s.DayFraction < 1
                ? (Math.Abs(ahead) < 5 * 60_000 ? "On schedule" : $"{TimeUtil.FormatDuration(Math.Abs(ahead))} {(isAhead ? "ahead of" : "behind")} schedule")
                : $"Day total: {TimeUtil.FormatDuration(s.SpentOnPlanMs)} of {TimeUtil.FormatDuration(s.PlannedTotalMs)} planned";
        }
        SummarySubtitle.Text = s.PlannedTotalMs > 0
            ? $"{TimeUtil.FormatDuration(s.SpentOnPlanMs)} done of {TimeUtil.FormatDuration(s.PlannedTotalMs)} planned" +
              (today && s.DayFraction < 1 ? $" · by now ({s.DayFraction:P0} of the working day) you'd planned {TimeUtil.FormatDuration(s.ExpectedByNowMs)}" : "")
            : "Add \"~1h\" planned times to see ahead/behind.";
        if (verdict.Length > 0) SummaryList.Children.Add(Text(verdict, 16, bold: true, color: verdictColor, margin: new Thickness(0, 2, 0, 8)));

        foreach (var t in s.Tasks)
        {
            string planned = t.Task.PlannedMs is { } p ? $"{TimeUtil.FormatDuration(t.SpentMs)} / {TimeUtil.FormatDuration(p)}" : TimeUtil.FormatDuration(t.SpentMs);
            var header = (DockPanel)Row((t.Task.Done ? "✓ " : "") + t.Task.Title, planned, 13.5, t.Task.Done ? Muted : null, new Thickness(0, 10, 0, 0));
            ((TextBlock)header.Children[1]).FontWeight = FontWeights.SemiBold;
            SummaryList.Children.Add(header);
            double fraction = t.Task.PlannedMs is > 0 ? t.SpentMs / (double)t.Task.PlannedMs.Value : t.SpentMs > 0 ? 1 : 0;
            SummaryList.Children.Add(Bar(fraction, t.SpentMs > (t.Task.PlannedMs ?? long.MaxValue) ? new SolidColorBrush(Color.FromRgb(0xE8, 0x6A, 0x5A)) : Accent));
            var info = new List<string>();
            if (t.Task.ProgressTotal is { } total) info.Add($"{t.Task.ProgressDone}/{total} {t.Task.ProgressUnit ?? "done"}");
            if (t.RemainingMs is { } rem && !t.Task.Done) info.Add(t.FractionDone is > 0 and < 1 ? $"about {TimeUtil.FormatDuration(rem)} left at your pace" : $"{TimeUtil.FormatDuration(rem)} left of the plan");
            if (info.Count > 0) SummaryList.Children.Add(Text(string.Join(" · ", info), 12, color: Muted, margin: new Thickness(0, 0, 0, 2)));
            foreach (var (item, how, ms) in t.Items.Take(5))
                SummaryList.Children.Add(Row($"{item.Item}  ·  {item.App}", $"{TimeUtil.FormatDuration(ms)} · {HowText(how)}", 11.5, onClick: () => SeekToItem(item), tooltip: $"{item.App}: {item.Item}\nMatched by {HowText(how)}"));
            if (t.Items.Count > 5) SummaryList.Children.Add(Text($"+ {t.Items.Count - 5} more windows", 11.5, color: Muted, margin: new Thickness(10, 0, 0, 0)));
        }

        if (s.Unassigned.Count > 0)
        {
            SummaryList.Children.Add(Text($"Not matched to a task · {TimeUtil.FormatDuration(s.Unassigned.Sum(u => u.Ms))}", 13.5, bold: true, margin: new Thickness(0, 16, 0, 2)));
            SummaryList.Children.Add(Text("Pick a task to teach LifeReplay; similar windows will follow automatically.", 11.5, color: Muted, margin: new Thickness(0, 0, 0, 4)));
            foreach (var (item, ms, suggestion) in s.Unassigned.Take(25))
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var combo = new ComboBox { Width = 150, FontSize = 11.5, ToolTip = "Assign this window to a task" };
                combo.Items.Add(new ComboBoxItem { Content = suggestion != null ? $"Suggest: {suggestion}" : "Assign to…", IsEnabled = false });
                foreach (var task in plan.Tasks) combo.Items.Add(new ComboBoxItem { Content = task.Title, Tag = task.Title });
                combo.Items.Add(new ComboBoxItem { Content = "Not a task", Tag = "" });
                combo.SelectedIndex = 0;
                var text = item.Text;
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is ComboBoxItem { Tag: string task })
                    {
                        _store.SetTitleLabel(text, task);
                        UpdateSummary();
                    }
                };
                DockPanel.SetDock(combo, Dock.Right);
                row.Children.Add(combo);
                row.Children.Add(Row($"{item.Item}  ·  {item.App}", TimeUtil.FormatDuration(ms), 11.5, margin: new Thickness(0), onClick: () => SeekToItem(item), tooltip: $"{item.App}: {item.Item}"));
                SummaryList.Children.Add(row);
            }
        }
    }

    private static string HowText(MatchHow how) => how switch
    {
        MatchHow.Keyword => "keyword",
        MatchHow.Learned => "learned",
        MatchHow.Meaning => "meaning",
        MatchHow.Context => "context",
        MatchHow.Manual => "you",
        _ => "",
    };

    /// <summary>Working hours for ahead/behind: the todo file's "hours:", else recording hours, else 09:00–18:00.</summary>
    private (TimeSpan Start, TimeSpan End) DefaultWorkingHours()
    {
        try
        {
            var cfg = RecorderConfig.Load(_paths.ConfigPath);
            if (cfg.Hours.Enabled && RecordingHours.TryParse(cfg.Hours.Start, out var s) && RecordingHours.TryParse(cfg.Hours.End, out var e) && s < e)
                return (s, e);
        }
        catch { }
        return (new TimeSpan(9, 0, 0), new TimeSpan(18, 0, 0));
    }

    private void OpenTodo()
    {
        var path = TodoPath;
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, TodoPlan.Template(DateOnly.FromDateTime(DateTime.Now)));
                WatchTodo();
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // No app associated with .md: fall back to Notepad.
            Log.Warn("Opening todo list failed", ex);
            try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true }); } catch { }
        }
        UpdateSummary();
    }
}
