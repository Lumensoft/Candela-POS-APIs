using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Candela.Tests.Parity;

/// <summary>
/// Sends the same request to the old net48 endpoint and the new .NET 10 endpoint and
/// compares what comes back. This is the safety net for the whole migration: an endpoint
/// is not "moved" until this says the two answers are the same.
///
/// URLs come from environment variables so the same test suite runs against whatever
/// two hosts you have up:
///   PARITY_OLD_BASE   e.g. http://localhost:58408
///   PARITY_NEW_BASE   e.g. http://localhost:5099
///   PARITY_JWT        a valid bearer token issued by BOTH hosts (same Jwt:Secret)
///
/// If PARITY_OLD_BASE is not set the parity tests skip themselves rather than fail —
/// so CI without the legacy host still goes green, and you run the real comparison
/// locally with both hosts running.
/// </summary>
public sealed class ParityClient : IDisposable
{
    private readonly HttpClient? _old;
    private readonly HttpClient _new;
    private readonly string? _jwt;

    public bool OldHostConfigured => _old is not null;

    public ParityClient()
    {
        var oldBase = Environment.GetEnvironmentVariable("PARITY_OLD_BASE");
        var newBase = Environment.GetEnvironmentVariable("PARITY_NEW_BASE") ?? "http://localhost:5099";
        _jwt = Environment.GetEnvironmentVariable("PARITY_JWT");

        if (!string.IsNullOrWhiteSpace(oldBase))
            _old = new HttpClient { BaseAddress = new Uri(oldBase), Timeout = TimeSpan.FromSeconds(30) };

        _new = new HttpClient { BaseAddress = new Uri(newBase), Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Runs <paramref name="path"/> against both hosts and returns the comparison.
    /// Throws SkipException-style guidance if the old host is not configured.
    /// </summary>
    public async Task<ParityResult> CompareAsync(HttpMethod method, string path, object? body = null)
    {
        if (_old is null)
            throw new InvalidOperationException(
                "PARITY_OLD_BASE is not set — start the legacy host and set the env var to run this comparison.");

        var oldResp = await SendAsync(_old, method, path, body);
        var newResp = await SendAsync(_new, method, path, body);

        var oldBody = await oldResp.Content.ReadAsStringAsync();
        var newBody = await newResp.Content.ReadAsStringAsync();

        var diffs = new List<string>();

        if ((int)oldResp.StatusCode != (int)newResp.StatusCode)
            diffs.Add($"status: old={(int)oldResp.StatusCode} new={(int)newResp.StatusCode}");

        DiffJson(oldBody, newBody, diffs);

        return new ParityResult(
            (int)oldResp.StatusCode, (int)newResp.StatusCode,
            oldBody, newBody, diffs);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body)
    {
        using var req = new HttpRequestMessage(method, path);
        if (!string.IsNullOrWhiteSpace(_jwt))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwt);
        if (body is not null)
            req.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        return await client.SendAsync(req);
    }

    /// <summary>
    /// Structural JSON comparison. Key order and whitespace are ignored; a changed
    /// value, a renamed field, a field that appears or disappears, or a number
    /// formatted differently all count as a difference.
    /// </summary>
    private static void DiffJson(string oldRaw, string newRaw, List<string> diffs)
    {
        JToken oldTok, newTok;
        try
        {
            oldTok = string.IsNullOrWhiteSpace(oldRaw) ? JValue.CreateNull() : JToken.Parse(oldRaw);
            newTok = string.IsNullOrWhiteSpace(newRaw) ? JValue.CreateNull() : JToken.Parse(newRaw);
        }
        catch
        {
            if (!string.Equals(oldRaw.Trim(), newRaw.Trim(), StringComparison.Ordinal))
                diffs.Add($"body (non-JSON): old={Trim(oldRaw)} new={Trim(newRaw)}");
            return;
        }

        Walk("$", oldTok, newTok, diffs);
    }

    private static void Walk(string path, JToken a, JToken b, List<string> diffs)
    {
        if (a.Type != b.Type)
        {
            // int vs float of the same numeric value is fine; anything else is a diff.
            if (IsNumeric(a) && IsNumeric(b))
            {
                if (a.Value<decimal>() != b.Value<decimal>())
                    diffs.Add($"{path}: old={a} new={b}");
                return;
            }
            diffs.Add($"{path}: type old={a.Type} new={b.Type}  ({Trim(a.ToString())} / {Trim(b.ToString())})");
            return;
        }

        switch (a.Type)
        {
            case JTokenType.Object:
                var ao = (JObject)a;
                var bo = (JObject)b;
                foreach (var prop in ao.Properties())
                {
                    if (bo[prop.Name] is null) diffs.Add($"{path}.{prop.Name}: missing on new");
                    else Walk($"{path}.{prop.Name}", prop.Value, bo[prop.Name]!, diffs);
                }
                foreach (var prop in bo.Properties())
                    if (ao[prop.Name] is null) diffs.Add($"{path}.{prop.Name}: extra on new");
                break;

            case JTokenType.Array:
                var aa = (JArray)a;
                var ba = (JArray)b;
                if (aa.Count != ba.Count)
                {
                    diffs.Add($"{path}: array length old={aa.Count} new={ba.Count}");
                    break;
                }
                for (var i = 0; i < aa.Count; i++)
                    Walk($"{path}[{i}]", aa[i], ba[i], diffs);
                break;

            default:
                if (!JToken.DeepEquals(a, b))
                    diffs.Add($"{path}: old={a} new={b}");
                break;
        }
    }

    private static bool IsNumeric(JToken t) => t.Type is JTokenType.Integer or JTokenType.Float;

    private static string Trim(string s) => s.Length > 200 ? s[..200] + "…" : s;

    public void Dispose()
    {
        _old?.Dispose();
        _new.Dispose();
    }
}

public sealed record ParityResult(
    int OldStatus, int NewStatus, string OldBody, string NewBody, IReadOnlyList<string> Diffs)
{
    public bool Identical => Diffs.Count == 0;

    public string Report() =>
        Identical
            ? $"IDENTICAL (status {NewStatus})"
            : $"MISMATCH (old {OldStatus} / new {NewStatus}):\n  " + string.Join("\n  ", Diffs);
}
