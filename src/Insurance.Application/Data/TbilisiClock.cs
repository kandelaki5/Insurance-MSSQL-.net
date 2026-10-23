namespace Insurance.Application.Data;

/// <summary>
/// The calendar day in Tbilisi of an instant (legacy todayInTbilisi). The application's "today" is
/// <see cref="Db.Today"/> / <see cref="Tx.Today"/>: this conversion applied to the clock of its <see cref="DbOptions"/>.
/// </summary>
public static class TbilisiClock
{
    public static DateOnly Day(DateTimeOffset instant) => Insurance.Domain.Common.TbilisiTime.Date(instant);
}
