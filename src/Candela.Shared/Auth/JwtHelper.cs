using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Candela.Shared.Auth;

/// <summary>
/// HS256 tokens, ported behaviour-for-behaviour from the .NET Framework host.
///
/// This MUST stay interchangeable with that host for the whole migration: while some
/// endpoints are served by one process and some by the other, a token minted on either
/// side has to validate on both. Validation re-signs the exact "header.body" string as
/// received, so only the shared secret and the algorithm matter — which is why this is
/// a faithful port rather than a rewrite onto Microsoft.IdentityModel.
///
/// Replacing this with the standard library is worth doing (it was logged as finding
/// M3), but only once both hosts can be changed together.
/// </summary>
public static class JwtHelper
{
    private static string _secret = "";
    private static int _expiryHours = 24;
    private static string _issuer = "CandelaPOS";

    public static void Configure(string? secret, int expiryHours, string? issuer)
    {
        _secret = secret ?? "";
        if (expiryHours > 0) _expiryHours = expiryHours;
        if (!string.IsNullOrWhiteSpace(issuer)) _issuer = issuer!;
    }

    public static string Generate(int userId, string userName, int shopId, string posCode, string deviceId,
                                  string groupName, int groupType, decimal saleReturnLimit,
                                  bool hasBelowCostRight = false, string controlRightsStr = "")
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new Dictionary<string, object>
        {
            { "iss",               _issuer },
            { "aud",               _issuer },
            { "iat",               now.ToUnixTimeSeconds() },
            { "exp",               now.AddHours(_expiryHours).ToUnixTimeSeconds() },
            { "user_id",           userId },
            { "user_name",         userName },
            { "shop_id",           shopId },
            { "pos_code",          posCode },
            { "device_id",         deviceId },
            { "group_name",        groupName },
            { "group_type",        groupType },
            { "sale_return_limit", saleReturnLimit },
            { "below_cost_right",  hasBelowCostRight ? 1 : 0 },
            { "scr_rights",        controlRightsStr }
        };

        var header = Base64UrlEncode(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var body = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload)));
        var signing = header + "." + body;

        return signing + "." + Sign(signing);
    }

    public static ClaimsPrincipal? Validate(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;

            var signing = parts[0] + "." + parts[1];
            if (!FixedTimeEquals(Sign(signing), parts[2])) return null;

            var json = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            var payload = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
            if (payload is null) return null;

            if (!payload.TryGetValue("exp", out var expRaw)) return null;
            var exp = Convert.ToInt64(expRaw);
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return null;

            if (!payload.TryGetValue("iss", out var iss) || iss?.ToString() != _issuer) return null;

            var identity = new ClaimsIdentity("JWT");
            foreach (var kv in payload)
                identity.AddClaim(new Claim(kv.Key, kv.Value?.ToString() ?? ""));

            return new ClaimsPrincipal(identity);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Signature segment of a raw token, or "" when malformed.</summary>
    public static string ExtractSignature(string rawToken)
    {
        if (string.IsNullOrEmpty(rawToken)) return "";
        var parts = rawToken.Split('.');
        return parts.Length == 3 ? parts[2] : "";
    }

    /// <summary>exp claim as UTC, or DateTime.MinValue when it cannot be read.</summary>
    public static DateTime ExtractExpiry(string rawToken)
    {
        try
        {
            var parts = rawToken.Split('.');
            if (parts.Length != 3) return DateTime.MinValue;
            var json = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            var payload = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
            if (payload is null || !payload.TryGetValue("exp", out var expRaw)) return DateTime.MinValue;
            return DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(expRaw)).UtcDateTime;
        }
        catch { return DateTime.MinValue; }
    }

    public static int GetUserId(ClaimsPrincipal p) => int.TryParse(p.FindFirst("user_id")?.Value, out var v) ? v : 0;
    public static int GetShopId(ClaimsPrincipal p) => int.TryParse(p.FindFirst("shop_id")?.Value, out var v) ? v : 0;
    public static string GetPosCode(ClaimsPrincipal p) => p.FindFirst("pos_code")?.Value ?? "POS";
    public static string GetDeviceId(ClaimsPrincipal p) => p.FindFirst("device_id")?.Value ?? "";
    public static string GetUserName(ClaimsPrincipal p) => p.FindFirst("user_name")?.Value ?? "";
    public static string GetGroupName(ClaimsPrincipal p) => p.FindFirst("group_name")?.Value ?? "";
    public static int GetGroupType(ClaimsPrincipal p) => int.TryParse(p.FindFirst("group_type")?.Value, out var v) ? v : 0;
    public static decimal GetSaleReturnLimit(ClaimsPrincipal p) => decimal.TryParse(p.FindFirst("sale_return_limit")?.Value, out var v) ? v : 0m;
    public static bool GetBelowCostRight(ClaimsPrincipal p) => p.FindFirst("below_cost_right")?.Value == "1";

    public static HashSet<string> GetControlRightsSet(ClaimsPrincipal p)
    {
        var raw = p.FindFirst("scr_rights")?.Value ?? "";
        return string.IsNullOrEmpty(raw)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(raw.Split(','), StringComparer.OrdinalIgnoreCase);
    }

    private static string Sign(string input)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
        return Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(input)));
    }

    /// <summary>
    /// Constant-time comparison. The .NET Framework version used ordinary string
    /// equality; comparing signatures byte by byte leaks where the first difference is.
    /// Safe to tighten here because the value being compared is our own recomputation.
    /// </summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        // Hand-rolled rather than CryptographicOperations.FixedTimeEquals, which does not
        // exist on netstandard2.0 and therefore not on the .NET Framework host either.
        // Compares every byte regardless of where the first difference is, so the time
        // taken does not reveal how much of a forged signature was correct.
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        if (ba.Length != bb.Length) return false;

        var diff = 0;
        for (var i = 0; i < ba.Length; i++) diff |= ba[i] ^ bb[i];
        return diff == 0;
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(s);
    }
}
