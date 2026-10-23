using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Compliance;
using Insurance.Integration.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace Insurance.Web.Tests;

/// <summary>
/// The test clock seam in the web application (docs/DEVELOPMENT.md "Test clock"): the application as configured runs on the system
/// clock; the test hosts give it the test database's clock, and the pages read "today" and "now" from it (the Db they already have).
/// </summary>
public sealed class ClockWiringTests(WebApp app) : IClassFixture<WebApp>
{
    [Fact]
    public void The_application_as_configured_uses_the_system_clock()
    {
        var program = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Insurance", "Server=localhost;Database=not_used;User ID=x;Password=x");
        });
        using (program)
        {
            Assert.Same(TimeProvider.System, program.Services.GetRequiredService<DbOptions>().Clock);
            Assert.Same(TimeProvider.System, program.Services.GetRequiredService<Db>().Clock);
        }
        // The test host's application carries the test database's clock instead.
        Assert.Same(app.Db.Clock, app.Factory.Services.GetRequiredService<Db>().Clock);
    }

    [Fact]
    public async Task A_form_shows_the_clocks_day_and_the_real_day_again_once_released()
    {
        var (client, _) = await app.SignedInAsync("admin");
        try
        {
            app.Db.Clock.SetToday(new DateOnly(2027, 2, 3));
            Assert.Contains("name=\"received_on\" value=\"2027-02-03\"", await Html.GetAsync(client, "/complaints/new"));
        }
        finally
        {
            app.Db.Clock.Release();
        }
        Assert.Contains($"name=\"received_on\" value=\"{Insurance.Web.Infrastructure.Fmt.Iso(Rates.TodayInTbilisi())}\"", await Html.GetAsync(client, "/complaints/new"));
    }

    [Fact]
    public async Task The_home_pages_unconfirmed_oral_instruction_counts_its_24_hours_on_the_clock()
    {
        var (accountant, _) = await app.SignedInAsync("accountant");
        var party = Guid.NewGuid();
        // An oral instruction of 07.11.2026 10:00 (after the real day): by the SQL server's own clock it would never be past its 24 hours here.
        var instructed = new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.FromHours(4));
        await using (var c = await app.Db.OpenAdminAsync())
            await c.ExecuteAsync("""
                insert core.party (id, kind, first_name, last_name, tax_id, risk_level) values (@party, N'person', N'ა', N'საათი', @taxId, N'low');
                insert core.fms_suspension (id, party_id, kind, scope, instructed_at, ends_at) values (newid(), @party, N'oral', N'all', @instructed, @ends);
                """, new { party, taxId = Fixtures.RandomTaxId(), instructed, ends = Suspension.SuspensionEnd(instructed) });
        try
        {
            app.Db.Clock.Set(instructed.AddHours(23));
            Assert.DoesNotContain(AmlTexts.Suspension.UnconfirmedReminder, await Html.GetAsync(accountant, "/"));
            app.Db.Clock.Set(instructed.AddHours(25));
            Assert.Contains($"<strong>1</strong> {AmlTexts.Suspension.UnconfirmedReminder}", await Html.GetAsync(accountant, "/"));
            // The suspension form on the client's card proposes the clock's Tbilisi time.
            Assert.Contains("name=\"instructed_at\" type=\"datetime-local\" value=\"2026-11-08T11:00\"", await Html.GetAsync(accountant, $"/parties/{party}"));
        }
        finally
        {
            app.Db.Clock.Release();
        }
    }
}
