using System;
using System.Configuration;
using CandelaPOS.Shared.Logging;

namespace CandelaPOS.Shared.Config
{
    /// <summary>
    /// Startup sanity checks on configuration that is dangerous to get wrong.
    ///
    /// These warn rather than throw. A shop till that refuses to start is worse than
    /// one running on a weak key — but the operator needs to see it, loudly, on every
    /// restart until it is fixed.
    /// </summary>
    public static class ConfigChecks
    {
        /// <summary>
        /// The JWT signing key that shipped in source control. Anyone with the repo can
        /// mint a token for any user, shop or till, so it must be replaced and the old
        /// one treated as compromised.
        /// </summary>
        private const string LeakedJwtSecret = "CandelaPOS@2026#SecretKey!XYZ789_lumen";

        public static void Run()
        {
            CheckJwtSecret();
            CheckSqlAccount();
            CheckDebugCompilation();
        }

        private static void CheckJwtSecret()
        {
            var secret = ConfigurationManager.AppSettings["Jwt:Secret"] ?? "";

            if (string.IsNullOrWhiteSpace(secret))
            {
                AppLog.Error(null, "CONFIG: Jwt:Secret is not set. Every token will fail to validate.");
                return;
            }
            if (string.Equals(secret, LeakedJwtSecret, StringComparison.Ordinal))
            {
                AppLog.Error(null,
                    "CONFIG: Jwt:Secret is still the value committed to source control. " +
                    "Anyone with the repository can mint a token for any user or shop. " +
                    "Generate a new 32+ character key and put it in App_Data/secrets.config.");
            }
            else if (secret.Length < 32)
            {
                AppLog.Warn("CONFIG: Jwt:Secret is only {0} characters. HMAC-SHA256 wants at least 32.",
                    secret.Length);
            }
        }

        private static void CheckSqlAccount()
        {
            try
            {
                var cs = ConfigurationManager.ConnectionStrings["CON_STR"];
                if (cs == null) { AppLog.Error(null, "CONFIG: connection string CON_STR is missing."); return; }

                var v = cs.ConnectionString ?? "";
                if (v.IndexOf("User ID=sa", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    v.IndexOf("User Id=sa", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    v.IndexOf("uid=sa",     StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    AppLog.Warn("CONFIG: the POS API is connecting to SQL Server as 'sa'. " +
                                "Use a dedicated least-privilege login instead — a SQL injection " +
                                "or a stolen config becomes total server compromise with sa.");
                }
            }
            catch (Exception ex) { AppLog.Warn("CONFIG: could not inspect CON_STR: {0}", ex.Message); }
        }

        private static void CheckDebugCompilation()
        {
            try
            {
                var section = ConfigurationManager.GetSection("system.web/compilation")
                              as System.Web.Configuration.CompilationSection;
                if (section != null && section.Debug)
                {
                    AppLog.Warn("CONFIG: <compilation debug=\"true\"> is set. This leaks stack traces, " +
                                "disables request timeouts and slows every page. Turn it off in production.");
                }
            }
            catch { }
        }
    }
}
