using Insurance.Application.Services;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Policies;

/// <summary>Downloads the upload template for the motor product (legacy policies/import/template/route.ts): any signed-in user.</summary>
public sealed class ImportTemplateModel : AppPageModel
{
    public async Task<IActionResult> OnGetAsync() =>
        File(await PolicyImport.TemplateAsync(Db), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "policies-template.xlsx");
}
