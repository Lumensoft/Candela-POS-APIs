using Candela.Shared.Logging;
using Microsoft.Data.SqlClient;

namespace Candela.Platform.Data;

/// <summary>
/// Hands out connections for one configured database.
///
/// Construction is deliberately cheap and never throws: the factory is resolved on every
/// request, including ones about to be rejected with a 401, so a missing connection
/// string must surface as a clear startup message rather than a 500 on a request that
/// was never going to reach SQL. <see cref="Validate"/> does that at startup.
/// </summary>
public sealed class SqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    private readonly string _connectionString = connectionString ?? "";

    public SqlConnection Create() => new(_connectionString);

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var con = new SqlConnection(_connectionString);
        try
        {
            await con.OpenAsync(ct).ConfigureAwait(false);
            return con;
        }
        catch
        {
            await con.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Startup checks worth shouting about.
    ///
    /// The important one: Microsoft.Data.SqlClient encrypts by default, while
    /// System.Data.SqlClient — which the legacy host uses — does not. Carrying a working
    /// connection string across unchanged therefore fails with a certificate error
    /// against any server using a self-signed certificate, which is most shop servers.
    ///
    /// TrustServerCertificate is NOT added silently: that would quietly weaken every
    /// deployment. Say exactly what to add instead, on every start, until an operator
    /// decides.
    /// </summary>
    public static void Validate(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            AppLog.Error(null,
                "CONFIG: connection string CON_STR is empty. Every request that touches the " +
                "database will fail. Set it in appsettings.Production.json or the environment.");
            return;
        }

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);

            var encryptStated = connectionString.Contains("Encrypt", StringComparison.OrdinalIgnoreCase);
            var trustStated = connectionString.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase);

            if (!encryptStated && !trustStated)
            {
                AppLog.Error(null,
                    "CONFIG: the connection string sets neither Encrypt nor TrustServerCertificate. " +
                    "Microsoft.Data.SqlClient encrypts by default (System.Data.SqlClient did not), so this " +
                    "will fail against a server with a self-signed certificate. Add TrustServerCertificate=True " +
                    "for a LAN server with its own certificate, or Encrypt=False to keep the previous behaviour.");
            }
            else if (builder.Encrypt && !builder.TrustServerCertificate)
            {
                AppLog.Info("SQL connections are encrypted and the server certificate is being validated.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("CONFIG: could not parse the connection string: {0}", ex.Message);
        }
    }
}
