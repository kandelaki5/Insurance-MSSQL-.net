using Dapper;
using Insurance.Application.Services;
using Insurance.Domain.Common;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Insurance.Web.Pages.Policies;

/// <summary>Policies awaiting approval (legacy policies/pending/page.tsx with approve-answer.tsx, action approvePolicy).</summary>
public sealed class PendingModel : AppPageModel
{
    public sealed record Row(Guid Id, string Number, string ContractNumber, string Policyholder, string? Object, DateOnly EffectiveFrom, DateOnly EffectiveTo,
        decimal Premium, string PremiumCurrency, DateOnly IssuedOn, string? IssuedBy, string? ChangeNote);

    public IReadOnlyList<Row> Rows { get; private set; } = [];
    public bool Approves { get; private set; }
    public PolicyActions.ApproveAnswer? Answer { get; private set; }

    private async Task LoadAsync()
    {
        Rows = (await Db.ReadAsync(c => c.QueryAsync<Row>("""
            select p.id as Id, p.number as Number, c.number as ContractNumber, h.display_name as Policyholder,
                   (select string_agg(o.description, '; ') from core.insured_object o where o.policy_id = p.id) as Object,
                   p.effective_from as EffectiveFrom, p.effective_to as EffectiveTo,
                   (select coalesce(sum(l.premium), 0) from core.policy_line l where l.policy_id = p.id) as Premium,
                   p.premium_currency as PremiumCurrency, p.issued_on as IssuedOn, u.full_name as IssuedBy, p.change_note as ChangeNote
            from core.policy p
            join core.contract c on c.id = p.contract_id
            join core.party h on h.id = c.policyholder_id
            left join core.app_user u on u.id = p.created_by
            where p.status = N'pending_approval'
            order by p.created_at
            """))).ToList();
        // The approve button is the permission policy.approve (the action checks it again, and refuses a policy the user created).
        var inputs = await PermissionStore.ReadAsync(Db, Me.Id);
        Approves = Permissions.EvaluatePermission(Me.Subject, "policy.approve", inputs.Overrides, inputs.Groups).Allowed;
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostApproveAsync()
    {
        Answer = await PolicyActions.ApprovePolicyAsync(Db, Me, Form("id") ?? "");
        await LoadAsync();
        return Page();
    }
}
