using System.Diagnostics;
using System.Drawing.Drawing2D;
using LifeReplay.Core;
using LifeReplay.Recorder.Recording;

namespace LifeReplay.Recorder.UI;

/// <summary>
/// System tray UI. The icon is the recording indicator: red dot = recording, grey pause bars = paused,
/// orange = problem, black square = privacy blanking active.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly RecorderHost _host;
    private readonly string? _dataDirArg;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem, _storageItem, _pauseItem, _pause15, _pause60, _blankItem, _autoStartItem, _syncEnabledItem, _syncPairItem, _syncStatusItem;
    private readonly System.Windows.Forms.Timer _refresh;
    private Hotkey _hotkey;
    private SettingsForm? _settings;

    private Hotkey CreateHotkey(string spec)
    {
        var hk = new Hotkey(spec);
        hk.Pressed += async () =>
        {
            await _host.Controller.TogglePauseAsync();
            _tray.ShowBalloonTip(1500, "LifeReplay", _host.Controller.UserPaused ? "Recording paused" : "Recording resumed", ToolTipIcon.Info);
        };
        return hk;
    }

    public void OpenSettings() => ShowSettings();

    private void ShowSettings()
    {
        if (_settings is { IsDisposed: false }) { _settings.Activate(); return; }
        _settings = new SettingsForm(_host, _dataDirArg);
        _settings.Saved += () =>
        {
            if (_host.Config.PauseHotkey != _hotkey.Description)
            {
                _hotkey.Dispose();
                _hotkey = CreateHotkey(_host.Config.PauseHotkey);
            }
            RefreshUi();
        };
        _settings.Show();
    }
    private readonly Dictionary<string, Icon> _icons = [];
    private string? _lastIconKey;
    private string? _lastErrorShown;
    private bool _exiting;

    public TrayApp(RecorderHost host, string? dataDirArg)
    {
        _host = host;
        _dataDirArg = dataDirArg;
        _menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem("Starting...") { Enabled = false };
        _storageItem = new ToolStripMenuItem("") { Enabled = false };
        _pauseItem = new ToolStripMenuItem("Pause recording", null, async (_, _) => await _host.Controller.TogglePauseAsync());
        _pause15 = new ToolStripMenuItem("Pause for 15 minutes", null, async (_, _) => await _host.Controller.PauseAsync(TimeSpan.FromMinutes(15)));
        _pause60 = new ToolStripMenuItem("Pause for 1 hour", null, async (_, _) => await _host.Controller.PauseAsync(TimeSpan.FromHours(1)));
        _blankItem = new ToolStripMenuItem("Black out video (privacy)", null, async (_, _) => await _host.Controller.SetManualBlankAsync(!_host.Controller.ManualBlank))
        { ToolTipText = "Keeps the timeline running but records black frames until you turn it off" };
        _autoStartItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutoStart()) { Checked = AutoStart.IsEnabled() };
        _syncStatusItem = new ToolStripMenuItem("") { Enabled = false };
        _syncEnabledItem = new ToolStripMenuItem("Enable iPhone sync (local network)", null, async (_, _) => await ToggleSync());
        _syncPairItem = new ToolStripMenuItem("Pair an iPhone...", null, (_, _) => ShowPairing());
        var syncMenu = new ToolStripMenuItem("iPhone sync");
        syncMenu.DropDownItems.AddRange([_syncStatusItem, _syncEnabledItem, _syncPairItem,
            new ToolStripMenuItem("Revoke all paired iPhones", null, (_, _) => RevokePairings())]);

        _menu.Items.AddRange([
            _statusItem, _storageItem, new ToolStripSeparator(),
            _pauseItem, _pause15, _pause60, _blankItem, new ToolStripSeparator(),
            new ToolStripMenuItem("Open replay...", null, (_, _) => OpenViewer()) { Font = new Font(_menu.Font, FontStyle.Bold) },
            new ToolStripMenuItem("Open recordings folder", null, (_, _) => Shell(_host.Paths.RecordingsDir)),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Settings...", null, (_, _) => ShowSettings()) { Font = new Font(_menu.Font, FontStyle.Bold) },
            _autoStartItem, syncMenu,
            new ToolStripMenuItem("View logs", null, (_, _) => Shell(_host.Paths.LogsDir)),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit (stop recording)", null, async (_, _) => await ExitAsync()),
        ]);
        _menu.Opening += (_, _) => RefreshUi();

        _tray = new NotifyIcon { ContextMenuStrip = _menu, Visible = true, Text = "LifeReplay" };
        _tray.DoubleClick += (_, _) => OpenViewer();

        _hotkey = CreateHotkey(host.Config.PauseHotkey);

        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _host.Controller.Changed += () => ui.Post(_ => { if (!_exiting) RefreshUi(); }, null);
        _refresh = new System.Windows.Forms.Timer { Interval = 2000 };
        _refresh.Tick += (_, _) => RefreshUi();
        _refresh.Start();
        RefreshUi();
        Log.Info("Tray started");
    }

    private void RefreshUi()
    {
        var c = _host.Controller;
        string iconKey;
        string status;
        var since = TimeUtil.ToLocal(c.StateSinceMs).ToString("HH:mm");
        switch (c.State)
        {
            case RecState.Recording:
                iconKey = c.Blanked ? "blank" : "rec";
                status = c.Blanked ? $"● Recording (blanked: {c.BlankedBy})" : $"● Recording {c.ActiveDisplays.Count} display(s) since {since}";
                break;
            case RecState.Paused:
                iconKey = c.StateReason == GapReasons.Error || c.StateReason == GapReasons.DiskFull ? "warn" : "pause";
                var until = c.PauseUntilMs is { } u ? $" until {TimeUtil.ToLocal(u):HH:mm}" : "";
                status = $"❚❚ Paused: {GapReasons.Describe(c.StateReason ?? "")}{until}";
                break;
            default:
                iconKey = "pause";
                status = "Stopped";
                break;
        }
        _statusItem.Text = status;
        long bytes = 0;
        try { bytes = _host.Store.GetTotalStorageBytes(); } catch { }
        long perHour = EncodingPlan.EstimateMaxBytesPerHour(c.ActiveDisplays.Select(d => d.Plan));
        _storageItem.Text = $"Storage: {TimeUtil.FormatBytes(bytes)} of {c.Config.MaxStorageGB:0} GB" + (perHour > 0 ? $" · about {TimeUtil.FormatBytes(perHour)}/hour" : "");
        _pauseItem.Text = c.UserPaused ? "Resume recording" + (_hotkey.Registered ? $"\t{_hotkey.Description}" : "") : "Pause recording" + (_hotkey.Registered ? $"\t{_hotkey.Description}" : "");
        _pause15.Visible = _pause60.Visible = !c.UserPaused;
        _blankItem.Checked = c.ManualBlank;
        _autoStartItem.Checked = AutoStart.IsEnabled(); // can change outside the app (settings window, scripts)
        var sync = _host.Sync;
        _syncEnabledItem.Checked = _host.Config.Sync.Enabled;
        _syncStatusItem.Text = sync != null ? $"Listening on port {sync.Port} · {_host.Pairing.Devices.Count} paired" :
            _host.SyncError != null ? $"Error: {_host.SyncError}" : "Off";
        _syncPairItem.Enabled = sync != null;

        if (iconKey != _lastIconKey)
        {
            _tray.Icon = GetIcon(iconKey);
            _lastIconKey = iconKey;
        }
        var tip = $"LifeReplay: {status}";
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
        if (c.LastError != null && c.LastError != _lastErrorShown)
        {
            _lastErrorShown = c.LastError;
            _tray.ShowBalloonTip(4000, "LifeReplay problem", c.LastError, ToolTipIcon.Warning);
        }
    }

    private Icon GetIcon(string key)
    {
        if (_icons.TryGetValue(key, out var icon)) return icon;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            switch (key)
            {
                case "rec":
                    g.FillEllipse(Brushes.White, 2, 2, 28, 28);
                    g.FillEllipse(new SolidBrush(Color.FromArgb(220, 30, 30)), 4, 4, 24, 24);
                    break;
                case "blank":
                    g.FillEllipse(new SolidBrush(Color.FromArgb(220, 30, 30)), 2, 2, 28, 28);
                    g.FillRectangle(Brushes.Black, 9, 9, 14, 14);
                    break;
                case "warn":
                    g.FillEllipse(new SolidBrush(Color.FromArgb(240, 150, 0)), 2, 2, 28, 28);
                    g.FillRectangle(Brushes.White, 14, 7, 4, 12);
                    g.FillRectangle(Brushes.White, 14, 22, 4, 4);
                    break;
                default:
                    g.FillEllipse(new SolidBrush(Color.FromArgb(120, 120, 120)), 2, 2, 28, 28);
                    g.FillRectangle(Brushes.White, 10, 9, 4, 14);
                    g.FillRectangle(Brushes.White, 18, 9, 4, 14);
                    break;
            }
        }
        var h = bmp.GetHicon();
        icon = (Icon)Icon.FromHandle(h).Clone();
        Native.DestroyIcon(h);
        _icons[key] = icon;
        return icon;
    }

    private void ToggleAutoStart()
    {
        try
        {
            bool enable = !AutoStart.IsEnabled();
            AutoStart.Set(enable, _dataDirArg);
            _autoStartItem.Checked = AutoStart.IsEnabled();
            Log.Info($"Start with Windows: {_autoStartItem.Checked}");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "LifeReplay"); }
    }

    private async Task ToggleSync()
    {
        bool enable = !_host.Config.Sync.Enabled;
        if (enable && MessageBox.Show(
                "This opens an encrypted (HTTPS) endpoint on your local network so a paired iPhone can upload its recordings.\n\n" +
                "Only devices that scanned a pairing code can connect. Windows may ask whether to allow LifeReplay through the firewall; allow it on private networks only.\n\nEnable iPhone sync?",
                "LifeReplay", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await _host.SetSyncEnabledAsync(enable);
        RefreshUi();
        if (_host.SyncError != null) MessageBox.Show("Sync could not start: " + _host.SyncError, "LifeReplay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void ShowPairing()
    {
        try
        {
            var (uri, host, port, fp) = _host.CreatePairing();
            using var dlg = new PairingDialog(uri, host, port, fp);
            dlg.ShowDialog();
        }
        catch (Exception ex) { Log.Error("Pairing failed", ex); MessageBox.Show(ex.Message, "LifeReplay"); }
    }

    private void RevokePairings()
    {
        if (MessageBox.Show("Revoke access for all paired iPhones? They will need to be paired again.", "LifeReplay", MessageBoxButtons.YesNo) == DialogResult.Yes)
            _host.Pairing.RevokeAll();
    }

    public static string? FindViewer()
    {
        var dir = AppContext.BaseDirectory;
        var candidate = Path.Combine(dir, "LifeReplay.Viewer.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private void OpenViewer()
    {
        var exe = FindViewer();
        if (exe == null) { MessageBox.Show("LifeReplay.Viewer.exe was not found next to the recorder.", "LifeReplay"); return; }
        Process.Start(new ProcessStartInfo(exe, $"--data-dir \"{_host.Paths.DataDir}\"") { UseShellExecute = false });
    }

    private static void Shell(string target, string? args = null)
    {
        try { Process.Start(new ProcessStartInfo(target, args ?? "") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Could not open {target}", ex); }
    }

    public Task RequestExitAsync() => ExitAsync();

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _refresh.Stop();
        _tray.Text = "LifeReplay: finishing recordings...";
        await _host.DisposeAsync();
        _hotkey.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }
}
