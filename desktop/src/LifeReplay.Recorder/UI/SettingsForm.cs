using System.Diagnostics;
using LifeReplay.Core;

namespace LifeReplay.Recorder.UI;

/// <summary>Settings window (tray → Settings…). Edits config.json and "Start with Windows", then applies them.</summary>
internal sealed class SettingsForm : Form
{
    private readonly RecorderHost _host;
    private readonly string? _dataDirArg;

    private readonly CheckBox _startWithWindows = new() { Text = "Start LifeReplay when I log in to Windows", AutoSize = true };
    private readonly CheckBox _recordOnLaunch = new() { Text = "Start recording as soon as LifeReplay starts", AutoSize = true };
    private readonly CheckBox _restartAfterCrash = new() { Text = "Restart LifeReplay automatically if it crashes", AutoSize = true };

    private readonly CheckBox _hoursEnabled = new() { Text = "Only record during these hours each day", AutoSize = true, Name = "HoursEnabled" };
    private readonly DateTimePicker _hoursStart = TimePicker("HoursStart");
    private readonly DateTimePicker _hoursEnd = TimePicker("HoursEnd");
    private readonly CheckBox _weekdaysOnly = new() { Text = "Monday to Friday only", AutoSize = true, Name = "WeekdaysOnly" };

    private static DateTimePicker TimePicker(string name) => new()
    {
        Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 90, Name = name,
    };

    private readonly NumericUpDown _fps = Num(1, 30);
    private readonly ComboBox _resolution = Combo("Native (no scaling)", "2160p", "1440p", "1080p", "720p");
    private readonly ComboBox _quality = Combo("Low", "Medium", "High");
    private readonly NumericUpDown _bitrate = Num(0, 100_000, 100);
    private readonly NumericUpDown _segmentMinutes = Num(1, 60);
    private readonly CheckBox _cursor = new() { Text = "Record the mouse pointer", AutoSize = true };
    private readonly CheckBox _border = new() { Text = "Show Windows' yellow capture border", AutoSize = true };
    private readonly CheckBox _hardware = new() { Text = "Use the graphics card's video encoder (recommended)", AutoSize = true };
    private readonly NumericUpDown _thumbs = Num(5, 600);

    private readonly NumericUpDown _retentionDays = Num(0, 3650);
    private readonly NumericUpDown _maxStorage = Num(0, 100_000);
    private readonly NumericUpDown _minFree = Num(0, 10_000);
    private readonly Label _estimate = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(520, 0) };

    private readonly CheckBox _logTitles = new() { Text = "Log the app and window title in front", AutoSize = true, Name = "LogTitles" };
    private readonly NumericUpDown _idleMinutes = Num(1, 120);
    private readonly TextBox _excludedApps = Multi();
    private readonly TextBox _excludedTitles = Multi();
    private readonly TextBox _hotkey = new() { Width = 220 };

    public event Action? Saved;

    public SettingsForm(RecorderHost host, string? dataDirArg)
    {
        _host = host;
        _dataDirArg = dataDirArg;
        Text = "LifeReplay settings";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f);
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = true;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var hoursRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(20, 2, 0, 2) };
        hoursRow.Controls.AddRange([new Label { Text = "From", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, _hoursStart,
            new Label { Text = "to", AutoSize = true, Margin = new Padding(10, 6, 6, 0) }, _hoursEnd]);
        _weekdaysOnly.Margin = new Padding(20, 2, 0, 2);
        _hoursEnabled.CheckedChanged += (_, _) => { hoursRow.Enabled = _weekdaysOnly.Enabled = _hoursEnabled.Checked; };
        tabs.TabPages.Add(Page("General",
            Section("Startup"), _startWithWindows, _recordOnLaunch, _restartAfterCrash,
            Section("Recording hours"), _hoursEnabled, hoursRow, _weekdaysOnly,
            Hint("Outside these hours LifeReplay stays in the tray but records nothing; the timeline shows \"Outside recording hours\". If the end is earlier than the start, the hours run past midnight."),
            Section("Pause hotkey"), Row("Hotkey", _hotkey), Hint("Example: Ctrl+Alt+Shift+P. Press it anywhere to pause or resume.")));
        tabs.TabPages.Add(Page("Video",
            Row("Frames per second", _fps), Row("Maximum resolution", _resolution), Row("Quality", _quality),
            Row("Fixed bitrate (kbps)", _bitrate), Hint("0 = automatic, based on quality and resolution."),
            Row("Segment length (minutes)", _segmentMinutes), Hint("Each recording file covers this long. A crash loses at most the current segment."),
            Row("Timeline preview every (s)", _thumbs),
            _cursor, _border, _hardware));
        tabs.TabPages.Add(Page("Storage",
            Row("Keep recordings for (days)", _retentionDays), Hint("Older recordings are deleted. 0 = keep forever."),
            Row("Maximum storage (GB)", _maxStorage), Hint("Oldest recordings are deleted above this. 0 = no limit."),
            Row("Pause if free disk below (GB)", _minFree),
            _estimate,
            Link("Open recordings folder", () => Open(_host.Paths.RecordingsDir))));
        tabs.TabPages.Add(Page("Privacy",
            Section("App and window title log"), _logTitles,
            Hint("Records which app and tab / document / file was in front, for the viewer's \"time per app\" summary. Only while recording; excluded windows are logged as (private)."),
            Row("Count as idle after (minutes)", _idleMinutes),
            Hint("No keyboard/mouse input and no screen change for this long is not counted as time on an app."),
            Section("Black out the video while these apps are in front"), _excludedApps,
            Hint("One program per line, e.g. KeePass.exe"),
            Section("...or while the window title contains"), _excludedTitles,
            Hint("One phrase per line, e.g. InPrivate. Note: only the window in front is checked."),
            Hint("Locked screen, sleep and display-off are never recorded.")));

        var save = new Button { Text = "Save", Width = 90, Height = 30, Name = "Save" };
        var cancel = new Button { Text = "Cancel", Width = 90, Height = 30, DialogResult = DialogResult.Cancel, Name = "Cancel" };
        // Names double as UI Automation ids (screen readers, scripted tests).
        _fps.Name = "Fps"; _resolution.Name = "Resolution"; _quality.Name = "Quality"; _segmentMinutes.Name = "SegmentMinutes";
        _startWithWindows.Name = "StartWithWindows"; _retentionDays.Name = "RetentionDays"; _maxStorage.Name = "MaxStorage";
        var advanced = new LinkLabel { Text = "Edit config.json (advanced)", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        advanced.LinkClicked += (_, _) => Open("notepad.exe", _host.Paths.ConfigPath);
        save.Click += async (_, _) => await SaveAsync(save);
        cancel.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 46, Padding = new Padding(8) };
        buttons.Controls.AddRange([cancel, save, advanced]);
        AcceptButton = save;
        CancelButton = cancel;

        Controls.Add(tabs);
        Controls.Add(buttons);
        ClientSize = new Size(620, 640);

        LoadValues();
        foreach (var c in new Control[] { _fps, _resolution, _quality, _bitrate })
        {
            if (c is NumericUpDown n) n.ValueChanged += (_, _) => UpdateEstimate();
            if (c is ComboBox cb) cb.SelectedIndexChanged += (_, _) => UpdateEstimate();
        }
        UpdateEstimate();
    }

    // ------------------------------------------------------------------ values

    private static readonly int[] Heights = [0, 2160, 1440, 1080, 720];

    private void LoadValues()
    {
        var c = _host.Config;
        _startWithWindows.Checked = AutoStart.IsEnabled();
        _recordOnLaunch.Checked = c.RecordOnLaunch;
        _restartAfterCrash.Checked = c.AutoRestartAfterCrash;
        _fps.Value = Math.Clamp(c.Fps, 1, 30);
        int hi = Array.IndexOf(Heights, c.MaxOutputHeight);
        _resolution.SelectedIndex = hi >= 0 ? hi : 3;
        _quality.SelectedIndex = c.Quality switch { "low" => 0, "high" => 2, _ => 1 };
        _bitrate.Value = Math.Clamp(c.BitrateKbps, 0, 100_000);
        _segmentMinutes.Value = Math.Clamp(c.SegmentSeconds / 60, 1, 60);
        _thumbs.Value = Math.Clamp(c.ThumbnailIntervalSeconds, 5, 600);
        _cursor.Checked = c.CaptureCursor;
        _border.Checked = c.ShowCaptureBorder;
        _hardware.Checked = c.HardwareEncoding;
        _retentionDays.Value = Math.Clamp(c.RetentionDays, 0, 3650);
        _maxStorage.Value = (decimal)Math.Clamp(c.MaxStorageGB, 0, 100_000);
        _minFree.Value = (decimal)Math.Clamp(c.MinFreeDiskGB, 0, 10_000);
        _excludedApps.Lines = c.ExcludedProcesses.ToArray();
        _excludedTitles.Lines = c.ExcludedWindowTitles.ToArray();
        _hotkey.Text = c.PauseHotkey;
        _logTitles.Checked = c.LogWindowTitles;
        _idleMinutes.Value = Math.Clamp(c.IdleMinutes, 1, 120);
        RecordingHours.TryParse(c.Hours.Start, out var hs);
        RecordingHours.TryParse(c.Hours.End, out var he);
        _hoursStart.Value = DateTime.Today + hs;
        _hoursEnd.Value = DateTime.Today + he;
        _weekdaysOnly.Checked = c.Hours.WeekdaysOnly;
        _hoursEnabled.Checked = !c.Hours.Enabled; // force CheckedChanged so the time fields' enabled state is set
        _hoursEnabled.Checked = c.Hours.Enabled;
    }

    private RecorderConfig BuildConfig()
    {
        var old = _host.Config;
        var cfg = new RecorderConfig
        {
            SegmentSeconds = (int)_segmentMinutes.Value * 60,
            Fps = (int)_fps.Value,
            MaxOutputHeight = Heights[Math.Max(0, _resolution.SelectedIndex)],
            Quality = _quality.SelectedIndex switch { 0 => "low", 2 => "high", _ => "medium" },
            BitrateKbps = (int)_bitrate.Value,
            CaptureCursor = _cursor.Checked,
            ShowCaptureBorder = _border.Checked,
            HardwareEncoding = _hardware.Checked,
            RecordOnLaunch = _recordOnLaunch.Checked,
            AutoRestartAfterCrash = _restartAfterCrash.Checked,
            RetentionDays = (int)_retentionDays.Value,
            MaxStorageGB = (double)_maxStorage.Value,
            MinFreeDiskGB = (double)_minFree.Value,
            ThumbnailIntervalSeconds = (int)_thumbs.Value,
            ExcludedProcesses = Lines(_excludedApps),
            ExcludedWindowTitles = Lines(_excludedTitles),
            PauseHotkey = _hotkey.Text.Trim(),
            LogWindowTitles = _logTitles.Checked,
            IdleMinutes = (int)_idleMinutes.Value,
            Hours = new RecordingHours
            {
                Enabled = _hoursEnabled.Checked,
                Start = _hoursStart.Value.ToString("HH:mm"),
                End = _hoursEnd.Value.ToString("HH:mm"),
                WeekdaysOnly = _weekdaysOnly.Checked,
            },
            Sync = old.Sync, // managed from the tray's "iPhone app tracking" menu
        };
        cfg.Normalize();
        return cfg;
    }

    private static List<string> Lines(TextBox t) =>
        t.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void UpdateEstimate()
    {
        try
        {
            var cfg = BuildConfig();
            long perHour = 0;
            var parts = new List<string>();
            foreach (var s in Screen.AllScreens)
            {
                var plan = EncodingPlan.Compute(s.Bounds.Width, s.Bounds.Height, cfg);
                perHour += plan.MaxBytesPerHour;
                parts.Add($"{plan.Width}×{plan.Height}");
            }
            _estimate.Text = $"Estimated space: about {TimeUtil.FormatBytes(perHour)} per hour of recording " +
                             $"({Screen.AllScreens.Length} display(s): {string.Join(", ", parts)}), " +
                             $"about {TimeUtil.FormatBytes(perHour * 10)} for a 10-hour day.\nCurrently stored: {TimeUtil.FormatBytes(_host.Store.GetTotalStorageBytes())}.";
        }
        catch (Exception ex) { _estimate.Text = ex.Message; }
    }

    private async Task SaveAsync(Button save)
    {
        if (!Hotkey.TryParse(_hotkey.Text.Trim(), out _, out _))
        {
            MessageBox.Show(this, "The pause hotkey must be like Ctrl+Alt+Shift+P (at least one of Ctrl/Alt/Shift/Win plus a key).", "LifeReplay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_hoursEnabled.Checked && _hoursStart.Value.ToString("HH:mm") == _hoursEnd.Value.ToString("HH:mm"))
        {
            MessageBox.Show(this, "The start and end of the recording hours must be different.", "LifeReplay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        save.Enabled = false;
        try
        {
            if (_startWithWindows.Checked != AutoStart.IsEnabled()) AutoStart.Set(_startWithWindows.Checked, _dataDirArg);
            await _host.SaveAndApplyConfigAsync(BuildConfig());
            Log.Info("Settings saved from the settings window");
            Saved?.Invoke();
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
            MessageBox.Show(this, "Could not save settings: " + ex.Message, "LifeReplay", MessageBoxButtons.OK, MessageBoxIcon.Error);
            save.Enabled = true;
        }
    }

    // ------------------------------------------------------------------ layout helpers

    private static TabPage Page(string title, params Control[] controls)
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(14) };
        foreach (var c in controls) { c.Margin = c.Margin == default ? new Padding(0, 4, 0, 4) : c.Margin; flow.Controls.Add(c); }
        var page = new TabPage(title);
        page.Controls.Add(flow);
        return page;
    }

    private static Control Row(string label, Control input)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 3, 0, 3) };
        panel.Controls.Add(new Label { Text = label, Width = 260, TextAlign = ContentAlignment.MiddleLeft, Height = 28, AutoEllipsis = true });
        panel.Controls.Add(input);
        return panel;
    }

    private static Label Section(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold), Margin = new Padding(0, 10, 0, 4),
    };

    private static Label Hint(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(530, 0), Margin = new Padding(0, 0, 0, 6),
    };

    private static LinkLabel Link(string text, Action onClick)
    {
        var l = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        l.LinkClicked += (_, _) => onClick();
        return l;
    }

    private static NumericUpDown Num(int min, int max, int step = 1) => new() { Minimum = min, Maximum = max, Increment = step, Width = 110 };

    private static ComboBox Combo(params string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        c.Items.AddRange(items);
        return c;
    }

    private static TextBox Multi() => new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 530, Height = 110, AcceptsReturn = true };

    private static void Open(string target, string? args = null)
    {
        try { Process.Start(new ProcessStartInfo(target, args ?? "") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Could not open {target}", ex); }
    }
}
