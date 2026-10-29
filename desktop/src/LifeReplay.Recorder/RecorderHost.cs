using LifeReplay.Core;
using LifeReplay.Recorder.Recording;
using LifeReplay.Recorder.UI;
using LifeReplay.Sync;
using Microsoft.Win32;

namespace LifeReplay.Recorder;

/// <summary>
/// Wires the controller to Windows events (sleep, lock, display changes, logoff), the config file,
/// the privacy exclusion watcher and the optional sync server. Used by both tray and headless modes.
/// Must be created on the UI (message loop) thread.
/// </summary>
internal sealed class RecorderHost : IAsyncDisposable
{
    public AppPaths Paths { get; }
    public MetadataStore Store { get; }
    public string DeviceId { get; }
    public RecordingController Controller { get; }
    public RecorderConfig Config { get; private set; }
    public PairingStore Pairing { get; }
    public string? SyncError { get; private set; }
    public SyncServer? Sync => _sync;

    private readonly ExclusionWatcher _exclusions;
    private readonly DisplayPowerWatcher _displayPower;
    private readonly FocusTracker _focus;
    private readonly FileSystemWatcher _configWatcher;
    private readonly System.Windows.Forms.Timer _configDebounce;
    private readonly bool _persistConfig;
    private SyncServer? _sync;
    private bool _disposed;

    public RecorderHost(AppPaths paths, MetadataStore store, string deviceId, RecorderConfig cfg, bool persistConfig = true)
    {
        Paths = paths;
        Store = store;
        DeviceId = deviceId;
        Config = cfg;
        _persistConfig = persistConfig;
        Controller = new RecordingController(new RecorderContext(store, paths, deviceId), cfg);
        Pairing = new PairingStore(paths.SyncDir);

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionEnding += OnSessionEnding;

        _exclusions = new ExclusionWatcher(() => Config);
        _exclusions.Changed += match => _ = Controller.SetExclusionAsync(match);
        _focus = new FocusTracker(this);
        _displayPower = new DisplayPowerWatcher();
        _displayPower.DisplayOffChanged += off => _ = Controller.SetDisplayOffAsync(off);

        _configDebounce = new System.Windows.Forms.Timer { Interval = 1500 };
        _configDebounce.Tick += (_, _) => { _configDebounce.Stop(); _ = ReloadConfigAsync(); };
        _configWatcher = new FileSystemWatcher(paths.DataDir, "config.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
        _configWatcher.Changed += (_, _) => SyncContextPost(() => { _configDebounce.Stop(); _configDebounce.Start(); });
        _configWatcher.EnableRaisingEvents = persistConfig;
    }

    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private void SyncContextPost(Action a) { if (_ui != null) _ui.Post(_ => a(), null); else a(); }

    public async Task StartAsync()
    {
        _exclusions.Check();
        await Controller.StartAsync();
        if (Config.Sync.Enabled) await StartSyncAsync();
    }

    public async Task ReloadConfigAsync()
    {
        try
        {
            var cfg = RecorderConfig.Load(Paths.ConfigPath);
            if (cfg.LoadWarning != null) Log.Warn(cfg.LoadWarning);
            bool syncChanged = cfg.Sync.Enabled != Config.Sync.Enabled || cfg.Sync.Port != Config.Sync.Port;
            Config = cfg;
            await Controller.ApplyConfigAsync(cfg);
            if (syncChanged)
            {
                await StopSyncAsync();
                if (cfg.Sync.Enabled) await StartSyncAsync();
            }
            _exclusions.Check();
        }
        catch (Exception ex) { Log.Error("Reloading config failed", ex); }
    }

    /// <summary>Saves settings from the Settings window and applies them (capture restarts with the new values).</summary>
    public async Task SaveAndApplyConfigAsync(RecorderConfig cfg)
    {
        cfg.Normalize();
        Config = cfg;
        SaveConfig();
        await Controller.ApplyConfigAsync(cfg);
        _exclusions.Check();
    }

    public void SaveConfig()
    {
        if (!_persistConfig) return;
        _configWatcher.EnableRaisingEvents = false;
        try { Config.Save(Paths.ConfigPath); }
        finally { _configWatcher.EnableRaisingEvents = true; }
    }

    // ------------------------------------------------------------------ sync

    public async Task SetSyncEnabledAsync(bool enabled)
    {
        Config.Sync.Enabled = enabled;
        // Shortcuts only accepts trusted certificates, so the phone reaches LifeReplay through Tailscale Serve,
        // which forwards to this PC's loopback address. Nothing needs to listen on the LAN.
        if (enabled && Config.Sync.BindAddress == "0.0.0.0") Config.Sync.BindAddress = "127.0.0.1";
        SaveConfig();
        await StopSyncAsync();
        if (enabled) await StartSyncAsync();
    }

    private async Task StartSyncAsync()
    {
        try
        {
            var cert = CertificateManager.GetOrCreate(Paths.SyncDir, DeviceId);
            var bind = System.Net.IPAddress.TryParse(Config.Sync.BindAddress, out var ip) ? ip : System.Net.IPAddress.Any;
            _sync = await SyncServer.StartAsync(Config.Sync.Port, cert, Pairing, Store, Environment.MachineName, bind);
            SyncError = null;
        }
        catch (Exception ex)
        {
            SyncError = ex.Message;
            Log.Error("Could not start sync server", ex);
        }
        Controller.SyncEnabled = _sync != null;
    }

    private async Task StopSyncAsync()
    {
        if (_sync != null) { await _sync.DisposeAsync(); _sync = null; }
        Controller.SyncEnabled = false;
    }

    /// <summary>Creates a token for the iPhone's Shortcuts and works out the address the phone should use.</summary>
    public (string Token, string? EndpointUrl, int Port) CreatePairing()
    {
        var token = Pairing.CreateToken($"iPhone Shortcuts {DateTime.Now:yyyy-MM-dd HH:mm}");
        var name = TailscaleName();
        return (token, name != null ? $"https://{name}/api/v1/app-events" : null, Config.Sync.Port);
    }

    /// <summary>This PC's Tailscale MagicDNS name (e.g. "my-pc.tail1234.ts.net"), if Tailscale is installed and signed in.</summary>
    public static string? TailscaleName()
    {
        try
        {
            var exe = new[] { "tailscale.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe") }
                .FirstOrDefault(p => !Path.IsPathRooted(p) || File.Exists(p)) ?? "tailscale.exe";
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "status --json")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            if (p == null) return null;
            var json = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(3000) || !json.Wait(1000)) { try { p.Kill(); } catch { } return null; }
            using var doc = System.Text.Json.JsonDocument.Parse(json.Result);
            var dns = doc.RootElement.GetProperty("Self").GetProperty("DNSName").GetString()?.TrimEnd('.');
            return string.IsNullOrEmpty(dns) ? null : dns;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ Windows events

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _ = Controller.SetSleepingAsync(true);
        else if (e.Mode == PowerModes.Resume) _ = Controller.SetSleepingAsync(false);
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                _ = Controller.SetLockedAsync(true);
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                _ = Controller.SetLockedAsync(false);
                break;
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Controller.DisplaysChanged();

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        // Logoff/shutdown: finish the open files now; Windows gives us a few seconds.
        Log.Info($"Session ending ({e.Reason}); finalizing recordings");
        // Task.Run so the awaits inside don't need this (blocked) UI thread.
        try { Task.Run(() => Controller.ShutdownAsync("windows " + e.Reason.ToString().ToLowerInvariant())).Wait(TimeSpan.FromSeconds(8)); }
        catch (Exception ex) { Log.Error("Shutdown on session end failed", ex); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _exclusions.Dispose();
        _focus.Dispose();
        _displayPower.Dispose();
        _configWatcher.Dispose();
        _configDebounce.Dispose();
        await StopSyncAsync();
        await Controller.DisposeAsync();
    }
}
