using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Insurance.Tools;

/// <summary>
/// A read-only fingerprint of every table of schema core (legacy scripts/history-fingerprint.ts and recovery-observer.ts: the
/// same rows before and after an update, or in a database and in its restored copy): per table its row count and a SHA-256
/// over all its rows and columns that does not depend on the order the rows are read in.
///
/// Each row is written as one canonical text by SQL Server itself — the row as a JSON object of all its columns in their
/// defined order, nulls included (FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES: exact decimals, ISO dates with
/// their offsets, uniqueidentifiers and binaries as text) — and hashed there with SHA-256 (HASHBYTES, UTF-16 of the text). The
/// table's fingerprint is the SHA-256 of its row hashes sorted bytewise, so two tables with the same rows, duplicates counted,
/// in any order and any physical layout, have the same fingerprint, and any changed column of any row changes it.
/// CHECKSUM_AGG(BINARY_CHECKSUM(*)) was not taken: it is 32 bits, its aggregate cancels a pair of equal rows out, and
/// BINARY_CHECKSUM leaves some column types out, so a changed table can keep its checksum.
///
/// It reads only: one snapshot transaction (a consistent view of all tables, nothing locked), committed with nothing written.
/// No column value is printed, only counts and hashes.
/// </summary>
public static class Fingerprint
{
    public sealed record Table(string Name, long Rows, string Sha256);

    public static async Task<IReadOnlyList<Table>> ReadAsync(string connection)
    {
        await using var db = new SqlConnection(connection);
        await db.OpenAsync();
        await using var transaction = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Snapshot);
        var columns = (await db.QueryAsync<(string Table, string Column)>("""
            select t.name, c.name from sys.tables t join sys.columns c on c.object_id = t.object_id
            where t.schema_id = schema_id(N'core') order by t.name, c.column_id
            """, transaction: transaction)).ToList();
        var tables = new List<Table>();
        foreach (var table in columns.Select(c => c.Table).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var list = string.Join(", ", columns.Where(c => c.Table == table).Select(c => $"t.{Quote(c.Column)} as {Quote(c.Column)}"));
            var hashes = (await db.QueryAsync<byte[]>(
                $"select hashbytes('SHA2_256', (select {list} for json path, without_array_wrapper, include_null_values)) from core.{Quote(table)} t",
                transaction: transaction, commandTimeout: 600)).ToList();
            hashes.Sort(static (a, b) => a.AsSpan().SequenceCompareTo(b));
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var h in hashes) sha.AppendData(h);
            tables.Add(new Table(table, hashes.Count, Convert.ToHexStringLower(sha.GetHashAndReset())));
        }
        await transaction.CommitAsync();
        return tables;
    }

    private static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>One line per table — <c>core.name rows sha256</c> — and a last line over all of them.</summary>
    public static IEnumerable<string> Lines(IReadOnlyList<Table> tables)
    {
        var lines = tables.Select(t => $"core.{t.Name} {t.Rows.ToString(CultureInfo.InvariantCulture)} {t.Sha256}").ToList();
        var all = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines))));
        return [.. lines, $"ALL {tables.Count.ToString(CultureInfo.InvariantCulture)} tables {tables.Sum(t => t.Rows).ToString(CultureInfo.InvariantCulture)} rows {all}"];
    }
}
