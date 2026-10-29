using LifeReplay.Core;
using LifeReplay.Recorder.Recording;
using LifeReplay.Recorder.UI;
using Windows.Graphics.Capture;
using Windows.Media.Editing;
using Windows.Storage;

namespace LifeReplay.Recorder;

internal static class Program
{
    private const string Usage = """
        LifeReplay recorder

          LifeReplay.exe                         Run in the system tray and record (default)
          LifeReplay.exe --record-for <seconds>  Record without tray UI for N seconds, then exit (testing)
          LifeReplay.exe --verify [--deep]       Check every finalized segment; --deep also decodes a frame
          LifeReplay.exe --settings              Open the settings window of the running recorder
          LifeReplay.exe --pause | --resume | --shutdown | --blank | --unblank
                                                 Control the recorder that is already running
                                                 (--blank keeps recording but blacks out the video)
        Options:
          --data-dir <path>       Data folder (default %LOCALAPPDATA%\LifeReplay or LIFEREPLAY_DATA)
          --segment-seconds <n>   Override segment length for this run
          --fps <n>               Override frame rate for this run
          --software-encoder      Force the software H.264 encoder for this run
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        string? dataDir = Arg(args, "--data-dir");
        bool verify = args.Contains("--verify");
        int? recordFor = int.TryParse(Arg(args, "--record-for"), out var rf) ? rf : null;
        string? command = CommandChannel.Commands.FirstOrDefault(c => args.Contains("--" + c));
        bool cli = verify || recordFor != null || command != null || args.Contains("--help");
        if (cli && !Console.IsOutputRedirected) Native.AttachConsole(-1);
        if (args.Contains("--help")) { Console.WriteLine(Usage); return 0; }

        var paths = AppPaths.Resolve(dataDir);
        var instanceKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(paths.DataDir.ToUpperInvariant())))[..16];
        if (command != null)
        {
            bool sent = CommandChannel.Send(instanceKey, command);
            Console.WriteLine(sent ? $"Sent '{command}' to the running recorder." : "No running recorder found for this data folder.");
            return sent ? 0 : 1;
        }
        paths.EnsureCreated();
        Log.Init(paths.LogsDir, "recorder");
        Log.Info($"LifeReplay recorder {AppInfo.Version} starting; data dir {paths.DataDir}; args: {string.Join(' ', args)}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Unobserved task exception", e.Exception); e.SetObserved(); };

        var store = new MetadataStore(paths.DatabasePath);
        store.Migrate();

        if (verify) return VerifyAsync(store, paths, args.Contains("--deep")).GetAwaiter().GetResult();
        if (args.Contains("--export-frame")) return ExportFrameAsync(args).GetAwaiter().GetResult();

        // One recorder per data folder.
        var mutexName = @"Local\LifeReplay.Recorder." + instanceKey;
        using var mutex = new Mutex(true, mutexName, out bool first);
        if (!first)
        {
            Log.Warn("Another recorder is already running for this data folder; exiting");
            if (!cli) MessageBox.Show("LifeReplay is already running (see the tray icon).", "LifeReplay", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else Console.Error.WriteLine("Another LifeReplay recorder is already running for this data folder.");
            return 2;
        }

        if (!GraphicsCaptureSession.IsSupported())
        {
            const string msg = "Screen capture (Windows.Graphics.Capture) is not supported on this system.";
            Log.Error(msg);
            if (!cli) MessageBox.Show(msg, "LifeReplay", MessageBoxButtons.OK, MessageBoxIcon.Error); else Console.Error.WriteLine(msg);
            return 3;
        }
        TryRequestBorderlessAccess();

        var cfg = RecorderConfig.Load(paths.ConfigPath);
        if (cfg.LoadWarning != null) Log.Warn(cfg.LoadWarning);
        bool overridden = false;
        if (int.TryParse(Arg(args, "--segment-seconds"), out var segSec)) { cfg.SegmentSeconds = segSec; overridden = true; }
        if (int.TryParse(Arg(args, "--fps"), out var fps)) { cfg.Fps = fps; overridden = true; }
        if (args.Contains("--software-encoder")) { cfg.HardwareEncoding = false; overridden = true; }
        if (recordFor != null) { cfg.RecordOnLaunch = true; overridden = true; }
        cfg.Normalize();

        var deviceId = paths.GetOrCreateDeviceId();
        long now = TimeUtil.NowMs();
        store.UpsertDevice(new DeviceRecord { Id = deviceId, Name = Environment.MachineName, Platform = Platforms.Windows, CreatedUtcMs = now });
        // A clean exit leaves a "stopped" gap open; it ends now. Then resolve anything a crash left behind.
        CloseStoppedGaps(store, deviceId, now);
        var report = Recovery.Run(store, paths, deviceId, now);
        if (report.Recovered + report.Lost + report.SessionsClosed > 0)
            Log.Warn($"Previous run ended uncleanly: {report.Recovered} segment(s) recovered, {report.Lost} lost, {report.SessionsClosed} session(s) closed");
        store.Heartbeat(deviceId, now);
        try { RecordingDeleter.ApplyRetention(store, paths, cfg, now); } catch (Exception ex) { Log.Warn("Retention at startup failed", ex); }

        if (cfg.AutoRestartAfterCrash && recordFor == null)
        {
            // Windows Error Reporting relaunches us after a crash (if we ran for at least 60 s).
            Native.RegisterApplicationRestart("--restarted" + (dataDir != null ? $" --data-dir \"{dataDir}\"" : ""), 0);
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (recordFor != null) return RunHeadless(paths, store, deviceId, cfg, recordFor.Value, instanceKey);

        var ctx = new TrayBootstrap(paths, store, deviceId, cfg, dataDir, persistConfig: !overridden, instanceKey);
        Application.Run(ctx);
        Log.Info("Recorder exited");
        return 0;
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void CloseStoppedGaps(MetadataStore store, string deviceId, long now)
    {
        using var c = store.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE gaps SET end_utc = MAX(start_utc, $now) WHERE device_id = $d AND end_utc IS NULL AND reason = 'stopped'";
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.ExecuteNonQuery();
    }

    private static void TryRequestBorderlessAccess()
    {
        try
        {
            if (Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess"))
            {
                var status = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().GetAwaiter().GetResult();
                Log.Info($"Borderless capture access: {status}");
            }
        }
        catch (Exception ex) { Log.Info($"Borderless capture access not available: {ex.Message}"); }
    }

    /// <summary>Tray mode: the host is created inside the message loop so Windows events reach it.</summary>
    private sealed class TrayBootstrap : ApplicationContext
    {
        private CommandChannel? _commands;

        public TrayBootstrap(AppPaths paths, MetadataStore store, string deviceId, RecorderConfig cfg, string? dataDir, bool persistConfig, string instanceKey)
        {
            var timer = new System.Windows.Forms.Timer { Interval = 1 };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                var ui = SynchronizationContext.Current!;
                var host = new RecorderHost(paths, store, deviceId, cfg, persistConfig);
                var tray = new TrayApp(host, dataDir);
                tray.ThreadExit += (_, _) => { _commands?.Dispose(); ExitThread(); };
                _commands = new CommandChannel(instanceKey, cmd => ui.Post(async _ =>
                {
                    switch (cmd)
                    {
                        case "pause": await host.Controller.PauseAsync(); break;
                        case "resume": await host.Controller.ResumeAsync(); break;
                        case "blank": await host.Controller.SetManualBlankAsync(true); break;
                        case "unblank": await host.Controller.SetManualBlankAsync(false); break;
                        case "settings": tray.OpenSettings(); break;
                        case "shutdown": await tray.RequestExitAsync(); break;
                    }
                }, null));
                await host.StartAsync();
            };
            timer.Start();
        }
    }

    private static int RunHeadless(AppPaths paths, MetadataStore store, string deviceId, RecorderConfig cfg, int seconds, string instanceKey)
    {
        long startMs = TimeUtil.NowMs();
        int exit = 0;
        var ctx = new ApplicationContext();
        var timer = new System.Windows.Forms.Timer { Interval = 1 };
        RecorderHost? host = null;
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                host = new RecorderHost(paths, store, deviceId, cfg, persistConfig: false);
                var ui = SynchronizationContext.Current!;
                var h = host;
                using var stop = new CancellationTokenSource();
                using var commands = new CommandChannel(instanceKey, cmd => ui.Post(async _ =>
                {
                    if (cmd == "pause") await h.Controller.PauseAsync();
                    else if (cmd == "resume") await h.Controller.ResumeAsync();
                    else if (cmd == "blank") await h.Controller.SetManualBlankAsync(true);
                    else if (cmd == "unblank") await h.Controller.SetManualBlankAsync(false);
                    else if (cmd == "shutdown") stop.Cancel();
                }, null));
                await host.StartAsync();
                Console.WriteLine($"Recording for {seconds} s into {paths.DataDir} (segment {cfg.SegmentSeconds} s, max {cfg.Fps} fps)...");
                foreach (var d in host.Controller.ActiveDisplays)
                    Console.WriteLine($"  {d.Name}: {d.Plan.SourceWidth}x{d.Plan.SourceHeight} -> {d.Plan.Width}x{d.Plan.Height}, {d.Plan.BitrateBps / 1000} kbps, " +
                                      $"{(d.Hardware ? "hardware" : "software")} encoder, about {TimeUtil.FormatBytes(d.Plan.MaxBytesPerHour)}/hour");
                try { await Task.Delay(TimeSpan.FromSeconds(seconds), stop.Token); } catch (TaskCanceledException) { Console.WriteLine("Shutdown requested."); }
                await host.DisposeAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Headless recording failed", ex);
                Console.Error.WriteLine("Recording failed: " + ex.Message);
                exit = 1;
            }
            ctx.ExitThread();
        };
        timer.Start();
        Application.Run(ctx);

        var segs = store.GetSegmentsWhere("device_id = $d AND created_utc >= $t", ("$d", deviceId), ("$t", startMs - 1000));
        Console.WriteLine($"{segs.Count} segment(s):");
        foreach (var s in segs)
            Console.WriteLine($"  {s.Status,-9} {TimeUtil.ToLocal(s.StartUtcMs):HH:mm:ss.fff} -> {(s.EndUtcMs is { } e ? TimeUtil.ToLocal(e).ToString("HH:mm:ss.fff") : "?"),-12} " +
                              $"{(s.DurationMs ?? 0) / 1000.0,7:0.0}s {s.FrameCount,6} frames {TimeUtil.FormatBytes(s.FileSize ?? 0),10}  {s.FilePath}");
        if (segs.Count == 0 || segs.Any(s => s.Status != SegmentStatus.Finalized)) exit = exit == 0 ? 4 : exit;
        Log.Info($"Headless run finished, exit code {exit}");
        return exit;
    }

    /// <summary>--export-frame &lt;file.mp4&gt; &lt;seconds&gt; &lt;out.jpg&gt;: decodes one frame (diagnostics).</summary>
    private static async Task<int> ExportFrameAsync(string[] args)
    {
        int i = Array.IndexOf(args, "--export-frame");
        if (args.Length < i + 4) { Console.Error.WriteLine("usage: --export-frame <file.mp4> <seconds> <out.jpg>"); return 1; }
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(args[i + 1]));
        var clip = await MediaClip.CreateFromFileAsync(file);
        var comp = new MediaComposition();
        comp.Clips.Add(clip);
        var at = TimeSpan.FromSeconds(double.Parse(args[i + 2], System.Globalization.CultureInfo.InvariantCulture));
        using var thumb = await comp.GetThumbnailAsync(at, 0, 0, VideoFramePrecision.NearestFrame);
        using var input = thumb.AsStreamForRead();
        using var output = File.Create(args[i + 3]);
        await input.CopyToAsync(output);
        Console.WriteLine($"frame at {at.TotalSeconds:0.000}s of {clip.OriginalDuration.TotalSeconds:0.000}s -> {args[i + 3]}");
        return 0;
    }

    private static async Task<int> VerifyAsync(MetadataStore store, AppPaths paths, bool deep)
    {
        var segs = store.GetSegmentsWhere("status = $s", ("$s", SegmentStatus.Finalized));
        int bad = 0;
        Console.WriteLine($"Verifying {segs.Count} finalized segment(s){(deep ? " (decoding a frame from each)" : "")}...");
        foreach (var s in segs)
        {
            var full = paths.ToFull(s.FilePath);
            string result;
            var info = Mp4Inspector.Inspect(full);
            if (!info.IsValid) result = "INVALID: " + info.Error;
            // File time must track wall-clock time (seeking relies on it); allow one frame plus rounding.
            // iPhone files are variable-frame-rate and may end at the last frame, so they get more slack.
            else if (Math.Abs(info.DurationMs - (s.DurationMs ?? 0)) > (s.Encoder.StartsWith("mf-") ? 1000.0 / Math.Max(1, s.Fps) + 250 : 3000))
                result = $"DURATION MISMATCH file {info.DurationMs} ms vs clock {s.DurationMs} ms";
            else
            {
                result = $"ok {info.Width}x{info.Height} {info.DurationMs / 1000.0:0.0}s";
                if (deep)
                {
                    try
                    {
                        var file = await StorageFile.GetFileFromPathAsync(full);
                        var clip = await MediaClip.CreateFromFileAsync(file);
                        var comp = new MediaComposition();
                        comp.Clips.Add(clip);
                        var at = TimeSpan.FromMilliseconds(Math.Max(0, info.DurationMs / 2));
                        using var thumb = await comp.GetThumbnailAsync(at, 160, 0, VideoFramePrecision.NearestFrame);
                        result += thumb != null && thumb.Size > 0 ? $", decoded frame at {at.TotalSeconds:0.0}s ({thumb.Size} bytes)" : ", DECODE FAILED";
                        if (thumb == null || thumb.Size == 0) info = info with { IsValid = false };
                    }
                    catch (Exception ex) { result += ", DECODE FAILED: " + ex.Message; info = info with { IsValid = false }; }
                }
            }
            if (!result.StartsWith("ok") || !info.IsValid) bad++;
            Console.WriteLine($"  {TimeUtil.ToLocal(s.StartUtcMs):yyyy-MM-dd HH:mm:ss} {s.DisplayId,-14} {result}");
        }
        Console.WriteLine(bad == 0 ? "All segments OK." : $"{bad} problem(s) found.");
        return bad == 0 ? 0 : 5;
    }
}
