using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Insurance.Application.Data;
using Insurance.Domain.Common;
using Insurance.Domain.Compliance;

namespace Insurance.Application.Services;

/// <summary>Where the UN consolidated list is downloaded from (configuration Insurance:Sanctions:UnListUrl).</summary>
public sealed class SanctionsOptions
{
    /// <summary>The legacy address of the UN Security Council consolidated list.</summary>
    public const string DefaultUnListUrl = "https://scsanctions.un.org/resources/xml/en/consolidated.xml";

    /// <summary>An http(s) address, or a file: address (tests read a local fixture this way, never the network).</summary>
    public string UnListUrl { get; set; } = DefaultUnListUrl;
}

/// <summary>Database side of sanctions screening: loads the UN list and screens clients and owners (legacy src/lib/sanctions.ts).</summary>
public static class SanctionsScreening
{
    // The UN server answers a plain client with 404; a browser user agent is served.
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/128 Safari/537.36";

    public sealed record LoadResult(bool Ok, int Entries, string? GeneratedAt);

    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string JsonArray(IEnumerable<string> items) => JsonSerializer.Serialize(items.ToArray(), Json);

    /// <summary>The list's text from its address: null when it cannot be fetched (a refusal, a network error, a missing file).</summary>
    public static async Task<string?> FetchAsync(HttpClient http, string url, CancellationToken ct = default)
    {
        try
        {
            var uri = new Uri(url);
            if (uri.IsFile) return await File.ReadAllTextAsync(uri.LocalPath, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("user-agent", UserAgent);
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true, NoCache = true };
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UriFormatException or TaskCanceledException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Downloads the UN consolidated list and replaces the stored copy (one transaction; nothing changes when the list is empty).</summary>
    public static async Task<LoadResult> LoadUnListAsync(Db db, HttpClient http, string url)
    {
        var xml = await FetchAsync(http, url);
        if (xml is null) return new LoadResult(false, 0, null);
        var list = Sanctions.ParseUnList(xml);
        if (list.Entries.Count == 0) return new LoadResult(false, 0, null);
        DateTimeOffset? generatedAt = list.GeneratedAt is { } g
            ? DateTimeOffset.Parse(g, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
            : null;
        await db.InTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync("delete from core.sanction_list_entry where source = N'un'");
            // 11 parameters a row, well under SQL Server's 2 100 per statement.
            foreach (var chunk in list.Entries.Chunk(150))
            {
                var p = new DynamicParameters();
                var values = new List<string>();
                for (var i = 0; i < chunk.Length; i++)
                {
                    var e = chunk[i];
                    values.Add($"(N'un', @d{i}, @k{i}, @n{i}, @a{i}, @t{i}, @r{i}, @l{i}, @c{i}, @b{i}, @o{i})");
                    p.Add($"d{i}", e.DataId);
                    p.Add($"k{i}", e.Kind.ToSnakeCode());
                    p.Add($"n{i}", e.Name);
                    p.Add($"a{i}", JsonArray(e.Aliases));
                    p.Add($"t{i}", e.ListType);
                    p.Add($"r{i}", e.Reference);
                    p.Add($"l{i}", e.ListedOn);
                    p.Add($"c{i}", JsonArray(e.Nationalities));
                    p.Add($"b{i}", JsonArray(e.BirthYears));
                    p.Add($"o{i}", JsonArray(e.Documents));
                }
                await tx.ExecuteAsync(
                    "insert into core.sanction_list_entry (source, data_id, kind, name, aliases, list_type, reference, listed_on, nationalities, birth_years, documents) values "
                    + string.Join(", ", values), p);
            }
            await tx.ExecuteAsync("""
                merge core.sanction_list_version with (holdlock) as v
                using (select N'un' as source) as s on v.source = s.source
                when matched then update set generated_at = @generatedAt, loaded_at = sysdatetimeoffset(), entries = @entries, content_sha256 = @content
                when not matched then insert (source, generated_at, loaded_at, entries, content_sha256) values (N'un', @generatedAt, sysdatetimeoffset(), @entries, @content);
                """, new { generatedAt, entries = list.Entries.Count, content = ScreeningFingerprint.List("un", generatedAt, list.Entries) });
            return 0;
        });
        return new LoadResult(true, list.Entries.Count, list.GeneratedAt);
    }

    public sealed record Version(string? GeneratedAt, string LoadedAt, int Entries);

    /// <summary>The stored list's version (Tbilisi time), or null when no list was ever loaded.</summary>
    public static Task<Version?> ListVersionAsync(Tx db) =>
        db.QuerySingleOrDefaultAsync<Version>("""
            select format(switchoffset(generated_at, '+04:00'), 'dd.MM.yyyy HH:mm') as GeneratedAt,
                   format(switchoffset(loaded_at, '+04:00'), 'dd.MM.yyyy HH:mm') as LoadedAt, entries as Entries
            from core.sanction_list_version where source = N'un'
            """);

    private sealed record EntryRow(
        string DataId, string Kind, string Name, string Aliases, string? ListType, string? Reference, DateOnly? ListedOn,
        string Nationalities, string BirthYears, string Documents);

    private static IReadOnlyList<string> Strings(string json) => JsonSerializer.Deserialize<string[]>(json) ?? [];

    private static async Task<(IReadOnlyList<SanctionEntry> Entries, DateTimeOffset? GeneratedAt)> LoadEntriesAsync(Tx db)
    {
        var rows = await db.QueryAsync<EntryRow>("""
            select data_id as DataId, kind as Kind, name as Name, aliases as Aliases, list_type as ListType, reference as Reference,
                   listed_on as ListedOn, nationalities as Nationalities, birth_years as BirthYears, documents as Documents
            from core.sanction_list_entry where source = N'un'
            """);
        var generatedAt = await db.QuerySingleOrDefaultAsync<DateTimeOffset?>("select generated_at from core.sanction_list_version where source = N'un'");
        return (rows.Select(r => new SanctionEntry(
            r.DataId, r.Kind == "entity" ? SanctionKind.Entity : SanctionKind.Individual, r.Name, Strings(r.Aliases), r.ListType ?? "", r.Reference ?? "",
            r.ListedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Strings(r.Nationalities), Strings(r.BirthYears), Strings(r.Documents))).ToList(), generatedAt);
    }

    private sealed record Subject(string SubjectKind, Guid SubjectId, Guid PartyId, ScreeningSubject Screening, DateOnly? BirthDate)
    {
        public string Fingerprint => ScreeningFingerprint.Subject(SubjectKind, SubjectId, Screening, BirthDate);
    }

    private sealed record PartySubjectRow(Guid Id, string Kind, string? DisplayName, DateOnly? BirthDate, string? TaxId, string? ForeignId, string? FirstName, string? LastName);

    private sealed record OwnerSubjectRow(Guid Id, Guid PartyId, string FirstName, string LastName, DateOnly? BirthDate, string? PersonalNumber, string? DocumentNumber);

    private static string? Year(DateOnly? d) => d?.ToString("yyyy", CultureInfo.InvariantCulture);

    private static async Task<List<Subject>> SubjectsOfAsync(Tx db, Guid? partyId)
    {
        var parties = await db.QueryAsync<PartySubjectRow>("""
            select id as Id, kind as Kind, display_name as DisplayName, birth_date as BirthDate, tax_id as TaxId, foreign_id as ForeignId,
                   first_name as FirstName, last_name as LastName
            from core.party where @partyId is null or id = @partyId
            """, new { partyId });
        var owners = await db.QueryAsync<OwnerSubjectRow>("""
            select id as Id, party_id as PartyId, first_name as FirstName, last_name as LastName, birth_date as BirthDate,
                   personal_number as PersonalNumber, document_number as DocumentNumber
            from core.party_beneficial_owner where valid_to is null and (@partyId is null or party_id = @partyId)
            """, new { partyId });
        return
        [
            .. parties.Select(p => new Subject("party", p.Id, p.Id, new ScreeningSubject(
                Name: p.DisplayName ?? "",
                Kind: p.Kind == "person" ? SanctionKind.Individual : SanctionKind.Entity,
                Aliases: p.Kind == "person" && !string.IsNullOrEmpty(p.FirstName) && !string.IsNullOrEmpty(p.LastName) ? [$"{p.FirstName} {p.LastName}"] : [],
                BirthYear: Year(p.BirthDate),
                Documents: new[] { p.TaxId, p.ForeignId }.Where(d => !string.IsNullOrEmpty(d)).Select(d => d!).ToList()), p.BirthDate)),
            .. owners.Select(o => new Subject("owner", o.Id, o.PartyId, new ScreeningSubject(
                Name: $"{o.LastName} {o.FirstName}",
                Kind: SanctionKind.Individual,
                Aliases: [$"{o.FirstName} {o.LastName}"],
                BirthYear: Year(o.BirthDate),
                Documents: new[] { o.PersonalNumber, o.DocumentNumber }.Where(d => !string.IsNullOrEmpty(d)).Select(d => d!).ToList()), o.BirthDate)),
        ];
    }

    /// <summary>The matches as stored: the old object's fields and order (score as a JavaScript number).</summary>
    public static string MatchesJson(IEnumerable<ScreeningMatch> matches) =>
        new JsonArray(matches.Select(m => (JsonNode)new JsonObject
        {
            ["dataId"] = m.DataId,
            ["name"] = m.Name,
            ["reference"] = m.Reference,
            ["listType"] = m.ListType,
            ["score"] = (double)m.Score,
            ["matchedName"] = m.MatchedName,
            ["strong"] = m.Strong,
            ["reasons"] = new JsonArray(m.Reasons.Select(r => (JsonNode)JsonValue.Create(r.ToSnakeCode())).ToArray()),
        }).ToArray()).ToJsonString(Json);

    public sealed record StoredMatch(string DataId, string Name, string Reference, string ListType, double Score, string MatchedName, bool Strong, IReadOnlyList<string> Reasons);

    public static IReadOnlyList<StoredMatch> ParseMatches(string json) =>
        JsonNode.Parse(json) is JsonArray a
            ? a.Where(n => n is JsonObject).Select(n => new StoredMatch(
                n!["dataId"]?.ToString() ?? "", n["name"]?.ToString() ?? "", n["reference"]?.ToString() ?? "", n["listType"]?.ToString() ?? "",
                n["score"]?.GetValue<double>() ?? 0, n["matchedName"]?.ToString() ?? "", n["strong"]?.GetValue<bool>() ?? false,
                (n["reasons"] as JsonArray)?.Select(r => r?.ToString() ?? "").ToList() ?? [])).ToList()
            : [];

    /// <param name="Reused">Subjects whose hits were closed by an earlier false-positive decision on the same data.</param>
    public sealed record ScreeningResult(int Screened, int Hits, int Reused = 0);

    private static Task LockSubjectAsync(Tx tx, Guid subjectId) => tx.LockAsync("core.sanctions_subject", Uuid.Text(subjectId));

    /// <summary>
    /// Screens every client and beneficial owner (or one client's) against the stored list and records a screening row
    /// each; a clear result marks the client or the owner checked today.
    ///
    /// A subject with hits is recorded as possible, for a review, unless an authorised user already dismissed exactly
    /// these hits as a false positive, with a reason, for the same subject with the same screening data against the same
    /// list (date and whole content): then the row is a false positive that points to that decision (owner, 23.10.2026).
    /// A changed name, identifier or birth date, a new list version or a changed entry, or a match more or less asks for
    /// a new review; so does a later confirmation of the same hits. A reuse never lifts a client's sanctions block or an
    /// FMS suspension, and it is recorded as its own row. (The legacy code meant something similar but compared the
    /// stored jsonb text with JSON.stringify of the new matches, which never equal, and wrote a plain "clear".)
    /// </summary>
    public static async Task<ScreeningResult> ScreenPartiesAsync(Tx tx, Guid userId, Guid? partyId = null)
    {
        var (entries, generatedAt) = await LoadEntriesAsync(tx);
        if (entries.Count == 0) return new ScreeningResult(0, 0);
        var listSha = ScreeningFingerprint.List("un", generatedAt, entries);
        var subjects = await SubjectsOfAsync(tx, partyId);
        var today = tx.Today();
        var (hits, reused) = (0, 0);
        foreach (var s in subjects)
        {
            var matches = Sanctions.ScreenSubject(s.Screening, entries);
            var (subjectSha, matchesSha) = (s.Fingerprint, ScreeningFingerprint.Matches(matches));
            var outcome = matches.Count > 0 ? "possible" : "clear";
            Guid? reuses = null;
            if (outcome == "possible")
            {
                await LockSubjectAsync(tx, s.SubjectId);
                // The latest human decision on exactly these hits: reused only when it is a reasoned dismissal.
                var last = await tx.QuerySingleOrDefaultAsync<(Guid Id, string Outcome, string? Decision, Guid? DecidedBy, string? Note)?>("""
                    select top (1) id, outcome, decision, decided_by, note
                    from core.sanctions_screening
                    where subject_kind = @kind and subject_id = @subjectId and reuses_screening_id is null
                      and subject_sha256 = @subjectSha and matches_sha256 = @matchesSha and list_sha256 = @listSha
                      and ((list_generated_at is null and @generatedAt is null) or list_generated_at = @generatedAt)
                      and outcome in (N'false_positive', N'confirmed') and decided_at is not null
                    order by decided_at desc, id
                    """, new { kind = s.SubjectKind, subjectId = s.SubjectId, subjectSha, matchesSha, listSha, generatedAt });
                if (last is { Outcome: "false_positive", Decision: "dismiss", DecidedBy: not null } d && !string.IsNullOrWhiteSpace(d.Note))
                    (outcome, reuses) = ("false_positive", d.Id);
            }
            if (outcome == "possible") hits++;
            if (reuses is not null) reused++;
            await tx.ExecuteAsync("""
                insert into core.sanctions_screening (id, subject_kind, subject_id, party_id, list_generated_at, matches, outcome, created_by,
                                                      subject_sha256, matches_sha256, list_sha256, reuses_screening_id)
                values (@id, @subjectKind, @subjectId, @partyId, @generatedAt, @matches, @outcome, @userId, @subjectSha, @matchesSha, @listSha, @reuses)
                """, new { id = Guid.NewGuid(), subjectKind = s.SubjectKind, subjectId = s.SubjectId, partyId = s.PartyId, generatedAt, matches = MatchesJson(matches), outcome, userId,
                           subjectSha, matchesSha, listSha, reuses });
            // A reused dismissal dates the client checked, as the dismissal itself did; a clear owner dates the owner.
            if ((outcome == "clear" && s.SubjectKind == "party") || reuses is not null)
                await tx.ExecuteAsync("update core.party set sanctions_checked_at = @today where id = @id", new { today, id = s.PartyId });
            else if (outcome == "clear")
                await tx.ExecuteAsync("update core.party_beneficial_owner set sanctions_checked_at = @today where id = @id", new { today, id = s.SubjectId });
        }
        return new ScreeningResult(subjects.Count, hits, reused);
    }

    public sealed record ScreeningRow(
        Guid Id, string SubjectKind, string? SubjectName, Guid PartyId, string PartyName, string ScreenedAt, string Matches, string Outcome,
        string? Note, string? DecidedByName, Guid? ReusesId = null, string? ReusedDecidedAt = null)
    {
        public IReadOnlyList<StoredMatch> MatchList => ParseMatches(Matches);
    }

    /// <summary>Screenings awaiting a decision (possible or confirmed), newest first; or one client's full history (the latest 200).</summary>
    public static async Task<IReadOnlyList<ScreeningRow>> ScreeningsAsync(Tx db, Guid? partyId = null) =>
        (await db.QueryAsync<ScreeningRow>("""
            select top (200) s.id as Id, s.subject_kind as SubjectKind,
                   case s.subject_kind when N'party' then p.display_name
                        else (select o.last_name + N' ' + o.first_name from core.party_beneficial_owner o where o.id = s.subject_id) end as SubjectName,
                   s.party_id as PartyId, p.display_name as PartyName,
                   format(switchoffset(s.screened_at, '+04:00'), 'dd.MM.yyyy HH:mm') as ScreenedAt,
                   s.matches as Matches, s.outcome as Outcome, coalesce(s.note, r.note) as Note, u.full_name as DecidedByName,
                   s.reuses_screening_id as ReusesId, format(switchoffset(r.decided_at, '+04:00'), 'dd.MM.yyyy HH:mm') as ReusedDecidedAt
            from core.sanctions_screening s
            join core.party p on p.id = s.party_id
            left join core.sanctions_screening r on r.id = s.reuses_screening_id
            left join core.app_user u on u.id = coalesce(s.decided_by, r.decided_by)
            where (@partyId is null and s.outcome in (N'possible', N'confirmed')) or s.party_id = @partyId
            order by s.screened_at desc
            """, new { partyId })).ToList();

    /// <summary>
    /// Decides a screening with hits: dismissed as a false positive, confirmed (client blocked), or the block lifted.
    /// Anything that does not fit the screening's outcome writes nothing.
    /// </summary>
    public static async Task DecideAsync(Tx tx, Guid userId, Guid id, string decision, string note)
    {
        var row = await tx.QuerySingleOrDefaultAsync<(Guid PartyId, Guid SubjectId, string Outcome)?>("select party_id, subject_id, outcome from core.sanctions_screening where id = @id", new { id });
        if (row is not { } r) return;
        // The same lock as a re-screening of the subject: a decision and a reuse of it never cross.
        await LockSubjectAsync(tx, r.SubjectId);
        r = (await tx.QuerySingleOrDefaultAsync<(Guid PartyId, Guid SubjectId, string Outcome)?>("select party_id, subject_id, outcome from core.sanctions_screening where id = @id", new { id }))!.Value;
        var today = tx.Today();
        if (decision == "dismiss" && r.Outcome == "possible")
        {
            await tx.ExecuteAsync("update core.sanctions_screening set outcome = N'false_positive', decision = N'dismiss', decided_by = @userId, decided_at = sysdatetimeoffset(), note = @note where id = @id", new { userId, note, id });
            await tx.ExecuteAsync("update core.party set sanctions_checked_at = @today where id = @partyId", new { today, partyId = r.PartyId });
        }
        else if (decision == "confirm" && r.Outcome == "possible")
        {
            await tx.ExecuteAsync("update core.sanctions_screening set outcome = N'confirmed', decision = N'confirm', decided_by = @userId, decided_at = sysdatetimeoffset(), note = @note where id = @id", new { userId, note, id });
            await tx.ExecuteAsync("update core.party set sanctions_blocked = 1 where id = @partyId", new { partyId = r.PartyId });
        }
        else if (decision == "lift" && r.Outcome == "confirmed")
        {
            await tx.ExecuteAsync("update core.sanctions_screening set outcome = N'false_positive', decision = N'lift', decided_by = @userId, decided_at = sysdatetimeoffset(), note = @note where id = @id", new { userId, note, id });
            // Lifting one confirmed hit does not lift another one of the same client (its own or an owner's) that still stands
            // (owner, 23.10.2026: one false positive lifts no other confirmed hit).
            await tx.ExecuteAsync("""
                update core.party set sanctions_checked_at = @today,
                       sanctions_blocked = case when exists (select 1 from core.sanctions_screening where party_id = @partyId and outcome = N'confirmed') then 1 else 0 end
                where id = @partyId
                """, new { today, partyId = r.PartyId });
        }
    }
}
