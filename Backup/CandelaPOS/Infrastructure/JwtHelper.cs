using System;
using System.Collections.Generic;
using System.Configuration;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

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

        private static SymmetricSecurityKey SigningKey =>
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret));

        public static string Generate(int userId, string userName, int shopId, string deviceId)
        {
            var claims = new List<Claim>
            {
                new Claim("user_id",   userId.ToString()),
                new Claim("user_name", userName),
                new Claim("shop_id",   shopId.ToString()),
                new Claim("device_id", deviceId),
            };

            var token = new JwtSecurityToken(
                issuer:   Issuer,
                audience: Issuer,
                claims:   claims,
                expires:  DateTime.UtcNow.AddHours(ExpiryHours),
                signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>
        /// Returns the claims principal if the token is valid; null otherwise.
        /// </summary>
        public static ClaimsPrincipal Validate(string token)
        {
            try
            {
                var handler = new JwtSecurityTokenHandler();
                var principal = handler.ValidateToken(token, new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey         = SigningKey,
                    ValidateIssuer           = true,
                    ValidIssuer              = Issuer,
                    ValidateAudience         = true,
                    ValidAudience            = Issuer,
                    ValidateLifetime         = true,
                    ClockSkew                = TimeSpan.FromMinutes(1)
                }, out _);
                return principal;
            }
            catch
            {
                return null;
            }
        }

        public static int GetUserId(ClaimsPrincipal principal) =>
            int.Parse(principal.FindFirst("user_id")?.Value ?? "0");

        public static int GetShopId(ClaimsPrincipal principal) =>
            int.Parse(principal.FindFirst("shop_id")?.Value ?? "0");

        public static string GetDeviceId(ClaimsPrincipal principal) =>
            principal.FindFirst("device_id")?.Value ?? "";

        public static string GetUserName(ClaimsPrincipal principal) =>
            principal.FindFirst("user_name")?.Value ?? "";
    }
}
