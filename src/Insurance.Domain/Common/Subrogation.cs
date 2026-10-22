// Regress operations (docs/08 version 8): the vocabulary and the checks of src/lib/subrogation.ts that need neither the
// store nor the money rules. The operations themselves (openCase, addParty, recordEvidence, recordResponse, addMovement,
// receive, reverse) read and write through a store in one transaction and apply the rules of src/finance/subrogation.ts
// (totals, checkMovement, statusAfterMoney); they are ported with the store, once the finance rules are in
// Insurance.Domain.Finance. Migration 0030 checks the same rules again in its triggers.

namespace Insurance.Domain.Common;

/// <summary>The document that shows who was at fault. Codes 'police_report', 'court_decision', 'admission'.</summary>
public enum BasisKind { PoliceReport, CourtDecision, Admission }

/// <summary>Codes 'liable_person', 'liability_insurer'.</summary>
public enum PartyRole { LiablePerson, LiabilityInsurer }

/// <summary>The debtor's answer (Response in the old code). Codes 'accepted', 'disputed', 'rejected'.</summary>
public enum SubrogationResponse { Accepted, Disputed, Rejected }

/// <summary>
/// The document a movement rests on (docs/08 §4). Codes 'demand_letter', 'debtor_acceptance', 'court_decision',
/// 'bank_statement', 'correction'.
/// </summary>
public enum DocumentKind { DemandLetter, DebtorAcceptance, CourtDecision, BankStatement, Correction }

/// <summary>The movements entered by hand with a document (addMovement): the claim, its confirmation, its cancellation by a court.</summary>
public enum DocumentedMovement { Claimed, Confirmed, Cancelled }

/// <param name="Limit">The liability insurer's limit, in tetri; null when not known.</param>
public sealed record NewParty(string CaseId, string PartyId, PartyRole Role, string? PolicyNumber, string? Coverage, long? Limit);

/// <summary>
/// The errors of the operations that are not the money rules' own ('amount', 'cap', 'balance', 'document', 'target',
/// 'reversed', 'date' and 'class', 'theft', 'coverage' come from those).
/// </summary>
public static class SubrogationErrors
{
    /// <summary>No such claim or case.</summary>
    public const string Missing = "missing";

    /// <summary>The claim has its case already (one regress per claim).</summary>
    public const string Open = "open";

    /// <summary>The same form was sent again (request key) or the bank reference was used on the case.</summary>
    public const string Duplicate = "duplicate";

    /// <summary>The payer is not a party of the case.</summary>
    public const string Payer = "payer";

    /// <summary>Insurer details on a person, or a movement that needs another operation.</summary>
    public const string Kind = "kind";

    /// <summary>The case's number series has run out.</summary>
    public const string Series = "series";

    /// <summary>Not a positive amount (here: an insurer's limit).</summary>
    public const string Amount = "amount";

    /// <summary>A document missing or of the wrong kind.</summary>
    public const string Document = "document";
}

public static class Subrogation
{
    private static readonly IReadOnlyDictionary<DocumentedMovement, IReadOnlyList<DocumentKind>> Documents = new Dictionary<DocumentedMovement, IReadOnlyList<DocumentKind>>
    {
        [DocumentedMovement.Claimed] = [DocumentKind.DemandLetter],
        [DocumentedMovement.Confirmed] = [DocumentKind.DebtorAcceptance, DocumentKind.CourtDecision],
        [DocumentedMovement.Cancelled] = [DocumentKind.CourtDecision],
    };

    /// <summary>The documents a hand-entered movement may rest on: a demand letter for a claim, the debtor's acceptance or a court decision for a confirmation, a court decision for a cancellation.</summary>
    public static IReadOnlyList<DocumentKind> DocumentsFor(DocumentedMovement kind) => Documents[kind];

    /// <summary>addMovement's document check: 'document' when the movement may not rest on this kind of document.</summary>
    public static string? DocumentProblem(DocumentedMovement kind, DocumentKind documentKind) =>
        Documents[kind].Contains(documentKind) ? null : SubrogationErrors.Document;

    /// <summary>
    /// addParty's own checks, before the case is looked up: a liable person carries no insurer details (policy number,
    /// coverage, limit) — 'kind'; a limit, when given, is more than zero — 'amount'.
    /// </summary>
    public static string? PartyProblem(NewParty party)
    {
        var insurerDetails = party.PolicyNumber is not null || party.Coverage is not null || party.Limit is not null;
        if (party.Role == PartyRole.LiablePerson && insurerDetails) return SubrogationErrors.Kind;
        if (party.Limit is { } limit && !(limit > 0)) return SubrogationErrors.Amount;
        return null;
    }

    /// <summary>The bank reference of a receipt as it is stored and compared: trimmed, and null when nothing is left.</summary>
    public static string? BankReference(string? text)
    {
        var trimmed = JsText.Trim(text ?? "");
        return trimmed == "" ? null : trimmed;
    }

    /// <summary>
    /// recordEvidence (the evidence number) and reverse (the reason) refuse a blank text with 'document'; the text is stored
    /// trimmed.
    /// </summary>
    public static bool IsBlank(string text) => JsText.Trim(text) == "";
}
