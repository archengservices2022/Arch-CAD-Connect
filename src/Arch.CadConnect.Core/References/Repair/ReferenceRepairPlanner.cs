namespace Arch.CadConnect.Core.References;

/// <summary>
/// Turns one selected P5B-B <see cref="ReferenceVersionAssessment"/> + a
/// resolved <see cref="RepairTargetResolution"/> + the referencing document's
/// writability into a deterministic <see cref="ReferenceRepairPlan"/>. PURE:
/// no COM, no HTTP, no I/O, no mutation.
///
/// IDENTITY RULE (fail closed):
///  * only an APPLICABLE, <see cref="PlmVersionStatus.Stale"/> assessment is a
///    candidate - CURRENT / UNKNOWN VERSION / not-applicable are NOT REPAIRABLE;
///  * the repair target is the authoritative latest FileVersion for the SAME
///    <c>cadDocumentId</c>. The candidate's stable identity
///    (<c>cadDocumentId</c> + <c>fileVersionId</c>) must match EXACTLY and must
///    be proven by a verified workspace-manifest binding - a filename-only
///    candidate, an identity mismatch, or an ambiguous resolution is REJECTED;
///  * the referencing document must already be writable under existing Arch
///    rules - P5C never checks it out.
/// </summary>
public static class ReferenceRepairPlanner
{
    public static ReferenceRepairPlan Plan(
        ReferenceVersionAssessment assessment,
        RepairTargetResolution target,
        RepairReferencingContext referencing)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(referencing);

        var reference = assessment.Entry.Reference;
        var notes = new List<string>(target.Notes);

        ReferenceRepairPlan Result(
            ReferenceRepairEligibility eligibility,
            string detail,
            string reason,
            string? proposedTargetPath = null) =>
            new(
                Eligibility: eligibility,
                ReferencingDocumentPath: referencing.DocumentAbsolutePath,
                ObservedReferenceName: reference.InventorReportedName,
                RelationshipKind: reference.RelationshipKind,
                CadDocumentId: assessment.CadDocumentId,
                CurrentPinnedFileVersionId: assessment.PinnedLocalFileVersionId,
                AuthoritativeTargetFileVersionId: assessment.AuthoritativeLatestFileVersionId,
                CurrentReferencePath: reference.ResolvedAbsolutePath,
                ProposedTargetPath: proposedTargetPath,
                RepairReason: reason,
                EligibilityDetail: detail,
                Notes: notes,
                // SERVER-authoritative target integrity - carried onto EVERY
                // plan (only meaningful on an eligible one). Never the manifest.
                ServerAuthoritativeTargetFileSize: eligibility == ReferenceRepairEligibility.Eligible
                    ? assessment.AuthoritativeTargetFileSize
                    : -1,
                ServerAuthoritativeTargetSha256: eligibility == ReferenceRepairEligibility.Eligible
                    ? assessment.AuthoritativeTargetSha256 ?? ""
                    : "");

        // ---- 1. only a STALE, applicable managed reference is a candidate ----
        if (!assessment.Applicable)
        {
            return Result(ReferenceRepairEligibility.NotRepairable,
                "The selected reference is not a locally managed reference with an exact stable Arch identity "
                + "(unresolved / unmanaged). P5C cannot repair it without guessing - classified NOT REPAIRABLE.",
                "No repair - the reference has no provable stable Arch identity.");
        }

        switch (assessment.Status)
        {
            case PlmVersionStatus.Current:
                return Result(ReferenceRepairEligibility.NotRepairable,
                    "The reference is already at the authoritative latest version (CURRENT). Nothing to repair.",
                    "No repair - the reference is already current.");
            case PlmVersionStatus.UnknownVersion:
                return Result(ReferenceRepairEligibility.NotRepairable,
                    "The authoritative version status is UNKNOWN VERSION - it could not be established safely. "
                    + "P5C fails closed and does NOT repair.",
                    "No repair - authoritative version status is unknown (fail closed).");
        }

        // ---- 2. the exact stable identity must be present ----
        var cadDocumentId = assessment.CadDocumentId;
        var pinned = assessment.PinnedLocalFileVersionId;
        var targetFileVersionId = assessment.AuthoritativeLatestFileVersionId;

        if (IsMalformed(cadDocumentId) || IsMalformed(pinned) || IsMalformed(targetFileVersionId))
        {
            return Result(ReferenceRepairEligibility.NotRepairable,
                "The exact stable identity (cadDocumentId + pinned local fileVersionId + authoritative target "
                + "fileVersionId) could not be established. P5C will not repair on a partial identity.",
                "No repair - the stable identity is incomplete.");
        }

        if (assessment.Entry.ManagedIdentity is not { IsVerified: true })
        {
            return Result(ReferenceRepairEligibility.Rejected,
                "The current reference's workspace-manifest binding is Unverified, so its pinned local "
                + "fileVersionId cannot authorize repair. Run Get Latest and retry - REJECTED.",
                "No repair - the current reference identity is not verified.");
        }

        // ---- 2b. the SERVER-authoritative target integrity must be present ----
        // The mutable local manifest is NEVER the authority for the target
        // binary; without canonical server size + SHA-256 there is nothing
        // trustworthy to authorize the repair against.
        if (!assessment.HasAuthoritativeTargetIntegrity)
        {
            return Result(ReferenceRepairEligibility.NotRepairable,
                "The authenticated Arch PLM server did not provide canonical integrity metadata (byte size + "
                + "SHA-256) for the authoritative target FileVersion. P5C will not repair without a "
                + "server-authoritative target to verify against - fail closed.",
                "No repair - no server-authoritative target integrity metadata.");
        }

        if (string.Equals(pinned, targetFileVersionId, StringComparison.Ordinal))
        {
            return Result(ReferenceRepairEligibility.NotRepairable,
                "The pinned local FileVersion already equals the authoritative latest. Nothing to repair.",
                "No repair - already at the authoritative latest FileVersion.");
        }

        var repairReason =
            $"STALE - the referenced managed document (cadDocumentId {cadDocumentId}) is pinned locally to "
            + $"FileVersion {pinned}, but the authoritative Arch PLM latest is {targetFileVersionId}.";

        // ---- 3. the authoritative target binary must be safely available ----
        switch (target.Outcome)
        {
            case RepairTargetOutcome.AmbiguousTargets:
                return Result(ReferenceRepairEligibility.Rejected,
                    "The authoritative target could not be resolved to a SINGLE local file - more than one "
                    + "managed copy claims the exact target identity. Fail closed - REJECTED.",
                    repairReason);

            case RepairTargetOutcome.TargetManifestServerMismatch:
                return Result(ReferenceRepairEligibility.Rejected,
                    "The local workspace manifest's recorded size / checksum for the target FileVersion DISAGREE "
                    + "with the authenticated server's authoritative values. The manifest is not the integrity "
                    + "authority; P5C fails closed and never reconciles it. Run Get Latest for the target "
                    + "document, then retry - REJECTED.",
                    repairReason);

            case RepairTargetOutcome.TargetBinaryVerificationFailed:
                return Result(ReferenceRepairEligibility.Rejected,
                    "The authoritative target file's CURRENT bytes do not match the server-authoritative size / "
                    + "SHA-256. Fail closed - REJECTED.",
                    repairReason);

            case RepairTargetOutcome.Resolved:
                break; // candidate checks below

            case RepairTargetOutcome.NotAttempted:
            case RepairTargetOutcome.NoServerIntegrityMetadata:
            case RepairTargetOutcome.NoManagedCopyFound:
            case RepairTargetOutcome.TargetVersionNotLocal:
            case RepairTargetOutcome.TargetCopyUnverified:
            case RepairTargetOutcome.TargetFileMissing:
            default:
                return Result(ReferenceRepairEligibility.NotRepairable,
                    "The authoritative target FileVersion is not safely available on this machine as a verified "
                    + "managed copy. Run Get Latest for the target document, then retry. P5C never substitutes a "
                    + "similarly named file and never runs a broad Get Latest.",
                    repairReason);
        }

        var candidate = target.Candidate;
        if (candidate is null)
        {
            return Result(ReferenceRepairEligibility.NotRepairable,
                "The target resolution reported success but carried no candidate. Fail closed.",
                repairReason);
        }

        // ---- 4. the candidate's identity must be PROVEN and EXACT ----
        if (candidate.IdentitySource != RepairTargetIdentitySource.WorkspaceManifestVerified)
        {
            return Result(ReferenceRepairEligibility.Rejected,
                "The candidate target path is not proven by a verified workspace-manifest binding (it would be a "
                + "filename / path / timestamp match). P5C never selects a repair target that way - REJECTED.",
                repairReason);
        }

        if (!string.Equals(candidate.CadDocumentId, cadDocumentId, StringComparison.Ordinal))
        {
            return Result(ReferenceRepairEligibility.Rejected,
                $"The candidate target's cadDocumentId ({candidate.CadDocumentId}) does not match the reference's "
                + $"cadDocumentId ({cadDocumentId}). REJECTED.",
                repairReason);
        }

        if (!string.Equals(candidate.FileVersionId, targetFileVersionId, StringComparison.Ordinal))
        {
            return Result(ReferenceRepairEligibility.Rejected,
                $"The candidate target's fileVersionId ({candidate.FileVersionId}) does not match the authoritative "
                + $"latest fileVersionId ({targetFileVersionId}). REJECTED.",
                repairReason);
        }

        if (!candidate.ExistsOnDisk)
        {
            return Result(ReferenceRepairEligibility.NotRepairable,
                "The authoritative target file is not present on disk. Run Get Latest, then retry.",
                repairReason);
        }

        if (!candidate.ManifestAgreesWithServer
            || candidate.ServerFileSize != assessment.AuthoritativeTargetFileSize
            || !string.Equals(candidate.ServerSha256, assessment.AuthoritativeTargetSha256 ?? "",
                StringComparison.Ordinal))
        {
            return Result(ReferenceRepairEligibility.Rejected,
                "The resolved target candidate was not verified against the SAME server-authoritative integrity "
                + "metadata this assessment carries. Fail closed - REJECTED.",
                repairReason);
        }

        if (!candidate.BinaryIntegrityVerified)
        {
            return Result(ReferenceRepairEligibility.NotRepairable,
                "The authoritative target file's CURRENT bytes were not verified against the server-authoritative "
                + "size and SHA-256. Fail closed.",
                repairReason);
        }

        // ---- 5. the referencing document must already be writable ----
        if (!referencing.IsWritable)
        {
            return Result(ReferenceRepairEligibility.Rejected,
                referencing.WritabilityDetail + " P5C fails closed - REJECTED.",
                repairReason,
                proposedTargetPath: candidate.AbsolutePath);
        }

        // ---- eligible ----
        notes.Add("Referencing document: " + referencing.WritabilityDetail);
        return Result(ReferenceRepairEligibility.Eligible,
            "STALE + MANAGED with an exact stable identity, an exact verified authoritative target available "
            + "locally, and a writable referencing document. Ready for preview + explicit confirmation.",
            repairReason,
            proposedTargetPath: candidate.AbsolutePath);
    }

    /// <summary>A stable identifier is opaque; blank or surrounding whitespace
    ///  is malformed, never normalised away.</summary>
    private static bool IsMalformed(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1]);
}
