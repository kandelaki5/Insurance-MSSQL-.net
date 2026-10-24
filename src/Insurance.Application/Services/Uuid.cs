using System.Text.RegularExpressions;

namespace Insurance.Application.Services;

/// <summary>
/// An id as the legacy actions accepted it from a form or the address bar: exactly the 8-4-4-4-12 hexadecimal form
/// (legacy UUID regex, either case). Anything else — braces, no dashes, spaces — is not an id, where Guid.TryParse
/// would accept it.
/// </summary>
public static partial class Uuid
{
    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z")]
    private static partial Regex Pattern();

    public static bool IsUuid(string? value) => value is not null && Pattern().IsMatch(value);

    public static bool TryParse(string? value, out Guid id)
    {
        id = Guid.Empty;
        return IsUuid(value) && Guid.TryParse(value, out id);
    }

    /// <summary>The id as PostgreSQL printed a uuid: lower case, with dashes (the order the legacy code sorted ids in).</summary>
    public static string Text(Guid id) => id.ToString("D");
}
