using QRCoder;

namespace LifeReplay.Recorder.UI;

/// <summary>
/// Pairs the iPhone for Shortcuts app tracking. The QR code opens the phone's setup page
/// (https://&lt;pc&gt;.ts.net/setup?k=&lt;token&gt;), which copies each automation's one-line URL. The token is shown
/// only here; the PC stores only its hash. Advanced (queue) setup: docs/iphone-shortcuts.md.
/// </summary>
internal sealed class PairingDialog : Form
{
    public PairingDialog(string token, string? endpointUrl, int port)
    {
        Text = "아이폰 단축어 연결";
        if (AppIcon.Get() is { } appIcon) Icon = appIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 600);
        Font = new Font("Malgun Gothic", 9.5f);

        // https://<pc>.ts.net/api/v1/app-events → https://<pc>.ts.net
        string? origin = endpointUrl == null ? null : new Uri(endpointUrl).GetLeftPart(UriPartial.Authority);
        string setupUrl = origin != null ? $"{origin}/setup?k={Uri.EscapeDataString(token)}" : token;

        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(setupUrl, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        var picture = new PictureBox { Image = Image.FromStream(new MemoryStream(png)), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(130, 12, 260, 260) };

        var steps = new Label
        {
            Bounds = new Rectangle(20, 282, 480, 150),
            Text = origin == null
                ? "Tailscale을 찾지 못했어요. 이 PC에서 Tailscale을 설치하고 로그인한 뒤, 터미널에서 한 번 실행하세요:\r\n" +
                  $"   tailscale serve --bg https+insecure://127.0.0.1:{port}\r\n그리고 이 창을 다시 여세요."
                : "1. 아이폰에서 Tailscale을 켜세요 (PC와 같은 계정).\r\n" +
                  "2. 아이폰 카메라로 이 QR 코드를 찍고, 뜨는 링크를 누르세요.\r\n" +
                  "3. Safari에 설정 페이지가 열리고 \"✓ PC와 연결됨\"이 보여요.\r\n" +
                  "4. 페이지의 안내대로, 앱마다 \"복사\" → 단축어 자동화에 붙여넣기.\r\n\r\n" +
                  "이 QR 코드는 지금 한 번만 보여요. 나중에 다시 하려면 트레이 메뉴에서 새로 만드세요.",
        };

        var linkLabel = new Label { Text = "설정 페이지 주소 (QR과 같음):", Bounds = new Rectangle(20, 440, 480, 20) };
        var linkBox = new TextBox { Text = setupUrl, ReadOnly = true, Bounds = new Rectangle(20, 462, 390, 26) };
        var copy = new Button { Text = "복사", Bounds = new Rectangle(420, 461, 80, 28) };
        copy.Click += (_, _) => Clipboard.SetText(setupUrl);

        var note = new Label
        {
            Bounds = new Rectangle(20, 498, 480, 50), ForeColor = SystemColors.GrayText,
            Text = "이 주소에는 접속 키가 들어 있어요. 다른 사람에게 보내지 마세요. 휴대폰을 잃어버리면 " +
                   "트레이 → iPhone app tracking → Revoke all paired iPhones.",
        };
        var close = new Button { Text = "닫기", DialogResult = DialogResult.OK, Bounds = new Rectangle(410, 556, 90, 30) };
        Controls.AddRange([picture, steps, linkLabel, linkBox, copy, note, close]);
        AcceptButton = close;
    }
}
