using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Insurance.Application.Data;

/// <summary>
/// A database refusal in the terms the legacy code used: the PostgreSQL SQLSTATE or the custom code of a
/// guard, and the constraint name when a constraint refused. The schema's guards raise "[CODE] message"
/// (error 51000); SQL Server's own unique, check and foreign-key errors are mapped to 23505, 23514, 23503.
/// </summary>
public sealed partial record DbError(string Code, string? Constraint, string Message)
{
    public const string UniqueViolation = "23505";
    public const string CheckViolation = "23514";
    public const string ForeignKeyViolation = "23503";

    /// <summary>The refusal carried by <paramref name="exception"/> (or one of its inner exceptions), or null.</summary>
    public static DbError? From(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
            if (e is SqlException sql)
                return FromSql(sql);
        return null;
    }

    public static DbError? FromSql(SqlException exception)
    {
        foreach (SqlError error in exception.Errors)
        {
            switch (error.Number)
            {
                case 51000:
                    var m = GuardMessage().Match(error.Message);
                    if (m.Success) return new DbError(m.Groups[1].Value, null, m.Groups[2].Value);
                    break;
                case 2627: // unique constraint
                case 2601: // unique index
                    return new DbError(UniqueViolation, ObjectName().Match(error.Message) is { Success: true } u ? u.Groups[1].Value : null, error.Message);
                case 547:
                    var c = ConflictName().Match(error.Message);
                    var kind = error.Message.Contains("FOREIGN KEY", StringComparison.Ordinal) || error.Message.Contains("REFERENCE", StringComparison.Ordinal)
                        ? ForeignKeyViolation : CheckViolation;
                    return new DbError(kind, c.Success ? c.Groups[1].Value : null, error.Message);
                case 515: // null into not-null column
                    return new DbError("23502", null, error.Message);
                case 1205: // deadlock victim
                    return new DbError("40P01", null, error.Message);
            }
        }
        return null;
    }

    [GeneratedRegex(@"^\[([0-9A-Z]{5})\] (.*)$", RegexOptions.Singleline)]
    private static partial Regex GuardMessage();

    [GeneratedRegex("(?:constraint|index) '([^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex ObjectName();

    [GeneratedRegex("constraint \"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ConflictName();
}
