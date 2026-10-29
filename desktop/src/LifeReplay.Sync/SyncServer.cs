using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LifeReplay.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace LifeReplay.Sync;

/// <summary>
/// Opt-in HTTPS endpoint that receives iPhone recordings on the local network.
/// Every request needs "Authorization: Bearer &lt;token&gt;" from pairing; there are no unauthenticated routes.
/// Protocol: shared/sync-protocol.md.
/// </summary>
public sealed class SyncServer : IAsyncDisposable
{
    public const string MetadataHeader = "X-LifeReplay-Metadata";
    public const int ApiVersion = 1;

    private readonly WebApplication _app;
    public int Port { get; }
    public string Fingerprint { get; }

    private SyncServer(WebApplication app, int port, string fingerprint)
    {
        _app = app; Port = port; Fingerprint = fingerprint;
    }

    public static async Task<SyncServer> StartAsync(int port, X509Certificate2 certificate, PairingStore pairing, IngestService ingest,
        AppPaths paths, string serverName, IPAddress? bindAddress = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 4L * 1024 * 1024 * 1024;
            k.Listen(bindAddress ?? IPAddress.Any, port, o =>
            {
                o.Protocols = HttpProtocols.Http1AndHttp2;
                o.UseHttps(certificate);
            });
        });
        var app = builder.Build();
        var failures = new ConcurrentDictionary<string, (int Count, long WindowStart)>();

        // Authentication for every route, with a simple lockout after repeated bad tokens.
        app.Use(async (ctx, next) =>
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            long now = TimeUtil.NowMs();
            if (failures.TryGetValue(ip, out var f) && now - f.WindowStart < 600_000 && f.Count >= 20)
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }
            var auth = ctx.Request.Headers.Authorization.ToString();
            var token = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..].Trim() : null;
            if (!pairing.Validate(token))
            {
                failures.AddOrUpdate(ip, _ => (1, now), (_, old) => now - old.WindowStart > 600_000 ? (1, now) : (old.Count + 1, old.WindowStart));
                Log.Warn($"Sync: rejected request from {ip} {ctx.Request.Method} {ctx.Request.Path} (bad token)");
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            failures.TryRemove(ip, out _);
            await next();
        });

        app.MapGet("/api/v1/hello", () => Results.Json(new { server = serverName, apiVersion = ApiVersion }));

        app.MapPut("/api/v1/segments/{id}", async (HttpContext ctx, string id) =>
        {
            IngestSegmentMetadata? meta;
            try
            {
                var raw = ctx.Request.Headers[MetadataHeader].ToString();
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(raw));
                meta = JsonSerializer.Deserialize<IngestSegmentMetadata>(json, RecorderConfig.JsonOptions);
            }
            catch { meta = null; }
            if (meta == null || meta.SegmentId != id) return Results.BadRequest(new { error = $"missing or invalid {MetadataHeader} header" });
            var reject = IngestService.Validate(meta, TimeUtil.NowMs());
            if (reject != null) return Results.BadRequest(new { error = reject });

            Directory.CreateDirectory(paths.IncomingDir);
            var temp = Path.Combine(paths.IncomingDir, $"{Guid.NewGuid():N}.upload");
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                await ctx.Request.Body.CopyToAsync(fs, ctx.RequestAborted);
            var result = ingest.IngestSegment(meta, temp);
            return result.Outcome switch
            {
                IngestOutcome.Created => Results.Json(new { status = "created", id }, statusCode: 201),
                IngestOutcome.AlreadyExists => Results.Json(new { status = "exists", id }),
                _ => Results.UnprocessableEntity(new { error = result.Message }),
            };
        });

        app.MapPut("/api/v1/segments/{id}/thumbnails/{ts:long}", async (HttpContext ctx, string id, long ts) =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            var r = ingest.IngestThumbnail(id, ts, ms.ToArray());
            return r.Outcome == IngestOutcome.Rejected ? Results.UnprocessableEntity(new { error = r.Message }) : Results.Json(new { status = "created" }, statusCode: 201);
        });

        app.MapPost("/api/v1/devices/{deviceId}/gaps", async (HttpContext ctx, string deviceId) =>
        {
            List<IngestGap>? gaps;
            try { gaps = await JsonSerializer.DeserializeAsync<List<IngestGap>>(ctx.Request.Body, RecorderConfig.JsonOptions, ctx.RequestAborted); }
            catch (JsonException ex) { return Results.BadRequest(new { error = "invalid JSON: " + ex.Message }); }
            var r = ingest.IngestGaps(deviceId, gaps ?? []);
            return r.Outcome == IngestOutcome.Rejected ? Results.BadRequest(new { error = r.Message }) : Results.Json(new { status = r.Message });
        });

        await app.StartAsync();
        var fp = CertificateManager.Fingerprint(certificate);
        Log.Info($"Sync: listening on https://{bindAddress ?? IPAddress.Any}:{port} (cert sha256 {fp})");
        return new SyncServer(app, port, fp);
    }

    public async ValueTask DisposeAsync()
    {
        try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await _app.StopAsync(cts.Token); } catch { }
        await _app.DisposeAsync();
        Log.Info("Sync: stopped");
    }
}
