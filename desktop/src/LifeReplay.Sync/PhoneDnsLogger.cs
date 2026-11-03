using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LifeReplay.Core;

namespace LifeReplay.Sync;

/// <summary>
/// A small DNS forwarder on this PC's Tailscale address (UDP port 53). Every query is passed on to the upstream
/// server and its answer sent back unchanged. Queries from your other devices whose name belongs to a known app
/// (<see cref="NetworkApps"/>) are stored as <see cref="DnsHit"/>s; nothing else is stored.
///
/// Set the PC as a Tailscale nameserver together with a public one (1.1.1.1): Tailscale asks all of them at once
/// and uses the first answer, so the phone keeps working when this PC is off.
/// </summary>
public sealed class PhoneDnsLogger : IAsyncDisposable
{
    private readonly MetadataStore _store;
    private readonly Func<IReadOnlyList<IPEndPoint>> _upstreams;
    private readonly Func<IPAddress, string?> _deviceOf;
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<DnsHit> _pending = new();
    private readonly SemaphoreSlim _inFlight = new(64);
    private readonly Timer _flush;
    private readonly Task _loop;

    public IPEndPoint Listening { get; }
    public long Queries => Interlocked.Read(ref _queries);
    public long Saved => Interlocked.Read(ref _saved);
    public long? LastQueryUtcMs { get; private set; }
    public string? LastDevice { get; private set; }
    private long _queries, _saved;

    private PhoneDnsLogger(MetadataStore store, UdpClient udp, IPEndPoint listening, Func<IReadOnlyList<IPEndPoint>> upstreams, Func<IPAddress, string?> deviceOf)
    {
        _store = store;
        _udp = udp;
        Listening = listening;
        _upstreams = upstreams;
        _deviceOf = deviceOf;
        _flush = new Timer(_ => Flush(), null, 10_000, 10_000);
        _loop = Task.Run(LoopAsync);
    }

    /// <param name="upstreams">Servers to forward to; all are asked at once and the first answer wins.</param>
    /// <param name="deviceOf">Device name for a source address, or null to not store its queries (e.g. this PC).</param>
    public static PhoneDnsLogger Start(MetadataStore store, IPAddress bind, Func<IReadOnlyList<IPEndPoint>> upstreams, Func<IPAddress, string?> deviceOf, int port = 53)
    {
        var udp = NoConnReset(new UdpClient(AddressFamily.InterNetwork));
        udp.Client.Bind(new IPEndPoint(bind, port));
        Log.Info($"Phone DNS: listening on {bind}:{port}, forwarding to {string.Join(", ", upstreams())}");
        return new PhoneDnsLogger(store, udp, (IPEndPoint)udp.Client.LocalEndPoint!, upstreams, deviceOf);
    }

    /// <summary>Windows reports an ICMP "port unreachable" from an earlier send as an error on the next receive; turn that off.</summary>
    private static UdpClient NoConnReset(UdpClient u)
    {
        const int SIO_UDP_CONNRESET = -1744830452;
        try { u.Client.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch { /* not on this platform */ }
        return u;
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) { Log.Warn("Phone DNS: receive failed: " + ex.Message); continue; }
            if (!await _inFlight.WaitAsync(0)) continue; // overloaded: drop; the client retries or asks the other server
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(r.Buffer, r.RemoteEndPoint); }
                catch (Exception ex) { Log.Warn("Phone DNS: query failed: " + ex.Message); }
                finally { _inFlight.Release(); }
            });
        }
    }

    private async Task HandleAsync(byte[] query, IPEndPoint client)
    {
        Interlocked.Increment(ref _queries);
        var q = ParseQuestion(query);
        if (q is { } question)
        {
            var device = _deviceOf(client.Address);
            if (device != null)
            {
                LastQueryUtcMs = TimeUtil.NowMs();
                LastDevice = device;
                if (question.Type is 1 or 28 or 65 && NetworkApps.Match(question.Name) is { } app) // A, AAAA, HTTPS
                {
                    _pending.Enqueue(new DnsHit(device, TimeUtil.NowMs(), question.Name, app));
                    Interlocked.Increment(ref _saved);
                }
            }
        }

        // One socket, the query sent to every upstream; the first answer from any of them goes back.
        var upstreams = _upstreams();
        using var up = NoConnReset(new UdpClient(AddressFamily.InterNetwork));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        foreach (var u in upstreams)
            try { await up.SendAsync(query, u, timeout.Token); } catch (SocketException) { /* unreachable right now */ }
        while (true)
        {
            UdpReceiveResult answer;
            try { answer = await up.ReceiveAsync(timeout.Token); }
            catch (SocketException) { continue; } // one upstream unreachable: keep waiting for the others
            if (!upstreams.Any(u => u.Equals(answer.RemoteEndPoint)) || answer.Buffer.Length < 2 || answer.Buffer[0] != query[0] || answer.Buffer[1] != query[1]) continue;
            await _udp.SendAsync(answer.Buffer, client, _cts.Token);
            return;
        }
    }

    /// <summary>The first question's name and type, or null if the packet isn't a plain DNS query.</summary>
    public static (string Name, int Type)? ParseQuestion(byte[] p)
    {
        if (p.Length < 17) return null;
        bool isResponse = (p[2] & 0x80) != 0;
        int qd = (p[4] << 8) | p[5];
        if (isResponse || qd < 1) return null;
        var sb = new StringBuilder();
        int i = 12;
        while (true)
        {
            if (i >= p.Length) return null;
            int len = p[i++];
            if (len == 0) break;
            if (len > 63 || i + len > p.Length) return null; // compression pointers don't appear in a question
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(p, i, len));
            i += len;
            if (sb.Length > 253) return null;
        }
        if (i + 4 > p.Length) return null;
        int type = (p[i] << 8) | p[i + 1];
        return (sb.ToString().ToLowerInvariant(), type);
    }

    private void Flush()
    {
        if (_pending.IsEmpty) return;
        var batch = new List<DnsHit>();
        while (_pending.TryDequeue(out var h)) batch.Add(h);
        try { _store.InsertDnsHits(batch); }
        catch (Exception ex) { Log.Error($"Phone DNS: couldn't save {batch.Count} lookups", ex); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _udp.Dispose();
        try { await _loop; } catch { /* stopping */ }
        await _flush.DisposeAsync();
        Flush();
        _cts.Dispose();
    }
}
