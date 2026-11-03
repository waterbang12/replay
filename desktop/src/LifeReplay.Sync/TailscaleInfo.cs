using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace LifeReplay.Sync;

/// <summary>What "tailscale status --json" says about this PC and your other devices.</summary>
public sealed record TailscaleInfo(string? DnsName, IPAddress? SelfIPv4, IReadOnlyDictionary<IPAddress, string> Peers)
{
    public static string Exe()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        return File.Exists(installed) ? installed : "tailscale.exe";
    }

    /// <summary>Null if Tailscale isn't installed, isn't running, or didn't answer within 3 s.</summary>
    public static TailscaleInfo? Read()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Exe(), "status --json")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                // tailscale writes UTF-8; the default here is the console code page (cp949 on Korean Windows),
                // which garbles multi-byte text and breaks the JSON.
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            });
            if (p == null) return null;
            var json = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(3000) || !json.Wait(1000)) { try { p.Kill(); } catch { } return null; }
            return Parse(json.Result);
        }
        catch { return null; }
    }

    public static TailscaleInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("Self", out var self)) return null;
        var dns = self.TryGetProperty("DNSName", out var d) ? d.GetString()?.TrimEnd('.') : null;
        var peers = new Dictionary<IPAddress, string>();
        if (root.TryGetProperty("Peer", out var peerMap) && peerMap.ValueKind == JsonValueKind.Object)
            foreach (var peer in peerMap.EnumerateObject())
            {
                // The Tailscale name ("iphone-15-pro" from "iphone-15-pro.tail1.ts.net."); iPhones report HostName "localhost".
                var dnsName = peer.Value.TryGetProperty("DNSName", out var dn) ? dn.GetString()?.Split('.')[0] : null;
                var name = !string.IsNullOrEmpty(dnsName) ? dnsName : peer.Value.TryGetProperty("HostName", out var h) ? h.GetString() : null;
                if (string.IsNullOrEmpty(name)) continue;
                foreach (var ip in V4(peer.Value)) peers[ip] = name;
            }
        return new TailscaleInfo(string.IsNullOrEmpty(dns) ? null : dns, V4(self).FirstOrDefault(), peers);
    }

    private static IEnumerable<IPAddress> V4(JsonElement node)
    {
        if (!node.TryGetProperty("TailscaleIPs", out var ips) || ips.ValueKind != JsonValueKind.Array) yield break;
        foreach (var ip in ips.EnumerateArray())
            if (IPAddress.TryParse(ip.GetString(), out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) yield return a;
    }
}
