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
}
