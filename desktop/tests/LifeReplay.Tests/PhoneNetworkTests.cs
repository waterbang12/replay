using System.Net;
using System.Net.Sockets;
using LifeReplay.Core;
using LifeReplay.Sync;

namespace LifeReplay.Tests;

public class NetworkAppsTests
{
    [Theory]
    [InlineData("rr3---sn-ab5l6nzr.googlevideo.com", "YouTube")]
    [InlineData("www.youtube.com.", "YouTube")]
    [InlineData("scontent-ssn1-1.cdninstagram.com", "Instagram")]
    [InlineData("talk-pilsner.kakao.com", "KakaoTalk")]
    [InlineData("chzzk.naver.com", "치지직")]
    [InlineData("www.naver.com", null)]            // shared portal: not one app
    [InlineData("notyoutube.com", null)]           // whole labels only
    [InlineData("fonts.googleapis.com", null)]
    public void DomainsMapToOneAppOrNone(string domain, string? app) => Assert.Equal(app, NetworkApps.Match(domain));

    private static DnsHit H(string app, int minute, int second = 0, string domain = "x") =>
        new("iphone", minute * 60_000L + second * 1000L, domain, app);

    [Fact]
    public void BurstsBecomeSessionsAndSingleLookupsDont()
    {
        var hits = new List<DnsHit>
        {
            H("YouTube", 0), H("YouTube", 1), H("YouTube", 3), H("YouTube", 5),   // ≤ 3 min apart: one session 0:00–6:00
            H("YouTube", 30), H("YouTube", 31),                                    // only 2: a notification, not use
            H("Instagram", 10, 0, "a"), H("Instagram", 10, 0, "a"), H("Instagram", 10, 0, "b"), // same second: counts once
        };
        var s = NetworkSessions.Build(hits);
        var yt = Assert.Single(s);
        Assert.Equal(("YouTube", 0L, 6 * 60_000L, SessionConfidence.Estimated), (yt.App, yt.StartUtcMs, yt.EndUtcMs, yt.Confidence));
        Assert.True(yt.HasDuration);
    }

    [Fact]
    public void ShortcutsSessionsWinOverNetworkEstimatesForTheSameApp()
    {
        var shortcuts = new List<AppSession> { new("iphone", "YouTube", 0, 100, SessionConfidence.Complete, "ios_shortcuts", null) };
        var network = new List<AppSession>
        {
            new("my-iphone", "youtube", 0, 200, SessionConfidence.Estimated, "network", null),
            new("my-iphone", "Instagram", 300, 400, SessionConfidence.Estimated, "network", null),
        };
        var all = NetworkSessions.Combine(shortcuts, network);
        Assert.Equal(["YouTube", "Instagram"], all.Select(x => x.App));
    }

    [Fact]
    public void EstimatedTimeCountsAsInferredInTotals()
    {
        var t = Assert.Single(AppSessions.Totals([new AppSession("p", "YouTube", 0, 60_000, SessionConfidence.Estimated, "network", null)], 0, 86_400_000));
        Assert.Equal((0L, 60_000L), (t.KnownMs, t.InferredMs));
    }

    [Fact]
    public void HitsAreStoredAndExpire()
    {
        using var t = new TempData();
        t.Store.InsertDnsHits([new DnsHit("p", 1000, "www.youtube.com", "YouTube"), new DnsHit("p", 5000, "i.instagram.com", "Instagram")]);
        Assert.Equal(2, t.Store.GetDnsHits(0, 10_000).Count);
        Assert.Equal(5000, t.Store.LastDnsHitUtcMs());
        t.Store.DeleteDnsHitsBefore(2000);
        Assert.Equal("Instagram", Assert.Single(t.Store.GetDnsHits(0, 10_000)).App);
    }
}

public class PhoneDnsLoggerTests
{
    /// <summary>A DNS query for <paramref name="name"/> (type A), as a phone would send it.</summary>
    internal static byte[] Query(string name, ushort id = 0x1234)
    {
        var bytes = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.')) { bytes.Add((byte)label.Length); bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label)); }
        bytes.AddRange([0, 0, 1, 0, 1]);
        return bytes.ToArray();
    }

    [Fact]
    public void ParsesTheQuestion()
    {
        Assert.Equal(("rr1.googlevideo.com", 1), PhoneDnsLogger.ParseQuestion(Query("rr1.GoogleVideo.com")));
        var response = Query("a.com");
        response[2] |= 0x80;
        Assert.Null(PhoneDnsLogger.ParseQuestion(response));   // answers aren't queries
        Assert.Null(PhoneDnsLogger.ParseQuestion([1, 2, 3]));  // junk
    }

    [Fact]
    public async Task ForwardsEveryQueryAndStoresOnlyKnownAppsFromOtherDevices() // the first upstream is dead (port 9)
    {
        using var t = new TempData();
        // Fake upstream: answers each query with the same bytes and the "response" bit set.
        using var upstream = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var upstreamEp = (IPEndPoint)upstream.Client.LocalEndPoint!;
        using var stop = new CancellationTokenSource();
        var fake = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var r = await upstream.ReceiveAsync(stop.Token);
                var a = r.Buffer.ToArray();
                a[2] |= 0x80;
                await upstream.SendAsync(a, r.RemoteEndPoint, stop.Token);
            }
        });

        string? device = "my-iphone";
        await using (var dns = PhoneDnsLogger.Start(t.Store, IPAddress.Loopback,
                   () => [new IPEndPoint(IPAddress.Loopback, 9), upstreamEp], _ => device, port: 0))
        {
            using var phone = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            async Task<byte[]> Ask(string name)
            {
                await phone.SendAsync(Query(name), dns.Listening);
                using var timeout = new CancellationTokenSource(5000);
                return (await phone.ReceiveAsync(timeout.Token)).Buffer;
            }
            var answer = await Ask("rr1.googlevideo.com");
            Assert.Equal(0x80, answer[2] & 0x80);                         // the upstream's answer came back
            await Ask("www.example.org");                                  // forwarded, not stored
            device = null;                                                 // e.g. this PC's own lookups
            await Ask("www.youtube.com");
            Assert.Equal(3, dns.Queries);
        }
        stop.Cancel();
        var hit = Assert.Single(t.Store.GetDnsHits(0, long.MaxValue));     // saved when the logger stopped
        Assert.Equal(("my-iphone", "rr1.googlevideo.com", "YouTube"), (hit.DeviceId, hit.Domain, hit.App));
    }

    [Fact]
    public void ReadsDevicesFromTailscaleStatus()
    {
        var json = """
            {"Self":{"DNSName":"pc.tail1.ts.net.","TailscaleIPs":["100.64.0.1","fd7a::1"]},
             "Peer":{"k1":{"HostName":"localhost","DNSName":"iphone-15-pro.tail1.ts.net.","TailscaleIPs":["100.64.0.2"]},
                     "k2":{"HostName":"","TailscaleIPs":["100.64.0.3"]},
                     "k3":{"HostName":"laptop","TailscaleIPs":["100.64.0.4"]}},
             "User":{"1":{"DisplayName":"이종현"}}}
            """;
        var ts = TailscaleInfo.Parse(json)!;
        Assert.Equal(("pc.tail1.ts.net", "100.64.0.1"), (ts.DnsName, ts.SelfIPv4!.ToString()));
        Assert.Equal(["iphone-15-pro", "laptop"], ts.Peers.Values.Order());
    }
}
