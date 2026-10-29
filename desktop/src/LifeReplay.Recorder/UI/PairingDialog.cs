using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using QRCoder;

namespace LifeReplay.Recorder.UI;

/// <summary>Shows the one-time pairing QR code. Scanning it with the iPhone camera opens the LifeReplay app.</summary>
internal sealed class PairingDialog : Form
{
    public PairingDialog(string uri, string host, int port, string fingerprint)
    {
        Text = "Pair iPhone with LifeReplay";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(440, 600);
        Font = new Font("Segoe UI", 9.5f);

        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        var picture = new PictureBox
        {
            Image = Image.FromStream(new MemoryStream(png)),
            SizeMode = PictureBoxSizeMode.Zoom,
            Bounds = new Rectangle(20, 20, 400, 360),
        };
        var info = new TextBox
        {
            Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control,
            Bounds = new Rectangle(20, 390, 400, 160), ScrollBars = ScrollBars.Vertical,
            Text = "1. On the iPhone, open the Camera app and scan this code (or open LifeReplay > Sync > Scan).\r\n" +
                   "2. Allow LifeReplay to find devices on your local network.\r\n\r\n" +
                   $"Server: {host}:{port}\r\nCertificate SHA-256: {fingerprint}\r\n\r\n" +
                   "This code contains a secret access token. It is shown only once — close this window when done. " +
                   "Use \"Revoke paired devices\" in the tray menu to invalidate it.",
        };
        var close = new Button { Text = "Done", DialogResult = DialogResult.OK, Bounds = new Rectangle(330, 560, 90, 30) };
        Controls.AddRange([picture, info, close]);
        AcceptButton = close;
    }

    /// <summary>Best guess at this PC's LAN address (first private IPv4 on an active adapter).</summary>
    public static string LocalAddress()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || nic.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = a.Address.GetAddressBytes();
                    if (b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)) return a.Address.ToString();
                }
            }
        }
        catch { }
        return Dns.GetHostName();
    }
}
