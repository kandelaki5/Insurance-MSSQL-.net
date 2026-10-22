using System.Data;
using Dapper;

namespace Insurance.Application.Data;

/// <summary>Type handling shared by every query: DateOnly for SQL date columns.</summary>
public static class DapperSetup
{
    private static int _done;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _done, 1) == 1) return;
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyHandler());
    }

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value.ToDateTime(TimeOnly.MinValue);
        }

        public override DateOnly Parse(object value) => value switch
        {
            DateOnly d => d,
            DateTime t => DateOnly.FromDateTime(t),
            _ => DateOnly.Parse((string)value, System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private sealed class NullableDateOnlyHandler : SqlMapper.TypeHandler<DateOnly?>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly? value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value is { } d ? d.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
        }

        public override DateOnly? Parse(object value) => value is null or DBNull ? null : value switch
        {
            DateOnly d => d,
            DateTime t => DateOnly.FromDateTime(t),
            _ => DateOnly.Parse((string)value, System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
