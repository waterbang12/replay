using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LifeReplay.Core;

namespace LifeReplay.Viewer;

/// <summary>
/// Day timeline: hour axis, one lane per screen, recorded segments (darker where nothing changed on screen),
/// gaps hatched with their reason, playhead. Click or drag to seek, mouse wheel to zoom, Shift+wheel or
/// right-drag to pan. Hovering shows the nearest thumbnail.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    private const double LabelWidth = 170;
    private const double AxisHeight = 24;
    private const double LaneHeight = 30;
    private const double LaneGap = 6;

    private DayTimeline? _data;
    private long _cursor;
    private long _viewStart, _viewEnd;
    private Point? _hover;
    private bool _dragging;
    private Point? _panStart;
    private long _panViewStart;
    private readonly Popup _popup;
    private readonly Image _popupImage;
    private readonly TextBlock _popupText;
    private readonly Dictionary<string, BitmapImage?> _imageCache = [];
    private readonly Queue<string> _imageOrder = new();

    private static readonly Typeface Font = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Brush BgBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x22)));
    private static readonly Brush LaneBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)));
    private static readonly Brush WinBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3D, 0x8B, 0xFD)));
    private static readonly Brush IosBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59)));
    private static readonly Brush IdleOverlay = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0x15, 0x15, 0x18)));
    private static readonly Brush BadBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)));
    private static readonly Brush RecordingBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x6B, 0xFF)));
    private static readonly Brush FutureBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xCE)));
    private static readonly Brush MutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x88)));
    private static readonly Brush CursorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x3D)));
    private static readonly Pen TickPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x4A)), 1));
    private static readonly Pen HoverPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen CursorPen = Freeze(new Pen(CursorBrush, 2));
    private static readonly Brush GapHatch = MakeHatch(Color.FromArgb(0x55, 0xE5, 0x48, 0x4D));

    public event Action<long>? SeekRequested;
    /// <summary>Returns the thumbnail file nearest to time t for a lane, if any.</summary>
    public Func<TimelineLane, long, string?>? ThumbnailLookup { get; set; }

    public TimelineControl()
    {
        Focusable = true;
        ClipToBounds = true;
        _popupImage = new Image { Width = 320, Stretch = Stretch.Uniform };
        _popupText = new TextBlock { Foreground = Brushes.White, Margin = new Thickness(0, 4, 0, 0), FontSize = 12 };
        var panel = new StackPanel();
        panel.Children.Add(_popupImage);
        panel.Children.Add(_popupText);
        _popup = new Popup
        {
            Child = new Border { Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1A)), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Padding = new Thickness(4), Child = panel },
            PlacementTarget = this, Placement = PlacementMode.Relative, AllowsTransparency = true, IsHitTestVisible = false,
        };
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static readonly Color[] AppPalette =
    [
        Color.FromRgb(0x5B, 0x6C, 0xD9), Color.FromRgb(0x2E, 0x9E, 0x8F), Color.FromRgb(0xC2, 0x6A, 0x2E), Color.FromRgb(0x9C, 0x4D, 0xB8),
        Color.FromRgb(0x3F, 0x8F, 0x3A), Color.FromRgb(0xB8, 0x45, 0x5E), Color.FromRgb(0x4A, 0x86, 0xB5), Color.FromRgb(0x8A, 0x7A, 0x2A),
    ];
    private readonly Dictionary<string, Brush> _appBrushes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stable color per app, so the same app always looks the same.</summary>
    private Brush AppBrush(string process)
    {
        if (_appBrushes.TryGetValue(process, out var b)) return b;
        uint h = 2166136261;
        foreach (char c in process.ToLowerInvariant()) h = (h ^ c) * 16777619;
        b = Freeze(new SolidColorBrush(process == "unknown" ? Color.FromRgb(0x55, 0x55, 0x5C) : AppPalette[h % AppPalette.Length]));
        _appBrushes[process] = b;
        return b;
    }

    private static Brush MakeHatch(Color c)
    {
        var g = new GeometryGroup();
        g.Children.Add(new LineGeometry(new Point(0, 8), new Point(8, 0)));
        var brush = new DrawingBrush(new GeometryDrawing(null, new Pen(new SolidColorBrush(c), 2), g))
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 8, 8), ViewboxUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    public DayTimeline? Data
    {
        get => _data;
        set
        {
            bool newDay = _data == null || value == null || _data.DayStartMs != value.DayStartMs;
            _data = value;
            if (value != null && newDay) { _viewStart = value.DayStartMs; _viewEnd = value.DayEndMs; }
            int lanes = Math.Max(1, value?.Lanes.Count ?? 1) + (value?.Focus.Count > 0 ? 1 : 0);
            Height = AxisHeight + lanes * (LaneHeight + LaneGap) + 8;
            InvalidateVisual();
        }
    }

    public long Playhead
    {
        get => _cursor;
        set
        {
            if (_cursor == value) return;
            _cursor = value;
            // Keep the playhead visible when zoomed in.
            long span = _viewEnd - _viewStart;
            if (_data != null && span < _data.DayEndMs - _data.DayStartMs && (value < _viewStart || value > _viewEnd))
            {
                SetView(value - span / 4, value - span / 4 + span);
            }
            InvalidateVisual();
        }
    }

    public void ZoomTo(long start, long end) => SetView(start, end);

    private void SetView(long start, long end)
    {
        if (_data == null) return;
        long dayLen = _data.DayEndMs - _data.DayStartMs;
        long len = Math.Clamp(end - start, 60_000, dayLen);
        start = Math.Clamp(start, _data.DayStartMs, _data.DayEndMs - len);
        _viewStart = start;
        _viewEnd = start + len;
        InvalidateVisual();
    }

    private double TrackLeft => LabelWidth;
    private double TrackWidth => Math.Max(10, ActualWidth - LabelWidth - 12);
    private double X(long t) => TrackLeft + (t - _viewStart) / (double)(_viewEnd - _viewStart) * TrackWidth;
    private long T(double x) => _viewStart + (long)((x - TrackLeft) / TrackWidth * (_viewEnd - _viewStart));
    private double LaneTop(int i) => AxisHeight + i * (LaneHeight + LaneGap);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(BgBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_data == null || _viewEnd <= _viewStart) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var clip = new RectangleGeometry(new Rect(TrackLeft, 0, TrackWidth, ActualHeight));

        // Axis ticks
        long span = _viewEnd - _viewStart;
        long[] steps = [60_000, 300_000, 600_000, 900_000, 1_800_000, 3_600_000, 7_200_000, 10_800_000];
        long step = steps.FirstOrDefault(s => TrackWidth / (span / (double)s) >= 70, steps[^1]);
        var firstLocal = TimeUtil.ToLocal(_viewStart);
        var tickLocal = new DateTime(firstLocal.Year, firstLocal.Month, firstLocal.Day, firstLocal.Hour, 0, 0);
        dc.PushClip(clip);
        for (int guard = 0; guard < 2000; guard++)
        {
            long t = TimeUtil.LocalToUtcMs(tickLocal);
            if (t > _viewEnd) break;
            if (t >= _viewStart)
            {
                double x = X(t);
                dc.DrawLine(TickPen, new Point(x, AxisHeight - 6), new Point(x, ActualHeight));
                var label = new FormattedText(tickLocal.ToString("HH:mm"), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 11, MutedBrush, dpi);
                dc.DrawText(label, new Point(x + 3, 4));
            }
            tickLocal = tickLocal.AddMilliseconds(step);
        }
        dc.Pop();

        long now = _data.NowMs;
        for (int i = 0; i < _data.Lanes.Count; i++)
        {
            var lane = _data.Lanes[i];
            double top = LaneTop(i);
            var name = new FormattedText((lane.Platform == Platforms.Ios ? "📱 " : "🖥 ") + lane.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 12, TextBrush, dpi)
            { MaxTextWidth = LabelWidth - 14, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(name, new Point(10, top + (LaneHeight - name.Height) / 2));

            dc.PushClip(clip);
            dc.DrawRectangle(LaneBrush, null, new Rect(TrackLeft, top, TrackWidth, LaneHeight));

            // Gaps: anything not covered between the first and last moment of the day we care about.
            long gapEnd = Math.Min(_data.DayEndMs, now);
            foreach (var (gs, ge) in lane.Uncovered(_data.DayStartMs, gapEnd, now))
            {
                if (ge < _viewStart || gs > _viewEnd) continue;
                var r = new Rect(X(gs), top, Math.Max(1, X(ge) - X(gs)), LaneHeight);
                dc.DrawRectangle(GapHatch, null, r);
                var reason = _data.GapAt(lane, gs + (ge - gs) / 2);
                if (r.Width > 90 && reason != null)
                {
                    var txt = new FormattedText(GapReasons.Describe(reason.Reason), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 10.5, MutedBrush, dpi)
                    { MaxTextWidth = r.Width - 6, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                    dc.DrawText(txt, new Point(r.X + 4, top + (LaneHeight - txt.Height) / 2));
                }
            }

            foreach (var s in lane.Segments)
            {
                long end = s.EffectiveEnd(now);
                if (end < _viewStart || s.StartUtcMs > _viewEnd) continue;
                double x0 = X(s.StartUtcMs), x1 = X(end);
                var rect = new Rect(x0, top + 3, Math.Max(1.5, x1 - x0), LaneHeight - 6);
                Brush fill = s.Status switch
                {
                    SegmentStatus.Finalized => lane.Platform == Platforms.Ios ? IosBrush : WinBrush,
                    SegmentStatus.Recording => RecordingBrush,
                    _ => BadBrush,
                };
                dc.DrawRectangle(fill, null, rect);
                // Shade 10 s buckets where nothing changed on screen.
                var buckets = Activity.Decode(s.Activity);
                double bucketW = Activity.BucketMs / (double)(_viewEnd - _viewStart) * TrackWidth;
                if (buckets.Length > 0 && bucketW >= 0.75)
                {
                    for (int b = 0; b < buckets.Length; b++)
                    {
                        if (buckets[b] > 0) continue;
                        long bs = s.StartUtcMs + b * (long)Activity.BucketMs;
                        long be = Math.Min(end, bs + Activity.BucketMs);
                        if (be < _viewStart || bs > _viewEnd) continue;
                        dc.DrawRectangle(IdleOverlay, null, new Rect(X(bs), rect.Y, Math.Max(0.75, X(be) - X(bs)), rect.Height));
                    }
                }
            }
            if (now < _viewEnd) dc.DrawRectangle(FutureBrush, null, new Rect(X(Math.Max(now, _viewStart)), top, TrackWidth, LaneHeight));
            dc.Pop();
        }

        // Apps lane (window-title log): one colored block per app/window, labelled when wide enough.
        if (_data.Focus.Count > 0)
        {
            double top = LaneTop(_data.Lanes.Count);
            var name = new FormattedText("🗂 Apps / tabs", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 12, TextBrush, dpi);
            dc.DrawText(name, new Point(10, top + (LaneHeight - name.Height) / 2));
            dc.PushClip(clip);
            dc.DrawRectangle(LaneBrush, null, new Rect(TrackLeft, top, TrackWidth, LaneHeight));
            foreach (var f in _data.Focus)
            {
                if (f.EndUtcMs < _viewStart || f.StartUtcMs > _viewEnd) continue;
                double x0 = X(f.StartUtcMs), x1 = X(f.EndUtcMs);
                var r = new Rect(x0, top + 3, Math.Max(1, x1 - x0), LaneHeight - 6);
                dc.DrawRectangle(AppBrush(f.Process), null, r);
                if (r.Width > 60)
                {
                    var label = new FormattedText(FocusSummary.ItemName(f.Process, f.Title), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 10.5, Brushes.White, dpi)
                    { MaxTextWidth = r.Width - 6, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                    dc.DrawText(label, new Point(r.X + 3, top + (LaneHeight - label.Height) / 2));
                }
            }
            dc.Pop();
        }

        dc.PushClip(clip);
        if (_hover is { } h && h.X >= TrackLeft)
            dc.DrawLine(HoverPen, new Point(h.X, AxisHeight - 4), new Point(h.X, ActualHeight));
        if (_cursor >= _viewStart && _cursor <= _viewEnd)
        {
            double cx = X(_cursor);
            dc.DrawLine(CursorPen, new Point(cx, AxisHeight - 6), new Point(cx, ActualHeight));
            var tri = new StreamGeometry();
            using (var g = tri.Open())
            {
                g.BeginFigure(new Point(cx - 6, AxisHeight - 12), true, true);
                g.LineTo(new Point(cx + 6, AxisHeight - 12), true, false);
                g.LineTo(new Point(cx, AxisHeight - 4), true, false);
            }
            dc.DrawGeometry(CursorBrush, null, tri);
        }
        dc.Pop();

        // Zoom indicator
        if (span < _data.DayEndMs - _data.DayStartMs)
        {
            var z = new FormattedText($"{TimeUtil.ToLocal(_viewStart):HH:mm}–{TimeUtil.ToLocal(_viewEnd):HH:mm} (wheel to zoom)", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 10.5, MutedBrush, dpi);
            dc.DrawText(z, new Point(10, 5));
        }
        else
        {
            var z = new FormattedText("Scroll to zoom", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 10.5, MutedBrush, dpi);
            dc.DrawText(z, new Point(10, 5));
        }
    }

    // ------------------------------------------------------------------ input

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        if (p.X < TrackLeft || _data == null) return;
        _dragging = true;
        CaptureMouse();
        SeekRequested?.Invoke(Math.Clamp(T(p.X), _data.DayStartMs, _data.DayEndMs - 1));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        _panStart = e.GetPosition(this);
        _panViewStart = _viewStart;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        _panStart = null;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        _hover = p;
        if (_data == null) return;
        if (_panStart is { } ps)
        {
            long delta = (long)((ps.X - p.X) / TrackWidth * (_viewEnd - _viewStart));
            long len = _viewEnd - _viewStart;
            SetView(_panViewStart + delta, _panViewStart + delta + len);
        }
        else if (_dragging)
        {
            SeekRequested?.Invoke(Math.Clamp(T(p.X), _data.DayStartMs, _data.DayEndMs - 1));
        }
        UpdatePopup(p);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = null;
        _popup.IsOpen = false;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_data == null) return;
        var p = e.GetPosition(this);
        long len = _viewEnd - _viewStart;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            long shift = (long)(len * 0.15) * (e.Delta > 0 ? -1 : 1);
            SetView(_viewStart + shift, _viewEnd + shift);
        }
        else
        {
            long anchor = p.X >= TrackLeft ? T(p.X) : _cursor;
            double factor = e.Delta > 0 ? 0.75 : 1 / 0.75;
            long newLen = (long)(len * factor);
            double frac = (anchor - _viewStart) / (double)len;
            long start = anchor - (long)(newLen * frac);
            SetView(start, start + newLen);
        }
        UpdatePopup(p);
        e.Handled = true;
    }

    private void UpdatePopup(Point p)
    {
        if (_data == null || p.X < TrackLeft || p.Y < 0 || p.Y > ActualHeight) { _popup.IsOpen = false; return; }
        long t = T(p.X);
        int laneIndex = (int)((p.Y - AxisHeight) / (LaneHeight + LaneGap));
        var lanes = _data.Lanes;
        if (laneIndex == lanes.Count && _data.Focus.Count > 0)
        {
            var f = _data.FocusAt(t);
            _popupImage.Source = null;
            _popupImage.Visibility = Visibility.Collapsed;
            _popupText.Text = TimeUtil.ToLocal(t).ToString("HH:mm:ss") + (f == null ? "  ·  no app logged (idle or not recording)"
                : $"\n{FocusSummary.AppName(f.Process)}: {f.Title}\n{TimeUtil.ToLocal(f.StartUtcMs):HH:mm}–{TimeUtil.ToLocal(f.EndUtcMs):HH:mm} ({TimeUtil.FormatDuration(f.DurationMs)})");
            _popupText.MaxWidth = 420;
            _popupText.TextWrapping = TextWrapping.Wrap;
            _popup.HorizontalOffset = Math.Clamp(p.X - 210, 0, Math.Max(0, ActualWidth - 430));
            _popup.VerticalOffset = -70;
            _popup.IsOpen = true;
            return;
        }
        // Prefer the lane under the mouse, otherwise the first lane recorded at that time.
        TimelineLane? lane = laneIndex >= 0 && laneIndex < lanes.Count && lanes[laneIndex].SegmentAt(t, _data.NowMs) != null
            ? lanes[laneIndex] : lanes.FirstOrDefault(l => l.SegmentAt(t, _data.NowMs) != null);
        string text = TimeUtil.ToLocal(t).ToString("HH:mm:ss");
        BitmapImage? img = null;
        if (lane != null)
        {
            text += "  ·  " + lane.Label;
            var path = ThumbnailLookup?.Invoke(lane, t);
            if (path != null) img = LoadImage(path);
            if (!_data.IsActive(t)) text += "  ·  no screen changes";
        }
        else
        {
            var anyLane = laneIndex >= 0 && laneIndex < lanes.Count ? lanes[laneIndex] : lanes.FirstOrDefault();
            var gap = anyLane != null ? _data.GapAt(anyLane, t) : null;
            text += gap != null ? $"  ·  {GapReasons.Describe(gap.Reason)}" : "  ·  not recorded";
        }
        _popupImage.Source = img;
        _popupImage.Visibility = img != null ? Visibility.Visible : Visibility.Collapsed;
        _popupText.Text = text;
        double popupWidth = img != null ? 330 : 240;
        _popup.HorizontalOffset = Math.Clamp(p.X - popupWidth / 2, 0, Math.Max(0, ActualWidth - popupWidth));
        _popup.VerticalOffset = -(img != null ? 215 : 34);
        _popup.IsOpen = true;
    }

    private BitmapImage? LoadImage(string path)
    {
        if (_imageCache.TryGetValue(path, out var cached)) return cached;
        BitmapImage? bmp = null;
        try
        {
            if (File.Exists(path))
            {
                bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
                bmp.DecodePixelWidth = 320;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
            }
        }
        catch { bmp = null; }
        _imageCache[path] = bmp;
        _imageOrder.Enqueue(path);
        while (_imageOrder.Count > 400) _imageCache.Remove(_imageOrder.Dequeue());
        return bmp;
    }

    public void ClearImageCache()
    {
        _imageCache.Clear();
        _imageOrder.Clear();
    }
}
