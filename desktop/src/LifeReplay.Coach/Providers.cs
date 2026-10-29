using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LifeReplay.Coach;

public sealed record ProviderRequest(string Instructions, string Input, string SchemaName, JsonObject Schema, int MaxOutputTokens);

public sealed record ProviderResponse(string Text, int? InputTokens, int? OutputTokens);

public enum ProviderFailure { Timeout, Offline, RateLimited, Server, Rejected, BadResponse }

/// <summary>A failed call. <see cref="Retryable"/> = worth another attempt (timeouts, network, 429, 5xx).</summary>
public sealed class ProviderException(ProviderFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public ProviderFailure Failure { get; } = failure;
    public bool Retryable => Failure is ProviderFailure.Timeout or ProviderFailure.Offline or ProviderFailure.RateLimited or ProviderFailure.Server;
}

/// <summary>Anything that can turn a coaching request into JSON text. Implementations do one attempt; retries live in <see cref="CoachService"/>.</summary>
public interface ICoachingProvider
{
    string Name { get; }
    string Model { get; }
    string Endpoint { get; }
    Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct);
}

/// <summary>
/// OpenAI Responses API (POST {baseUrl}/responses) with strict structured output (text.format = json_schema).
/// Works with OpenAI itself and with OpenAI-compatible gateways such as BAZE (verified against
/// https://api.baze.co/v1/gateway with gpt-6-astra on 2026-10-29).
/// </summary>
public sealed class OpenAiResponsesProvider : ICoachingProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _apiKey;
    private readonly string _reasoningEffort;
    private readonly TimeSpan _timeout;

    public string Name => "openai-responses";
    public string Model { get; }
    public string Endpoint { get; }

    public OpenAiResponsesProvider(string baseUrl, string apiKey, string model, TimeSpan timeout, string reasoningEffort = "", HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("No API key", nameof(apiKey));
        Endpoint = baseUrl.TrimEnd('/') + "/responses";
        _apiKey = apiKey;
        Model = model;
        _timeout = timeout;
        _reasoningEffort = reasoningEffort;
        _http = handler != null ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
        _http.Timeout = Timeout.InfiniteTimeSpan; // per-request timeout below, so it can be told apart from cancellation
        _ownsHttp = true;
    }

    public async Task<ProviderResponse> CompleteAsync(ProviderRequest request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["instructions"] = request.Instructions,
            ["input"] = request.Input,
            ["max_output_tokens"] = request.MaxOutputTokens,
            ["store"] = false,
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject { ["type"] = "json_schema", ["name"] = request.SchemaName, ["strict"] = true, ["schema"] = request.Schema.DeepClone() },
            },
        };
        if (_reasoningEffort.Length > 0) body["reasoning"] = new JsonObject { ["effort"] = _reasoningEffort };

        using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        HttpResponseMessage resp;
        string text;
        try
        {
            resp = await _http.SendAsync(msg, timeout.Token).ConfigureAwait(false);
            text = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException(ProviderFailure.Timeout, $"No answer within {_timeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException(ProviderFailure.Offline, "Network error: " + ex.Message, ex);
        }
        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.TooManyRequests) throw new ProviderException(ProviderFailure.RateLimited, "429 rate limited");
            if ((int)resp.StatusCode >= 500) throw new ProviderException(ProviderFailure.Server, $"{(int)resp.StatusCode} from the API");
            if (!resp.IsSuccessStatusCode) throw new ProviderException(ProviderFailure.Rejected, $"{(int)resp.StatusCode}: {Short(text)}");
        }
        return Parse(text);
    }

    /// <summary>Extracts the output text from a Responses API result.</summary>
    public static ProviderResponse Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var st) && st.GetString() is { } s && s != "completed")
                throw new ProviderException(ProviderFailure.BadResponse, $"Response status '{s}'" +
                    (root.TryGetProperty("incomplete_details", out var inc) ? " " + inc.ToString() : ""));
            var sb = new StringBuilder();
            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                foreach (var item in output.EnumerateArray())
                    if (item.TryGetProperty("type", out var t) && t.GetString() == "message" && item.TryGetProperty("content", out var content))
                        foreach (var c in content.EnumerateArray())
                        {
                            if (c.TryGetProperty("type", out var ct) && ct.GetString() == "refusal")
                                throw new ProviderException(ProviderFailure.BadResponse, "Model refused: " + (c.TryGetProperty("refusal", out var r) ? r.GetString() : ""));
                            if (c.TryGetProperty("text", out var tx)) sb.Append(tx.GetString());
                        }
            if (sb.Length == 0) throw new ProviderException(ProviderFailure.BadResponse, "No output text in the response");
            int? inTok = null, outTok = null;
            if (root.TryGetProperty("usage", out var u))
            {
                if (u.TryGetProperty("input_tokens", out var i) && i.ValueKind == JsonValueKind.Number) inTok = i.GetInt32();
                if (u.TryGetProperty("output_tokens", out var o) && o.ValueKind == JsonValueKind.Number) outTok = o.GetInt32();
            }
            return new ProviderResponse(sb.ToString(), inTok, outTok);
        }
        catch (JsonException ex) { throw new ProviderException(ProviderFailure.BadResponse, "Response is not JSON", ex); }
    }

    private static string Short(string s) => s.Length <= 300 ? s : s[..300] + "…";

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
