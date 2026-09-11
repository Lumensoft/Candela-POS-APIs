using System.Data;

namespace Candela.Platform.Data;

/// <summary>
/// The one way this API talks to SQL.
///
/// Every method opens a connection, runs the command and disposes it, so no repository
/// repeats connection handling or picks its own timeout. That was the gap worth closing:
/// the .NET Framework host had 87 hand-written `new SqlConnection(...)` blocks, each free
/// to leak, to forget a timeout, or to forget cancellation.
///
/// It deliberately does NOT re-implement Dapper. Query/Execute/Scalar/stored procedures
/// are Dapper's job and it already does them well; this only standardises the parts
/// Dapper leaves to the caller — when the connection opens, when it closes, how long a
/// command may run, and that the cancellation token is always passed through.
///
/// Writes that must keep Candela's SQL log, activity log and inventory posting correct
/// do NOT belong here. Those go through ILegacyHostClient so CityDAL and friends run.
/// </summary>
public interface IDb
{
    /// <summary>Many rows.</summary>
    Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? param = null,
        CancellationToken ct = default, int? timeoutSeconds = null);

    /// <summary>
    /// Rows as dictionaries keyed by column name, for queries whose shape is decided by
    /// the SQL rather than by a class.
    ///
    /// This is the projection SqlDataAdapter.Fill produced in the .NET Framework host, so
    /// endpoints ported from there keep returning byte-identical JSON. Prefer a typed
    /// result for anything new; use this when the column names ARE the contract.
    /// </summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> QueryRowsAsync(string sql, object? param = null,
        CancellationToken ct = default, int? timeoutSeconds = null);

    /// <summary>One row, or null when there is none. Throws if the query returns more than one.</summary>
    Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null,
        CancellationToken ct = default, int? timeoutSeconds = null);

    /// <summary>
    /// The first row, or null when there are none. Does NOT throw on more than one.
    ///
    /// This is the exact semantic of the .NET Framework pattern
    /// <c>dt.Rows.Count == 0 ? default : dt.Rows[0]</c> — which appears all over the
    /// Candela DAL — so ported endpoints must use this, not QuerySingleOrDefault, or a
    /// query that legacy quietly took row[0] from becomes a 500.
    /// </summary>
    Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? param = null,
        CancellationToken ct = default, int? timeoutSeconds = null);

    /// <summary>A single value — COUNT, MAX, an identity, an EXISTS check.</summary>
    Task<T?> ExecuteScalarAsync<T>(string sql, object? param = null,
        CancellationToken ct = default, int? timeoutSeconds = null);

    /// <summary>A statement with no result set. Returns rows affected.</summary>
    Task<int> ExecuteAsync(string sql, object? param = null,
        CancellationToken ct = default, int? timeoutSeconds = null);

    /// <summary>Many rows from a stored procedure. Candela has a lot of these.</summary>
    Task<IReadOnlyList<T>> QueryProcAsync<T>(string procedureName, object? param = null,
        CancellationToken ct = default, int? timeoutSeconds = null);

    /// <summary>A stored procedure with no result set. Returns rows affected.</summary>
    Task<int> ExecuteProcAsync(string procedureName, object? param = null,
        CancellationToken ct = default, int? timeoutSeconds = null);

    /// <summary>
    /// Several statements that must succeed or fail together, inside one transaction.
    ///
    /// Use sparingly. If the work needs Candela's SQL log or inventory posting it belongs
    /// in the legacy host, not here — this is for writes that genuinely stand alone, such
    /// as the API's own bookkeeping tables.
    /// </summary>
    Task<T> InTransactionAsync<T>(Func<IDbConnection, IDbTransaction, Task<T>> work,
        CancellationToken ct = default);
}
