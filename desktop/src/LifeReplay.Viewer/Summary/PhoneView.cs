using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LifeReplay.Analysis;
using LifeReplay.Core;

namespace LifeReplay.Viewer;

/// <summary>Summary → iPhone: per-app phone time next to Windows time, and missing events.</summary>
public partial class MainWindow
{
    // ------------------------------------------------------------------ iPhone

    /// <summary>iPhone app time (from Shortcuts events) next to Windows time for the same day.</summary>
    private void RenderPhone()
    {
        if (_timeline == null) return;
        long from = _timeline.DayStartMs, to = Math.Min(_timeline.DayEndMs, Math.Max(_timeline.NowMs, from));
        var sessions = _timeline.PhoneSessions;
        var totals = AppSessions.Totals(sessions, from, _timeline.DayEndMs);
        long phone = totals.Sum(t => t.TotalMs), inferred = totals.Sum(t => t.InferredMs);
        long windows = FocusSummary.Summarize(_timeline.Focus, from, _timeline.DayEndMs).Sum(a => a.DurationMs);
        long both = AppSessions.OverlapMs(sessions, _timeline.Focus, from, _timeline.DayEndMs);
        var (syncText, stale) = TimelineControl.PhoneSyncText(_timeline);

        SummaryTitle.Text = $"iPhone apps · {_day:ddd, MMM d}";
        SummarySubtitle.Text = _timeline.PhoneStatus.Count == 0
            ? "No iPhone events yet. Set up the Shortcuts automations (guide: docs/iphone-shortcuts.md)."
            : "From Shortcuts \"App is opened / closed\" automations (iCloud log). Only apps you set up are tracked. Click a row to jump there.";
        SummaryList.Children.Clear();

        SummaryList.Children.Add(Text((stale ? "⚠ " : "") + syncText + (stale ? " — the iPhone hasn't sent events for a while (offline, Tailscale off, or automations disabled)" : ""),
            12, color: stale ? Brushes.Goldenrod : Muted, margin: new Thickness(0, 0, 0, 8)));

        // Combined Windows + iPhone view
        SummaryList.Children.Add(Text("Windows + iPhone", 13, bold: true));
        SummaryList.Children.Add(Row("🖥 Windows (active)", TimeUtil.FormatDuration(windows)));
        SummaryList.Children.Add(Row("📱 iPhone (tracked apps)", TimeUtil.FormatDuration(phone) + (inferred > 0 ? $"  (≈{TimeUtil.FormatDuration(inferred)} inferred)" : ""),
            tooltip: "Inferred = one of the two events was missing; the end was taken from the next app, or the start from the previous event (≤ 30 min)."));
        SummaryList.Children.Add(Row("Both at the same time", TimeUtil.FormatDuration(both), tooltip: "Phone in use while the PC also logged active use"));
        SummaryList.Children.Add(Row("Total screen time (either)", TimeUtil.FormatDuration(windows + phone - both), margin: new Thickness(10, 1, 0, 12)));

        if (totals.Count == 0)
        {
            SummaryList.Children.Add(Text("No iPhone app sessions on this day.", 12, color: Muted));
            return;
        }
        SummaryList.Children.Add(Text("Per app", 13, bold: true));
        long max = Math.Max(1, totals.Max(t => t.TotalMs));
        foreach (var t in totals)
        {
            string right = TimeUtil.FormatDuration(t.TotalMs) + (t.InferredMs > 0 ? " ≈" : "");
            if (t.UnknownSessions > 0) right += $"  +{t.UnknownSessions} incomplete";
            var first = sessions.FirstOrDefault(s => s.App == t.App && s.EndUtcMs >= from);
            SummaryList.Children.Add(Row(t.App, right, 13, onClick: first == null ? null : () => Seek(Math.Max(from, first.StartUtcMs)),
                tooltip: $"{t.App}: {t.Sessions} sessions · known {TimeUtil.FormatDuration(t.KnownMs)} · inferred {TimeUtil.FormatDuration(t.InferredMs)}" +
                         (t.UnknownSessions > 0 ? $" · {t.UnknownSessions} with unknown duration (not counted)" : "")));
            SummaryList.Children.Add(Bar(t.TotalMs / (double)max, Accent));
        }

        var incomplete = sessions.Where(s => s.Confidence != SessionConfidence.Complete).ToList();
        if (incomplete.Count == 0) return;
        SummaryList.Children.Add(Text($"Missing events ({incomplete.Count})", 13, bold: true, margin: new Thickness(0, 12, 0, 2)));
        SummaryList.Children.Add(Text("Sessions where an \"opened\" or \"closed\" event never arrived. Often a Shortcuts automation that didn't run or isn't set up for both events.", 11.5, color: Muted, margin: new Thickness(0, 0, 0, 4)));
        foreach (var s in incomplete.Take(30))
        {
            var s1 = s;
            SummaryList.Children.Add(Row($"{TimeUtil.ToLocal(s.StartUtcMs):HH:mm}  {s.App}", TimelineControl.ConfidenceText(s.Confidence), 12,
                onClick: () => Seek(Math.Max(from, s1.StartUtcMs)), tooltip: TimelineControl.DescribePhoneSession(s)));
        }
        if (incomplete.Count > 30) SummaryList.Children.Add(Text($"+ {incomplete.Count - 30} more", 12, color: Muted, margin: new Thickness(10, 1, 0, 1)));
    }
}
