using Arch.CadConnect.Core.References;

using static Arch.CadConnect.Core.Tests.References.Repair.RepairFixtures;

namespace Arch.CadConnect.Core.Tests.References.Repair;

/// <summary>
/// Deterministic, COM-free coverage of P5C repair PLANNING: which single
/// reference is eligible, and the exact fail-closed reasons for everything
/// else. Never a filename / display-name / path / timestamp match.
/// </summary>
public class ReferenceRepairPlannerTests
{
    // ---- the one eligible shape --------------------------------------

    [Fact]
    public void Stale_managed_exact_identity_is_eligible_when_an_exact_verified_target_is_available()
    {
        var plan = ReferenceRepairPlanner.Plan(
            Stale(cad: "cad_a", pinned: "fv_v1", latest: "fv_v3"),
            VerifiedTarget(cad: "cad_a", fv: "fv_v3"),
            WritableReferencing());

        Assert.Equal(ReferenceRepairEligibility.Eligible, plan.Eligibility);
        Assert.True(plan.CanProceed);
        Assert.Equal("cad_a", plan.CadDocumentId);
        Assert.Equal("fv_v1", plan.CurrentPinnedFileVersionId);
        Assert.Equal("fv_v3", plan.AuthoritativeTargetFileVersionId);
        Assert.Equal(P("Arch", "Latest", "PART-A.ipt"), plan.ProposedTargetPath);
        Assert.Equal(P("Arch", "Job1", "PART-A.ipt"), plan.CurrentReferencePath);
        Assert.Contains("STALE", plan.RepairReason);
    }

    // ---- not repairable (out of P5C scope / fail closed) -------------

    [Fact]
    public void Current_reference_is_not_repairable()
    {
        var plan = ReferenceRepairPlanner.Plan(Current(), VerifiedTarget(fv: "fv_v3"), WritableReferencing());
        Assert.Equal(ReferenceRepairEligibility.NotRepairable, plan.Eligibility);
        Assert.Contains("current", plan.EligibilityDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_version_reference_is_not_repairable_fail_closed()
    {
        var plan = ReferenceRepairPlanner.Plan(UnknownVersion(), VerifiedTarget(), WritableReferencing());
        Assert.Equal(ReferenceRepairEligibility.NotRepairable, plan.Eligibility);
        Assert.Contains("UNKNOWN VERSION", plan.EligibilityDetail);
    }

    [Fact]
    public void Unmanaged_reference_is_not_repairable()
    {
        var assessment = Assess(UnmanagedRef(),
            LatestVersionLookup.FromResults(new[] { LatestVersionResult.Found("cad_a", "fv_v3") }));
        var plan = ReferenceRepairPlanner.Plan(assessment, VerifiedTarget(), WritableReferencing());
        Assert.Equal(ReferenceRepairEligibility.NotRepairable, plan.Eligibility);
    }

    [Fact]
    public void Unresolved_reference_without_provable_identity_is_not_repairable()
    {
        var assessment = Assess(MissingRef(),
            LatestVersionLookup.FromResults(new[] { LatestVersionResult.Found("cad_a", "fv_v3") }));
        var plan = ReferenceRepairPlanner.Plan(assessment, VerifiedTarget(), WritableReferencing());
        Assert.Equal(ReferenceRepairEligibility.NotRepairable, plan.Eligibility);
        Assert.False(plan.CanProceed);
    }

    [Fact]
    public void Unverified_current_reference_binding_is_rejected()
    {
        var assessment = Assess(ManagedRef(verified: false),
            LatestVersionLookup.FromResults(new[] { LatestVersionResult.Found("cad_a", "fv_v3") }));

        var plan = ReferenceRepairPlanner.Plan(assessment, VerifiedTarget(), WritableReferencing());

        Assert.Equal(ReferenceRepairEligibility.Rejected, plan.Eligibility);
        Assert.Contains("Unverified", plan.EligibilityDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(RepairTargetOutcome.NoManagedCopyFound)]
    [InlineData(RepairTargetOutcome.TargetVersionNotLocal)]
    [InlineData(RepairTargetOutcome.TargetCopyUnverified)]
    [InlineData(RepairTargetOutcome.TargetFileMissing)]
    public void Missing_or_unusable_target_binary_is_not_repairable(RepairTargetOutcome outcome)
    {
        var plan = ReferenceRepairPlanner.Plan(
            Stale(), RepairTargetResolution.Failure(outcome, "n/a"), WritableReferencing());
        Assert.Equal(ReferenceRepairEligibility.NotRepairable, plan.Eligibility);
    }

    [Fact]
    public void Target_that_exists_only_as_a_manifest_entry_but_not_on_disk_is_not_repairable()
    {
        var plan = ReferenceRepairPlanner.Plan(
            Stale(), VerifiedTarget(exists: false), WritableReferencing());
        Assert.Equal(ReferenceRepairEligibility.NotRepairable, plan.Eligibility);
    }

    // ---- rejected (an identity / target / writability check FAILED) --

    [Fact]
    public void Target_cadDocumentId_mismatch_is_rejected()
    {
        var plan = ReferenceRepairPlanner.Plan(
            Stale(cad: "cad_a", latest: "fv_v3"),
            VerifiedTarget(cad: "cad_DIFFERENT", fv: "fv_v3"),
            WritableReferencing());
        Assert.Equal(ReferenceRepairEligibility.Rejected, plan.Eligibility);
        Assert.Contains("cadDocumentId", plan.EligibilityDetail);
    }

    [Fact]
    public void Target_fileVersionId_mismatch_is_rejected()
    {
        var plan = ReferenceRepairPlanner.Plan(
            Stale(cad: "cad_a", latest: "fv_v3"),
            VerifiedTarget(cad: "cad_a", fv: "fv_v2"),
            WritableReferencing());
        Assert.Equal(ReferenceRepairEligibility.Rejected, plan.Eligibility);
        Assert.Contains("fileVersionId", plan.EligibilityDetail);
    }

    [Fact]
    public void A_filename_only_candidate_is_rejected()
    {
        var filenameOnly = RepairTargetResolution.Found(new RepairTargetCandidate(
            "cad_a", "fv_v3", P("Arch", "Latest", "PART-A.ipt"),
            RepairTargetIdentitySource.Unproven, ExistsOnDisk: true, WorkspaceRoot: P("Arch", "Latest"),
            BinaryIntegrityVerified: true));

        var plan = ReferenceRepairPlanner.Plan(
            Stale(cad: "cad_a", latest: "fv_v3"), filenameOnly, WritableReferencing());

        Assert.Equal(ReferenceRepairEligibility.Rejected, plan.Eligibility);
        Assert.Contains("filename", plan.EligibilityDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_ambiguous_target_resolution_is_rejected()
    {
        var plan = ReferenceRepairPlanner.Plan(
            Stale(),
            RepairTargetResolution.Failure(RepairTargetOutcome.AmbiguousTargets, "two paths"),
            WritableReferencing());
        Assert.Equal(ReferenceRepairEligibility.Rejected, plan.Eligibility);
    }

    // ---- P5C-B: server-authoritative target integrity is the authority ----

    [Fact]
    public void No_server_authoritative_target_integrity_metadata_is_not_repairable_fail_closed()
    {
        // A STALE assessment whose authoritative response carried NO canonical
        // integrity metadata (server size -1 / blank checksum).
        var assessment = Assess(ManagedRef("cad_a", "fv_v1"),
            LatestVersionLookup.FromResults(new[] { LatestVersionResult.Found("cad_a", "fv_v3") }));

        var plan = ReferenceRepairPlanner.Plan(assessment, VerifiedTarget(cad: "cad_a", fv: "fv_v3"), WritableReferencing());

        Assert.Equal(ReferenceRepairEligibility.NotRepairable, plan.Eligibility);
        Assert.Contains("server", plan.EligibilityDetail, StringComparison.OrdinalIgnoreCase);
        Assert.False(plan.HasServerAuthoritativeTargetIntegrity);
    }

    [Fact]
    public void An_uppercase_or_short_server_checksum_is_not_canonical_and_is_not_repairable()
    {
        var assessment = Assess(ManagedRef("cad_a", "fv_v1"),
            LatestVersionLookup.FromResults(new[]
            {
                LatestVersionResult.Found("cad_a", "fv_v3", 4096, "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789AB"),
            }));

        var plan = ReferenceRepairPlanner.Plan(assessment, VerifiedTarget(cad: "cad_a", fv: "fv_v3"), WritableReferencing());

        Assert.Equal(ReferenceRepairEligibility.NotRepairable, plan.Eligibility);
    }

    [Fact]
    public void A_manifest_that_disagrees_with_the_server_integrity_is_rejected_never_reconciled()
    {
        var plan = ReferenceRepairPlanner.Plan(
            Stale(cad: "cad_a", latest: "fv_v3"),
            RepairTargetResolution.Failure(RepairTargetOutcome.TargetManifestServerMismatch, "manifest disagrees"),
            WritableReferencing());

        Assert.Equal(ReferenceRepairEligibility.Rejected, plan.Eligibility);
        Assert.Contains("manifest", plan.EligibilityDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authoritative", plan.EligibilityDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_target_whose_current_bytes_do_not_match_the_server_metadata_is_rejected()
    {
        var plan = ReferenceRepairPlanner.Plan(
            Stale(cad: "cad_a", latest: "fv_v3"),
            RepairTargetResolution.Failure(RepairTargetOutcome.TargetBinaryVerificationFailed, "hash mismatch"),
            WritableReferencing());

        Assert.Equal(ReferenceRepairEligibility.Rejected, plan.Eligibility);
    }

    [Fact]
    public void A_candidate_verified_against_DIFFERENT_server_integrity_than_the_assessment_is_rejected()
    {
        // The assessment says the server size is 4096; the resolved candidate was
        // verified against a different size. Fail closed.
        var mismatchedCandidate = RepairTargetResolution.Found(new RepairTargetCandidate(
            "cad_a", "fv_v3", P("Arch", "Latest", "PART-A.ipt"),
            RepairTargetIdentitySource.WorkspaceManifestVerified, ExistsOnDisk: true,
            WorkspaceRoot: P("Arch", "Latest"),
            BinaryIntegrityVerified: true, ManifestAgreesWithServer: true,
            ServerFileSize: 9999, ServerSha256: TargetSha));

        var plan = ReferenceRepairPlanner.Plan(
            Stale(cad: "cad_a", latest: "fv_v3"), mismatchedCandidate, WritableReferencing());

        Assert.Equal(ReferenceRepairEligibility.Rejected, plan.Eligibility);
    }

    [Fact]
    public void An_eligible_plan_carries_the_server_authoritative_target_integrity()
    {
        var plan = ReferenceRepairPlanner.Plan(
            Stale(cad: "cad_a", pinned: "fv_v1", latest: "fv_v3"),
            VerifiedTarget(cad: "cad_a", fv: "fv_v3"),
            WritableReferencing());

        Assert.True(plan.CanProceed);
        Assert.True(plan.HasServerAuthoritativeTargetIntegrity);
        Assert.Equal(TargetSize, plan.ServerAuthoritativeTargetFileSize);
        Assert.Equal(TargetSha, plan.ServerAuthoritativeTargetSha256);
    }

    [Fact]
    public void A_read_only_or_not_checked_out_referencing_document_is_rejected()
    {
        var plan = ReferenceRepairPlanner.Plan(
            Stale(cad: "cad_a", latest: "fv_v3"),
            VerifiedTarget(cad: "cad_a", fv: "fv_v3"),
            ReadOnlyReferencing());

        Assert.Equal(ReferenceRepairEligibility.Rejected, plan.Eligibility);
        Assert.False(plan.CanProceed);
        Assert.Contains("not checked out", plan.EligibilityDetail, StringComparison.OrdinalIgnoreCase);
        // still shows the proposed target so the engineer knows what it would do
        Assert.Equal(P("Arch", "Latest", "PART-A.ipt"), plan.ProposedTargetPath);
    }

    [Fact]
    public void Planning_is_deterministic()
    {
        var a = ReferenceRepairPlanner.Plan(Stale(), VerifiedTarget(), WritableReferencing());
        var b = ReferenceRepairPlanner.Plan(Stale(), VerifiedTarget(), WritableReferencing());
        Assert.Equal(a.Eligibility, b.Eligibility);
        Assert.Equal(a.EligibilityDetail, b.EligibilityDetail);
        Assert.Equal(a.RepairReason, b.RepairReason);
        Assert.Equal(a.ProposedTargetPath, b.ProposedTargetPath);
        Assert.Equal(a.Notes, b.Notes);
    }

    [Fact]
    public void Preview_text_carries_the_key_facts_and_no_secret()
    {
        var text = ReferenceRepairTextReport.Render(
            ReferenceRepairPlanner.Plan(Stale(cad: "cad_a", pinned: "fv_v1", latest: "fv_v3"),
                VerifiedTarget(cad: "cad_a", fv: "fv_v3"), WritableReferencing()));

        Assert.Contains("cad_a", text);
        Assert.Contains("fv_v1", text);
        Assert.Contains("fv_v3", text);
        Assert.Contains("ELIGIBLE", text);
        Assert.DoesNotContain("Bearer", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
    }
}
