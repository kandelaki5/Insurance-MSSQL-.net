using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Domain.Compliance;

namespace Insurance.Integration.Tests;

/// <summary>
/// Reusing a dismissed false positive on an unchanged re-screening (owner, 23.10.2026; migration 0010), through the service on
/// the application's own login. The same subject with the same screening data, against the same list (date and content), with
/// the same hits, is closed by the earlier reasoned dismissal and points to it; any change asks for a new review; a reuse never
/// lifts a sanctions block or an FMS suspension; the database refuses a reuse that does not fit.
/// </summary>
public sealed class SanctionsReuseTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private const string SecondZurab = """
        <INDIVIDUAL>
          <DATAID>2</DATAID>
          <FIRST_NAME>Zurab</FIRST_NAME>
          <SECOND_NAME>Sankcioneli</SECOND_NAME>
          <UN_LIST_TYPE>Test</UN_LIST_TYPE>
          <REFERENCE_NUMBER>T.002</REFERENCE_NUMBER>
          <INDIVIDUAL_ALIAS><QUALITY>Good</QUALITY><ALIAS_NAME>Zurabi Sanktsioneli</ALIAS_NAME></INDIVIDUAL_ALIAS>
        </INDIVIDUAL>
        """;

    private async Task LoadAsync(string generated = "2026-10-02T23:00:05.487Z", string extra = "", string entityReference = "CDe.001")
    {
        var file = ComplianceFixtures.UnListFile(generated, extra, entityReference);
        try
        {
            using var http = new HttpClient();
            Assert.True((await SanctionsScreening.LoadUnListAsync(db.App, http, new Uri(file).AbsoluteUri)).Ok);
        }
        finally { File.Delete(file); }
    }

    private async Task AdminAsync(string sql, object? param = null)
    {
        await using var c = await db.OpenAdminAsync();
        await c.ExecuteAsync(sql, param);
    }

    private async Task<T> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var c = await db.OpenAdminAsync();
        return (await c.ExecuteScalarAsync<T>(sql, param))!;
    }

    /// <summary>A client the list's entry 1 matches (name and birth year).</summary>
    private async Task<Guid> HitAsync(string birth = "1980-05-01")
    {
        var id = Guid.NewGuid();
        await AdminAsync("insert core.party (id, kind, first_name, last_name, tax_id, birth_date) values (@id, N'person', N'ზურაბ', N'სანქციონელი', @tax, @birth)",
            new { id, tax = Fixtures.RandomTaxId(), birth });
        return id;
    }

    private Task<SanctionsScreening.ScreeningResult> ScreenAsync(Guid actor, Guid party) =>
        db.App.AsUserAsync(actor, tx => SanctionsScreening.ScreenPartiesAsync(tx, actor, party));

    private Task DecideAsync(Guid actor, Guid id, string decision, string note = "სხვა პირი: სხვა პირადი ნომერი") =>
        db.App.AsUserAsync(actor, async tx => { await SanctionsScreening.DecideAsync(tx, actor, id, decision, note); return 0; });

    private sealed record Row(Guid Id, string Outcome, Guid? ReusesScreeningId, string? Decision, Guid? DecidedBy, string? Note, string Matches);

    private async Task<Row> LatestAsync(Guid subject)
    {
        await using var c = await db.OpenAdminAsync();
        return await c.QuerySingleAsync<Row>("""
            select top (1) id as Id, outcome as Outcome, reuses_screening_id as ReusesScreeningId, decision as Decision, decided_by as DecidedBy, note as Note, matches as Matches
            from core.sanctions_screening where subject_id = @subject order by screened_at desc, id
            """, new { subject });
    }

    /// <summary>Screened once and the hit dismissed with a reason: the decision a re-screening may reuse.</summary>
    private async Task<(Guid Actor, Guid Party, Row Decision)> DismissedAsync(string birth = "1980-05-01")
    {
        var actor = await Fixtures.UserAsync(db, "accountant");
        var party = await HitAsync(birth);
        Assert.Equal(1, (await ScreenAsync(actor, party)).Hits);
        var first = await LatestAsync(party);
        Assert.Equal("possible", first.Outcome);
        await DecideAsync(actor, first.Id, "dismiss");
        return (actor, party, await LatestAsync(party));
    }

    [Fact]
    public async Task An_unchanged_rescreening_reuses_the_dismissal_points_to_it_and_leaves_it_as_it_was()
    {
        await LoadAsync();
        var (actor, party, decision) = await DismissedAsync();
        Assert.Equal(("false_positive", "dismiss"), (decision.Outcome, decision.Decision));

        var other = await Fixtures.UserAsync(db, "accountant");
        var result = await ScreenAsync(other, party);
        Assert.Equal((1, 0, 1), (result.Screened, result.Hits, result.Reused));
        var second = await LatestAsync(party);
        Assert.Equal(("false_positive", (Guid?)decision.Id, (string?)null, (Guid?)null, (string?)null), (second.Outcome, second.ReusesScreeningId, second.Decision, second.DecidedBy, second.Note));
        Assert.Equal(decision.Matches, second.Matches);
        // The third time it points to the decision itself, not to the second row; the decision row is untouched.
        await ScreenAsync(actor, party);
        Assert.Equal(decision.Id, (await LatestAsync(party)).ReusesScreeningId);
        await using var c = await db.OpenAdminAsync();
        Assert.Equal((decision.Outcome, decision.Note, actor), await c.QuerySingleAsync<(string, string, Guid)>("select outcome, note, decided_by from core.sanctions_screening where id = @id", new { id = decision.Id }));
        // Not open for review; the client is checked today; the reuse is in the audit log as its own row, by the screening user.
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.sanctions_screening where subject_id = @party and outcome = N'possible'", new { party }));
        Assert.Equal(await db.App.ReadTxAsync(tx => Task.FromResult(tx.Today())), DateOnly.FromDateTime((await c.ExecuteScalarAsync<DateTime?>("select sanctions_checked_at from core.party where id = @party", new { party }))!.Value));
        Assert.Equal(other, await c.ExecuteScalarAsync<Guid>("""
            select user_id from core.audit_log where table_name = N'sanctions_screening' and action = N'insert' and row_id = lower(convert(nchar(36), @id))
            """, new { id = second.Id }));
        // The screens show the decision it came from, with its author and reason.
        var rows = await db.App.ReadTxAsync(tx => SanctionsScreening.ScreeningsAsync(tx, party));
        var shown = rows.Single(r => r.Id == second.Id);
        Assert.Equal((decision.Id, decision.Note), (shown.ReusesId, shown.Note));
        Assert.NotNull(shown.DecidedByName);
        Assert.NotNull(shown.ReusedDecidedAt);
    }

    [Fact]
    public async Task White_space_and_key_order_of_a_stored_match_text_do_not_count_as_a_change()
    {
        await LoadAsync();
        var (actor, party, decision) = await DismissedAsync();
        // The stored text as PostgreSQL's jsonb writes it (spaces, its own key order), as an imported row would hold it.
        await AdminAsync("update core.sanctions_screening set matches = @text where id = @id", new
        {
            id = decision.Id,
            text = """[{"name": "Zurab Sankcioneli", "dataId": "1", "score": 1, "strong": true, "reasons": ["name", "birth_year"], "listType": "Test", "reference": "T.001", "matchedName": "Zurabi Sanktsioneli"}]""",
        });
        Assert.Equal(1, (await ScreenAsync(actor, party)).Reused);
        Assert.Equal(decision.Id, (await LatestAsync(party)).ReusesScreeningId);
    }

    [Fact]
    public async Task A_change_of_the_clients_screening_data_asks_for_a_new_review()
    {
        await LoadAsync();
        foreach (var change in new[]
        {
            "update core.party set birth_date = '1980-05-02' where id = @party",            // the full date, same year
            "update core.party set tax_id = @tax where id = @party",                        // an identifier
            "update core.party set foreign_id = N'P-778899' where id = @party",             // a second identifier
        })
        {
            var (actor, party, _) = await DismissedAsync();
            await AdminAsync(change, new { party, tax = Fixtures.RandomTaxId() });
            var result = await ScreenAsync(actor, party);
            Assert.Equal((1, 0), (result.Hits, result.Reused));
            Assert.Equal(("possible", (Guid?)null), ((await LatestAsync(party)).Outcome, (await LatestAsync(party)).ReusesScreeningId));
        }
    }

    [Fact]
    public async Task A_new_list_version_changed_list_content_or_an_extra_match_asks_for_a_new_review()
    {
        // The same entries under a new list date.
        await LoadAsync();
        var (actor, party, _) = await DismissedAsync();
        await LoadAsync(generated: "2026-11-02T23:00:05.487Z");
        Assert.Equal((1, 0), ((await ScreenAsync(actor, party)).Hits, (await LatestAsync(party)).ReusesScreeningId is null ? 0 : 1));

        // The same list date, another entry changed (one that does not even match the client).
        await LoadAsync();
        (actor, party, _) = await DismissedAsync();
        await LoadAsync(entityReference: "CDe.009");
        Assert.Equal("possible", (await ScreenAsync(actor, party)).Hits == 1 ? (await LatestAsync(party)).Outcome : "?");

        // A second listed entry matches the client too: both hits are shown for review.
        await LoadAsync();
        (actor, party, _) = await DismissedAsync();
        await LoadAsync(extra: SecondZurab);
        Assert.Equal((1, 0), ((await ScreenAsync(actor, party)) is var r ? (r.Hits, r.Reused) : default));
        Assert.Equal(2, SanctionsScreening.ParseMatches((await LatestAsync(party)).Matches).Count);

        // Back to the first list: the first decision fits again and is reused.
        await LoadAsync();
        Assert.Equal(1, (await ScreenAsync(actor, party)).Reused);
    }

    [Fact]
    public async Task Only_a_reasoned_dismissal_is_reused_not_a_lifted_confirmation_a_later_confirmation_or_another_clients_decision()
    {
        await LoadAsync();
        // A confirmed hit whose block was lifted is a false positive too, but not one to reuse.
        var actor = await Fixtures.UserAsync(db, "accountant");
        var lifted = await HitAsync();
        await ScreenAsync(actor, lifted);
        var confirmed = await LatestAsync(lifted);
        await DecideAsync(actor, confirmed.Id, "confirm", "დადასტურდა");
        await DecideAsync(actor, confirmed.Id, "lift", "ფმს-მა მოხსნა");
        Assert.Equal(("false_positive", "lift"), ((await LatestAsync(lifted)).Outcome, (await LatestAsync(lifted)).Decision));
        Assert.Equal((1, 0), ((await ScreenAsync(actor, lifted)) is var a ? (a.Hits, a.Reused) : default));

        // The latest decision on the same hits is a confirmation: no reuse, and the block stays.
        var (_, party, dismissed) = await DismissedAsync();
        await AdminAsync("update core.sanctions_screening set decided_at = dateadd(day, -1, decided_at) where id = @id", new { id = dismissed.Id });
        await ScreenAsync(actor, party);
        var again = await LatestAsync(party);
        Assert.Equal(dismissed.Id, again.ReusesScreeningId);
        var reopened = await HitAsync();
        await ScreenAsync(actor, reopened);
        var r1 = await LatestAsync(reopened);
        await DecideAsync(actor, r1.Id, "dismiss");
        await ScreenAsync(actor, reopened);
        await AdminAsync("update core.sanctions_screening set outcome = N'possible', decision = null, decided_by = null, decided_at = null, note = null where id = (select top (1) id from core.sanctions_screening where subject_id = @reopened and reuses_screening_id is null and outcome = N'false_positive')", new { reopened });
        // (the dismissal above put back to possible by hand, then confirmed through the service)
        var open = await ScalarAsync<Guid>("select top (1) id from core.sanctions_screening where subject_id = @reopened and outcome = N'possible'", new { reopened });
        await DecideAsync(actor, open, "confirm", "დადასტურდა");
        Assert.Equal((1, 0), ((await ScreenAsync(actor, reopened)) is var b ? (b.Hits, b.Reused) : default));

        // Another client with the same name and birth date: its own subject, no reuse of this one's decision.
        var twin = await HitAsync();
        Assert.Equal((1, 0), ((await ScreenAsync(actor, twin)) is var t ? (t.Hits, t.Reused) : default));
    }

    [Fact]
    public async Task A_decision_without_its_author_reason_or_fingerprints_is_not_reused()
    {
        await LoadAsync();
        foreach (var damage in new[]
        {
            "update core.sanctions_screening set note = N'   ' where id = @id",
            "update core.sanctions_screening set decision = null where id = @id",                       // as a row from before migration 0010
            "update core.sanctions_screening set subject_sha256 = null, matches_sha256 = null, list_sha256 = null where id = @id", // an imported row
        })
        {
            var (actor, party, decision) = await DismissedAsync();
            await AdminAsync(damage, new { id = decision.Id });
            Assert.Equal((1, 0), ((await ScreenAsync(actor, party)) is var r ? (r.Hits, r.Reused) : default));
        }
    }

    [Fact]
    public async Task A_reuse_never_lifts_a_sanctions_block_or_an_FMS_suspension()
    {
        await LoadAsync();
        var (actor, party, _) = await DismissedAsync();
        // The client is blocked (another confirmed hit, set as an earlier decision would) and under an FMS suspension.
        await AdminAsync("update core.party set sanctions_blocked = 1 where id = @party", new { party });
        await AdminAsync("""
            insert core.fms_suspension (id, party_id, kind, scope, instructed_at, written_confirmed_at, ends_at)
            values (newid(), @party, N'written', N'all', sysdatetimeoffset(), sysdatetimeoffset(), dateadd(day, 3, sysdatetimeoffset()))
            """, new { party });
        var suspension = await ScalarAsync<string>("select string_agg(concat(id, N'|', ends_at, N'|', lifted_at), N',') from core.fms_suspension where party_id = @party", new { party });
        Assert.Equal(1, (await ScreenAsync(actor, party)).Reused);
        Assert.True(await ScalarAsync<bool>("select sanctions_blocked from core.party where id = @party", new { party }));
        Assert.Equal(suspension, await ScalarAsync<string>("select string_agg(concat(id, N'|', ends_at, N'|', lifted_at), N',') from core.fms_suspension where party_id = @party", new { party }));
        Assert.NotNull(await db.App.AsUserAsync(actor, tx => Aml.SuspensionInTransactionAsync(tx, party)));
    }

    [Fact]
    public async Task Lifting_one_confirmed_hit_keeps_the_client_blocked_while_another_confirmed_one_stands()
    {
        await LoadAsync();
        var actor = await Fixtures.UserAsync(db, "accountant");
        var org = Guid.NewGuid();
        await AdminAsync("""
            insert core.party (id, kind, name, tax_id) values (@org, N'organization', N'შპს ორი მფლობელი', @tax);
            insert core.party_beneficial_owner (party_id, first_name, last_name, criterion, share_percent, document_number) values (@org, N'Eric', N'Badege', N'share', 60, N'AB123456');
            insert core.party_beneficial_owner (party_id, first_name, last_name, criterion, share_percent, birth_date) values (@org, N'Zurab', N'Sankcioneli', N'share', 40, '1980-05-01');
            """, new { org, tax = Fixtures.RandomTaxId()[..9] });
        Assert.Equal(2, (await ScreenAsync(actor, org)).Hits);
        var hits = (await db.App.ReadTxAsync(tx => SanctionsScreening.ScreeningsAsync(tx, org))).Where(r => r.Outcome == "possible").Select(r => r.Id).ToList();
        foreach (var id in hits) await DecideAsync(actor, id, "confirm", "დადასტურდა");
        Assert.True(await ScalarAsync<bool>("select sanctions_blocked from core.party where id = @org", new { org }));
        await DecideAsync(actor, hits[0], "lift", "ფმს-მა მოხსნა");
        Assert.True(await ScalarAsync<bool>("select sanctions_blocked from core.party where id = @org", new { org }));
        await DecideAsync(actor, hits[1], "lift", "ფმს-მა მოხსნა");
        Assert.False(await ScalarAsync<bool>("select sanctions_blocked from core.party where id = @org", new { org }));
    }

    [Fact]
    public async Task The_database_refuses_a_reuse_that_does_not_fit_and_any_change_of_one()
    {
        await LoadAsync();
        var (actor, party, decision) = await DismissedAsync();
        await ScreenAsync(actor, party);
        var reuse = await LatestAsync(party);
        var other = (await DismissedAsync()).Decision;
        var confirmedParty = await HitAsync();
        await ScreenAsync(actor, confirmedParty);
        var confirmed = await LatestAsync(confirmedParty);
        await DecideAsync(actor, confirmed.Id, "confirm", "დადასტურდა");

        async Task<string?> Refused(string sql, object param)
        {
            var e = await Assert.ThrowsAnyAsync<Exception>(() => db.App.AsUserAsync(actor, tx => tx.ExecuteAsync(sql, param)));
            return DbError.From(e)?.Code;
        }
        const string insert = """
            insert core.sanctions_screening (subject_kind, subject_id, party_id, list_generated_at, matches, outcome, subject_sha256, matches_sha256, list_sha256, reuses_screening_id)
            select subject_kind, subject_id, party_id, list_generated_at, matches, N'false_positive', subject_sha256, matches_sha256, list_sha256, @target
            from core.sanctions_screening where id = @source
            """;
        var before = await ScalarAsync<int>("select count(*) from core.sanctions_screening");
        Assert.Equal("SS001", await Refused(insert, new { source = confirmed.Id, target = confirmed.Id }));   // a confirmed hit
        Assert.Equal("SS001", await Refused(insert, new { source = reuse.Id, target = other.Id }));           // another client's decision
        Assert.Equal("SS001", await Refused(insert, new { source = reuse.Id, target = reuse.Id }));           // a reuse, not a decision
        Assert.Equal("SS001", await Refused(insert.Replace("N'false_positive', subject_sha256,", "N'false_positive', N'" + new string('0', 64) + "',"),
            new { source = reuse.Id, target = decision.Id }));                                                  // other subject data
        Assert.Equal(before, await ScalarAsync<int>("select count(*) from core.sanctions_screening"));
        Assert.Equal("SS002", await Refused("update core.sanctions_screening set reuses_screening_id = null where id = @id", new { id = reuse.Id }));
        // Made a confirmation, or given a reason of its own: the check constraint refuses it before the trigger does.
        Assert.Equal("23514", await Refused("update core.sanctions_screening set outcome = N'confirmed' where id = @id", new { id = reuse.Id }));
        Assert.Equal("23514", await Refused("update core.sanctions_screening set note = N'x' where id = @id", new { id = reuse.Id }));
        Assert.Equal("SS002", await Refused("update core.sanctions_screening set reuses_screening_id = @other where id = @id", new { id = reuse.Id, other = other.Id }));
        // A decision on a reused row is not one of the service's: nothing changes.
        await DecideAsync(actor, reuse.Id, "confirm", "x");
        await DecideAsync(actor, reuse.Id, "lift", "x");
        Assert.Equal(("false_positive", (Guid?)decision.Id, (string?)null), ((await LatestAsync(party)).Outcome, (await LatestAsync(party)).ReusesScreeningId, (await LatestAsync(party)).Note));
        Assert.False(await ScalarAsync<bool>("select sanctions_blocked from core.party where id = @party", new { party }));
    }
}
