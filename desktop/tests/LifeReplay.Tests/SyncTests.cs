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

    private static HttpClient Client(string fingerprint) => new(new HttpClientHandler
    {
        // In production Tailscale Serve presents a trusted certificate; in the test we pin the test certificate.
        ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            cert != null && Convert.ToHexString(SHA256.HashData(cert.RawData)).Equals(fingerprint, StringComparison.OrdinalIgnoreCase),
    });

    private static string Ts(DateTimeOffset t) => t.ToString("yyyy-MM-ddTHH:mm:sszzz");

    [Fact]
    public async Task AppEventsNeedTokenAndAreStoredOnce()
    {
        using var t = new TempData();
        using var cert = TestCertificate();
        var pairing = new PairingStore(t.Paths.SyncDir);
        var token = pairing.CreateToken("phone");
        int port = FreePort();
        await using var server = await SyncServer.StartAsync(port, cert, pairing, t.Store, "test", IPAddress.Loopback);
        using var http = Client(server.Fingerprint);
        var url = $"https://127.0.0.1:{port}/api/v1/app-events";
        var now = DateTimeOffset.Now;
        var body = $$"""{"device_id":"iphone","source":"ios_shortcuts","app_name":"Instagram","event_type":"opened","timestamp":"{{Ts(now.AddMinutes(-5))}}","event_id":"e1"}""";

        // No token / wrong token
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync(url, new StringContent(body))).StatusCode);
        var wrong = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body) };
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(wrong)).StatusCode);
        Assert.Empty(t.Store.GetAppEvents(0, long.MaxValue));

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var first = await http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("\"accepted\":1", await first.Content.ReadAsStringAsync());

        // The offline queue re-sends everything as JSON Lines: the known event is a duplicate, the new one is stored.
        var queue = body + "\n" + $$"""{"device_id":"iphone","app_name":"Instagram","event_type":"closed","timestamp":"{{Ts(now.AddMinutes(-1))}}"}""";
        var second = await http.PostAsync(url, new StringContent(queue, Encoding.UTF8, "text/plain"));
        var json = JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, json.GetProperty("accepted").GetInt32());
        Assert.Equal(1, json.GetProperty("duplicates").GetInt32());
        Assert.Equal(2, t.Store.GetAppEvents(0, long.MaxValue).Count);

        // Nothing valid → 400 with reasons.
        var bad = await http.PostAsync(url, new StringContent("""{"device_id":"iphone","app_name":"X","event_type":"maybe","timestamp":"now"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("event_type", await bad.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ClientRejectsUnpinnedCertificate()
    {
        using var t = new TempData();
        using var cert = TestCertificate();
        var pairing = new PairingStore(t.Paths.SyncDir);
        int port = FreePort();
        await using var server = await SyncServer.StartAsync(port, cert, pairing, t.Store, "test", IPAddress.Loopback);
        using var http = Client(new string('0', 64));
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"https://127.0.0.1:{port}/api/v1/hello"));
    }
}
