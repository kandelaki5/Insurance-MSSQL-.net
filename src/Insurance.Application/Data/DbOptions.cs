namespace Insurance.Application.Data;

/// <summary>
/// Connection settings. The application connects as a member of role core_app; migrations and imports
/// use a separate administrative connection. Both come from configuration (environment variables or
/// user secrets), never from code: ConnectionStrings:Insurance and ConnectionStrings:InsuranceAdmin.
/// </summary>
public sealed class DbOptions
{
    public required string ConnectionString { get; init; }

    /// <summary>
    /// Tests only: called before every command on the connections this Db opens (see <see cref="ICommandInterceptor"/>), to
    /// make one read fail or to change something between two statements. Null in production — Program.cs and the tools never set it.
    /// </summary>
    public ICommandInterceptor? Interceptor { get; init; }

    /// <summary>
    /// Where "now" and Tbilisi's "today" come from (<see cref="Db.Now"/>, <see cref="Db.Today"/>, <see cref="Tx.Today"/>). The system
    /// clock in production — Program.cs and the tools never set it; a test sets a clock of its own to run an action on a given day.
    /// SQL stamps (created_at, audit, closed_at …) stay the database server's real time.
    /// </summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;
}
