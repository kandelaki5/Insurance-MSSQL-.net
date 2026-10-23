using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Insurance.Application.Texts;

namespace Insurance.Web.Tests;

/// <summary>
/// The admin's permissions panel over HTTP on a real database (ported from scripts/integration/permission-panel.test.ts,
/// which rendered the page on a model of the tables): who may open it, what it shows for a user and a group, and its own
/// forms posted back to the page, with the audit naming the admin. Expected answers are written out by hand.
/// </summary>
public sealed partial class PermissionPanelTests(WebApp app) : IClassFixture<WebApp>
{
    private const string Key = "premium.receipt.create";
    private static readonly PermissionTextsPanel P = new();

    private sealed class PermissionTextsPanel
    {
        public string Yes => PermissionTexts.Panel.Yes;
        public string No => PermissionTexts.Panel.No;
        public string None => PermissionTexts.Panel.None;
        public string Allowed => PermissionTexts.Panel.Allowed;
        public string Refused => PermissionTexts.Panel.Refused;
    }

    private sealed record World(string Tag, Guid Anna, Guid Beka, Guid Cara, Guid Cashiers, Guid Sales, string CashiersName, string SalesName);

    /// <summary>Anna and Beka (underwriters), Cara (accountant) and two groups, named with this test's own tag.</summary>
    private async Task<World> WorldAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        await using var c = await app.Db.OpenAdminAsync();
        async Task<Guid> User(string username, string fullName, string role)
        {
            var id = Guid.NewGuid();
            await c.ExecuteAsync("insert core.app_user (id, username, full_name, password_hash, role_code) values (@id, @username, @fullName, N'x', @role)",
                new { id, username = $"{username}.{tag}", fullName, role });
            return id;
        }
        async Task<Guid> Group(string name)
        {
            var id = Guid.NewGuid();
            await c.ExecuteAsync("insert core.user_group (id, name) values (@id, @name)", new { id, name });
            return id;
        }
        return new World(tag,
            await User("anna.k", $"ანა კაპანაძე {tag}", "underwriter"), await User("beka.m", $"ბექა მაისურაძე {tag}", "underwriter"),
            await User("cara.b", $"ცირა ბერიძე {tag}", "accountant"),
            await Group($"სალაროები {tag}"), await Group($"გაყიდვები {tag}"), $"სალაროები {tag}", $"გაყიდვები {tag}");
    }

    [GeneratedRegex("<section[^>]*\\bid=\"([^\"]+)\"[^>]*>[\\s\\S]*?</section>")]
    private static partial Regex Sections();

    private static string Section(string html, string id) => Sections().Matches(html).FirstOrDefault(m => m.Groups[1].Value == id)?.Value ?? "";

    private static string RowOf(string html, string attr, string value) =>
        new Regex($"<tr[^>]*\\b{attr}=\"{Regex.Escape(value)}\"[^>]*>([\\s\\S]*?)</tr>").Match(html) is { Success: true } m ? m.Groups[1].Value : "";

    private static string Cell(string row, string name) =>
        Html.Text(new Regex($"<td[^>]*data-cell=\"{name}\"[^>]*>([\\s\\S]*?)</td>").Match(row).Groups[1].Value);

    private static string? Selected(string row) =>
        Regex.Match(row, "<option value=\"(\\w+)\" selected=\"selected\"") is { Success: true } m ? m.Groups[1].Value : null;

    private sealed record Shown(string Role, string Groups, string? Own, string Result, string Source);

    private static Shown ShownFor(string html, string key = Key)
    {
        var row = RowOf(Section(html, "chosen-user"), "data-permission", key);
        return new Shown(Cell(row, "role"), Cell(row, "groups"), Selected(row), Cell(row, "result"), Cell(row, "source"));
    }

    private static string Answer(string html) =>
        Regex.Match(html, "<span class=\"(?:badge low|field-error)\" role=\"(?:status|alert)\">([^<]*)</span>") is { Success: true } m ? m.Groups[1].Value : "";

    private static async Task<string> PostAsync(HttpClient client, string handler, string query, Dictionary<string, string> fields)
    {
        var url = $"/users/permissions?handler={handler}" + (query == "" ? "" : "&" + query);
        return await Html.BodyAsync(await WebApp.PostFormAsync(client, url, fields, "/users/permissions"));
    }

    [Theory]
    [InlineData("accountant")]
    [InlineData("underwriter")]
    [InlineData("operations_head")]
    [InlineData("viewer")]
    public async Task Only_the_admin_opens_the_panel_even_with_a_chosen_user_and_a_forged_role_in_the_address(string role)
    {
        var (client, _) = await app.SignedInAsync(role);
        Assert.Equal("/", Html.Location(await client.GetAsync($"/users/permissions?user={Guid.NewGuid()}&role=admin&actor=x")));
        Assert.Equal("/", Html.Location(await client.GetAsync("/users")));
        var (admin, _) = await app.SignedInAsync("admin");
        var page = await Html.GetAsync(admin, "/users/permissions");
        Assert.Contains(PermissionTexts.Panel.NotYet, page);
        Assert.Contains("href=\"/users/permissions\"", await Html.GetAsync(admin, "/users"));
        Assert.Equal("/login", Html.Location(await app.Client().GetAsync("/users/permissions")));
    }

    [Fact]
    public async Task Users_are_found_by_name_in_any_case_and_each_shows_where_the_permission_comes_from()
    {
        var w = await WorldAsync();
        var (client, adminId) = await app.SignedInAsync("admin");
        await using var c = await app.Db.OpenAdminAsync();
        await c.ExecuteAsync("""
            insert core.user_group_member (group_id, user_id) values (@cashiers, @anna);
            insert core.user_group_permission (group_id, permission) values (@cashiers, @key);
            insert core.user_permission (user_id, permission, decision) values (@beka, @key, N'deny');
            """, new { cashiers = w.Cashiers, anna = w.Anna, beka = w.Beka, key = Key });

        static List<string> Found(string html) => Regex.Matches(html, "<tr[^>]*data-user=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal([w.Anna.ToString(), w.Beka.ToString(), w.Cara.ToString()], Found(await Html.GetAsync(client, $"/users/permissions?q={w.Tag}")));
        Assert.Equal([w.Beka.ToString()], Found(await Html.GetAsync(client, $"/users/permissions?q=BEKA.M.{w.Tag.ToUpperInvariant()}")));
        Assert.Equal([w.Anna.ToString(), w.Beka.ToString(), w.Cara.ToString()], Found(await Html.GetAsync(client, $"/users/permissions?q={Uri.EscapeDataString("ძე " + w.Tag)}")));
        var none = await Html.GetAsync(client, "/users/permissions?q=nobody-at-all");
        Assert.Empty(Found(none));
        Assert.Contains(PermissionTexts.Panel.NoUsers, none);
        var hrefPage = await Html.GetAsync(client, $"/users/permissions?q={Uri.EscapeDataString("ანა კაპანაძე " + w.Tag)}");
        // The address as URLSearchParams wrote it (the page writes + as &#x2B; and & as &amp;, which the browser reads back).
        Assert.Contains($"href=\"/users/permissions?q=%E1%83%90%E1%83%9C%E1%83%90+%E1%83%99%E1%83%90%E1%83%9E%E1%83%90%E1%83%9C%E1%83%90%E1%83%AB%E1%83%94+{w.Tag}&user={w.Anna}\"", System.Net.WebUtility.HtmlDecode(hrefPage));

        var anna = await Html.GetAsync(client, $"/users/permissions?user={w.Anna}");
        Assert.StartsWith($"ანა კაპანაძე {w.Tag} (anna.k.{w.Tag}) — ", Html.Text(Section(anna, "chosen-user")));
        Assert.Equal(new Shown(P.No, w.CashiersName, "inherit", P.Allowed, PermissionTexts.Sources["group"]), ShownFor(anna));
        var memberships = Section(anna, "chosen-user");
        Assert.Equal(PermissionTexts.Panel.Member, Cell(RowOf(memberships, "data-group", w.Cashiers.ToString()), "member"));
        Assert.Equal(PermissionTexts.Panel.NotMember, Cell(RowOf(memberships, "data-group", w.Sales.ToString()), "member"));
        Assert.Equal(new Shown(P.No, P.None, "deny", P.Refused, PermissionTexts.Sources["revoked"]), ShownFor(await Html.GetAsync(client, $"/users/permissions?user={w.Beka}")));
        Assert.Equal(new Shown(P.Yes, P.None, "inherit", P.Allowed, PermissionTexts.Sources["role"]), ShownFor(await Html.GetAsync(client, $"/users/permissions?user={w.Cara}")));
        Assert.Equal(new Shown(P.Yes, P.None, "inherit", P.Allowed, PermissionTexts.Sources["role"]), ShownFor(await Html.GetAsync(client, $"/users/permissions?user={adminId}")));

        foreach (var id in new[] { Guid.NewGuid().ToString(), "anna", "1 or 1=1" })
        {
            var chosen = Section(await Html.GetAsync(client, $"/users/permissions?user={Uri.EscapeDataString(id)}"), "chosen-user");
            Assert.Contains(PermissionTexts.Panel.MissingUser, chosen);
            Assert.DoesNotContain("data-permission", chosen);
        }
    }

    [Fact]
    public async Task A_chosen_group_shows_its_ticks_and_members_and_groups_are_found_by_name()
    {
        var w = await WorldAsync();
        var (client, _) = await app.SignedInAsync("admin");
        await using var c = await app.Db.OpenAdminAsync();
        await c.ExecuteAsync("insert core.user_group_member (group_id, user_id) values (@g, @anna); insert core.user_group_permission (group_id, permission) values (@g, @key)",
            new { g = w.Cashiers, anna = w.Anna, key = Key });
        var cashiers = Section(await Html.GetAsync(client, $"/users/permissions?group={w.Cashiers}"), "chosen-group");
        Assert.Equal(PermissionTexts.Panel.Ticked, Cell(RowOf(cashiers, "data-permission", Key), "ticked"));
        Assert.Equal([w.Anna.ToString()], Regex.Matches(cashiers, "data-member=\"([^\"]+)\"").Select(m => m.Groups[1].Value));
        var sales = Section(await Html.GetAsync(client, $"/users/permissions?group={w.Sales}"), "chosen-group");
        Assert.Equal(PermissionTexts.Panel.NotTicked, Cell(RowOf(sales, "data-permission", Key), "ticked"));
        Assert.Contains(PermissionTexts.Panel.NoMembers, sales);
        Assert.Contains(PermissionTexts.Panel.MissingGroup, Section(await Html.GetAsync(client, $"/users/permissions?group={Guid.NewGuid()}"), "chosen-group"));
        static List<string> Listed(string html) => Regex.Matches(html, "<li[^>]*data-group=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal([w.Sales.ToString(), w.Cashiers.ToString()], Listed(await Html.GetAsync(client, $"/users/permissions?gq={w.Tag}")));
        Assert.Equal([w.Cashiers.ToString()], Listed(await Html.GetAsync(client, $"/users/permissions?gq={Uri.EscapeDataString("სალარ")}&q={w.Tag}")).Where(id => id == w.Cashiers.ToString()));
        Assert.Empty(Listed(await Html.GetAsync(client, "/users/permissions?gq=xyz-none")));
    }

    [Fact]
    public async Task The_panels_own_forms_tick_join_deny_inherit_leave_untick_and_a_repeat_writes_nothing()
    {
        var w = await WorldAsync();
        var (client, adminId) = await app.SignedInAsync("admin");
        var query = $"user={w.Anna}&group={w.Cashiers}";
        var page = $"/users/permissions?{query}";
        await using var c = await app.Db.OpenAdminAsync();
        async Task<int> AuditRows() => await c.ExecuteScalarAsync<int>("select count(*) from core.audit_log where table_name in (N'user_permission', N'user_group_member', N'user_group_permission')");
        var steps = new List<object> { ShownFor(await Html.GetAsync(client, page)).Source };

        var html = await PostAsync(client, "SetGroupGrant", query, new() { ["answer_for"] = $"grant:{Key}", ["group_id"] = w.Cashiers.ToString(), ["permission"] = Key, ["granted"] = "true" });
        Assert.Equal(PermissionTexts.Changes.Saved, Answer(html));
        html = await PostAsync(client, "SetGroupMember", query, new() { ["answer_for"] = $"member:user:{w.Cashiers}", ["group_id"] = w.Cashiers.ToString(), ["user_id"] = w.Anna.ToString(), ["member"] = "true" });
        Assert.Equal(PermissionTexts.Changes.Saved, Answer(html));
        steps.Add(ShownFor(html));
        html = await PostAsync(client, "SetUserDecision", query, new() { ["answer_for"] = $"decision:{Key}", ["user_id"] = w.Anna.ToString(), ["permission"] = Key, ["decision"] = "deny" });
        Assert.Equal(PermissionTexts.Changes.Saved, Answer(html));
        steps.Add(ShownFor(html));
        var before = await AuditRows();
        html = await PostAsync(client, "SetUserDecision", query, new() { ["answer_for"] = $"decision:{Key}", ["user_id"] = w.Anna.ToString(), ["permission"] = Key, ["decision"] = "deny" });
        Assert.Equal(PermissionTexts.Changes.Unchanged, Answer(html));
        Assert.Equal(before, await AuditRows());
        html = await PostAsync(client, "SetUserDecision", query, new() { ["answer_for"] = $"decision:{Key}", ["user_id"] = w.Anna.ToString(), ["permission"] = Key, ["decision"] = "inherit" });
        steps.Add(ShownFor(html));
        // Out of the group from the group's member list, then the tick taken off.
        html = await PostAsync(client, "SetGroupMember", query, new() { ["answer_for"] = $"member:group:{w.Anna}", ["group_id"] = w.Cashiers.ToString(), ["user_id"] = w.Anna.ToString(), ["member"] = "false" });
        // Her row in the group's member list is gone with its answer, as the legacy form went with its row.
        Assert.Equal("", Answer(html));
        Assert.DoesNotContain($"data-member=\"{w.Anna}\"", html);
        steps.Add(ShownFor(html));
        html = await PostAsync(client, "SetGroupGrant", query, new() { ["answer_for"] = $"grant:{Key}", ["group_id"] = w.Cashiers.ToString(), ["permission"] = Key, ["granted"] = "false" });
        steps.Add(Cell(RowOf(Section(html, "chosen-group"), "data-permission", Key), "ticked"));

        Assert.Equal(new object[]
        {
            PermissionTexts.Sources["not-in-role"],
            new Shown(P.No, w.CashiersName, "inherit", P.Allowed, PermissionTexts.Sources["group"]),
            new Shown(P.No, w.CashiersName, "deny", P.Refused, PermissionTexts.Sources["revoked"]),
            new Shown(P.No, w.CashiersName, "inherit", P.Allowed, PermissionTexts.Sources["group"]),
            new Shown(P.No, P.None, "inherit", P.Refused, PermissionTexts.Sources["not-in-role"]),
            PermissionTexts.Panel.NotTicked,
        }, steps);
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.user_permission where user_id = @anna", new { anna = w.Anna }));
        // Every change is in the audit with the admin as its author: grant +1 −1, member +1 −1, decision insert, update? no — insert and delete.
        var actors = (await c.QueryAsync<Guid?>("""
            select user_id from core.audit_log where table_name in (N'user_permission', N'user_group_member', N'user_group_permission')
              and (new_row like @anna or old_row like @anna or new_row like @group or old_row like @group)
            """, new { anna = $"%{w.Anna}%", group = $"%{w.Cashiers}%" })).ToList();
        Assert.Equal(6, actors.Count);
        Assert.All(actors, a => Assert.Equal(adminId, a));
    }

    [Fact]
    public async Task Refusals_are_answered_in_Georgian_and_write_nothing()
    {
        var w = await WorldAsync();
        var (client, _) = await app.SignedInAsync("admin");
        await using var c = await app.Db.OpenAdminAsync();
        var before = await c.ExecuteScalarAsync<int>("select count(*) from core.audit_log");
        var cases = new (string Handler, Dictionary<string, string> Fields, string Refusal)[]
        {
            ("SetUserDecision", new() { ["user_id"] = "anna", ["permission"] = Key, ["decision"] = "allow" }, "invalid-id"),
            ("SetUserDecision", new() { ["user_id"] = w.Anna.ToString(), ["permission"] = "premium.receipt.delete", ["decision"] = "allow" }, "unknown-permission"),
            ("SetUserDecision", new() { ["user_id"] = w.Anna.ToString(), ["permission"] = Key, ["decision"] = "ALLOW" }, "invalid-decision"),
            ("SetUserDecision", new() { ["user_id"] = Guid.NewGuid().ToString(), ["permission"] = Key, ["decision"] = "allow" }, "missing-user"),
            ("SetGroupMember", new() { ["group_id"] = w.Cashiers.ToString(), ["user_id"] = w.Anna.ToString(), ["member"] = "yes" }, "invalid-decision"),
            ("SetGroupMember", new() { ["group_id"] = "cashiers", ["user_id"] = w.Anna.ToString(), ["member"] = "true" }, "invalid-id"),
            ("SetGroupMember", new() { ["group_id"] = Guid.NewGuid().ToString(), ["user_id"] = w.Anna.ToString(), ["member"] = "true" }, "missing-group"),
            ("SetGroupMember", new() { ["group_id"] = w.Cashiers.ToString(), ["user_id"] = Guid.NewGuid().ToString(), ["member"] = "true" }, "missing-user"),
            ("SetGroupGrant", new() { ["group_id"] = w.Cashiers.ToString(), ["permission"] = "__proto__", ["granted"] = "true" }, "unknown-permission"),
            ("CreateGroup", new() { ["name"] = "   " }, "invalid-name"),
            ("CreateGroup", new() { ["name"] = new string('x', 101) }, "invalid-name"),
            ("CreateGroup", new() { ["name"] = w.CashiersName }, "duplicate-name"),
            ("RenameGroup", new() { ["group_id"] = w.Sales.ToString(), ["name"] = w.CashiersName }, "duplicate-name"),
            ("RenameGroup", new() { ["group_id"] = "x", ["name"] = "ა" }, "invalid-id"),
        };
        foreach (var (handler, fields, refusal) in cases)
        {
            var html = await PostAsync(client, handler, "", new Dictionary<string, string>(fields) { ["answer_for"] = handler == "CreateGroup" ? "groupname:new" : "x" });
            if (handler == "CreateGroup") Assert.Equal(PermissionTexts.Changes.Refusals[refusal], Answer(html));
            else Assert.DoesNotContain(PermissionTexts.Changes.Saved, Answer(html));
        }
        Assert.Equal(before, await c.ExecuteScalarAsync<int>("select count(*) from core.audit_log"));

        // A taken name in another case (Latin letters: SQL Server's lower() does not know Georgian capitals, see database/README.md).
        Assert.Equal(PermissionTexts.Changes.Saved, Answer(await PostAsync(client, "CreateGroup", "", new() { ["answer_for"] = "groupname:new", ["name"] = $"Cashiers {w.Tag}" })));
        Assert.Equal(PermissionTexts.Changes.Refusals["duplicate-name"], Answer(await PostAsync(client, "CreateGroup", "", new() { ["answer_for"] = "groupname:new", ["name"] = $"CASHIERS {w.Tag.ToUpperInvariant()}" })));
        before = await c.ExecuteScalarAsync<int>("select count(*) from core.audit_log");

        // A new group and a new name from the panel's own forms.
        var created = await PostAsync(client, "CreateGroup", "", new() { ["answer_for"] = "groupname:new", ["name"] = $"  ბუღალტერია {w.Tag}  " });
        Assert.Equal(PermissionTexts.Changes.Saved, Answer(created));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("select count(*) from core.user_group where name = @name", new { name = $"ბუღალტერია {w.Tag}" }));
        var renamed = await PostAsync(client, "RenameGroup", $"group={w.Cashiers}", new() { ["answer_for"] = $"groupname:{w.Cashiers}", ["group_id"] = w.Cashiers.ToString(), ["name"] = $"სალარო {w.Tag}" });
        Assert.Equal(PermissionTexts.Changes.Saved, Answer(renamed));
        Assert.Equal($"სალარო {w.Tag}", await c.ExecuteScalarAsync<string>("select name from core.user_group where id = @id", new { id = w.Cashiers }));
        var same = await PostAsync(client, "RenameGroup", $"group={w.Cashiers}", new() { ["answer_for"] = $"groupname:{w.Cashiers}", ["group_id"] = w.Cashiers.ToString(), ["name"] = $"სალარო {w.Tag}" });
        Assert.Equal(PermissionTexts.Changes.Unchanged, Answer(same));
    }

    [Theory]
    [InlineData(199)]
    [InlineData(200)]
    public async Task A_group_past_the_first_200_is_found_by_name_and_Anna_is_put_in_and_taken_out(int fillers)
    {
        // Its own database: the 200 groups of this test must be the only ones.
        await using var own = new WebApp();
        await own.InitializeAsync();
        var (client, _) = await own.SignedInAsync("admin");
        var anna = Guid.NewGuid();
        var target = Guid.NewGuid();
        await using (var c = await own.Db.OpenAdminAsync())
        {
            await c.ExecuteAsync("insert core.app_user (id, username, full_name, password_hash, role_code) values (@anna, N'anna.k', N'ანა კაპანაძე', N'x', N'underwriter')", new { anna });
            await c.ExecuteAsync("insert core.user_group (name) select concat(N'Filler ', format(value, '000')) from generate_series(0, @last)", new { last = fillers - 1 });
            await c.ExecuteAsync("insert core.user_group (id, name) values (@target, N'Zeta target')", new { target });
        }
        string Forms(string html) => string.Join("|", Regex.Matches(Section(html, "chosen-user"), $"name=\"group_id\" value=\"{target}\" />\\s*<input type=\"hidden\" name=\"user_id\" value=\"{anna}\" />\\s*<input type=\"hidden\" name=\"member\" value=\"(\\w+)\"").Select(m => m.Groups[1].Value));
        var query = $"user={anna}&gq=Zeta&group={target}";
        var html = await Html.GetAsync(client, $"/users/permissions?{query}");
        Assert.Equal([target.ToString()], Regex.Matches(html, "<li[^>]*data-group=\"([^\"]+)\"").Select(m => m.Groups[1].Value));
        Assert.Equal("true", Forms(html));
        html = await PostAsync(client, "SetGroupMember", query, new() { ["answer_for"] = $"member:user:{target}", ["group_id"] = target.ToString(), ["user_id"] = anna.ToString(), ["member"] = "true" });
        Assert.Equal(PermissionTexts.Changes.Saved, Answer(html));
        await PostAsync(client, "SetGroupGrant", query, new() { ["answer_for"] = $"grant:{Key}", ["group_id"] = target.ToString(), ["permission"] = Key, ["granted"] = "true" });
        html = await Html.GetAsync(client, $"/users/permissions?{query}");
        Assert.Equal(new Shown(P.No, "Zeta target", "inherit", P.Allowed, PermissionTexts.Sources["group"]), ShownFor(html));
        // Without any search her own group is still there to take her out of.
        Assert.Equal("false", Forms(await Html.GetAsync(client, $"/users/permissions?user={anna}")));
        html = await PostAsync(client, "SetGroupMember", query, new() { ["answer_for"] = $"member:user:{target}", ["group_id"] = target.ToString(), ["user_id"] = anna.ToString(), ["member"] = "false" });
        Assert.Equal(PermissionTexts.Sources["not-in-role"], ShownFor(html).Source);
        await using var check = await own.Db.OpenAdminAsync();
        Assert.Equal(0, await check.ExecuteScalarAsync<int>("select count(*) from core.user_group_member where user_id = @anna", new { anna }));
    }

    [Fact]
    public async Task A_group_in_the_first_200_found_and_chosen_gets_one_membership_form()
    {
        var w = await WorldAsync();
        var (client, _) = await app.SignedInAsync("admin");
        var html = Section(await Html.GetAsync(client, $"/users/permissions?user={w.Anna}&gq={w.Tag}&group={w.Cashiers}"), "chosen-user");
        var ids = Regex.Matches(html, "name=\"group_id\" value=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Single(ids, id => id == w.Cashiers.ToString());
    }

    [Fact]
    public async Task The_policy_approval_and_the_month_close_are_shown_and_changed_like_the_others()
    {
        var w = await WorldAsync();
        var (client, _) = await app.SignedInAsync("admin");
        var head = Guid.NewGuid();
        await using var c = await app.Db.OpenAdminAsync();
        await c.ExecuteAsync("""
            insert core.app_user (id, username, full_name, password_hash, role_code) values (@head, @username, N'ოთარ ხელმძღვანელი', N'x', N'operations_head');
            insert core.user_group_member (group_id, user_id) values (@sales, @anna);
            insert core.user_group_permission (group_id, permission) values (@sales, N'policy.approve');
            """, new { head, username = $"head.o.{w.Tag}", sales = w.Sales, anna = w.Anna });
        const string approve = "policy.approve", close = "accounting.period.close";
        Assert.Equal(new Shown(P.Yes, P.None, "inherit", P.Allowed, PermissionTexts.Sources["role"]), ShownFor(await Html.GetAsync(client, $"/users/permissions?user={head}"), approve));
        Assert.Equal(new Shown(P.No, P.None, "inherit", P.Refused, PermissionTexts.Sources["not-in-role"]), ShownFor(await Html.GetAsync(client, $"/users/permissions?user={w.Cara}"), approve));
        var anna = await Html.GetAsync(client, $"/users/permissions?user={w.Anna}");
        Assert.Equal(new Shown(P.No, w.SalesName, "inherit", P.Allowed, PermissionTexts.Sources["group"]), ShownFor(anna, approve));
        Assert.Contains(PermissionTexts.Permissions[approve], RowOf(Section(anna, "chosen-user"), "data-permission", approve));
        var html = await PostAsync(client, "SetUserDecision", $"user={w.Anna}", new() { ["answer_for"] = $"decision:{approve}", ["user_id"] = w.Anna.ToString(), ["permission"] = approve, ["decision"] = "deny" });
        Assert.Equal(new Shown(P.No, w.SalesName, "deny", P.Refused, PermissionTexts.Sources["revoked"]), ShownFor(html, approve));
        Assert.Equal("inherit", ShownFor(html).Own);
        await PostAsync(client, "SetUserDecision", $"user={w.Anna}", new() { ["user_id"] = w.Anna.ToString(), ["permission"] = approve, ["decision"] = "inherit" });

        var cara = await Html.GetAsync(client, $"/users/permissions?user={w.Cara}");
        Assert.Equal(new Shown(P.Yes, P.None, "inherit", P.Allowed, PermissionTexts.Sources["role"]), ShownFor(cara, close));
        html = await PostAsync(client, "SetUserDecision", $"user={w.Cara}", new() { ["user_id"] = w.Cara.ToString(), ["permission"] = close, ["decision"] = "deny" });
        Assert.Equal(new Shown(P.Yes, P.None, "deny", P.Refused, PermissionTexts.Sources["revoked"]), ShownFor(html, close));
        Assert.Equal(new Shown(P.Yes, P.None, "inherit", P.Allowed, PermissionTexts.Sources["role"]), ShownFor(html));
        Assert.Equal([(w.Cara, close, "deny")], (await c.QueryAsync<(Guid, string, string)>(
            "select user_id, permission, decision from core.user_permission where user_id in (@anna, @cara)", new { anna = w.Anna, cara = w.Cara })).ToList());
    }
}
