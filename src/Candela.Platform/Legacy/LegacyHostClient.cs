using System.Net;
using System.Text;
using Candela.Platform.Http;
using Candela.Shared.Exceptions;
using Candela.Shared.Auth;
using Candela.Shared.Logging;
using Newtonsoft.Json;

namespace Candela.Platform.Legacy;

public sealed class LegacyHostOptions
{
    /// <summary>Base address of the legacy application, e.g. http://localhost:8080/legacy/ </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Must match Legacy:SharedSecret in the legacy host's Web.config.</summary>
    public string SharedSecret { get; set; } = "";

    /// <summary>Seconds. The legacy host talks to SQL through the Candela DAL, so allow for a slow write.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Root of the legacy application, e.g. http://localhost:8080 — one level above
    /// BaseUrl's /legacy segment. LegacyProxy forwards unmigrated /api/* routes here,
    /// so it needs the app root, not the internal /legacy channel.
    ///
    /// Leave it unset and it is derived from BaseUrl by dropping the trailing /legacy,
    /// which is the layout the deployment guide describes (one IIS site, the .NET
    /// Framework app nested at /legacy). Set it explicitly for any other layout.
    /// </summary>
    public string AppUrl { get; set; } = "";

    /// <summary>
    /// AppUrl if configured, otherwise BaseUrl with its trailing /legacy removed.
    /// Empty when neither is available, which the proxy reports as 502 rather than
    /// forwarding somewhere it guessed.
    /// </summary>
    public string LegacyAppUrl()
    {
        if (!string.IsNullOrWhiteSpace(AppUrl))
            return AppUrl.TrimEnd('/');

        var url = (BaseUrl ?? "").TrimEnd('/');
        if (url.EndsWith("/legacy", StringComparison.OrdinalIgnoreCase))
            return url[..^"/legacy".Length];

        return url;
    }
}

/// <summary>
/// The only route from this API into the Candela DAL.
///
/// Writes that must keep Candela's SQL log (which HO/shop replication replays),
/// activity log, inventory posting and accounting correct cannot be done with plain
/// SQL from here — they have to go through the DAL, and the DAL only runs on .NET
/// Framework. So this posts to the legacy host over loopback and that process does it.
///
/// Reads never come through here. They have no such requirement, so they query SQL
/// directly and skip the hop.
///
/// Every call carries the shared secret and the correlation id, so a request can be
/// followed across both processes with one grep.
/// </summary>
public interface ILegacyHostClient
{
    Task<T> PostAsync<T>(string path, object body, CancellationToken ct);
    Task<T> PutAsync<T>(string path, object body, CancellationToken ct);
    Task<T> DeleteAsync<T>(string path, CancellationToken ct);

    /// <summary>
    /// Sends a request whose JSON response is handed back untouched, along with its
    /// status code.
    ///
    /// Use this — not PostAsync&lt;T&gt; — when the legacy wrapper already produces the
    /// exact body the tablet must receive, and re-modelling it here would risk changing
    /// a field's casing, dropping a key that was only present on one branch, or adding a
    /// default value that was absent. PostReturn and the Sales writes are like that:
    /// their bodies vary by branch (idempotent replay, business-rule 422, DAL failure)
    /// and every variant is already the final wire contract.
    ///
    /// A non-2xx response is returned, not thrown — the caller relays the status and body
    /// as they are.
    /// </summary>
    Task<LegacyRawResponse> SendRawAsync(HttpMethod method, string path, string? jsonBody, CancellationToken ct);
}

/// <summary>A legacy-host response passed back verbatim: its status and its JSON body.</summary>
public sealed record LegacyRawResponse(int StatusCode, string Body, string ContentType);

public sealed class LegacyHostClient : ILegacyHostClient
{
    private readonly HttpClient _http;
    private readonly LegacyHostOptions _options;
    private readonly IHttpContextAccessor _context;

    public LegacyHostClient(HttpClient http, LegacyHostOptions options, IHttpContextAccessor context)
    {
        _http = http;
        _options = options;
        _context = context;
    }

    public Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
        => SendAsync<T>(HttpMethod.Post, path, body, ct);

    public Task<T> PutAsync<T>(string path, object body, CancellationToken ct)
        => SendAsync<T>(HttpMethod.Put, path, body, ct);

    public Task<T> DeleteAsync<T>(string path, CancellationToken ct)
        => SendAsync<T>(HttpMethod.Delete, path, null, ct);

    public async Task<LegacyRawResponse> SendRawAsync(HttpMethod method, string path,
        string? jsonBody, CancellationToken ct)
    {
        using var request = BuildRequest(method, path);

        if (jsonBody is not null)
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LegacyHostException($"Legacy host timed out after {_options.TimeoutSeconds}s on {method} {path}.");
        }
        catch (HttpRequestException ex)
        {
            throw new LegacyHostException($"Could not reach the legacy host for {method} {path}.", ex);
        }

        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(ct);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            return new LegacyRawResponse((int)response.StatusCode, payload, contentType);
        }
    }

    /// <summary>
    /// Builds a request to the legacy host with the shared secret, the correlation id and
    /// the validated caller context (X-Ctx-*). Shared by SendAsync and SendRawAsync so a
    /// forward and a raw forward carry identical headers.
    ///
    /// The /legacy/* routes are exempt from the legacy host's own JWT handler — they
    /// carry no cashier session and re-validating a token there would be wasted work — so
    /// the DAL wrappers read Request.Properties, which the legacy host's
    /// LegacyContextHandler fills from these headers. Safe because /legacy/* is
    /// loopback-only and shared-secret-guarded.
    /// </summary>
    private HttpRequestMessage BuildRequest(HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new LegacyHostException("Legacy:BaseUrl is not configured.");
        if (string.IsNullOrWhiteSpace(_options.SharedSecret))
            throw new LegacyHostException("Legacy:SharedSecret is not configured.");

        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Legacy-Secret", _options.SharedSecret);

        var correlationId = _context.HttpContext?.Items[CorrelationIdMiddleware.HeaderName] as string;
        if (!string.IsNullOrEmpty(correlationId))
            request.Headers.Add(CorrelationIdMiddleware.HeaderName, correlationId);

        AddContextHeader(request, "X-Ctx-User-Id", JwtHelper.GetUserId);
        AddContextHeader(request, "X-Ctx-Shop-Id", JwtHelper.GetShopId);
        AddContextHeader(request, "X-Ctx-Pos-Code", JwtHelper.GetPosCode);
        AddContextHeader(request, "X-Ctx-User-Name", JwtHelper.GetUserName);
        AddContextHeader(request, "X-Ctx-Device-Id", JwtHelper.GetDeviceId);
        AddContextHeader(request, "X-Ctx-Group-Name", JwtHelper.GetGroupName);
        AddContextHeader(request, "X-Ctx-Group-Type", JwtHelper.GetGroupType);

        return request;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = BuildRequest(method, path);

        if (body is not null)
        {
            request.Content = new StringContent(
                JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LegacyHostException($"Legacy host timed out after {_options.TimeoutSeconds}s on {method} {path}.");
        }
        catch (HttpRequestException ex)
        {
            throw new LegacyHostException($"Could not reach the legacy host for {method} {path}.", ex);
        }

        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                if (string.IsNullOrWhiteSpace(payload)) return default!;
                try
                {
                    return JsonConvert.DeserializeObject<T>(payload)!;
                }
                catch (JsonException ex)
                {
                    throw new LegacyHostException($"Legacy host returned unreadable JSON for {method} {path}.", ex);
                }
            }

            throw Translate(response.StatusCode, payload, method, path);
        }
    }

    /// <summary>
    /// Copies one claim onto the outgoing request as a header, when there is a validated
    /// principal to read it from. Silently skips it otherwise — the only caller with no
    /// principal is an internal job, and the wrappers it reaches do not read context.
    /// </summary>
    private void AddContextHeader<T>(HttpRequestMessage request, string headerName,
        Func<System.Security.Claims.ClaimsPrincipal, T> read)
    {
        var user = _context.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) return;

        var value = read(user)?.ToString();
        if (!string.IsNullOrEmpty(value))
            request.Headers.Add(headerName, value);
    }

    /// <summary>
    /// 422 is the legacy host reporting one of Candela's own business rules — "Name
    /// already exists", "Dependent Record Exists". That text is written for the person
    /// at the screen, so it is carried through unchanged.
    ///
    /// Anything else describes an internal hop the caller knows nothing about, so it is
    /// logged here and surfaced as a generic 502.
    /// </summary>
    private static ApiException Translate(HttpStatusCode status, string payload, HttpMethod method, string path)
    {
        var message = ExtractError(payload);

        if ((int)status == 422)
            return new BusinessRuleException(message ?? "The request was refused.");

        if (status == HttpStatusCode.BadRequest)
            return new ValidationException(message ?? "The legacy host rejected the request.");

        AppLog.Error(null, "Legacy host returned {0} for {1} {2}: {3}",
            (int)status, method, path, string.IsNullOrEmpty(payload) ? "(empty body)" : payload);

        return new LegacyHostException($"Legacy host returned {(int)status} for {method} {path}.");
    }

    private static string? ExtractError(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            var parsed = JsonConvert.DeserializeObject<Dictionary<string, object?>>(payload);
            return parsed is not null && parsed.TryGetValue("error", out var v) ? v?.ToString() : null;
        }
        catch
        {
            return null;
        }
    }
}
