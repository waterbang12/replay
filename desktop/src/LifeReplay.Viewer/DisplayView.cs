using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LifeReplay.Core;

namespace LifeReplay.Viewer;

/// <summary>
/// One video tile. The main window owns the clock; each tile loads whichever segment covers the current time
/// and keeps its MediaElement within a small tolerance of the clock (seeking only when it drifts).
/// </summary>
internal sealed class DisplayView : Border
{
    private readonly AppPaths _paths;
    private readonly MediaElement _media;
    private readonly TextBlock _label;
    private readonly TextBlock _overlay;
    private SegmentRecord? _loaded;
    private bool _opened;
    private bool _isPlaying;
    private double _speed = 1;
    private long _pendingOffsetMs;
    private bool _pendingPlay;

    public TimelineLane Lane { get; }
    public string? LoadedSegmentId => _loaded?.Id;
    public event Action<DisplayView, string>? Failed;

    public DisplayView(TimelineLane lane, AppPaths paths)
    {
        Lane = lane;
        _paths = paths;
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x34));
        BorderThickness = new Thickness(1);
        Background = Brushes.Black;
        _media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            ScrubbingEnabled = true,
            IsMuted = true,
            Stretch = Stretch.Uniform,
        };
        _media.MediaOpened += (_, _) =>
        {
            _opened = true;
            _media.SpeedRatio = _speed;
            _media.Position = Clamp(_pendingOffsetMs);
            if (_pendingPlay) { _media.Play(); _isPlaying = true; }
            else { _media.Pause(); _isPlaying = false; }
        };
        _media.MediaFailed += (_, e) =>
        {
            Log.Warn($"Playback failed for {_loaded?.FilePath}", e.ErrorException);
            _overlay!.Text = "Cannot play this segment:\n" + e.ErrorException?.Message;
            _overlay.Visibility = Visibility.Visible;
            Failed?.Invoke(this, e.ErrorException?.Message ?? "unknown error");
        };
        _media.MediaEnded += (_, _) => { _isPlaying = false; _media.Pause(); };
        _label = new TextBlock
        {
            Margin = new Thickness(8, 6, 8, 6), FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Color.FromArgb(0xAA, 0, 0, 0)),
            Padding = new Thickness(6, 2, 6, 2),
        };
        _overlay = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA8)), FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20),
        };
        var grid = new Grid();
        grid.Children.Add(_media);
        grid.Children.Add(_overlay);
        grid.Children.Add(_label);
        Child = grid;
    }

    private TimeSpan Clamp(long offsetMs)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, offsetMs));
        if (_media.NaturalDuration.HasTimeSpan)
        {
            var max = _media.NaturalDuration.TimeSpan - TimeSpan.FromMilliseconds(30);
            if (max > TimeSpan.Zero && t > max) t = max;
        }
        return t;
    }

    /// <summary>Brings the tile in line with the clock at <paramref name="t"/>.</summary>
    public void Sync(long t, bool playing, double speed, DayTimeline timeline)
    {
        var seg = Lane.SegmentAt(t, timeline.NowMs);
        _label.Text = $"{(Lane.Platform == Platforms.Ios ? "iPhone" : "Display")} · {Lane.Label}   {TimeUtil.ToLocal(t):HH:mm:ss}";
        if (seg == null)
        {
            Unload();
            var gap = timeline.GapAt(Lane, t);
            var inProgress = Lane.Segments.FirstOrDefault(s => s.Status == SegmentStatus.Recording && s.StartUtcMs <= t);
            _overlay.Text = inProgress != null ? "Recording in progress\n(this part becomes playable when the current segment closes)"
                : gap != null ? $"No recording: {GapReasons.Describe(gap.Reason)}" + (gap.Detail is { Length: > 0 } d && gap.Reason != GapReasons.Excluded ? $"\n{d}" : "")
                : "No recording at this time";
            _overlay.Visibility = Visibility.Visible;
            return;
        }
        _overlay.Visibility = Visibility.Collapsed;
        long offset = t - seg.StartUtcMs;
        if (_loaded?.Id != seg.Id)
        {
            Unload();
            _loaded = seg;
            _opened = false;
            _pendingOffsetMs = offset;
            _pendingPlay = playing;
            _speed = speed;
            _media.Source = new Uri(_paths.ToFull(seg.FilePath));
            _media.Pause(); // opens the file and shows the first frame
            return;
        }
        if (!_opened)
        {
            _pendingOffsetMs = offset;
            _pendingPlay = playing;
            _speed = speed;
            return;
        }
        if (Math.Abs(_speed - speed) > 0.01)
        {
            _speed = speed;
            _media.SpeedRatio = speed;
        }
        var target = Clamp(offset);
        bool pastEnd = _media.NaturalDuration.HasTimeSpan && TimeSpan.FromMilliseconds(offset) >= _media.NaturalDuration.TimeSpan;
        if (playing && !pastEnd)
        {
            if (!_isPlaying) { _media.Position = target; _media.Play(); _isPlaying = true; return; }
            double driftMs = Math.Abs((_media.Position - target).TotalMilliseconds);
            if (driftMs > 600 + 250 * speed) _media.Position = target;
        }
        else
        {
            if (_isPlaying) { _media.Pause(); _isPlaying = false; }
            if (Math.Abs((_media.Position - target).TotalMilliseconds) > 40) _media.Position = target;
        }
    }

    /// <summary>Releases the file (needed before deleting it).</summary>
    public void Unload()
    {
        if (_loaded == null) return;
        try { _media.Stop(); _media.Close(); } catch { }
        _media.Source = null;
        _loaded = null;
        _opened = false;
        _isPlaying = false;
    }
}
