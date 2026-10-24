// Ported from src/compliance/suspension.test.ts ("FMS suspension counter (AML law art. 36)").
using Insurance.Domain.Compliance;
using Insurance.Domain.Tests.Common;

namespace Insurance.Domain.Tests.Compliance;

public class SuspensionTests
{
    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    [Fact(DisplayName = "blocks every operation, or only the operation on its own target (review H11)")]
    public void BlocksByTarget()
    {
        var all = new SuspensionTargeting(SuspensionState.Active, SuspensionScope.All, null, null);
        var onPolicy = new SuspensionTargeting(SuspensionState.Active, SuspensionScope.Transaction, "P1", null);
        var onClaim = new SuspensionTargeting(SuspensionState.Awaiting, SuspensionScope.Transaction, null, "C1");
        Assert.True(Suspension.SuspensionBlocks(all, new OperationTargets()));
        Assert.True(Suspension.SuspensionBlocks(onPolicy, new OperationTargets(PolicyIds: ["P1"])));
        Assert.False(Suspension.SuspensionBlocks(onPolicy, new OperationTargets(PolicyIds: ["P2"])));
        Assert.True(Suspension.SuspensionBlocks(onClaim, new OperationTargets(PolicyIds: ["P1"], ClaimIds: ["C1"])));
        Assert.False(Suspension.SuspensionBlocks(onPolicy with { State = SuspensionState.Lapsed }, new OperationTargets(PolicyIds: ["P1"])));
        // An oral instruction whose written confirmation is overdue still blocks: the officer decides
        // whether to resume (art. 36 p. 3; the owner's decision of 04.10.2026).
        Assert.True(Suspension.SuspensionBlocks(all with { State = SuspensionState.Unconfirmed }, new OperationTargets()));
        // A transaction suspension with no target would block nothing — the reason it cannot be saved.
        var targetless = new SuspensionTargeting(SuspensionState.Active, SuspensionScope.Transaction, null, null);
        Assert.False(Suspension.SuspensionBlocks(targetless, new OperationTargets(PolicyIds: ["P1"], ClaimIds: ["C1"])));
    }

    [Fact(DisplayName = "a suspension saved before targets existed keeps blocking by its number, not wider (review H11 upgrade)")]
    public void LegacySuspensionBlocksByNumber()
    {
        var legacy = new SuspensionTargeting(SuspensionState.Active, SuspensionScope.Transaction, null, null, LegacyUnmapped: true, TransactionRef: "CAS-1");
        // The same operation the old rule blocked is still blocked after the upgrade…
        Assert.True(Suspension.SuspensionBlocks(legacy, new OperationTargets(PolicyIds: ["P1"], Numbers: ["cas-1 "])));
        // …an unrelated one is not, and the suspension is not widened to all operations.
        Assert.False(Suspension.SuspensionBlocks(legacy, new OperationTargets(PolicyIds: ["P2"], Numbers: ["CAS-2"])));
        Assert.False(Suspension.SuspensionBlocks(legacy with { TransactionRef = "" }, new OperationTargets(Numbers: [""])));
        Assert.False(Suspension.SuspensionBlocks(legacy with { State = SuspensionState.Lapsed }, new OperationTargets(Numbers: ["CAS-1"])));
        // A receipt or refund passes its bank reference too (review H11-U1): PAY-77 blocks, PAY-78 does not.
        var byReference = legacy with { TransactionRef = "PAY-77" };
        Assert.True(Suspension.SuspensionBlocks(byReference, new OperationTargets(PolicyIds: ["P1"], Numbers: ["CAS-1", "pay-77"])));
        Assert.False(Suspension.SuspensionBlocks(byReference, new OperationTargets(PolicyIds: ["P1"], Numbers: ["CAS-1", "PAY-78"])));
    }

    [Fact(DisplayName = "knows Orthodox Easter and the Georgian holidays around it")]
    public void OrthodoxEasterAndHolidays()
    {
        Assert.Equal(new DateOnly(2026, 4, 12), Suspension.OrthodoxEaster(2026));
        Assert.Equal(new DateOnly(2025, 4, 20), Suspension.OrthodoxEaster(2025));
        var h = Suspension.GeorgianHolidays(2026);
        Assert.True(h.Contains(new DateOnly(2026, 4, 10)) && h.Contains(new DateOnly(2026, 4, 11)) && h.Contains(new DateOnly(2026, 4, 12)) && h.Contains(new DateOnly(2026, 4, 13)));
        Assert.True(h.Contains(new DateOnly(2026, 5, 26)) && h.Contains(new DateOnly(2026, 1, 7)));
    }

    [Fact(DisplayName = "weekends and holidays are non-working days")]
    public void NonWorkingDays()
    {
        Assert.True(Suspension.IsNonWorkingDay(new DateOnly(2026, 10, 3))); // Saturday
        Assert.False(Suspension.IsNonWorkingDay(new DateOnly(2026, 10, 5))); // Monday
        Assert.True(Suspension.IsNonWorkingDay(new DateOnly(2026, 10, 14))); // Svetitskhovloba
    }

    [Fact(DisplayName = "72 hours started on a Monday morning end on Thursday morning")]
    public void MondayMorning()
    {
        var end = Suspension.SuspensionEnd(At("2026-10-05T06:00:00Z")); // Monday 10:00 Tbilisi
        Assert.Equal("2026-10-08T06:00:00.000Z", Parity.Iso(end));
    }

    [Fact(DisplayName = "a suspension started on Friday skips the weekend")]
    public void FridaySkipsWeekend()
    {
        // Friday 14:00 Tbilisi: 10 h left on Friday, then Mon (24) + Tue (24) + 14 h; Wed 14 Oct is a
        // holiday, so the last 14 h run on Thursday → Thursday 14:00 Tbilisi.
        var end = Suspension.SuspensionEnd(At("2026-10-09T10:00:00Z"));
        Assert.Equal("2026-10-15T10:00:00.000Z", Parity.Iso(end));
    }

    [Fact(DisplayName = "a suspension started on a holiday starts counting on the next working day")]
    public void StartedOnHoliday()
    {
        var end = Suspension.SuspensionEnd(At("2026-10-14T08:00:00Z"), 24); // holiday → counts Thursday 00:00–24:00
        Assert.Equal("2026-10-15T20:00:00.000Z", Parity.Iso(end)); // Thursday 24:00 Tbilisi
    }

    [Fact(DisplayName = "an oral instruction must be confirmed in writing within 24 clock hours")]
    public void WrittenConfirmation()
    {
        Assert.Equal("2026-10-10T10:00:00.000Z", Parity.Iso(Suspension.WrittenConfirmationDeadline(At("2026-10-09T10:00:00Z"))));
    }
}
