using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LifeReplay.Coach;
using LifeReplay.Core;
using Microsoft.Win32;

namespace LifeReplay.Viewer;

/// <summary>
/// Summary → Coach: the deterministic evaluation (always local), and on request a model suggestion that is sent
/// only after you approve the exact payload. Suggestions can be accepted, snoozed or dismissed; the coach later
/// measures whether focus actually went where it suggested.
/// </summary>
public partial class MainWindow
{
    private const string CoachRunValue = "LifeReplay Coach";
    private ICoachingProvider? _coachProvider;

    public void ShowCoach()
    {
        SummaryButton.IsChecked = true;
        ModeCoach.IsChecked = true;
    }

    private CoachService CoachSvc()
    {
        var cfg = CoachConfig.Load(_paths);
        (_coachProvider as IDisposable)?.Dispose();
        _coachProvider = CoachService.CreateProvider(cfg, _paths);
        return new CoachService(new CoachStore(_store), cfg, _coachProvider);
    }

    private static readonly Brush GoodBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xC3, 0x6E));
    private static readonly Brush BadBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x6A, 0x5A));

    private Brush StateBrush(ScheduleState s) => s switch
    {
        ScheduleState.Ahead => GoodBrush,
        ScheduleState.Behind => BadBrush,
        ScheduleState.InsufficientEvidence => Brushes.Goldenrod,
        _ => (Brush)FindResource("Fg"),
    };

    private async Task RenderCoachAsync()
    {
        int version = ++_summaryVersion;
        SummaryList.Children.Clear();
        SummaryTitle.Text = $"Coach · {_day:ddd, MMM d}";
        SummarySubtitle.Text = "Evaluating…";
        var day = _day;
        bool today = day == DateOnly.FromDateTime(DateTime.Now);
        long now = today ? TimeUtil.NowMs() : TimeUtil.LocalDayRange(day).EndMs - 1;
        bool fullModel = Analysis.Model.IsInstalled;
        DaySnapshot? snap;
        try
        {
            snap = await Task.Run(() => CoachDay.Load(_store, _paths, day, now, fullModel
                ? (plan, focus, from, to) => { var (s, e) = CoachDay.DefaultHours(_paths); return Analysis.Plan(plan, focus, from, to, now, s, e); }
                : null));
        }
        catch (Exception ex) { Log.Error("Coach evaluation failed", ex); SummarySubtitle.Text = "Coach evaluation failed: " + ex.Message; return; }
        if (version != _summaryVersion) return;
        if (snap == null)
        {
            SummarySubtitle.Text = "No todo list yet. Create one in Summary → Plan.";
            return;
        }
        var svc = CoachSvc();
        var ev = snap.Evaluation;
        if (today)
            try
            {
                svc.Record(ev);
                svc.MeasureOutcomes((from, to) => CoachDay.FocusByTask(_store, _paths, snap.Plan, from, to, now));
            }
            catch (Exception ex) { Log.Warn("Coach: recording evaluation failed", ex); }

        SummarySubtitle.Text = ev.Reason;
        SummaryList.Children.Add(Text($"{CoachNames.Label(ev.State)} · evidence confidence {ev.Confidence:P0}", 16, bold: true, color: StateBrush(ev.State), margin: new Thickness(0, 2, 0, 4)));
        SummaryList.Children.Add(Text("Only verified outputs count: ticked tasks, \"progress: x/y\" lines and commits (\"repo:\"). Time in an app is shown but isn't progress.", 11.5, color: Muted, margin: new Thickness(0, 0, 0, 6)));
        if (ev.Overcommitted)
            SummaryList.Children.Add(Text($"⚠ {TimeUtil.FormatDuration(ev.RemainingPlannedMs)} of planned work left, {TimeUtil.FormatDuration(ev.RemainingWorkMs)} of working time left.", 12.5, color: Brushes.Goldenrod));
        if (ev.DistractionMs > 0)
            SummaryList.Children.Add(Text($"YouTube / social today: {TimeUtil.FormatDuration(ev.DistractionMs)}" + (ev.DistractionLastHourMs > 0 ? $" ({TimeUtil.FormatDuration(ev.DistractionLastHourMs)} in the last hour)" : ""), 12, color: Muted));
        if (svc.EstimateRatio(day) is { } ratio)
            SummaryList.Children.Add(Text($"Calibration (14 days): finished tasks took {ratio:0.0}× their planned time.", 12, color: Muted));
        foreach (var g in ev.Goals)
            SummaryList.Children.Add(Text($"🎯 {g.Title}" + (g.Deadline is { } dl ? $" · {dl.DayNumber - day.DayNumber} days left" : ""), 12.5, margin: new Thickness(0, 2, 0, 0)));

        foreach (var t in ev.Tasks)
        {
            var header = (DockPanel)Row(t.Title, CoachNames.Label(t.State), 13.5, margin: new Thickness(0, 10, 0, 0));
            ((TextBlock)header.Children[0]).Foreground = StateBrush(t.State);
            SummaryList.Children.Add(header);
            if (t.VerifiedFraction is { } f) SummaryList.Children.Add(Bar(f, StateBrush(t.State)));
            var info = new List<string> { $"focus {TimeUtil.FormatDuration(t.FocusMs)}" + (t.PlannedMs is { } p ? $" / {TimeUtil.FormatDuration(p)} planned" : "") };
            info.AddRange(t.Outputs.Select(o => o.Detail));
            SummaryList.Children.Add(Text(string.Join(" · ", info), 12, color: Muted, margin: new Thickness(10, 0, 0, 0)));
            SummaryList.Children.Add(Text(t.Reason, 11.5, color: Muted, margin: new Thickness(10, 0, 0, 0)));
        }

        // ---- ask the coach
        SummaryList.Children.Add(Text("Ask the coach", 13.5, bold: true, margin: new Thickness(0, 16, 0, 2)));
        var cfg = svc.Config;
        string status = !cfg.Enabled ? "AI suggestions are off: the buttons give the rule-based suggestion."
            : svc.Provider == null ? $"No API key found (BAZE_API_KEY or {ApiKeys.SecretsPath(_paths)}): rule-based suggestions only."
            : $"{cfg.Model} via {new Uri(cfg.BaseUrl).Host} · {svc.Budget().Describe()}. You see exactly what is sent before it goes.";
        SummaryList.Children.Add(Text(status, 11.5, color: Muted, margin: new Thickness(0, 0, 0, 4)));
        var asks = new WrapPanel();
        foreach (var (label, kind) in new[] { ("Morning plan", CoachKind.Morning), ("Check now", CoachKind.Check), ("End-of-day review", CoachKind.Review) })
        {
            var b = new Button { Content = label, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 4) };
            b.Click += async (_, _) => await AskCoachAsync(kind, snap);
            asks.Children.Add(b);
        }
        SummaryList.Children.Add(asks);

        var settings = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        var enable = new CheckBox { Content = "AI suggestions", IsChecked = cfg.Enabled, Margin = new Thickness(0, 0, 12, 4), ToolTip = "Send approved requests to the model in coach.json" };
        enable.Click += (_, _) => { var c = CoachConfig.Load(_paths); c.Enabled = enable.IsChecked == true; c.Save(_paths); UpdateSummary(); };
        var notify = new CheckBox { Content = "Notifications", IsChecked = cfg.Notifications, Margin = new Thickness(0, 0, 12, 4), ToolTip = "Rule-based reminders from a small tray app (no model calls), started with Windows" };
        notify.Click += (_, _) => SetNotifications(notify.IsChecked == true);
        settings.Children.Add(enable);
        settings.Children.Add(notify);
        var openCfg = new Button { Content = "coach.json", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 4), ToolTip = "Model, budget caps, quiet hours, privacy options" };
        openCfg.Click += (_, _) => { var c = CoachConfig.Load(_paths); c.Save(_paths); OpenFile(CoachConfig.PathFor(_paths)); };
        var audit = new Button { Content = "Sent requests…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 4), ToolTip = "Audit log: every payload sent, with status and credits" };
        audit.Click += (_, _) => ShowAudit(svc);
        settings.Children.Add(openCfg);
        settings.Children.Add(audit);
        SummaryList.Children.Add(settings);

        var bridge = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        var copy = new Button { Content = "Copy context for ChatGPT", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 4), ToolTip = "Refine the plan in the ChatGPT website (no API credits)" };
        copy.Click += (_, _) =>
        {
            var ctx = svc.Prepare(CoachKind.Morning, ev, snap.Git);
            Clipboard.SetText(ChatGptBridge.BuildPack(ctx, ev));
            ShowToast("Copied. Paste it into ChatGPT; when done, paste its plan block back here.");
        };
        var paste = new Button { Content = "Paste plan back…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 4) };
        paste.Click += (_, _) => PastePlan(svc, ev);
        bridge.Children.Add(copy);
        bridge.Children.Add(paste);
        SummaryList.Children.Add(bridge);

        // ---- today's suggestions and notifications
        var interventions = svc.Store.Interventions(day).Where(i => i.Source != InterventionSources.User).OrderByDescending(i => i.CreatedUtcMs).ToList();
        if (interventions.Count > 0) SummaryList.Children.Add(Text("Suggestions", 13.5, bold: true, margin: new Thickness(0, 14, 0, 2)));
        foreach (var i in interventions.Take(12)) SummaryList.Children.Add(InterventionCard(svc, i, snap));

        // ---- private ledger
        var ledger = svc.Store.Ledger(day.AddDays(-13), day);
        if (ledger.Count > 0)
        {
            SummaryList.Children.Add(Text("Finished work (private ledger)", 13.5, bold: true, margin: new Thickness(0, 14, 0, 2)));
            SummaryList.Children.Add(Text("Everything stays private unless you tick \"show\" (for a portfolio export later).", 11.5, color: Muted));
            foreach (var row in ledger.OrderByDescending(r => r.Day).Take(20))
            {
                var line = new DockPanel { Margin = new Thickness(10, 1, 0, 1) };
                var show = new CheckBox { Content = "show", IsChecked = row.Showable, FontSize = 11 };
                var id = row.Id;
                show.Click += (_, _) => svc.Store.SetShowable(id, show.IsChecked == true);
                DockPanel.SetDock(show, Dock.Right);
                line.Children.Add(show);
                line.Children.Add(new TextBlock { Text = $"{row.Day[5..]}  {row.Task}  ·  {TimeUtil.FormatDuration(row.FocusMs)}" + (row.PlannedMs is { } pm ? $" / {TimeUtil.FormatDuration(pm)}" : ""), FontSize = 12, Foreground = (Brush)FindResource("Fg"), TextTrimming = TextTrimming.CharacterEllipsis });
                SummaryList.Children.Add(line);
            }
        }
    }

    private UIElement InterventionCard(CoachService svc, Intervention i, DaySnapshot snap)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 4) };
        string source = i.Source switch { InterventionSources.Model => "model", InterventionSources.Rule => "notification", _ => "rule-based" };
        panel.Children.Add(Text($"{TimeUtil.ToLocal(i.CreatedUtcMs):HH:mm} · {i.Kind} · {source}" + (i.Confidence is { } c && i.Source == InterventionSources.Model ? $" · {c} confidence" : ""), 11, color: Muted));
        panel.Children.Add(Text(i.Message, 12.5));
        foreach (var a in i.Actions.Where(a => a.Type != ActionTypes.None))
        {
            var title = snap.Evaluation.Tasks.FirstOrDefault(t => t.Id == a.TaskId)?.Title;
            panel.Children.Add(Text($"→ {a.Type.Replace('_', ' ')}{(title != null ? $": {title}" : "")}{(a.Minutes > 0 ? $" ({a.Minutes} min)" : "")}{(a.Detail.Length > 0 ? $" — {a.Detail}" : "")}", 12, margin: new Thickness(8, 0, 0, 0)));
        }
        if (i.Evidence.Count > 0) panel.Children.Add(Text("Evidence: " + string.Join("; ", i.Evidence), 11, color: Muted, margin: new Thickness(8, 1, 0, 0)));
        if (!string.IsNullOrEmpty(i.Counterpoint)) panel.Children.Add(Text("Counterpoint: " + i.Counterpoint, 11.5, color: Muted, margin: new Thickness(8, 1, 0, 0)));
        if (i.Outcome is { } o)
            panel.Children.Add(Text(o.Followed ? $"✓ Followed: {TimeUtil.FormatDuration(o.FocusMs)} on it in the next hour" : $"✗ Not followed: {TimeUtil.FormatDuration(o.FocusMs)} on it in the next hour", 11.5,
                color: o.Followed ? GoodBrush : Muted, margin: new Thickness(8, 1, 0, 0)));
        if (i.Response != null) panel.Children.Add(Text($"You: {i.Response}", 11.5, color: Muted, margin: new Thickness(8, 1, 0, 0)));
        else
        {
            var buttons = new WrapPanel { Margin = new Thickness(8, 2, 0, 0) };
            foreach (var (label, response) in new[] { ("Accept", "accepted"), ("Not now", "snoozed"), ("Dismiss", "dismissed") })
            {
                var b = new Button { Content = label, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 4, 0), FontSize = 11.5 };
                b.Click += (_, _) => { svc.Feedback(i.Id, response); UpdateSummary(); };
                buttons.Children.Add(b);
            }
            panel.Children.Add(buttons);
        }
        return new Border { BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x88)), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 6), Child = panel };
    }

    private async Task AskCoachAsync(CoachKind kind, DaySnapshot snap)
    {
        var svc = CoachSvc();
        var ctx = svc.Prepare(kind, snap.Evaluation, snap.Git);
        if (svc.Config.Enabled && svc.Provider != null)
        {
            var budget = svc.Budget();
            if (!budget.Allows()) { ShowToast("Coach budget cap reached: " + budget.Describe()); }
            else if (!ShowPreview(svc.Preview(ctx), $"{budget.Describe()} · this request: {svc.Config.CreditsPerRequest} credits (a retry costs the same again)", "Send"))
                return;
            SummarySubtitle.Text = "Asking the coach…";
        }
        try
        {
            var r = await svc.SendAsync(ctx, snap.Evaluation);
            if (!r.FromModel && svc.Config.Enabled) ShowToast($"Coach: {r.Status}. Showing the rule-based suggestion.");
        }
        catch (Exception ex) { Log.Error("Coach request failed", ex); ShowToast("Coach request failed: " + ex.Message); }
        UpdateSummary();
    }

    /// <summary>Modal preview of exactly what will be sent. Returns true only on an explicit click.</summary>
    private bool ShowPreview(string text, string footer, string okText)
    {
        var win = new Window
        {
            Title = "Send to the coach?", Owner = this, Width = 760, Height = 640, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("Panel"),
        };
        var box = new TextBox { Text = text, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap };
        var ok = new Button { Content = okText, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(6, 0, 0, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
        bool result = false;
        ok.Click += (_, _) => { result = true; win.Close(); };
        cancel.Click += (_, _) => win.Close();
        var bottom = new DockPanel { Margin = new Thickness(8) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(new TextBlock { Text = footer, Foreground = Muted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(box);
        win.Content = root;
        win.ShowDialog();
        return result;
    }

    private void PastePlan(CoachService svc, DayEvaluation ev)
    {
        var win = new Window { Title = "Paste ChatGPT's plan", Owner = this, Width = 700, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)FindResource("Panel") };
        var input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 12, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 220 };
        if (Clipboard.ContainsText() && Clipboard.GetText().Contains(ChatGptBridge.BlockName)) input.Text = Clipboard.GetText();
        var result = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 12, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var check = new Button { Content = "Check", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 6, 6, 6) };
        var append = new Button { Content = "Append to todo list", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 6, 6, 6), IsEnabled = false };
        string? markdown = null;
        check.Click += (_, _) =>
        {
            var (plan, errors, warnings) = ChatGptBridge.Parse(input.Text, DateOnly.FromDateTime(DateTime.Now), ev.WindowEndMs - ev.WindowStartMs, svc.EstimateRatio(ev.Day));
            markdown = plan != null ? ChatGptBridge.ToMarkdown(plan) : null;
            result.Text = (errors.Count > 0 ? "Problems:\n- " + string.Join("\n- ", errors) + "\n\n" : "") +
                          (warnings.Count > 0 ? "Warnings:\n- " + string.Join("\n- ", warnings) + "\n\n" : "") +
                          (markdown != null ? "Will be added to the todo list:\n\n" + markdown : "");
            append.IsEnabled = markdown != null;
        };
        append.Click += (_, _) =>
        {
            if (markdown == null) return;
            try { ChatGptBridge.AppendToTodo(TodoPath, markdown); ShowToast("Added to the todo list (backup: todo.md.bak)."); win.Close(); UpdateSummary(); }
            catch (Exception ex) { result.Text = "Couldn't write the todo list: " + ex.Message; }
        };
        var stack = new DockPanel { Margin = new Thickness(8) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "Paste ChatGPT's reply (it must contain a ```lifereplay-plan block):", Foreground = (Brush)FindResource("Fg"), Margin = new Thickness(0, 0, 0, 4) });
        top.Children.Add(input);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(check);
        row.Children.Add(append);
        top.Children.Add(row);
        DockPanel.SetDock(top, Dock.Top);
        stack.Children.Add(top);
        stack.Children.Add(result);
        win.Content = stack;
        win.ShowDialog();
    }

    private void ShowAudit(CoachService svc)
    {
        var rows = svc.Store.RecentRequests(30);
        var text = rows.Count == 0 ? "Nothing has been sent yet." : string.Join("\n\n", rows.Select(r =>
            $"#{r.Id}  {TimeUtil.ToLocal(r.CreatedUtcMs):yyyy-MM-dd HH:mm}  {r.Kind}  {r.Model}  status {r.Status}  attempts {r.Attempts}  credits {r.Credits}  {r.Bytes} bytes\n" +
            $"POST {r.Endpoint}\n{r.Payload}" + (r.Error != null ? $"\nnotes: {r.Error}" : "") + (r.Response != null ? $"\nresponse: {r.Response}" : "")));
        ShowPreview(text, svc.Budget().Describe(), "Close");
    }

    /// <summary>Turns the rule-based notifier on/off: coach.json + a per-user Run entry for LifeReplay.CoachTray.exe.</summary>
    private void SetNotifications(bool on)
    {
        try
        {
            var c = CoachConfig.Load(_paths);
            c.Notifications = on;
            c.Save(_paths);
            var exe = Path.Combine(AppContext.BaseDirectory, "LifeReplay.CoachTray.exe");
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (on && File.Exists(exe))
            {
                bool customDir = !string.Equals(_paths.DataDir, AppPaths.Resolve().DataDir, StringComparison.OrdinalIgnoreCase);
                string args = customDir ? $" --data-dir \"{_paths.DataDir}\"" : "";
                run?.SetValue(CoachRunValue, $"\"{exe}\"{args}");
                Process.Start(new ProcessStartInfo(exe, args.Trim()) { UseShellExecute = false });
                ShowToast("Coach notifications on (tray icon; quiet hours and limits in coach.json).");
            }
            else
            {
                run?.DeleteValue(CoachRunValue, throwOnMissingValue: false);
                ShowToast(on ? "LifeReplay.CoachTray.exe not found next to the viewer." : "Coach notifications off.");
            }
        }
        catch (Exception ex) { Log.Error("Changing coach notifications failed", ex); ShowToast("Couldn't change notifications: " + ex.Message); }
        UpdateSummary();
    }

    private static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true }); } catch { } }
    }
}
