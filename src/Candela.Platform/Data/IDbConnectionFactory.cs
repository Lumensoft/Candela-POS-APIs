using Microsoft.Data.SqlClient;

namespace Candela.Platform.Data;

/// <summary>
/// Creates connections. Note it is a factory, not a shared connection: each call hands
/// back a new one for the caller to dispose.
///
/// That is the opposite of the Candela DAL, where the connection string lives in a
/// process-wide static (SQLHelper.CON_STR) — the thing that makes it unsafe to serve
/// more than one shop database from one process.
///
/// Most code should use <see cref="IDb"/> rather than this. Take a connection directly
/// only when you genuinely need to hold one open across several statements.
/// </summary>
public interface IDbConnectionFactory
{
    SqlConnection Create();
    Task<SqlConnection> OpenAsync(CancellationToken ct = default);
}
