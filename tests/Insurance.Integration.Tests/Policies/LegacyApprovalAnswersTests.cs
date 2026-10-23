using Insurance.Application.Services;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// Legacy scripts/approval-answers-regression.ts (Codex PR #19 item 3, H28, H30): every press of "approve" gets its own answer
/// from the action — malformed and unknown ids refused without a database error, a role without the right told so, a
/// repeated press "already", the approval confirmed — and two presses at the same moment (two tabs) approve once.
/// Real ApprovePolicyAsync as an operations head and an operator (sales) on a real database.
/// </summary>
public sealed class LegacyApprovalAnswersTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);
    private static readonly DateOnly Start = new(2026, 1, 1);

    private async Task<string> PressAsync(CurrentUser user, string id)
    {
        var a = await PolicyActions.ApprovePolicyAsync(w.App, user, id);
        return a.Number == "" ? a.Outcome : $"{a.Outcome} {a.Number}";
    }

    private async Task<(string Status, int Issues)> StateAsync(Guid id) =>
        (await w.ScalarAsync<string>("select status from core.policy where id = @id", new { id }), (await w.PremiumRowsAsync(id)).Count);

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1' or '1'='1")]
    [InlineData("00000000-0000-4000-8000-000000000000")]
    public async Task A_malformed_or_unknown_id_is_answered_invalid(string id)
    {
        var head = await w.UserAsync("operations_head");
        Assert.Equal("invalid", await PressAsync(head, id));
    }

    [Fact]
    public async Task Operator_forbidden_head_done_again_already()
    {
        var head = await w.UserAsync("operations_head");
        var operatorUser = await w.UserAsync("sales");
        // One instalment of 365 on the start day, as the script's pending policy.
        var a = await w.PendingPolicyAsync(await w.ClientAsync(), Start, schedule: [(0, 365m)]);
        Assert.Equal("forbidden", await PressAsync(operatorUser, a.Id.ToString()));
        Assert.Equal(("pending_approval", 0), await StateAsync(a.Id));
        Assert.Equal($"done {a.Number}", await PressAsync(head, a.Id.ToString()));
        Assert.Equal(("active", 1), await StateAsync(a.Id));
        Assert.Equal($"already {a.Number}", await PressAsync(head, a.Id.ToString()));
        Assert.Equal(("active", 1), await StateAsync(a.Id));
    }

    [Fact]
    public async Task Two_presses_at_the_same_moment_each_get_their_own_answer_and_approve_once()
    {
        var head = await w.UserAsync("operations_head");
        var b = await w.PendingPolicyAsync(await w.ClientAsync(), Start, schedule: [(0, 365m)]);
        var answers = await Task.WhenAll(PressAsync(head, b.Id.ToString()), PressAsync(head, b.Id.ToString()));
        Assert.Equal([$"already {b.Number}", $"done {b.Number}"], answers.Order(StringComparer.Ordinal));
        Assert.Equal(("active", 1), await StateAsync(b.Id));
    }
}
