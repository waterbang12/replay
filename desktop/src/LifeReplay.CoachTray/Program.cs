using System.Diagnostics;
using LifeReplay.Coach;
using LifeReplay.Core;

namespace LifeReplay.CoachTray;

/// <summary>
/// LifeReplay coach notifier: a small tray app that evaluates the day every 5 minutes and shows a notification
/// when the rules allow (no model call). A separate process: if it crashes, recording is unaffected.
///
/// Command line (for testing and scripting):
///   LifeReplay.CoachTray.exe --once [--data-dir DIR]                 print today's evaluation
///   LifeReplay.CoachTray.exe --once --send check|morning|review      print the request preview, then send it
///                                    --yes                           (required to actually send: your approval)
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? dataDir = Arg(args, "--data-dir");
        var paths = AppPaths.Resolve(dataDir);
        paths.EnsureCreated();
        Log.Init(paths.LogsDir, "coach");
        if (args.Contains("--once")) return Once(paths, args);

        using var mutex = new Mutex(true, @"Local\LifeReplay.CoachTray." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(paths.DataDir.ToLowerInvariant())))[..16], out bool first);
        if (!first) return 0;
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.Error("Coach tray UI error", e.Exception);
        Log.Info("Coach tray starting");
        Application.Run(new CoachTrayContext(paths, dataDir));
        return 0;
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int Once(AppPaths paths, string[] args)
    {
        Native.AttachConsole();
        var store = new MetadataStore(paths.DatabasePath);
        store.Migrate();
        var day = DateOnly.FromDateTime(DateTime.Now);
        var snap = CoachDay.Load(store, paths, day, TimeUtil.NowMs());
        if (snap == null) { Console.WriteLine($"No todo list at {CoachDay.TodoPath(paths)}"); return 1; }
        var ev = snap.Evaluation;
        Console.WriteLine($"{day:yyyy-MM-dd}  {CoachNames.Label(ev.State)} (confidence {ev.Confidence:0.00})  {ev.Reason}");
        foreach (var t in ev.Tasks)
            Console.WriteLine($"  {t.Id} {CoachNames.Label(t.State),-20} {t.Title}  focus {TimeUtil.FormatDuration(t.FocusMs)}" +
                              (t.VerifiedFraction is { } f ? $"  verified {f:P0}" : "") + $"  {string.Join(", ", t.Outputs.Select(o => o.Detail))}");
        if (ev.Overcommitted) Console.WriteLine($"  Overcommitted: {TimeUtil.FormatDuration(ev.RemainingPlannedMs)} planned left, {TimeUtil.FormatDuration(ev.RemainingWorkMs)} working time left");

        var kindArg = Arg(args, "--send");
        if (kindArg == null) return 0;
        var kind = kindArg switch { "morning" => CoachKind.Morning, "review" => CoachKind.Review, _ => CoachKind.Check };
        var cfg = CoachConfig.Load(paths);
        using var provider = CoachService.CreateProvider(cfg, paths) as IDisposable;
        var svc = new CoachService(new CoachStore(store), cfg, provider as ICoachingProvider);
        var ctx = svc.Prepare(kind, ev, snap.Git);
        Console.WriteLine("\n" + svc.Preview(ctx));
        Console.WriteLine($"\nBudget: {svc.Budget().Describe()} · this request: {cfg.CreditsPerRequest} credits (+ retries)");
        if (!args.Contains("--yes")) { Console.WriteLine("Not sent (add --yes to approve and send)."); return 0; }
        var r = svc.SendAsync(ctx, ev).GetAwaiter().GetResult();
        Console.WriteLine($"\nStatus: {r.Status} · credits {r.Credits}" + (r.Errors.Count > 0 ? "\nNotes: " + string.Join(" | ", r.Errors) : ""));
        Console.WriteLine($"Message: {r.Suggestion.Message}");
        foreach (var a in r.Suggestion.Actions) Console.WriteLine($"  action: {a.Type} {a.TaskId} {a.Minutes} min — {a.Detail}");
        Console.WriteLine($"Evidence: {string.Join(", ", r.Suggestion.EvidenceIds.Select(id => ctx.Evidence.TryGetValue(id, out var e) ? $"{id} ({e})" : id))}");
        Console.WriteLine($"Counterpoint: {r.Suggestion.Counterpoint}  · confidence {r.Suggestion.Confidence}");
        return r.FromModel ? 0 : 2;
    }
}

internal static class Native
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
    public static void AttachConsole() => AttachConsole(-1);
}

/// <summary>Tray icon + 5-minute timer. Every step is wrapped so an error is logged and the next tick tries again.</summary>
internal sealed class CoachTrayContext : ApplicationContext
{
    private readonly AppPaths _paths;
    private readonly string? _dataDir;
    private readonly MetadataStore _store;
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private long? _lastNotificationId;

    public CoachTrayContext(AppPaths paths, string? dataDir)
    {
        _paths = paths;
        _dataDir = dataDir;
        _store = new MetadataStore(paths.DatabasePath);
        try { _store.Migrate(); } catch (Exception ex) { Log.Error("Coach: database not usable", ex); }
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open coach", null, (_, _) => OpenViewer());
        menu.Items.Add("Check now", null, (_, _) => Tick(manual: true));
        menu.Items.Add("Snooze notifications (1 hour)", null, (_, _) => Feedback("snoozed"));
        menu.Items.Add("Quiet for today", null, (_, _) => Safe(() => Service().QuietToday(DateOnly.FromDateTime(DateTime.Now))));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit coach", null, (_, _) => { _icon!.Visible = false; ExitThread(); });
        _icon = new NotifyIcon { Icon = TrayIcon(), Text = "LifeReplay coach", Visible = true, ContextMenuStrip = menu };
        _icon.BalloonTipClicked += (_, _) => OpenViewer();
        _icon.DoubleClick += (_, _) => OpenViewer();
        _timer = new System.Windows.Forms.Timer { Interval = 5 * 60_000 };
        _timer.Tick += (_, _) => Tick(manual: false);
        _timer.Start();
        Tick(manual: false);
    }

    /// <summary>The rabbit icon at tray size (lifereplay.ico next to the exe), else the system "info" icon.</summary>
    private static Icon TrayIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "lifereplay.ico");
            if (File.Exists(path)) return new Icon(path, SystemInformation.SmallIconSize);
        }
        catch (Exception ex) { Log.Warn("Coach tray icon could not be loaded", ex); }
        return SystemIcons.Information;
    }

    private CoachService Service() => new(new CoachStore(_store), CoachConfig.Load(_paths), null);

    private void Tick(bool manual)
    {
        Safe(() =>
        {
            var svc = Service();
            var day = DateOnly.FromDateTime(DateTime.Now);
            var snap = CoachDay.Load(_store, _paths, day, TimeUtil.NowMs());
            if (snap == null) { _icon.Text = "LifeReplay coach: no todo list"; return; }
            var ev = snap.Evaluation;
            _icon.Text = Trim($"LifeReplay coach: {CoachNames.Label(ev.State)}");
            svc.Record(ev);
            svc.MeasureOutcomes((from, to) => CoachDay.FocusByTask(_store, _paths, snap.Plan, from, to, TimeUtil.NowMs()));
            var d = svc.CheckNotification(ev);
            if (d.Notify)
            {
                _lastNotificationId = svc.Store.Interventions(day).LastOrDefault(i => i.Source == InterventionSources.Rule)?.Id;
                _icon.ShowBalloonTip(15_000, "LifeReplay coach", d.Message ?? d.Reason, ToolTipIcon.Info);
                Log.Info($"Coach: notified ({d.Trigger})");
            }
            else if (manual)
                _icon.ShowBalloonTip(8_000, "LifeReplay coach", $"{CoachNames.Label(ev.State)}. {ev.Reason}\n(No notification: {d.Reason}.)", ToolTipIcon.None);
        });
    }

    private void Feedback(string response) => Safe(() => { if (_lastNotificationId is { } id) Service().Feedback(id, response); });

    private void OpenViewer()
    {
        Safe(() =>
        {
            var viewer = Path.Combine(AppContext.BaseDirectory, "LifeReplay.Viewer.exe");
            var args = "--coach" + (_dataDir != null ? $" --data-dir \"{_dataDir}\"" : "");
            Process.Start(new ProcessStartInfo(viewer, args) { UseShellExecute = false });
        });
    }

    private static string Trim(string s) => s.Length <= 63 ? s : s[..63];

    private static void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { Log.Error("Coach tray step failed (will retry on the next tick)", ex); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _icon.Dispose(); }
        base.Dispose(disposing);
    }
}
