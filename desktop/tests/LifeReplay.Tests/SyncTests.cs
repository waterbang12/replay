using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LifeReplay.Core;
using LifeReplay.Sync;

namespace LifeReplay.Tests;

public class IngestTests
{
    private static (IngestSegmentMetadata Meta, string File) Upload(TempData t, long start, long end)
    {
        var bytes = Mp4Builder.Build(end - start, 886, 1920);
        var file = Path.Combine(t.Paths.DataDir, Guid.NewGuid() + ".upload");
        File.WriteAllBytes(file, bytes);
        var meta = new IngestSegmentMetadata
        {
            SegmentId = Guid.NewGuid().ToString(), SessionId = Guid.NewGuid().ToString(), DeviceId = Guid.NewGuid().ToString(),
            DeviceName = "Test iPhone", StartUtcMs = start, EndUtcMs = end, Width = 886, Height = 1920, Fps = 15, FrameCount = 100,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        };
        return (meta, file);
    }

    [Fact]
    public void ValidSegmentIsStoredUnderIphoneFolder()
    {
        using var t = new TempData();
        long now = TimeUtil.NowMs();
        var (meta, file) = Upload(t, now - 60_000, now);
        var r = new IngestService(t.Store, t.Paths).IngestSegment(meta, file);
        Assert.Equal(IngestOutcome.Created, r.Outcome);
        var seg = t.Store.GetSegment(meta.SegmentId)!;
        Assert.Equal(SegmentStatus.Finalized, seg.Status);
        Assert.Contains("/iphone/", seg.FilePath);
        Assert.True(File.Exists(t.Paths.ToFull(seg.FilePath)));
        Assert.Equal(Platforms.Ios, t.Store.GetDevice(meta.DeviceId)!.Platform);
        Assert.False(File.Exists(file)); // temp file consumed
    }

    [Fact]
    public void DuplicateUploadIsAcknowledged()
    {
        using var t = new TempData();
        long now = TimeUtil.NowMs();
        var svc = new IngestService(t.Store, t.Paths);
        var (meta, file) = Upload(t, now - 60_000, now);
        Assert.Equal(IngestOutcome.Created, svc.IngestSegment(meta, file).Outcome);
        File.WriteAllBytes(file, Mp4Builder.Build(60_000, 886, 1920));
        Assert.Equal(IngestOutcome.AlreadyExists, svc.IngestSegment(meta, file).Outcome);
    }

    [Fact]
    public void CorruptedUploadIsRejected()
    {
        using var t = new TempData();
        long now = TimeUtil.NowMs();
        var (meta, file) = Upload(t, now - 60_000, now);
        meta.Sha256 = new string('0', 64);
        var r = new IngestService(t.Store, t.Paths).IngestSegment(meta, file);
        Assert.Equal(IngestOutcome.Rejected, r.Outcome);
        Assert.Null(t.Store.GetSegment(meta.SegmentId));
    }

    [Theory]
    [InlineData("../../evil", "segmentId")]
    [InlineData("not-a-guid", "segmentId")]
    public void PathTricksInIdsAreRejected(string id, string expected)
    {
        var m = new IngestSegmentMetadata { SegmentId = id, SessionId = Guid.NewGuid().ToString(), DeviceId = Guid.NewGuid().ToString(), StartUtcMs = TimeUtil.NowMs() - 1000, EndUtcMs = TimeUtil.NowMs(), Width = 1, Height = 1, Sha256 = new string('a', 64) };
        Assert.Contains(expected, IngestService.Validate(m, TimeUtil.NowMs()));
    }
}

public class PairingTests
{
    [Fact]
    public void TokenValidatesUntilRevoked()
    {
        using var t = new TempData();
        var store = new PairingStore(t.Paths.SyncDir);
        var token = store.CreateToken("phone");
        Assert.True(store.Validate(token));
        Assert.False(store.Validate(token + "x"));
        Assert.False(store.Validate(null));
        Assert.True(new PairingStore(t.Paths.SyncDir).Validate(token)); // persisted
        Assert.DoesNotContain(token, File.ReadAllText(Path.Combine(t.Paths.SyncDir, "pairing.json"))); // only the hash is stored
        store.RevokeAll();
        Assert.False(store.Validate(token));
    }
}

public class SyncServerTests
{
    private static X509Certificate2 TestCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=LifeReplay test", key, HashAlgorithmName.SHA256);
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static HttpClient PinnedClient(string fingerprint) => new(new HttpClientHandler
    {
        // Same rule as the iPhone app: trust exactly the paired certificate, nothing else.
        ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            cert != null && Convert.ToHexString(SHA256.HashData(cert.RawData)).Equals(fingerprint, StringComparison.OrdinalIgnoreCase),
    });

    [Fact]
    public async Task UploadRequiresTokenAndStoresSegment()
    {
        using var t = new TempData();
        using var cert = TestCertificate();
        var pairing = new PairingStore(t.Paths.SyncDir);
        var token = pairing.CreateToken("phone");
        int port = FreePort();
        await using var server = await SyncServer.StartAsync(port, cert, pairing, new IngestService(t.Store, t.Paths), t.Paths, "test", IPAddress.Loopback);
        using var http = PinnedClient(server.Fingerprint);
        var baseUrl = $"https://127.0.0.1:{port}";

        // No token / wrong token
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(baseUrl + "/api/v1/hello")).StatusCode);
        var wrong = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/v1/hello");
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(wrong)).StatusCode);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(baseUrl + "/api/v1/hello")).StatusCode);

        long now = TimeUtil.NowMs();
        var bytes = Mp4Builder.Build(30_000, 886, 1920);
        var meta = new IngestSegmentMetadata
        {
            SegmentId = Guid.NewGuid().ToString(), SessionId = Guid.NewGuid().ToString(), DeviceId = Guid.NewGuid().ToString(),
            DeviceName = "Test iPhone", StartUtcMs = now - 30_000, EndUtcMs = now, Width = 886, Height = 1920, Fps = 15,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        };
        HttpRequestMessage Put()
        {
            var req = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/api/v1/segments/{meta.SegmentId}") { Content = new ByteArrayContent(bytes) };
            req.Headers.Add(SyncServer.MetadataHeader, Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(meta, RecorderConfig.JsonOptions))));
            return req;
        }
        Assert.Equal(HttpStatusCode.Created, (await http.SendAsync(Put())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Put())).StatusCode); // idempotent retry
        Assert.Equal(SegmentStatus.Finalized, t.Store.GetSegment(meta.SegmentId)!.Status);

        var gaps = new StringContent(JsonSerializer.Serialize(new[] { new IngestGap(now - 90_000, now - 60_000, "phone call") }, RecorderConfig.JsonOptions), Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"{baseUrl}/api/v1/devices/{meta.DeviceId}/gaps", gaps)).StatusCode);
        Assert.Contains(t.Store.GetGapsInRange(0, long.MaxValue / 2), g => g.Reason == GapReasons.IosInterrupted);
    }

    [Fact]
    public async Task ClientRejectsUnpinnedCertificate()
    {
        using var t = new TempData();
        using var cert = TestCertificate();
        var pairing = new PairingStore(t.Paths.SyncDir);
        int port = FreePort();
        await using var server = await SyncServer.StartAsync(port, cert, pairing, new IngestService(t.Store, t.Paths), t.Paths, "test", IPAddress.Loopback);
        using var http = PinnedClient(new string('0', 64));
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"https://127.0.0.1:{port}/api/v1/hello"));
    }
}
