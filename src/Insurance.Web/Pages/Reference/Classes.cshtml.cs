using Dapper;
using Insurance.Web.Infrastructure;

namespace Insurance.Web.Pages.Reference;

public sealed class ClassesModel : AppPageModel
{
    public sealed record Row(string Code, string? ParentCode, string NameKa, string Category, decimal IbnrPercent);

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public async Task OnGetAsync() =>
        Rows = (await Db.ReadAsync(c => c.QueryAsync<Row>(
            "select code as Code, parent_code as ParentCode, name_ka as NameKa, category as Category, ibnr_percent as IbnrPercent from core.insurance_class order by sort_order"))).ToList();
}
