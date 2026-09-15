namespace Arch.CadConnect.Core.References;

/// <summary>
/// Whether ONE selected reference edge can be repaired by P5C.
///
/// P5C is intentionally narrow: only a <c>STALE</c> + <c>MANAGED</c> reference
/// with an exact stable Arch identity, for which an exact authoritative target
/// FileVersion is already available locally as a verified managed copy.
/// </summary>
public enum ReferenceRepairEligibility
{
    /// <summary>Every identity + target + writability check passed - the
    ///  engineer may confirm the repair.</summary>
    Eligible,

    /// <summary>P5C does not repair this kind of reference (CURRENT, UNKNOWN
    ///  VERSION, unmanaged, unresolved) or the authoritative target binary is
    ///  not safely available here. Not a failure - just out of P5C scope /
    ///  fail closed.</summary>
    NotRepairable,

    /// <summary>An identity or target check ACTIVELY failed: the candidate
    ///  target's stable identity does not match, it is only a filename match,
    ///  the resolution is ambiguous, or the referencing document is not
    ///  writable under existing Arch rules. Repair is refused.</summary>
    Rejected,
}

/// <summary>
/// The COM-free, deterministic preview of a single controlled reference repair.
/// It carries every fact the engineer needs to decide, and the exact reason it
/// is / is not eligible. Nothing here mutates anything - it is produced BEFORE
/// any confirmation and BEFORE Inventor is touched.
/// </summary>
public sealed record ReferenceRepairPlan(
    ReferenceRepairEligibility Eligibility,
    string ReferencingDocumentPath,
    string ObservedReferenceName,
    CadRelationshipKind RelationshipKind,
    string? CadDocumentId,
    string? CurrentPinnedFileVersionId,
    string? AuthoritativeTargetFileVersionId,
    string? CurrentReferencePath,
    string? ProposedTargetPath,
    string RepairReason,
    string EligibilityDetail,
    IReadOnlyList<string> Notes,
    /// <summary>SERVER-authoritative canonical byte size of the authoritative
    ///  target FileVersion (P5C-B). This - not the mutable local manifest - is
    ///  the integrity authority the mutation boundary and post-mutation
    ///  verification check against. <c>-1</c> on an ineligible plan.</summary>
    long ServerAuthoritativeTargetFileSize = -1,
    /// <summary>SERVER-authoritative canonical SHA-256 (64 lowercase hex) of the
    ///  authoritative target FileVersion (P5C-B). Empty on an ineligible plan.</summary>
    string? ServerAuthoritativeTargetSha256 = null)
{
    /// <summary>True only when the plan carries canonical server-authoritative
    ///  target integrity metadata - a precondition for any P5C mutation.</summary>
    public bool HasServerAuthoritativeTargetIntegrity =>
        FileVersionIntegrity.IsRepresentableFileSize(ServerAuthoritativeTargetFileSize)
        && FileVersionIntegrity.IsCanonicalSha256(ServerAuthoritativeTargetSha256);

    /// <summary>True only when the repair may proceed to confirmation + mutation.</summary>
    public bool CanProceed => Eligibility == ReferenceRepairEligibility.Eligible;

    public string EligibilityLabel => Eligibility switch
    {
        ReferenceRepairEligibility.Eligible => "ELIGIBLE",
        ReferenceRepairEligibility.Rejected => "REJECTED",
        _ => "NOT REPAIRABLE",
    };
}

/// <summary>
/// The writability situation of the document that OWNS the reference being
/// repaired. Replacing a reference makes the owning document dirty in memory;
/// P5C refuses (fail closed) unless that document may already be modified and
/// saved under existing Arch rules - it NEVER checks a document out, and NEVER
/// makes a writable copy to get around checkout / read-only / Vault control.
/// </summary>
public sealed record RepairReferencingContext(
    string DocumentAbsolutePath,
    bool IsWritable,
    string WritabilityDetail);
