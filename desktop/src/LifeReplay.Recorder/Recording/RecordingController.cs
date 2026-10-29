using LifeReplay.Core;
using LifeReplay.Recorder.Capture;

namespace LifeReplay.Recorder.Recording;

internal enum RecState { Stopped, Recording, Paused }

/// <summary>
/// Decides whether we should be recording right now and drives the per-display pipelines.
/// Recording runs only when: the user hasn't paused, the session isn't locked, the PC isn't asleep and
/// there's enough disk space. All state changes are serialized through one semaphore.
/// </summary>
internal sealed class RecordingController : IAsyncDisposable
{
    private readonly RecorderContext _ctx;
    private readonly SemaphoreSlim _ops = new(1, 1);
    private readonly List<DisplayPipeline> _pipelines = [];
    private readonly System.Threading.Timer _rotationTimer;
    private readonly System.Threading.Timer _heartbeatTimer;
    private readonly System.Threading.Timer _displayChangeTimer;
    private RecorderConfig _cfg;
    private string? _sessionId;
    private long? _openGapId;
    private string? _openGapReason;
    private long? _blankGapId;
    private bool _softwareOnly;
    private int _consecutiveFailures;
    private long _lastRetentionMs;
    private long _lastHeartbeatDbMs;
    private long _lastDiskCheckMs;
    private bool _disposed;

    // Inputs to the "should we record" decision.
    private bool _userPaused;
    private long? _pauseUntilMs;
    private bool _locked;
    private bool _sleeping;
    private bool _displayOff;
    private bool _diskLow;
    private long _lastTickMs = TimeUtil.NowMs();
    private bool? _lastInHours;

    public RecState State { get; private set; } = RecState.Stopped;
    public string? StateReason { get; private set; }
    public long StateSinceMs { get; private set; } = TimeUtil.NowMs();
    public string? LastError { get; private set; }
    public bool Blanked { get; private set; }
    public string? BlankedBy { get; private set; }
    public long? PauseUntilMs => _pauseUntilMs;
    public RecorderConfig Config => _cfg;
    public bool SyncEnabled { get; set; }
    public event Action? Changed;

    public IReadOnlyList<(string Name, EncodingPlan Plan, bool Hardware)> ActiveDisplays
    {
        get { lock (_pipelines) return _pipelines.Select(p => (p.Monitor.FriendlyName, p.Plan, p.Hardware)).ToList(); }
    }

    /// <summary>Total frames Windows has delivered on all displays; changes whenever anything on screen changes.</summary>
    public long TotalFramesArrived
    {
        get { lock (_pipelines) return _pipelines.Sum(p => Interlocked.Read(ref p.Capture.FramesArrived)); }
    }

    public RecordingController(RecorderContext ctx, RecorderConfig cfg)
    {
        _ctx = ctx;
        _cfg = cfg;
        _userPaused = !cfg.RecordOnLaunch;
        _rotationTimer = new System.Threading.Timer(_ => _ = RotateAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _heartbeatTimer = new System.Threading.Timer(_ => _ = HeartbeatAsync(), null, 1000, 5000);
        _displayChangeTimer = new System.Threading.Timer(_ => _ = RestartAsync("display configuration changed"), null, Timeout.Infinite, Timeout.Infinite);
    }

    // ------------------------------------------------------------------ public inputs

    public Task StartAsync() => EvaluateAsync();

    public Task PauseAsync(TimeSpan? duration = null)
    {
        _userPaused = true;
        _pauseUntilMs = duration.HasValue ? TimeUtil.NowMs() + (long)duration.Value.TotalMilliseconds : null;
        Log.Info(duration.HasValue ? $"Paused by user for {duration.Value.TotalMinutes:0} min" : "Paused by user");
        return EvaluateAsync();
    }

    public Task ResumeAsync()
    {
        _userPaused = false;
        _pauseUntilMs = null;
        Log.Info("Resumed by user");
        return EvaluateAsync();
    }

    public Task TogglePauseAsync() => _userPaused ? ResumeAsync() : PauseAsync();
    public bool UserPaused => _userPaused;

    public Task SetLockedAsync(bool locked) { _locked = locked; Log.Info(locked ? "Session locked" : "Session unlocked"); return EvaluateAsync(); }

    /// <summary>Console display turned off/on. On Modern Standby PCs this is the only signal before standby.</summary>
    public Task SetDisplayOffAsync(bool off)
    {
        if (_displayOff == off) return Task.CompletedTask;
        _displayOff = off;
        Log.Info(off ? "Display turned off" : "Display turned on");
        return EvaluateAsync();
    }

    public Task SetSleepingAsync(bool sleeping)
    {
        _sleeping = sleeping;
        Log.Info(sleeping ? "System is going to sleep" : "System resumed");
        return sleeping ? EvaluateAsync() : Task.Delay(3000).ContinueWith(_ => EvaluateAsync()).Unwrap();
    }

    /// <summary>Display add/remove/resolution change: restart capture after things settle.</summary>
    public void DisplaysChanged()
    {
        Log.Info("Display settings changed");
        _displayChangeTimer.Change(2500, Timeout.Infinite);
    }

    public async Task ApplyConfigAsync(RecorderConfig cfg)
    {
        _cfg = cfg;
        _lastInHours = null; // re-check recording hours on the next heartbeat
        await EvaluateAsync();
        Log.Info("Configuration reloaded");
        await RestartAsync("configuration changed");
    }

    private string? _exclusionMatch;
    private bool _manualBlank;

    /// <summary>An excluded app/title is in the foreground (null = none).</summary>
    public Task SetExclusionAsync(string? match) { _exclusionMatch = match; return ApplyBlankAsync(); }

    /// <summary>User-requested privacy blank (`--blank` / `--unblank`).</summary>
    public Task SetManualBlankAsync(bool on) { _manualBlank = on; return ApplyBlankAsync(); }
    public bool ManualBlank => _manualBlank;

    private Task ApplyBlankAsync() =>
        SetBlankedAsync(_manualBlank || _exclusionMatch != null, _manualBlank ? "manual privacy blank" : _exclusionMatch);

    /// <summary>Privacy blanking: video keeps running but shows black, and an "excluded" gap is recorded.</summary>
    private async Task SetBlankedAsync(bool blanked, string? by)
    {
        await _ops.WaitAsync();
        try
        {
            if (Blanked == blanked) return;
            Blanked = blanked;
            BlankedBy = blanked ? by : null;
            lock (_pipelines) foreach (var p in _pipelines) p.Capture.Blanked = blanked;
            long now = TimeUtil.NowMs();
            if (blanked)
            {
                Log.Info($"Blanking video: excluded window in foreground ({by})");
                _blankGapId = _ctx.Store.InsertGap(new GapRecord { DeviceId = _ctx.DeviceId, StartUtcMs = now, Reason = GapReasons.Excluded, Detail = by });
            }
            else if (_blankGapId is { } id)
            {
                Log.Info("Blanking ended");
                _ctx.Store.CloseGap(id, now);
                _blankGapId = null;
            }
        }
        finally { _ops.Release(); }
        Changed?.Invoke();
    }

    /// <summary>Stops recording for exit/shutdown. Leaves an open "stopped" gap that the next start closes.</summary>
    public async Task ShutdownAsync(string reason)
    {
        await _ops.WaitAsync();
        try
        {
            _disposed = true;
            _rotationTimer.Change(Timeout.Infinite, Timeout.Infinite);
            await StopCoreAsync(reason);
            long now = TimeUtil.NowMs();
            if (_openGapId is { } g) _ctx.Store.CloseGap(g, now);
            if (_blankGapId is { } b) _ctx.Store.CloseGap(b, now);
            _ctx.Store.InsertGap(new GapRecord { DeviceId = _ctx.DeviceId, StartUtcMs = now, Reason = GapReasons.Stopped, Detail = reason });
            _ctx.Store.Heartbeat(_ctx.DeviceId, now);
            SetState(RecState.Stopped, reason);
            WriteStatus();
        }
        finally { _ops.Release(); }
    }

    // ------------------------------------------------------------------ decision logic

    private string? BlockingReason()
    {
        if (_userPaused) return GapReasons.Paused;
        if (!_cfg.Hours.Allows(DateTime.Now)) return GapReasons.OutsideHours;
        if (_sleeping) return GapReasons.Sleep;
        if (_locked) return GapReasons.Locked;
        if (_displayOff) return GapReasons.DisplayOff;
        if (_diskLow) return GapReasons.DiskFull;
        return null;
    }

    private async Task EvaluateAsync()
    {
        await _ops.WaitAsync();
        try
        {
            if (_disposed) return;
            var reason = BlockingReason();
            if (reason == null)
            {
                if (State != RecState.Recording) await StartCoreAsync();
            }
            else
            {
                if (State == RecState.Recording) await StopCoreAsync(reason);
                if (_openGapReason != reason)
                {
                    long now = TimeUtil.NowMs();
                    if (_openGapId is { } old) _ctx.Store.CloseGap(old, now);
                    _openGapId = _ctx.Store.InsertGap(new GapRecord { DeviceId = _ctx.DeviceId, StartUtcMs = now, Reason = reason });
                    _openGapReason = reason;
                }
                SetState(RecState.Paused, reason);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error("State change failed", ex);
        }
        finally { _ops.Release(); }
        WriteStatus();
        Changed?.Invoke();
    }

    private async Task RestartAsync(string why)
    {
        await _ops.WaitAsync();
        try
        {
            if (_disposed || State != RecState.Recording) return;
            Log.Info($"Restarting capture: {why}");
            await StopCoreAsync(why);
            await StartCoreAsync();
        }
        catch (Exception ex) { LastError = ex.Message; Log.Error("Restart failed", ex); }
        finally { _ops.Release(); }
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ start / stop (call with _ops held)

    private async Task StartCoreAsync()
    {
        long now = TimeUtil.NowMs();
        if (_openGapId is { } g) { _ctx.Store.CloseGap(g, now); _openGapId = null; _openGapReason = null; }

        var monitors = Monitors.Enumerate();
        if (monitors.Count == 0)
        {
            LastError = "No displays found";
            SetState(RecState.Paused, GapReasons.Error);
            ScheduleRetry();
            return;
        }
        _sessionId = Guid.NewGuid().ToString();
        _ctx.Store.InsertSession(new SessionRecord { Id = _sessionId, DeviceId = _ctx.DeviceId, StartUtcMs = now, Status = SessionStatus.Active, AppVersion = AppInfo.Version });
        int started = 0;
        foreach (var m in monitors)
        {
            if (TryStartPipeline(m)) started++;
        }
        if (started == 0)
        {
            _ctx.Store.EndSession(_sessionId, TimeUtil.NowMs(), SessionStatus.Ended, "no display could be captured");
            _sessionId = null;
            SetState(RecState.Paused, GapReasons.Error);
            _openGapId = _ctx.Store.InsertGap(new GapRecord { DeviceId = _ctx.DeviceId, StartUtcMs = TimeUtil.NowMs(), Reason = GapReasons.Error, Detail = LastError });
            _openGapReason = GapReasons.Error;
            ScheduleRetry();
            return;
        }
        SetState(RecState.Recording, null);
        ScheduleRotation();
        await Task.CompletedTask;
    }

    private bool TryStartPipeline(MonitorInfo m)
    {
        if (_sessionId == null) return false;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool hw = _cfg.HardwareEncoding && !_softwareOnly;
            try
            {
                var p = DisplayPipeline.Start(_ctx, _cfg, _sessionId, m, hw, Blanked);
                p.Faulted += OnPipelineFaulted;
                lock (_pipelines) _pipelines.Add(p);
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"{m.DeviceName}: {ex.Message}";
                Log.Error($"Could not start capture of {m.DeviceName}", ex);
                if (!hw) break;
                _softwareOnly = true; // try once more with the software encoder
            }
        }
        return false;
    }

    private async Task StopCoreAsync(string reason)
    {
        _rotationTimer.Change(Timeout.Infinite, Timeout.Infinite);
        List<DisplayPipeline> list;
        lock (_pipelines) { list = _pipelines.ToList(); _pipelines.Clear(); }
        long ts = QpcClock.Now100ns();
        long utc = TimeUtil.NowMs();
        await Task.WhenAll(list.Select(p => p.StopAsync(ts, utc)));
        if (_sessionId != null)
        {
            _ctx.Store.EndSession(_sessionId, utc, SessionStatus.Ended, reason);
            _sessionId = null;
        }
        Log.Info($"Recording stopped ({reason})");
    }

    private void ScheduleRotation()
    {
        long now = TimeUtil.NowMs();
        long segMs = _cfg.SegmentSeconds * 1000L;
        long next = TimeUtil.NextSegmentBoundaryMs(now, segMs, Math.Min(30_000, segMs / 4));
        _rotationTimer.Change(Math.Max(100, next - now), Timeout.Infinite);
    }

    private void ScheduleRetry()
    {
        int delay = Math.Min(60_000, 5_000 * (1 << Math.Min(4, _consecutiveFailures)));
        _consecutiveFailures++;
        Log.Info($"Retrying capture in {delay / 1000} s");
        _ = Task.Delay(delay).ContinueWith(_ => EvaluateAsync());
    }

    private async Task RotateAsync()
    {
        await _ops.WaitAsync();
        try
        {
            if (State != RecState.Recording || _disposed) return;
            long ts = QpcClock.Now100ns();
            long utc = TimeUtil.NowMs();
            List<DisplayPipeline> list;
            lock (_pipelines) list = _pipelines.ToList();
            foreach (var p in list)
            {
                try { p.Rotate(ts, utc); }
                catch (Exception ex) { Log.Error($"Rotation failed for {p.Monitor.DeviceName}", ex); OnPipelineFaulted(p, ex.Message, false); }
            }
            _consecutiveFailures = 0;
        }
        finally
        {
            if (State == RecState.Recording) ScheduleRotation();
            _ops.Release();
        }
    }

    private void OnPipelineFaulted(DisplayPipeline p, string message, bool encoderProblem)
    {
        LastError = $"{p.Monitor.DeviceName}: {message}";
        _ = Task.Run(async () =>
        {
            await _ops.WaitAsync();
            try
            {
                bool removed;
                lock (_pipelines) removed = _pipelines.Remove(p);
                if (!removed) return;
                await p.StopAsync(QpcClock.Now100ns(), TimeUtil.NowMs());
                if (encoderProblem && p.Hardware && ++_consecutiveFailures >= 2)
                {
                    _softwareOnly = true;
                    Log.Warn("Hardware encoder failed repeatedly; switching to the software encoder");
                }
            }
            finally { _ops.Release(); }
            // Restart after a short delay; the monitor may be gone (then the display-change path takes over).
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Max(1, _consecutiveFailures))));
            await _ops.WaitAsync();
            try
            {
                if (State != RecState.Recording || _disposed) return;
                var m = Monitors.Enumerate().FirstOrDefault(x => x.Id == p.Monitor.Id);
                bool running;
                lock (_pipelines) running = _pipelines.Any(x => x.DisplayId == p.Monitor.Id);
                if (m != null && !running)
                {
                    Log.Info($"Restarting capture of {m.DeviceName}");
                    TryStartPipeline(m);
                }
            }
            finally { _ops.Release(); }
            Changed?.Invoke();
        });
        Changed?.Invoke();
    }

    private void SetState(RecState s, string? reason)
    {
        if (State == s && StateReason == reason) return;
        State = s;
        StateReason = reason;
        StateSinceMs = TimeUtil.NowMs();
    }

    // ------------------------------------------------------------------ periodic work

    private async Task HeartbeatAsync()
    {
        if (_disposed) return;
        long now = TimeUtil.NowMs();
        long sinceLastTick = now - Interlocked.Exchange(ref _lastTickMs, now);
        try
        {
            if (sinceLastTick > 30_000)
            {
                // This timer fires every 5 s; a long silence means the whole process was suspended
                // (Modern Standby sends no suspend event). Record the gap and start fresh segments.
                Log.Warn($"No timer tick for {sinceLastTick / 1000} s: system was asleep or the recorder was frozen");
                if (State == RecState.Recording)
                {
                    _ctx.Store.InsertGap(new GapRecord { DeviceId = _ctx.DeviceId, StartUtcMs = now - sinceLastTick, EndUtcMs = now, Reason = GapReasons.Sleep, Detail = "standby detected" });
                    await RestartAsync("resumed after standby");
                }
            }
            bool inHours = _cfg.Hours.Allows(DateTime.Now);
            if (inHours != _lastInHours)
            {
                _lastInHours = inHours;
                Log.Info(inHours ? $"Recording hours started ({_cfg.Hours.Describe()})" : $"Outside recording hours ({_cfg.Hours.Describe()})");
                await EvaluateAsync();
            }
            if (_userPaused && _pauseUntilMs is { } until && now >= until)
            {
                _userPaused = false;
                _pauseUntilMs = null;
                Log.Info("Timed pause ended");
                await EvaluateAsync();
            }
            if (now - _lastHeartbeatDbMs >= 4_500) // recovery uses this as "last known alive" after a crash
            {
                _ctx.Store.Heartbeat(_ctx.DeviceId, now);
                _lastHeartbeatDbMs = now;
            }
            if (now - _lastDiskCheckMs >= 30_000)
            {
                _lastDiskCheckMs = now;
                CheckDisk();
            }
            if (now - _lastRetentionMs >= 10 * 60_000)
            {
                _lastRetentionMs = now;
                RecordingDeleter.ApplyRetention(_ctx.Store, _ctx.Paths, _cfg, now);
            }
            WriteStatus();
        }
        catch (Exception ex) { Log.Warn("Heartbeat failed", ex); }
    }

    private void CheckDisk()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(_ctx.Paths.DataDir)!);
            double freeGb = drive.AvailableFreeSpace / 1024d / 1024 / 1024;
            if (!_diskLow && freeGb < _cfg.MinFreeDiskGB)
            {
                RecordingDeleter.ApplyRetention(_ctx.Store, _ctx.Paths, _cfg, TimeUtil.NowMs());
                freeGb = new DriveInfo(drive.Name).AvailableFreeSpace / 1024d / 1024 / 1024;
                if (freeGb < _cfg.MinFreeDiskGB)
                {
                    Log.Warn($"Only {freeGb:0.0} GB free (minimum {_cfg.MinFreeDiskGB} GB); pausing");
                    LastError = $"Low disk space: {freeGb:0.0} GB free";
                    _diskLow = true;
                    _ = EvaluateAsync();
                }
            }
            else if (_diskLow && freeGb > _cfg.MinFreeDiskGB + 1)
            {
                _diskLow = false;
                Log.Info("Disk space recovered; resuming");
                _ = EvaluateAsync();
            }
        }
        catch (Exception ex) { Log.Warn("Disk check failed", ex); }
    }

    public void WriteStatus()
    {
        try
        {
            var displays = ActiveDisplays;
            new RecorderStatus
            {
                State = State switch { RecState.Recording => "recording", RecState.Paused => "paused", _ => "stopped" },
                Reason = StateReason,
                SinceUtcMs = StateSinceMs,
                UpdatedUtcMs = TimeUtil.NowMs(),
                ProcessId = Environment.ProcessId,
                Displays = displays.Select(d => $"{d.Name}: {d.Plan.Width}x{d.Plan.Height} @ ≤{d.Plan.Fps} fps ({(d.Hardware ? "HW" : "SW")} H.264)").ToList(),
                StorageBytes = _ctx.Store.GetTotalStorageBytes(),
                EstimatedMaxBytesPerHour = EncodingPlan.EstimateMaxBytesPerHour(displays.Select(d => d.Plan)),
                LastError = LastError,
                Blanked = Blanked,
                SyncEnabled = SyncEnabled,
            }.Save(_ctx.Paths.StatusPath);
        }
        catch (Exception ex) { Log.Warn("Writing status failed", ex); }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed) await ShutdownAsync("recorder exited");
        await _rotationTimer.DisposeAsync();
        await _heartbeatTimer.DisposeAsync();
        await _displayChangeTimer.DisposeAsync();
    }
}

internal static class AppInfo
{
    public static string Version => typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
