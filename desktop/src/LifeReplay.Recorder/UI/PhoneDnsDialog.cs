using System.Diagnostics;
using LifeReplay.Core;

namespace LifeReplay.Recorder.UI;

/// <summary>
/// Setup for phone app tracking without Shortcuts (docs/phone-network-tracking.md): turn it on, allow it in the
/// Windows Firewall, add this PC as a Tailscale nameserver, then watch the status line for the first lookup.
/// </summary>
internal sealed class PhoneDnsDialog : Form
{
    internal const string FirewallRule = "LifeReplay phone DNS";
    private const string TailscaleDnsPage = "https://console.tailscale.com/admin/dns";

    private readonly RecorderHost _host;
    private readonly CheckBox _enabled;
    private readonly Label _listen, _firewall, _status;
    private readonly TextBox _ipBox;
    private readonly System.Windows.Forms.Timer _timer;

    public PhoneDnsDialog(RecorderHost host)
    {
        _host = host;
        Text = "휴대폰 앱 기록 (단축어 없이)";
        if (AppIcon.Get() is { } appIcon) Icon = appIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 520);
        Font = new Font("Malgun Gothic", 9.5f);

        int y = 14;
        Label Head(string s) { var l = new Label { Text = s, Font = new Font(Font, FontStyle.Bold), Bounds = new Rectangle(16, y, 528, 22) }; y += 24; return l; }
        Label Body(string s, int h) { var l = new Label { Text = s, Bounds = new Rectangle(16, y, 528, h) }; y += h + 4; return l; }

        var intro = Body("휴대폰이 YouTube·인스타그램 같은 서버를 찾을 때마다 이 PC가 그 시간을 기록해서, 어떤 앱을 언제 썼는지 추정합니다. " +
                         "목록에 있는 앱의 기록만 저장하고, 나머지는 그대로 전달만 합니다.", 40);

        _enabled = new CheckBox { Text = "켜기", Checked = host.Config.PhoneDns.Enabled, Bounds = new Rectangle(16, y, 80, 24) };
        _listen = new Label { Bounds = new Rectangle(100, y + 3, 444, 20), ForeColor = SystemColors.GrayText };
        y += 34;
        _enabled.CheckedChanged += async (_, _) => { _enabled.Enabled = false; await _host.SetPhoneDnsEnabledAsync(_enabled.Checked); _enabled.Enabled = true; Refresh2(); };

        var h1 = Head("1단계 · Windows 방화벽 허용 (한 번만)");
        var fwButton = new Button { Text = "방화벽 허용하기", Bounds = new Rectangle(16, y, 140, 30) };
        _firewall = new Label { Bounds = new Rectangle(166, y + 6, 378, 20) };
        fwButton.Click += (_, _) => AllowFirewall();
        y += 36;
        var fwNote = Body("관리자 확인 창이 뜨면 \"예\"를 누르세요. Tailscale 기기(100.x.x.x)에서 오는 요청만 허용합니다.", 20);

        var h2 = Head("2단계 · Tailscale 관리 페이지에서 DNS 설정 (한 번만)");
        var open = new Button { Text = "Tailscale DNS 페이지 열기", Bounds = new Rectangle(16, y, 190, 30) };
        open.Click += (_, _) => Process.Start(new ProcessStartInfo(TailscaleDnsPage) { UseShellExecute = true });
        y += 36;
        var steps = Body("① Global nameservers → Add nameserver → Custom 에 아래 주소를 붙여넣고 Save\r\n" +
                         "② 다시 Add nameserver → Cloudflare 선택 (PC가 꺼져도 휴대폰 인터넷이 되도록)\r\n" +
                         "③ Global nameservers 아래 \"Override DNS servers\" 켜기", 60);
        _ipBox = new TextBox { ReadOnly = true, Bounds = new Rectangle(16, y, 200, 26) };
        var copy = new Button { Text = "주소 복사", Bounds = new Rectangle(224, y - 1, 90, 28) };
        copy.Click += (_, _) => { if (_ipBox.Text.Length > 0) Clipboard.SetText(_ipBox.Text); };
        y += 38;

        var h3 = Head("3단계 · 확인");
        var check = Body("휴대폰에서 Tailscale을 켠 채로 YouTube를 1분 정도 보세요. 아래에 기록이 보이면 끝입니다.", 20);
        _status = new Label { Bounds = new Rectangle(16, y, 528, 40), Font = new Font(Font, FontStyle.Bold) };
        y += 46;

        var close = new Button { Text = "닫기", DialogResult = DialogResult.OK, Bounds = new Rectangle(454, ClientSize.Height - 44, 90, 30) };
        Controls.AddRange([intro, _enabled, _listen, h1, fwButton, _firewall, fwNote, h2, open, steps, _ipBox, copy, h3, check, _status, close]);
        AcceptButton = close;

        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += (_, _) => Refresh2();
        _timer.Start();
        Shown += (_, _) => { _ipBox.Text = _host.TailscaleIPv4()?.ToString() ?? ""; _firewall.Text = FirewallText(); Refresh2(); };
    }

    private void Refresh2()
    {
        var dns = _host.PhoneDns;
        if (!_host.Config.PhoneDns.Enabled) { _listen.Text = "꺼져 있음"; _listen.ForeColor = SystemColors.GrayText; }
        else if (dns != null) { _listen.Text = $"동작 중 · {dns.Listening}"; _listen.ForeColor = Color.ForestGreen; }
        else { _listen.Text = "시작 못 함: " + (_host.PhoneDnsError ?? "?") + " (1분마다 다시 시도)"; _listen.ForeColor = Color.Firebrick; }
        if (_ipBox.Text.Length == 0 && dns != null) _ipBox.Text = dns.Listening.Address.ToString();

        if (dns?.LastQueryUtcMs is { } last)
            _status.Text = $"✓ {TimeUtil.ToLocal(last):HH:mm:ss}에 {dns.LastDevice}에서 요청 받음 · 앱 기록 {dns.Saved}개 저장됨 (이번 실행 이후)";
        else
        {
            var saved = _host.Store.LastDnsHitUtcMs();
            _status.Text = saved is { } s ? $"마지막 앱 기록: {TimeUtil.ToLocal(s):MM-dd HH:mm}. 아직 이번 실행에서는 요청이 없어요." : "아직 휴대폰에서 받은 요청이 없어요.";
        }
    }

    /// <summary>The recorder exe the rule is limited to.</summary>
    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LifeReplay.exe");

    /// <summary>null = no rule; false = a rule exists but isn't limited to this program (older version); true = OK.</summary>
    private static bool? FirewallState()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{FirewallRule}\" verbose")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            if (p == null) return null;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            if (p.ExitCode != 0) return null;
            return text.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return null; }
    }

    private static string FirewallText() => FirewallState() switch
    {
        true => "✓ 허용됨 (LifeReplay만, 내 Tailscale 기기에서만)",
        false => "⚠ 예전 규칙: 다시 눌러서 LifeReplay 전용으로 바꾸세요",
        _ => "아직 허용 안 됨",
    };

    /// <summary>
    /// One inbound rule: UDP 53, only to LifeReplay.exe, only from Tailscale addresses (100.64.0.0/10). Any older
    /// rule with the same name is deleted first. Needs one admin prompt.
    /// </summary>
    private void AllowFirewall()
    {
        if (FirewallState() == true) { _firewall.Text = FirewallText(); return; }
        try
        {
            var add = $"netsh advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow protocol=UDP localport=53 " +
                      $"remoteip=100.64.0.0/10 program=\"{ExePath}\"";
            using var p = Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c netsh advfirewall firewall delete rule name=\"{FirewallRule}\" >nul 2>&1 & {add}")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            p?.WaitForExit(15000);
        }
        catch (System.ComponentModel.Win32Exception) { /* the user said no to the admin prompt */ }
        catch (Exception ex) { Log.Error("Adding the firewall rule failed", ex); MessageBox.Show(ex.Message, "LifeReplay"); }
        _firewall.Text = FirewallText();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
