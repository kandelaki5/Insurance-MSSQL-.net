using System.Globalization;
using System.Net.Http.Json;
using Dapper;
using Insurance.Application.Data;

namespace Insurance.Application.Services;

/// <summary>Official rates of the National Bank of Georgia (legacy src/lib/rates.ts).</summary>
public sealed class Rates(Db db, IHttpClientFactory http)
{
    public const string NbgUrl = "https://nbg.gov.ge/gw/api/ct/monetarypolicy/currencies/ka/json/";

    private sealed record NbgCurrency(string Code, decimal Quantity, decimal Rate);
    private sealed record NbgResponse(List<NbgCurrency>? Currencies);

    /// <summary>Loads the official USD and EUR rates for one date and stores them; false when the bank gives none.</summary>
    public async Task<bool> LoadNbgRatesAsync(DateOnly date)
    {
        List<NbgResponse>? payload;
        try
        {
            var iso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            using var client = http.CreateClient("nbg");
            using var response = await client.GetAsync($"{NbgUrl}?currencies=USD&currencies=EUR&date={iso}");
            if (!response.IsSuccessStatusCode) return false;
            payload = await response.Content.ReadFromJsonAsync<List<NbgResponse>>(new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            return false;
        }
        var rates = (payload?.FirstOrDefault()?.Currencies ?? [])
            .Where(c => c.Code is "USD" or "EUR" && c.Rate > 0 && c.Quantity > 0)
            .Select(c => new { currency = c.Code, rate = Math.Round(c.Rate / c.Quantity, 6, MidpointRounding.AwayFromZero) })
            .ToList();
        if (rates.Count == 0) return false;
        await StoreAsync(date, rates.Select(r => (r.currency, r.rate)));
        return true;
    }

    /// <summary>Stores rates for a date, replacing what was stored for that date and currency (legacy upsert).</summary>
    public Task StoreAsync(DateOnly date, IEnumerable<(string Currency, decimal Rate)> rates) =>
        db.InTransactionAsync(async tx =>
        {
            foreach (var (currency, rate) in rates)
                await tx.ExecuteAsync("""
                    merge core.exchange_rate with (holdlock) as t
                    using (select @currency as currency, @date as rate_date) as s on t.currency = s.currency and t.rate_date = s.rate_date
                    when matched then update set rate = @rate
                    when not matched then insert (currency, rate_date, rate) values (@currency, @date, @rate);
                    """, new { currency, date, rate });
            return 0;
        });

    /// <summary>Official rate (GEL per unit) on a date; fetched from the National Bank when not stored yet.</summary>
    public async Task<decimal?> RateOnAsync(string currency, DateOnly date)
    {
        if (currency == "GEL") return 1m;
        Task<decimal?> Stored() => db.ReadAsync(c => c.QuerySingleOrDefaultAsync<decimal?>(
            "select rate from core.exchange_rate where currency = @currency and rate_date = @date", new { currency, date }));
        var rate = await Stored();
        if (rate is null && await LoadNbgRatesAsync(date)) rate = await Stored();
        return rate;
    }

    /// <summary>
    /// Rate for valuing balances on a date: that day's official rate when published, otherwise the latest earlier one
    /// together with its date, so the caller can say which rate was used.
    /// </summary>
    public async Task<(decimal Rate, DateOnly Date)> LatestRateOnOrBeforeAsync(string currency, DateOnly date)
    {
        if (currency == "GEL") return (1m, date);
        if (await RateOnAsync(currency, date) is { } exact) return (exact, date);
        var row = await db.ReadAsync(c => c.QuerySingleOrDefaultAsync<(decimal Rate, DateOnly RateDate)?>(
            "select top (1) rate, rate_date from core.exchange_rate where currency = @currency and rate_date <= @date order by rate_date desc",
            new { currency, date }));
        return row is { } r ? (r.Rate, r.RateDate) : throw new InvalidOperationException($"No {currency} rate on or before {date:yyyy-MM-dd}");
    }

    /// <summary>
    /// Today's date in Tbilisi by the system clock (or of <paramref name="now"/>). The application never calls it: its "today" is
    /// <see cref="Db.Today"/> / <see cref="Tx.Today"/>, which a test can set (DbOptions.Clock). Kept for tests that compare with the real day.
    /// </summary>
    public static DateOnly TodayInTbilisi(DateTimeOffset? now = null) => TbilisiClock.Day(now ?? DateTimeOffset.UtcNow);
}
