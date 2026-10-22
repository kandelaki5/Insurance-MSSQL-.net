using Dapper;
using Microsoft.Data.SqlClient;

namespace Insurance.Application.Data;

/// <summary>One open transaction: the connection and the transaction travel together to every query.</summary>
/// <remarks>A read outside a transaction uses a Tx without one (Db.ReadTxAsync): each statement then sees what was committed when it started.</remarks>
public sealed class Tx(SqlConnection connection, SqlTransaction? transaction)
{
    public SqlConnection Connection { get; } = connection;
    public SqlTransaction? Transaction { get; } = transaction;

    public Task<int> ExecuteAsync(string sql, object? param = null) =>
        Connection.ExecuteAsync(sql, param, Transaction);

    public Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null) =>
        Connection.QueryAsync<T>(sql, param, Transaction);

    public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null) =>
        Connection.QuerySingleOrDefaultAsync<T>(sql, param, Transaction);

    public Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? param = null) =>
        Connection.QueryFirstOrDefaultAsync<T>(sql, param, Transaction);

    public Task<T?> ExecuteScalarAsync<T>(string sql, object? param = null) =>
        Connection.ExecuteScalarAsync<T>(sql, param, Transaction);

    /// <summary>A transaction-scoped named lock (legacy pg_advisory_xact_lock(hashtext(a), hashtext(b))).</summary>
    public Task LockAsync(string name, string? key = null, bool shared = false) =>
        ExecuteAsync("exec core.xact_lock @resource, @mode",
            new { resource = key is null ? name : $"{name}:{key.ToLowerInvariant()}", mode = shared ? "Shared" : "Exclusive" });

    /// <summary>The next number of a series (core.next_number); null when the series does not exist.</summary>
    public async Task<string?> NextNumberAsync(string series)
    {
        var p = new DynamicParameters();
        p.Add("seq", series);
        p.Add("number", dbType: System.Data.DbType.String, size: 200, direction: System.Data.ParameterDirection.Output);
        await Connection.ExecuteAsync("core.next_number", p, Transaction, commandType: System.Data.CommandType.StoredProcedure);
        return p.Get<string?>("number");
    }
}
