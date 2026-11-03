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
    public PhoneDnsLogger? PhoneDns => _dns;
    public string? PhoneDnsError { get; private set; }

    private readonly ExclusionWatcher _exclusions;
    private readonly DisplayPowerWatcher _displayPower;
    private readonly FocusTracker _focus;
    private readonly FileSystemWatcher _configWatcher;
    private readonly System.Windows.Forms.Timer _configDebounce;
    private readonly bool _persistConfig;
    private SyncServer? _sync;
    private PhoneDnsLogger? _dns;
    private System.Threading.Timer? _dnsRetry;
    private readonly object _dnsGate = new();
    private TailscaleInfo? _tailscale;
    private long _tailscaleReadMs;
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
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
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
        if (Config.PhoneDns.Enabled) await StartPhoneDnsAsync();
    }

    public async Task ReloadConfigAsync()
    {
        try
        {
            var cfg = RecorderConfig.Load(Paths.ConfigPath);
            if (cfg.LoadWarning != null) Log.Warn(cfg.LoadWarning);
            bool syncChanged = cfg.Sync.Enabled != Config.Sync.Enabled || cfg.Sync.Port != Config.Sync.Port;
            bool dnsChanged = cfg.PhoneDns.Enabled != Config.PhoneDns.Enabled || cfg.PhoneDns.Upstream != Config.PhoneDns.Upstream;
            Config = cfg;
            await Controller.ApplyConfigAsync(cfg);
            if (syncChanged)
            {
                await StopSyncAsync();
                if (cfg.Sync.Enabled) await StartSyncAsync();
            }
            if (dnsChanged)
            {
                await StopPhoneDnsAsync();
                if (cfg.PhoneDns.Enabled) await StartPhoneDnsAsync();
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
    public static string? TailscaleName() => TailscaleInfo.Read()?.DnsName;

    // ------------------------------------------------------------------ phone DNS (app tracking without Shortcuts)

    public async Task SetPhoneDnsEnabledAsync(bool enabled)
    {
        Config.PhoneDns.Enabled = enabled;
        SaveConfig();
        await StopPhoneDnsAsync();
        if (enabled) await StartPhoneDnsAsync();
    }

    /// <summary>This PC's Tailscale IPv4 address (the nameserver address to enter in the Tailscale admin page).</summary>
    public System.Net.IPAddress? TailscaleIPv4() => Tailscale(force: true)?.SelfIPv4;

    private TailscaleInfo? Tailscale(bool force = false)
    {
        lock (_dnsGate)
        {
            long now = TimeUtil.NowMs();
            if (force || _tailscale == null || now - _tailscaleReadMs > 60_000)
            {
                _tailscaleReadMs = now;
                _tailscale = TailscaleInfo.Read() ?? _tailscale;
            }
            return _tailscale;
        }
    }

    /// <summary>Name of the device a query came from; null for this PC (its own lookups aren't tracked).</summary>
    private string? DeviceOf(System.Net.IPAddress ip)
    {
        var ts = Tailscale();
        if (ts == null || ip.Equals(ts.SelfIPv4)) return null;
        if (ts.Peers.TryGetValue(ip, out var name)) return name;
        ts = Tailscale(force: TimeUtil.NowMs() - _tailscaleReadMs > 10_000); // a new device: re-read, at most every 10 s
        return ts != null && ts.Peers.TryGetValue(ip, out name) ? name : null;
    }

    private Task StartPhoneDnsAsync()
    {
        if (_dns != null || _disposed) return Task.CompletedTask;
        try
        {
            var ip = Tailscale(force: true)?.SelfIPv4
                     ?? throw new InvalidOperationException("Tailscale isn't running or isn't signed in on this PC");
            _dns = PhoneDnsLogger.Start(Store, ip, Upstreams, DeviceOf);
            PhoneDnsError = null;
        }
        catch (Exception ex)
        {
            PhoneDnsError = ex is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse }
                ? "port 53 on the Tailscale address is already used by another program" : ex.Message;
            Log.Warn("Phone DNS could not start (retrying every minute): " + PhoneDnsError);
        }
        // Health check every minute while on: Tailscale may not be up yet (right after boot), or the PC slept or
        // changed networks and the socket stopped working.
        _dnsRetry ??= new System.Threading.Timer(_ => SyncContextPost(() => _ = CheckPhoneDnsAsync()), null, 60_000, 60_000);
        return Task.CompletedTask;
    }

    private bool _checking;

    private async Task CheckPhoneDnsAsync()
    {
        if (!Config.PhoneDns.Enabled || _disposed || _checking) return;
        _checking = true;
        try
        {
            if (_dns == null) { await StartPhoneDnsAsync(); return; }
            var ip = await Task.Run(() => Tailscale(force: true)?.SelfIPv4);
            string? why = _dns.Faulted ? "socket failed"
                : ip != null && !ip.Equals(_dns.Listening.Address) ? $"Tailscale address changed to {ip}" : null;
            if (why == null) return;
            Log.Info($"Phone DNS: restarting ({why})");
            await _dns.DisposeAsync();
            _dns = null;
            await StartPhoneDnsAsync();
        }
        catch (Exception ex) { Log.Error("Phone DNS health check failed", ex); }
        finally { _checking = false; }
    }

    private IReadOnlyList<System.Net.IPEndPoint> _upstreams = [];
    private long _upstreamsReadMs;

    /// <summary>Forwarding targets, re-read every 30 s so a laptop that changes networks keeps working.</summary>
    private IReadOnlyList<System.Net.IPEndPoint> Upstreams()
    {
        long now = TimeUtil.NowMs();
        if (_upstreams.Count > 0 && now - _upstreamsReadMs < 30_000) return _upstreams;
        var list = new List<System.Net.IPAddress>();
        if (Config.PhoneDns.Upstream.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                        nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback ||
                        nic.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var a in nic.GetIPProperties().DnsAddresses)
                        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IsTailnet(a) && !list.Contains(a)) list.Add(a);
                }
            }
            catch (Exception ex) { Log.Warn("Phone DNS: couldn't read this PC's DNS servers: " + ex.Message); }
            foreach (var s in new[] { "8.8.8.8", "1.1.1.1" })
                if (!list.Contains(System.Net.IPAddress.Parse(s))) list.Add(System.Net.IPAddress.Parse(s));
        }
        else list.Add(System.Net.IPAddress.Parse(Config.PhoneDns.Upstream));
        _upstreams = list.Select(a => new System.Net.IPEndPoint(a, 53)).ToList();
        _upstreamsReadMs = now;
        return _upstreams;
    }

    /// <summary>100.64.0.0/10, Tailscale's address range (100.100.100.100 is its own resolver).</summary>
    private static bool IsTailnet(System.Net.IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 100 && (b[1] & 0xC0) == 64;
    }

    private async Task StopPhoneDnsAsync()
    {
        _dnsRetry?.Dispose();
        _dnsRetry = null;
        if (_dns != null) { await _dns.DisposeAsync(); _dns = null; }
    }

    // ------------------------------------------------------------------ Windows events

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _ = Controller.SetSleepingAsync(true);
        else if (e.Mode == PowerModes.Resume) { _ = Controller.SetSleepingAsync(false); SoonCheckPhoneDns(); }
    }

    /// <summary>After wake-up or a network change, Tailscale needs a few seconds to reconnect; check the DNS listener then.</summary>
    private void SoonCheckPhoneDns()
    {
        if (!Config.PhoneDns.Enabled) return;
        _ = Task.Delay(15_000).ContinueWith(_ => SyncContextPost(() => _ = CheckPhoneDnsAsync()));
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => SoonCheckPhoneDns();

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
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _exclusions.Dispose();
        _focus.Dispose();
        _displayPower.Dispose();
        _configWatcher.Dispose();
        _configDebounce.Dispose();
        await StopSyncAsync();
        await StopPhoneDnsAsync();
        await Controller.DisposeAsync();
    }
}
