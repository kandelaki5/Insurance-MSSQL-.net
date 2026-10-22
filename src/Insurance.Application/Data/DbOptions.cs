namespace Insurance.Application.Data;

/// <summary>
/// Connection settings. The application connects as a member of role core_app; migrations and imports
/// use a separate administrative connection. Both come from configuration (environment variables or
/// user secrets), never from code: ConnectionStrings:Insurance and ConnectionStrings:InsuranceAdmin.
/// </summary>
public sealed class DbOptions
{
    public required string ConnectionString { get; init; }
}
