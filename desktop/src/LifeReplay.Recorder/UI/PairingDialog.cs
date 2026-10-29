using QRCoder;

namespace LifeReplay.Recorder.UI;

/// <summary>
/// Shows what the iPhone's "LifeReplay Log" shortcut needs: the endpoint URL and a one-time access token.
/// The QR code contains only the token; the iPhone camera offers "Copy" for it. Setup: docs/iphone-shortcuts.md.
/// </summary>
internal sealed class PairingDialog : Form
{
    public PairingDialog(string token, string? endpointUrl, int port)
    {
        Text = "iPhone app tracking (Shortcuts)";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 640);
        Font = new Font("Segoe UI", 9.5f);

        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(token, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        var picture = new PictureBox { Image = Image.FromStream(new MemoryStream(png)), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(140, 12, 240, 240) };

        var url = endpointUrl ?? "https://<this-pc>.<your-tailnet>.ts.net/api/v1/app-events";
        var urlLabel = new Label { Text = "URL for \"Get Contents of URL\":", Bounds = new Rectangle(20, 262, 480, 20) };
        var urlBox = new TextBox { Text = url, ReadOnly = true, Bounds = new Rectangle(20, 284, 390, 26) };
        var copyUrl = new Button { Text = "Copy", Bounds = new Rectangle(420, 283, 80, 28) };
        copyUrl.Click += (_, _) => Clipboard.SetText(url);

        var tokenLabel = new Label { Text = "Authorization header value (shown once):", Bounds = new Rectangle(20, 322, 480, 20) };
        var tokenBox = new TextBox { Text = "Bearer " + token, ReadOnly = true, Bounds = new Rectangle(20, 344, 390, 26) };
        var copyToken = new Button { Text = "Copy", Bounds = new Rectangle(420, 343, 80, 28) };
        copyToken.Click += (_, _) => Clipboard.SetText("Bearer " + token);

        var info = new TextBox
        {
            Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control,
            Bounds = new Rectangle(20, 384, 480, 200), ScrollBars = ScrollBars.Vertical,
            Text = (endpointUrl == null
                       ? "Tailscale wasn't found on this PC. Install it, sign in, then run in a terminal:\r\n" +
                         $"  tailscale serve --bg https+insecure://127.0.0.1:{port}\r\nand open this window again to see the exact URL.\r\n\r\n"
                       : $"Make sure Tailscale Serve is forwarding to LifeReplay (run once in a terminal):\r\n  tailscale serve --bg https+insecure://127.0.0.1:{port}\r\n\r\n") +
                   "On the iPhone: install Tailscale and sign in with the same account. Scan the QR code with the Camera app " +
                   "and tap \"Copy\" to get the token, then paste it as the Authorization header in the \"LifeReplay Log\" shortcut " +
                   "(prefix it with \"Bearer \"). Step-by-step guide: docs/iphone-shortcuts.md in the LifeReplay repository.\r\n\r\n" +
                   "The token is stored only as a hash on this PC. Use \"Revoke all paired iPhones\" in the tray menu to invalidate it.",
        };
        var close = new Button { Text = "Done", DialogResult = DialogResult.OK, Bounds = new Rectangle(410, 598, 90, 30) };
        Controls.AddRange([picture, urlLabel, urlBox, copyUrl, tokenLabel, tokenBox, copyToken, info, close]);
        AcceptButton = close;
    }
}
