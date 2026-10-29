using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LifeReplay.Core;

namespace LifeReplay.Viewer;

public partial class MainWindow : Window
{
    private static readonly double[] Speeds = [1, 2, 4, 8, 16];

    private readonly AppPaths _paths;
    private readonly MetadataStore _store;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly Stopwatch _clock = new();
    private readonly List<DisplayView> _views = [];
    private Dictionary<string, List<ThumbnailRecord>> _thumbs = [];
    private List<DateOnly> _days = [];
    private DayTimeline? _timeline;
    private DateOnly _day;
    private long _t;
    private bool _playing;
    private double _speed = 1;
    private string _viewKey = "all";
    private bool _suppressEvents;
    private string? _initialTime;

    internal MainWindow(AppPaths paths, MetadataStore store, DateOnly? day, string? time)
    {
        _paths = paths;
        _store = store;
        _initialTime = time;
        InitializeComponent();
        // Fit the screen: a fixed 1400×900 window is bigger than a 1920×1200 display at 150% scaling,
        // which pushes the title bar (minimize / maximize / close) off-screen.
        var work = SystemParameters.WorkArea;
        Width = Math.Min(1400, work.Width * 0.92);
        Height = Math.Min(900, work.Height * 0.92);
        MinWidth = Math.Min(MinWidth, work.Width);
        MinHeight = Math.Min(MinHeight, work.Height);

        foreach (var s in Speeds) SpeedCombo.Items.Add($"{s:0}×");
        SpeedCombo.SelectedIndex = 0;
        SpeedCombo.SelectionChanged += (_, _) => { if (SpeedCombo.SelectedIndex >= 0) SetSpeed(Speeds[SpeedCombo.SelectedIndex]); };

        _clockTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _clockTimer.Tick += (_, _) => Tick();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refreshTimer.Tick += (_, _) => { if (_day == DateOnly.FromDateTime(DateTime.Now)) LoadDay(_day, keepPosition: true); UpdateStatus(); };
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };

        Timeline.SeekRequested += t => Seek(t);
        Timeline.ThumbnailLookup = FindThumbnail;
        PlayButton.Click += (_, _) => TogglePlay();
        Back10Button.Click += (_, _) => Seek(_t - 10_000);
        Back60Button.Click += (_, _) => Seek(_t - 60_000);
        Fwd10Button.Click += (_, _) => Seek(_t + 10_000);
        Fwd60Button.Click += (_, _) => Seek(_t + 60_000);
        PrevSegButton.Click += (_, _) => JumpSegment(-1);
        NextSegButton.Click += (_, _) => JumpSegment(1);
        JumpButton.Click += (_, _) => JumpToText();
        JumpBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { JumpToText(); e.Handled = true; } };
        RefreshButton.Click += (_, _) => { LoadDays(); LoadDay(_day, keepPosition: true); };
        TodayButton.Click += (_, _) => DayPicker.SelectedDate = DateTime.Today;
        PrevDayButton.Click += (_, _) => StepDay(-1);
        NextDayButton.Click += (_, _) => StepDay(1);
        DayPicker.SelectedDateChanged += (_, _) =>
        {
            if (_suppressEvents || DayPicker.SelectedDate is not { } d) return;
            LoadDay(DateOnly.FromDateTime(d), keepPosition: false);
        };
        DaysCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressEvents || DaysCombo.SelectedItem is not ComboBoxItem { Tag: DateOnly d }) return;
            DayPicker.SelectedDate = d.ToDateTime(TimeOnly.MinValue);
        };
        ViewCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressEvents || ViewCombo.SelectedItem is not ComboBoxItem { Tag: string key }) return;
            _viewKey = key;
            BuildViews();
            Apply();
        };
        DeleteSegmentItem.Click += (_, _) => DeleteAtPlayhead(session: false);
        DeleteSessionItem.Click += (_, _) => DeleteAtPlayhead(session: true);
        DeleteDayItem.Click += (_, _) => DeleteDay();
        SummaryButton.Checked += (_, _) => { SummaryPanel.Visibility = Visibility.Visible; UpdateSummary(); };
        SummaryButton.Unchecked += (_, _) => SummaryPanel.Visibility = Visibility.Collapsed;
        InitSummaryModes();
        PreviewKeyDown += OnKey;
        Closed += (_, _) => { foreach (var v in _views) v.Unload(); };

        LoadDays();
        var startDay = day ?? (_days.Count > 0 && !_days.Contains(DateOnly.FromDateTime(DateTime.Now)) ? _days[^1] : DateOnly.FromDateTime(DateTime.Now));
        _suppressEvents = true;
        DayPicker.SelectedDate = startDay.ToDateTime(TimeOnly.MinValue);
        _suppressEvents = false;
        LoadDay(startDay, keepPosition: false);
        UpdateStatus();
        _clockTimer.Start();
        _refreshTimer.Start();
    }

    // ------------------------------------------------------------------ loading

    private void LoadDays()
    {
        var tz = TimeZoneInfo.Local;
        var counts = _store.GetAllSegmentStarts().GroupBy(ms => TimeUtil.LocalDate(ms, tz)).ToDictionary(g => g.Key, g => g.Count());
        _days = counts.Keys.OrderBy(d => d).ToList();
        _suppressEvents = true;
        DaysCombo.Items.Clear();
        foreach (var d in Enumerable.Reverse(_days))
            DaysCombo.Items.Add(new ComboBoxItem { Content = $"{d:yyyy-MM-dd ddd}  ·  {counts[d]} segment(s)", Tag = d });
        if (_days.Count == 0) DaysCombo.Items.Add(new ComboBoxItem { Content = "No recordings yet", IsEnabled = false });
        _suppressEvents = false;
    }

    private void StepDay(int dir)
    {
        var candidates = dir < 0 ? _days.Where(d => d < _day).ToList() : _days.Where(d => d > _day).ToList();
        if (candidates.Count == 0) { ShowToast(dir < 0 ? "No earlier recordings" : "No later recordings"); return; }
        var target = dir < 0 ? candidates[^1] : candidates[0];
        DayPicker.SelectedDate = target.ToDateTime(TimeOnly.MinValue);
    }

    private void LoadDay(DateOnly day, bool keepPosition)
    {
        bool sameDay = day == _day && _timeline != null;
        _day = day;
        var (start, end) = TimeUtil.LocalDayRange(day);
        long now = TimeUtil.NowMs();
        var segments = _store.GetSegmentsInRange(start, end);
        var gaps = _store.GetGapsInRange(start, end);
        _timeline = DayTimeline.Build(start, end, now, segments, gaps, _store.GetDisplays(), _store.GetDevices());
        _timeline.Focus = _store.GetFocusInRange(start, end);
        // iPhone app sessions: read a margin around the day so sessions crossing midnight pair up correctly.
        _timeline.PhoneSessions = AppSessions.Reconstruct(_store.GetAppEvents(start - 86_400_000, end + 86_400_000))
            .Where(s => s.EndUtcMs >= start && s.StartUtcMs < end).ToList();
        _timeline.PhoneStatus = _store.GetAppEventStatus();
        UpdateSummary();
        _thumbs = _store.GetThumbnails(segments.Select(s => s.Id)).GroupBy(t => t.SegmentId).ToDictionary(g => g.Key, g => g.OrderBy(t => t.TsUtcMs).ToList());
        Timeline.Data = _timeline;
        if (!sameDay)
        {
            Timeline.ClearImageCache();
            // Zoom to the part of the day that has recordings (at least 1 hour, with a margin).
            var all = _timeline.Lanes.SelectMany(l => l.Segments).ToList();
            if (all.Count > 0)
            {
                long first = all.Min(s => s.StartUtcMs), last = all.Max(s => s.EffectiveEnd(now));
                long margin = Math.Max(10 * 60_000, (last - first) / 20);
                long from = first - margin, to = last + margin;
                if (to - from < 3_600_000) { long mid = (from + to) / 2; from = mid - 1_800_000; to = mid + 1_800_000; }
                Timeline.ZoomTo(from, to);
            }
        }

        _suppressEvents = true;
        DaysCombo.SelectedItem = DaysCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is DateOnly d && d == day);
        var prevKey = _viewKey;
        ViewCombo.Items.Clear();
        ViewCombo.Items.Add(new ComboBoxItem { Content = _timeline.Lanes.Count > 1 ? $"All screens ({_timeline.Lanes.Count})" : "All screens", Tag = "all" });
        foreach (var lane in _timeline.Lanes)
            ViewCombo.Items.Add(new ComboBoxItem { Content = (lane.Platform == Platforms.Ios ? "iPhone · " : "") + lane.Label, Tag = lane.DisplayId });
        var match = ViewCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == prevKey);
        ViewCombo.SelectedItem = match ?? ViewCombo.Items[0];
        _viewKey = (string)((ComboBoxItem)ViewCombo.SelectedItem).Tag;
        _suppressEvents = false;

        if (!(keepPosition && sameDay))
        {
            Pause();
            _t = _timeline.FirstRecordingStart() ?? start;
            if (_initialTime != null && TryParseLocalTime(_initialTime, out var it)) _t = it;
            _initialTime = null;
        }
        Log.Info($"Loaded {day}: {segments.Count} segment(s), {_timeline.Lanes.Count} lane(s), now {TimeUtil.ToLocal(now):HH:mm:ss}, position {TimeUtil.ToLocal(_t):HH:mm:ss}");
        BuildViews();
        EmptyText.Text = _timeline.Lanes.Count == 0
            ? $"No recordings on {day:dddd, MMMM d}.\n\nStart the LifeReplay recorder (tray icon) to begin recording, or pick another day."
            : "";
        Apply();
    }

    private void BuildViews()
    {
        foreach (var v in _views) v.Unload();
        _views.Clear();
        VideoGrid.Children.Clear();
        if (_timeline == null) return;
        var lanes = _viewKey == "all" ? _timeline.Lanes : _timeline.Lanes.Where(l => l.DisplayId == _viewKey).ToList();
        foreach (var lane in lanes)
        {
            var v = new DisplayView(lane, _paths);
            _views.Add(v);
            VideoGrid.Children.Add(v);
        }
        int n = _views.Count;
        VideoGrid.Columns = n <= 1 ? 1 : n <= 4 ? 2 : 3;
        VideoGrid.Rows = n <= 2 ? 1 : (int)Math.Ceiling(n / (double)VideoGrid.Columns);
    }

    private string? FindThumbnail(TimelineLane lane, long t)
    {
        var seg = lane.SegmentAt(t, _timeline?.NowMs ?? TimeUtil.NowMs());
        if (seg == null || !_thumbs.TryGetValue(seg.Id, out var list) || list.Count == 0) return null;
        ThumbnailRecord best = list[0];
        foreach (var th in list) { if (th.TsUtcMs <= t) best = th; else break; }
        return _paths.ToFull(best.FilePath);
    }

    // ------------------------------------------------------------------ clock & playback

    private void TogglePlay()
    {
        if (_playing) Pause(); else Play();
    }

    private void Play()
    {
        if (_timeline == null || _timeline.Lanes.Count == 0) return;
        if (!_timeline.AnyCoverage(_t))
        {
            var next = _timeline.NextSegmentStart(_t);
            if (next == null) { ShowToast("No more recordings on this day"); return; }
            _t = next.Value;
        }
        _playing = true;
        _clock.Restart();
        PlayButton.Content = "❚❚  Pause";
        Apply();
    }

    private void Pause()
    {
        _playing = false;
        _clock.Reset();
        PlayButton.Content = "▶  Play";
        Apply();
    }

    private void SetSpeed(double speed)
    {
        _speed = speed;
        int i = Array.IndexOf(Speeds, speed);
        if (i >= 0 && SpeedCombo.SelectedIndex != i) SpeedCombo.SelectedIndex = i;
        Apply();
    }

    private void Tick()
    {
        if (_timeline == null) return;
        if (_playing)
        {
            long elapsed = _clock.ElapsedMilliseconds;
            _clock.Restart();
            long next = _t + (long)(elapsed * _speed);
            long limit = Math.Min(_timeline.DayEndMs, TimeUtil.NowMs());
            if (!_timeline.AnyCoverage(next))
            {
                // Recording gaps are shown on the timeline; playback jumps straight to the next recording.
                var resume = _timeline.NextSegmentStart(next);
                if (resume == null || resume >= limit) { _t = Math.Min(next, limit); Pause(); ShowToast("End of recordings for this day"); return; }
                ShowToast($"Skipped gap {TimeUtil.ToLocal(next):HH:mm:ss} → {TimeUtil.ToLocal(resume.Value):HH:mm:ss}");
                next = resume.Value;
            }
            if (SkipInactiveCheck.IsChecked == true && !_timeline.IsActive(next))
            {
                var active = _timeline.NextActiveTime(next, limit);
                if (active == null) { _t = next; Pause(); ShowToast("No more activity on this day"); return; }
                if (active.Value - next > 3000) ShowToast($"Skipped inactive {TimeUtil.FormatDuration(active.Value - next)}");
                next = active.Value;
            }
            if (next >= limit) { _t = limit - 1; Pause(); return; }
            _t = next;
        }
        Apply();
    }

    private void Seek(long t)
    {
        if (_timeline == null) return;
        _t = Math.Clamp(t, _timeline.DayStartMs, _timeline.DayEndMs - 1);
        if (_playing) _clock.Restart();
        Apply();
    }

    private void JumpSegment(int dir)
    {
        if (_timeline == null) return;
        var target = dir > 0 ? _timeline.NextSegmentStart(_t) : _timeline.PreviousSegmentStart(_t);
        if (target == null) { ShowToast(dir > 0 ? "No later recording on this day" : "No earlier recording on this day"); return; }
        Seek(target.Value);
    }

    private void JumpToText()
    {
        if (TryParseLocalTime(JumpBox.Text, out var t))
        {
            Seek(t);
            if (_timeline != null && !_timeline.AnyCoverage(t)) ShowToast("Nothing recorded at that time (see the hatched gap on the timeline)");
        }
        else ShowToast("Enter a time like 14:30, 2:30 PM or 14:30:15");
    }

    private bool TryParseLocalTime(string text, out long utcMs)
    {
        utcMs = 0;
        text = text.Trim();
        string[] formats = ["H:mm", "H:mm:ss", "HH:mm", "HH:mm:ss", "h:mm tt", "h:mm:ss tt", "h tt", "Hmm"];
        if (!DateTime.TryParseExact(text, formats, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed)
            && !DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
            && !DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.NoCurrentDateDefault, out parsed))
            return false;
        var local = _day.ToDateTime(TimeOnly.FromTimeSpan(parsed.TimeOfDay));
        utcMs = TimeUtil.LocalToUtcMs(local);
        return true;
    }

    /// <summary>Pushes the current time to the timeline, the video tiles and the time label.</summary>
    private void Apply()
    {
        if (_timeline == null) return;
        Timeline.Playhead = _t;
        foreach (var v in _views) v.Sync(_t, _playing, _speed, _timeline);
        TimeText.Text = TimeUtil.ToLocal(_t).ToString("HH:mm:ss");
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: Seek(_t - (shift ? 60_000 : 10_000)); break;
            case Key.Right: Seek(_t + (shift ? 60_000 : 10_000)); break;
            case Key.PageUp: JumpSegment(-1); break;
            case Key.PageDown: JumpSegment(1); break;
            case Key.Up: SetSpeed(Speeds[Math.Min(Speeds.Length - 1, Array.IndexOf(Speeds, _speed) + 1)]); break;
            case Key.Down: SetSpeed(Speeds[Math.Max(0, Array.IndexOf(Speeds, _speed) - 1)]); break;
            case Key.F5: LoadDays(); LoadDay(_day, keepPosition: true); break;
            default: return;
        }
        e.Handled = true;
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ------------------------------------------------------------------ time per app / tab

    private void RenderApps()
    {
        if (_timeline == null) return;
        var apps = FocusSummary.Summarize(_timeline.Focus, _timeline.DayStartMs, _timeline.DayEndMs);
        long total = apps.Sum(a => a.DurationMs);
        SummaryTitle.Text = $"Time per app · {_day:ddd, MMM d}";
        SummarySubtitle.Text = apps.Count == 0
            ? "No window titles logged for this day. Logging starts when the recorder runs this version (Settings → Privacy)."
            : $"{TimeUtil.FormatDuration(total)} of active use. Idle time and pauses are not counted. Click an item to jump to it.";
        SummaryList.Children.Clear();
        var muted = (System.Windows.Media.Brush)FindResource("Muted");
        foreach (var app in apps)
        {
            var header = new DockPanel { Margin = new Thickness(0, 10, 0, 2) };
            var dur = new TextBlock { Text = TimeUtil.FormatDuration(app.DurationMs), FontWeight = FontWeights.SemiBold };
            DockPanel.SetDock(dur, Dock.Right);
            header.Children.Add(dur);
            header.Children.Add(new TextBlock { Text = app.App, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            SummaryList.Children.Add(header);
            // Share bar
            SummaryList.Children.Add(new Border
            {
                Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4),
                Width = Math.Max(2, 330 * app.DurationMs / (double)Math.Max(1, total)), Background = (System.Windows.Media.Brush)FindResource("Accent"),
            });
            foreach (var item in app.Items.Take(12))
            {
                var row = new DockPanel { Margin = new Thickness(10, 1, 0, 1), Cursor = Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent,
                                          ToolTip = item.Item };
                var d = new TextBlock { Text = TimeUtil.FormatDuration(item.DurationMs), Foreground = muted, FontSize = 12 };
                DockPanel.SetDock(d, Dock.Right);
                row.Children.Add(d);
                row.Children.Add(new TextBlock { Text = item.Item, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) });
                var appName = app.App;
                var itemName = item.Item;
                row.MouseLeftButtonUp += (_, _) =>
                {
                    var first = _timeline.Focus.FirstOrDefault(f => FocusSummary.AppName(f.Process) == appName && FocusSummary.ItemName(f.Process, f.Title) == itemName);
                    if (first != null) Seek(first.StartUtcMs);
                };
                SummaryList.Children.Add(row);
            }
            if (app.Items.Count > 12)
                SummaryList.Children.Add(new TextBlock { Text = $"+ {app.Items.Count - 12} more", Foreground = muted, FontSize = 12, Margin = new Thickness(10, 1, 0, 1) });
        }
    }

    // ------------------------------------------------------------------ status & deletion

    private void UpdateStatus()
    {
        long now = TimeUtil.NowMs();
        var st = RecorderStatus.TryLoad(_paths.StatusPath);
        string recorder = st == null || !st.IsFresh(now) ? "Recorder not running"
            : st.State == "recording" ? (st.Blanked ? "● Recording (blanked)" : $"● Recording {st.Displays.Count} display(s)")
            : $"❚❚ Recorder paused ({GapReasons.Describe(st.Reason ?? "")})";
        long used = _store.GetTotalStorageBytes();
        string free = "";
        try { free = $" · {TimeUtil.FormatBytes(new DriveInfo(Path.GetPathRoot(_paths.DataDir)!).AvailableFreeSpace)} free"; } catch { }
        string rate = st is { EstimatedMaxBytesPerHour: > 0 } ? $" · about {TimeUtil.FormatBytes(st.EstimatedMaxBytesPerHour)}/h while recording" : "";
        StatusText.Text = $"{recorder} · {TimeUtil.FormatBytes(used)} stored{rate}{free}";
        StatusText.ToolTip = $"Data folder: {_paths.DataDir}" + (st?.LastError is { } err ? $"\nLast recorder error: {err}" : "");
    }

    private void DeleteAtPlayhead(bool session)
    {
        if (_timeline == null) return;
        var atCursor = _timeline.Lanes.Select(l => l.SegmentAt(_t, _timeline.NowMs)).Where(s => s != null).Cast<SegmentRecord>().ToList();
        Log.Info($"Delete requested ({(session ? "session" : "segment")}) at {TimeUtil.ToLocal(_t):HH:mm:ss}: {atCursor.Count} segment(s) under the playhead");
        if (atCursor.Count == 0) { ShowToast("No recording at the playhead"); return; }
        List<SegmentRecord> targets;
        string what;
        if (session)
        {
            var ids = atCursor.Select(s => s.SessionId).ToHashSet();
            targets = ids.SelectMany(id => _store.GetSegmentsWhere("session_id = $s", ("$s", id))).ToList();
            var first = targets.Min(s => s.StartUtcMs);
            var last = targets.Max(s => s.EndUtcMs ?? s.StartUtcMs);
            what = $"the recording session from {TimeUtil.ToLocal(first):g} to {TimeUtil.ToLocal(last):t} ({targets.Count} segment file(s))";
        }
        else
        {
            targets = atCursor;
            what = $"{targets.Count} segment file(s) around {TimeUtil.ToLocal(_t):T}";
        }
        Delete(targets, what);
    }

    private void DeleteDay()
    {
        if (_timeline == null) return;
        var targets = _timeline.Lanes.SelectMany(l => l.Segments).Where(s => s.StartUtcMs >= _timeline.DayStartMs).ToList();
        if (targets.Count == 0) { ShowToast("Nothing to delete on this day"); return; }
        Delete(targets, $"all {targets.Count} segment file(s) that start on {_day:dddd, MMMM d}");
    }

    private void Delete(List<SegmentRecord> targets, string what)
    {
        var recording = targets.Count(s => s.Status == SegmentStatus.Recording);
        if (MessageBox.Show(this, $"Permanently delete {what}?\n\nThis cannot be undone." +
                (recording > 0 ? $"\n\n{recording} segment(s) still being recorded will be kept." : ""),
                "Delete recordings", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Pause();
        foreach (var v in _views) v.Unload(); // release file handles
        var result = RecordingDeleter.DeleteSegments(_store, _paths, targets.Select(s => s.Id));
        // Deleting recordings also deletes the app/title log for that time.
        foreach (var s in targets.Where(s => s.Status != SegmentStatus.Recording))
            _store.DeleteFocusInRange(s.StartUtcMs, s.EndUtcMs ?? s.StartUtcMs);
        Log.Info($"Viewer: user deleted {result.Deleted} segment(s), {TimeUtil.FormatBytes(result.BytesFreed)}");
        ShowToast($"Deleted {result.Deleted} segment(s), freed {TimeUtil.FormatBytes(result.BytesFreed)}" + (result.Skipped > 0 ? $"; {result.Skipped} skipped" : ""));
        LoadDays();
        LoadDay(_day, keepPosition: true);
        UpdateStatus();
    }
}
