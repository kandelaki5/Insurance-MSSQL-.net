using System.Globalization;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Web.Infrastructure;

namespace Insurance.Web.Pages.Audit;

/// <summary>
/// The shared change log (owner, 17.10.2026, docs/05 ვ8: the admin and the audit see it; legacy audit/page.tsx). Who may see
/// it is the permission audit.log.view, read for the user of the session before any row of the log is read: without it
/// the page says so and reads nothing of the log. Read-only.
/// </summary>
public sealed class IndexModel : AppPageModel
{
    public sealed record UserOption(Guid Id, string FullName);

    public bool Allowed { get; private set; }
    public AuditLog.Filter Filter { get; private set; } = new(null, null, null, null, null);
    public IReadOnlyList<UserOption> Users { get; private set; } = [];
    public IReadOnlyList<AuditLog.Entry> Rows { get; private set; } = [];
    public long? Next { get; private set; }

    /// <summary>The tables by their Georgian names, in Georgian alphabetical order (localeCompare 'ka').</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Tables { get; } =
        AuditTexts.Tables.OrderBy(t => t.Value, StringComparer.Create(CultureInfo.GetCultureInfo("ka-GE"), false)).ToList();

    /// <summary>The address of a page of the log under the same filter.</summary>
    public string Href(long? before)
    {
        var q = new List<string>();
        if (Filter.From is { } from) q.Add($"from={Fmt.Iso(from)}");
        if (Filter.To is { } to) q.Add($"to={Fmt.Iso(to)}");
        if (Filter.Table is { } table) q.Add($"table={Uri.EscapeDataString(table)}");
        if (Filter.UserId is { } user) q.Add($"user={user}");
        if (before is { } b) q.Add($"before={b.ToString(CultureInfo.InvariantCulture)}");
        return q.Count == 0 ? "/audit" : "/audit?" + string.Join("&", q);
    }

    public async Task OnGetAsync()
    {
        Allowed = await PermissionStore.HasPermissionAsync(Db, Me, "audit.log.view");
        if (!Allowed) return;
        Filter = AuditLog.Parse(Query);
        Users = (await Db.ReadTxAsync(tx => tx.QueryAsync<UserOption>("select id as Id, full_name as FullName from core.app_user order by full_name, lower(convert(nchar(36), id)) collate Latin1_General_BIN2"))).ToList();
        (Rows, Next) = await Db.ReadTxAsync(tx => AuditLog.ReadPageAsync(tx, Filter));
    }
}
