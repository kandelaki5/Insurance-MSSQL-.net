namespace Insurance.Application.Ports;

/// <summary>The database as the program sees it: reachable, and the last migration applied (the health check, ADR 0005).</summary>
public interface ISystemStatusQuery
{
    Task<SystemStatus> ReadAsync(CancellationToken ct = default);
}

/// <param name="Migrations">How many migration files are recorded in core.schema_migration.</param>
/// <param name="LastMigration">The last of them by name (they are applied in name order).</param>
public sealed record SystemStatus(int Migrations, string? LastMigration);
