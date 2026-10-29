using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using LifeReplay.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace LifeReplay.Sync;

/// <summary>
/// Opt-in HTTPS endpoint that receives iPhone app-usage events from Apple Shortcuts.
/// Every request needs "Authorization: Bearer &lt;token&gt;" from pairing; there are no unauthenticated routes.
/// Usually reached through Tailscale Serve (trusted certificate, tailnet-only). Protocol: shared/sync-protocol.md.
/// </summary>
public sealed class SyncServer : IAsyncDisposable
{
    public const int ApiVersion = 2;
    /// <summary>Plenty for a long offline queue (each event is ~200 bytes).</summary>
    public const long MaxBodyBytes = 2 * 1024 * 1024;

    private readonly WebApplication _app;
    public int Port { get; }
    public string Fingerprint { get; }

    private SyncServer(WebApplication app, int port, string fingerprint)
    {
        _app = app; Port = port; Fingerprint = fingerprint;
    }

    public static async Task<SyncServer> StartAsync(int port, X509Certificate2 certificate, PairingStore pairing, MetadataStore store,
        string serverName, IPAddress? bindAddress = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = MaxBodyBytes;
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
            var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
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

        // One event (JSON object), several (JSON array), or the Shortcuts queue file (one JSON object per line).
        app.MapPost("/api/v1/app-events", async (HttpContext ctx) =>
        {
            string body;
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                body = await reader.ReadToEndAsync(ctx.RequestAborted);
            }
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
            var r = AppEvents.Ingest(store, body, TimeUtil.NowMs());
            Log.Info($"Sync: app events from {ctx.Connection.RemoteIpAddress}: {r.Accepted} new, {r.Duplicates} duplicate, {r.Errors.Count} rejected" +
                     (r.Errors.Count > 0 ? $" (first: {r.Errors[0]})" : ""));
            var payload = new { accepted = r.Accepted, duplicates = r.Duplicates, rejected = r.Errors.Count, errors = r.Errors.Take(20) };
            // 200 if anything was usable (so the phone clears its queue); 400 only when nothing in the body was valid.
            return r.Accepted + r.Duplicates == 0 && r.Errors.Count > 0 ? Results.Json(payload, statusCode: 400) : Results.Json(payload);
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
