using System;
using System.Collections.Generic;
using System.Configuration;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace CandelaPOS.Infrastructure
{
    public static class JwtHelper
    {
        private static string Secret =>
            ConfigurationManager.AppSettings["Jwt:Secret"];

        private static int ExpiryHours =>
            int.Parse(ConfigurationManager.AppSettings["Jwt:ExpiryHours"] ?? "24");

        private static string Issuer =>
            ConfigurationManager.AppSettings["Jwt:Issuer"] ?? "CandelaPOS";

        public static string Generate(int userId, string userName, int shopId, string posCode, string deviceId)
        {
            var now = DateTimeOffset.UtcNow;
            var payload = new Dictionary<string, object>
            {
                { "iss",       Issuer },
                { "aud",       Issuer },
                { "iat",       now.ToUnixTimeSeconds() },
                { "exp",       now.AddHours(ExpiryHours).ToUnixTimeSeconds() },
                { "user_id",   userId },
                { "user_name", userName },
                { "shop_id",   shopId },
                { "pos_code",  posCode },
                { "device_id", deviceId }
            };

            string header  = Base64UrlEncode(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
            string body    = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload)));
            string signing = header + "." + body;
            string sig     = Sign(signing);

            return signing + "." + sig;
        }

        public static ClaimsPrincipal Validate(string token)
        {
            try
            {
                var parts = token.Split('.');
                if (parts.Length != 3) return null;

                string signing = parts[0] + "." + parts[1];
                if (Sign(signing) != parts[2]) return null;

                string json    = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
                var payload    = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);

                long exp = Convert.ToInt64(payload["exp"]);
                if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return null;

                if (payload["iss"].ToString() != Issuer) return null;

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

        // Returns the signature segment of a raw JWT string, or "" on malformed input.
        public static string ExtractSignature(string rawToken)
        {
            if (string.IsNullOrEmpty(rawToken)) return "";
            var parts = rawToken.Split('.');
            return parts.Length == 3 ? parts[2] : "";
        }

        // Returns the exp claim as a UTC DateTime, or DateTime.MinValue on failure.
        public static DateTime ExtractExpiry(string rawToken)
        {
            try
            {
                var parts = rawToken.Split('.');
                if (parts.Length != 3) return DateTime.MinValue;
                string json    = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
                var payload    = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                long exp       = Convert.ToInt64(payload["exp"]);
                return DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
            }
            catch { return DateTime.MinValue; }
        }

        public static int    GetUserId(ClaimsPrincipal p)   => int.Parse(p.FindFirst("user_id")?.Value   ?? "0");
        public static int    GetShopId(ClaimsPrincipal p)   => int.Parse(p.FindFirst("shop_id")?.Value   ?? "0");
        public static string GetPosCode(ClaimsPrincipal p)  => p.FindFirst("pos_code")?.Value  ?? "POS";
        public static string GetDeviceId(ClaimsPrincipal p) => p.FindFirst("device_id")?.Value ?? "";
        public static string GetUserName(ClaimsPrincipal p) => p.FindFirst("user_name")?.Value ?? "";

        private static string Sign(string input)
        {
            var key = Encoding.UTF8.GetBytes(Secret);
            using (var hmac = new HMACSHA256(key))
            {
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
                return Base64UrlEncode(hash);
            }
        }

        private static string Base64UrlEncode(byte[] data) =>
            Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] Base64UrlDecode(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "=";  break;
            }
            return Convert.FromBase64String(s);
        }
    }
}
