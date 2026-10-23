using System.Globalization;
using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Reference;

public sealed class RatesModel(Rates rates) : AppPageModel
{
    public sealed record Row(DateOnly RateDate, decimal? Usd, decimal? Eur);

    public IReadOnlyList<Row> Rows { get; private set; } = [];
    private string? today;
    public string Today => today ??= Fmt.Iso(Db.Today());

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        KeepTrimmedForm();
        if (!Me.CanWrite) FormError = Ka.Common.ReadOnly;
        else if (!DateOnly.TryParseExact(Form("date") ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                 || !await rates.LoadNbgRatesAsync(date))
            FormError = Ka.Rates.Failed;
        else
            Notice = Ka.Rates.Loaded;
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync() =>
        Rows = (await Db.ReadAsync(c => c.QueryAsync<Row>("""
            select top (60) rate_date as RateDate,
                   max(case when currency = N'USD' then rate end) as Usd,
                   max(case when currency = N'EUR' then rate end) as Eur
            from core.exchange_rate
            group by rate_date
            order by rate_date desc
            """))).ToList();
}
