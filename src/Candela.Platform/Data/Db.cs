using System.Data;
using Dapper;

namespace Candela.Platform.Data;

/// <summary>
/// Dapper plus a consistent connection lifetime and timeout. See <see cref="IDb"/> for
/// why this exists and where its boundary is.
/// </summary>
public sealed class Db(IDbConnectionFactory factory) : IDb
{
    /// <summary>
    /// 30 seconds, not the 1800 the Candela DAL uses. A web request that has been waiting
    /// half an hour has no caller left to answer; anything genuinely that slow belongs in
    /// a background job, not in a request.
    /// </summary>
    private const int DefaultTimeoutSeconds = 30;

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? param = null,
        CancellationToken ct = default, int? timeoutSeconds = null)
    {
        await using var con = await factory.OpenAsync(ct);
        var rows = await con.QueryAsync<T>(Command(sql, param, ct, timeoutSeconds));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> QueryRowsAsync(string sql,
        object? param = null, CancellationToken ct = default, int? timeoutSeconds = null)
    {
        await using var con = await factory.OpenAsync(ct);
        var rows = await con.QueryAsync(Command(sql, param, ct, timeoutSeconds));

        // Dapper hands back DapperRow, which is already an IDictionary keyed by column
        // name with DBNull flattened to null. Copying into a plain Dictionary keeps the
        // result serialisable and detached from Dapper.
        return rows
            .Cast<IDictionary<string, object?>>()
            .Select(r => new Dictionary<string, object?>(r))
            .ToList();
    }

    public async Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null,
        CancellationToken ct = default, int? timeoutSeconds = null)
    {
        await using var con = await factory.OpenAsync(ct);
        return await con.QuerySingleOrDefaultAsync<T>(Command(sql, param, ct, timeoutSeconds));
    }

    public async Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? param = null,
        CancellationToken ct = default, int? timeoutSeconds = null)
    {
        await using var con = await factory.OpenAsync(ct);
        return await con.QueryFirstOrDefaultAsync<T>(Command(sql, param, ct, timeoutSeconds));
    }

    public async Task<T?> ExecuteScalarAsync<T>(string sql, object? param = null,
        CancellationToken ct = default, int? timeoutSeconds = null)
    {
        await using var con = await factory.OpenAsync(ct);
        return await con.ExecuteScalarAsync<T>(Command(sql, param, ct, timeoutSeconds));
    }

    public async Task<int> ExecuteAsync(string sql, object? param = null,
        CancellationToken ct = default, int? timeoutSeconds = null)
    {
        await using var con = await factory.OpenAsync(ct);
        return await con.ExecuteAsync(Command(sql, param, ct, timeoutSeconds));
    }

    public async Task<IReadOnlyList<T>> QueryProcAsync<T>(string procedureName, object? param = null,
        CancellationToken ct = default, int? timeoutSeconds = null)
    {
        await using var con = await factory.OpenAsync(ct);
        var rows = await con.QueryAsync<T>(
            Command(procedureName, param, ct, timeoutSeconds, CommandType.StoredProcedure));
        return rows.AsList();
    }

    public async Task<int> ExecuteProcAsync(string procedureName, object? param = null,
        CancellationToken ct = default, int? timeoutSeconds = null)
    {
        await using var con = await factory.OpenAsync(ct);
        return await con.ExecuteAsync(
            Command(procedureName, param, ct, timeoutSeconds, CommandType.StoredProcedure));
    }

    public async Task<T> InTransactionAsync<T>(Func<IDbConnection, IDbTransaction, Task<T>> work,
        CancellationToken ct = default)
    {
        await using var con = await factory.OpenAsync(ct);
        using var tx = con.BeginTransaction();
        try
        {
            var result = await work(con, tx);
            tx.Commit();
            return result;
        }
        catch
        {
            // Rolling back can itself fail if the connection is already gone. Swallow that
            // so the original exception is what surfaces — it is the one worth reading.
            try { tx.Rollback(); } catch { }
            throw;
        }
    }

    private static CommandDefinition Command(string sql, object? param, CancellationToken ct,
        int? timeoutSeconds, CommandType? type = null)
        => new(sql, param,
               commandTimeout: timeoutSeconds ?? DefaultTimeoutSeconds,
               commandType: type,
               cancellationToken: ct);
}
