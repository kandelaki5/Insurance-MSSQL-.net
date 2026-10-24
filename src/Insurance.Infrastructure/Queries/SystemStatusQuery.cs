using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Ports;

namespace Insurance.Infrastructure.Queries;

/// <summary>Reads the migration journal through the application's own login (core_app reads it, cannot change it).</summary>
public sealed class SystemStatusQuery(Db db) : ISystemStatusQuery
{
    public Task<SystemStatus> ReadAsync(CancellationToken ct = default) =>
        db.ReadAsync(c => c.QuerySingleAsync<SystemStatus>(new CommandDefinition(
            "select count(*) as Migrations, max(name) as LastMigration from core.schema_migration", cancellationToken: ct)), ct);
}
