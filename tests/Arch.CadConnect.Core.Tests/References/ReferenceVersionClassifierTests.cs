using Arch.CadConnect.Core;
using Arch.CadConnect.Core.References;

namespace Arch.CadConnect.Core.Tests.References;

/// <summary>
/// Deterministic, COM-free coverage of the P5B-B authoritative version
/// classification: exact identity + pinned local fileVersionId + authenticated
/// authoritative server result -> CURRENT / STALE / UNKNOWN VERSION, always
/// failing closed.
/// </summary>
public class ReferenceVersionClassifierTests
{
    private static readonly string Base = OperatingSystem.IsWindows() ? @"C:\" : "/";

    private static string P(params string[] segments) => Path.GetFullPath(Path.Combine(Base, Path.Combine(segments)));

    private static CadReference Managed(string cad = "cad_a", string fv = "fv_a", string rel = "PART-A.ipt")
        => new()
        {
            ParentAbsolutePath = P("Arch", "Job1", "ROOT.iam"),
            InventorReportedName = rel,
            ResolvedAbsolutePath = P("Arch", "Job1", rel),
            ReferenceType = CadDocumentType.Ipt,
            RelationshipKind = CadRelationshipKind.Component,
            Resolution = CadReferenceResolution.Resolved,
            Scope = ReferenceWorkspaceScope.InsideWorkspace,
            ManifestIdentity = new CadManifestIdentity(cad, fv, "DOC-" + cad, rel),
        };

    private static CadReference Unmanaged() => new()
    {
        ParentAbsolutePath = P("Arch", "Job1", "ROOT.iam"),
        InventorReportedName = "SCRATCH.ipt",
        ResolvedAbsolutePath = P("Arch", "Job1", "SCRATCH.ipt"),
        ReferenceType = CadDocumentType.Ipt,
        RelationshipKind = CadRelationshipKind.Component,
        Resolution = CadReferenceResolution.Resolved,
        Scope = ReferenceWorkspaceScope.InsideWorkspace,
        ManifestIdentity = null,
    };

    private static CadReference Missing() => new()
    {
        ParentAbsolutePath = P("Arch", "Job1", "ROOT.iam"),
        InventorReportedName = "MOTOR.ipt",
        ResolvedAbsolutePath = null,
        ReferenceType = CadDocumentType.Ipt,
        RelationshipKind = CadRelationshipKind.Component,
        Resolution = CadReferenceResolution.Unresolved,
        Scope = ReferenceWorkspaceScope.Unknown,
        ManifestIdentity = null,
    };

    private static ReferenceHealthEntry Entry(CadReference reference)
        => ReferenceHealthDiagnoser.Evaluate(reference, childEnumerated: true);

    private static ReferenceVersionAssessment Assess(CadReference reference, ILatestVersionOracle oracle)
        => ReferenceVersionClassifier.Assess(Entry(reference), oracle);

    // ---- CURRENT / STALE from authoritative comparison ----------------

    [Fact]
    public void Exact_pinned_fileVersionId_equals_authoritative_latest_is_CURRENT()
    {
        var oracle = LatestVersionLookup.FromResults(new[] { LatestVersionResult.Found("cad_a", "fv_a") });

        var a = Assess(Managed("cad_a", "fv_a"), oracle);

        Assert.True(a.Applicable);
        Assert.Equal(PlmVersionStatus.Current, a.Status);
        Assert.Equal("fv_a", a.AuthoritativeLatestFileVersionId);
        Assert.Equal(ReferenceHealth.Healthy, a.HealthContribution);
    }

    [Fact]
    public void Exact_pinned_fileVersionId_differs_from_authoritative_latest_is_STALE()
    {
        var oracle = LatestVersionLookup.FromResults(new[] { LatestVersionResult.Found("cad_a", "fv_a_v3") });

        var a = Assess(Managed("cad_a", "fv_a_v1"), oracle);

        Assert.Equal(PlmVersionStatus.Stale, a.Status);
        Assert.Equal("fv_a_v3", a.AuthoritativeLatestFileVersionId);
        Assert.Equal(ReferenceHealth.Warning, a.HealthContribution);
    }

    // ---- fail-closed to UNKNOWN VERSION ------------------------------

    [Fact]
    public void Unresolved_reference_is_UNKNOWN_VERSION_not_applicable()
    {
        var a = Assess(Missing(), AnyFound());
        Assert.False(a.Applicable);
        Assert.Equal(PlmVersionStatus.UnknownVersion, a.Status);
        Assert.Equal(ReferenceHealth.Healthy, a.HealthContribution); // does not raise; the edge is already ERROR
    }

    [Fact]
    public void Unmanaged_reference_is_UNKNOWN_VERSION_not_applicable()
    {
        var a = Assess(Unmanaged(), AnyFound());
        Assert.False(a.Applicable);
        Assert.Equal(PlmVersionStatus.UnknownVersion, a.Status);
    }

    [Fact]
    public void Missing_local_stable_cadDocumentId_is_UNKNOWN_VERSION()
    {
        var a = Assess(Managed(cad: "   ", fv: "fv_a"), AnyFound());
        Assert.True(a.Applicable);
        Assert.Equal(PlmVersionStatus.UnknownVersion, a.Status);
        Assert.Equal(ReferenceHealth.Unknown, a.HealthContribution);
    }

    [Fact]
    public void Missing_pinned_local_fileVersionId_is_UNKNOWN_VERSION()
    {
        var a = Assess(Managed(cad: "cad_a", fv: ""), LatestVersionLookup.FromResults(
            new[] { LatestVersionResult.Found("cad_a", "fv_a") }));
        Assert.True(a.Applicable);
        Assert.Equal(PlmVersionStatus.UnknownVersion, a.Status);
    }

    [Fact]
    public void Server_unavailable_is_UNKNOWN_VERSION()
    {
        var a = Assess(Managed(), LatestVersionLookup.WholeFailure(LatestVersionOutcome.ServerUnavailable));
        Assert.Equal(PlmVersionStatus.UnknownVersion, a.Status);
        Assert.Contains(a.Reasons, r => r.Contains("could not be reached", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Authentication_failure_is_UNKNOWN_VERSION()
    {
        var a = Assess(Managed(), LatestVersionLookup.WholeFailure(LatestVersionOutcome.AuthenticationFailed));
        Assert.Equal(PlmVersionStatus.UnknownVersion, a.Status);
        Assert.Contains(a.Reasons, r => r.Contains("Authentication", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unknown_server_cad_document_is_UNKNOWN_VERSION()
    {
        // The id was queried but the server did not return / recognize it.
        var a = Assess(Managed("cad_missing", "fv_x"), LatestVersionLookup.FromResults(
            Array.Empty<LatestVersionResult>()));
        Assert.Equal(PlmVersionStatus.UnknownVersion, a.Status);
    }

    [Fact]
    public void Lookup_unavailable_endpoint_is_UNKNOWN_VERSION()
    {
        var a = Assess(Managed(), LatestVersionLookup.WholeFailure(LatestVersionOutcome.LookupUnavailable));
        Assert.Equal(PlmVersionStatus.UnknownVersion, a.Status);
    }

    [Fact]
    public void Malformed_authoritative_response_is_UNKNOWN_VERSION()
    {
        // Outcome says Found, but the identity in the payload does not match.
        var mismatched = new LatestVersionResult("cad_a", LatestVersionOutcome.Found,
            new AuthoritativeLatestVersion("cad_DIFFERENT", "fv_a"));
        var a = Assess(Managed("cad_a", "fv_a"), LatestVersionLookup.FromResults(new[] { mismatched }));
        Assert.Equal(PlmVersionStatus.UnknownVersion, a.Status);
        Assert.DoesNotContain(a.Reasons, r => r.Contains("IS the authoritative latest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Found_outcome_with_empty_latest_id_is_UNKNOWN_VERSION()
    {
        var empty = new LatestVersionResult("cad_a", LatestVersionOutcome.Found,
            new AuthoritativeLatestVersion("cad_a", ""));
        var a = Assess(Managed("cad_a", "fv_a"), LatestVersionLookup.FromResults(new[] { empty }));
        Assert.Equal(PlmVersionStatus.UnknownVersion, a.Status);
    }

    [Fact]
    public void CURRENT_is_never_asserted_without_an_authoritative_match()
    {
        // Every non-Found oracle outcome must never produce CURRENT.
        foreach (var outcome in new[]
        {
            LatestVersionOutcome.NotAttempted, LatestVersionOutcome.ServerUnavailable,
            LatestVersionOutcome.AuthenticationFailed, LatestVersionOutcome.LookupUnavailable,
            LatestVersionOutcome.DocumentNotRecognized, LatestVersionOutcome.MalformedResponse,
        })
        {
            var a = Assess(Managed(), LatestVersionLookup.WholeFailure(outcome));
            Assert.NotEqual(PlmVersionStatus.Current, a.Status);
        }
    }

    [Fact]
    public void Classification_is_deterministic()
    {
        var oracle = LatestVersionLookup.FromResults(new[] { LatestVersionResult.Found("cad_a", "fv_a") });
        var first = Assess(Managed("cad_a", "fv_a"), oracle);
        var second = Assess(Managed("cad_a", "fv_a"), oracle);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Reasons, second.Reasons);
    }

    // ---- HIGH 1: opaque stable identifiers, never trimmed -----------

    private sealed class FixedOracle(LatestVersionResult result) : ILatestVersionOracle
    {
        public LatestVersionResult Get(string cadDocumentId) => result;
    }

    [Fact]
    public void Exact_opaque_ids_still_produce_CURRENT()
    {
        var oracle = LatestVersionLookup.FromResults(new[] { LatestVersionResult.Found("cad a b", "fv a-b_c") });
        var a = Assess(Managed("cad a b", "fv a-b_c"), oracle);
        Assert.Equal(PlmVersionStatus.Current, a.Status); // internal spaces are part of the opaque id, that's fine
    }

    [Fact]
    public void A_whitespace_padded_authoritative_latest_fileVersionId_is_NEVER_CURRENT()
    {
        // local "fv_a" vs authoritative " fv_a " must not be normalised into a match.
        var padded = new FixedOracle(new LatestVersionResult("cad_a", LatestVersionOutcome.Found,
            new AuthoritativeLatestVersion("cad_a", " fv_a ")));

        var a = Assess(Managed("cad_a", "fv_a"), padded);

        Assert.NotEqual(PlmVersionStatus.Current, a.Status);
        Assert.Equal(PlmVersionStatus.UnknownVersion, a.Status);
    }

    [Fact]
    public void A_whitespace_padded_authoritative_cadDocumentId_cannot_match()
    {
        var padded = new FixedOracle(new LatestVersionResult("cad_a", LatestVersionOutcome.Found,
            new AuthoritativeLatestVersion(" cad_a ", "fv_a")));

        var a = Assess(Managed("cad_a", "fv_a"), padded);

        Assert.Equal(PlmVersionStatus.UnknownVersion, a.Status);
    }

    [Fact]
    public void A_whitespace_only_local_cadDocumentId_fails_closed()
    {
        var a = Assess(Managed(cad: " \t ", fv: "fv_a"), AnyFound());
        Assert.True(a.Applicable);
        Assert.Equal(PlmVersionStatus.UnknownVersion, a.Status);
    }

    [Fact]
    public void A_whitespace_padded_local_cadDocumentId_fails_closed()
    {
        var a = Assess(Managed(cad: " cad_a ", fv: "fv_a"), AnyFound());
        Assert.Equal(PlmVersionStatus.UnknownVersion, a.Status);
    }

    [Fact]
    public void A_whitespace_padded_local_fileVersionId_fails_closed()
    {
        var a = Assess(Managed(cad: "cad_a", fv: " fv_a "),
            LatestVersionLookup.FromResults(new[] { LatestVersionResult.Found("cad_a", "fv_a") }));
        Assert.Equal(PlmVersionStatus.UnknownVersion, a.Status);
    }

    [Fact]
    public void A_local_fv_that_would_only_equal_the_authoritative_latest_after_a_trim_is_STALE_not_CURRENT()
    {
        // local "  fv_a" (padded, so already fails closed) - but prove the
        // reverse too: authoritative "fv_a" vs a DIFFERENT local "fv_a_old"
        // stays STALE and a trim never rescues CURRENT.
        var oracle = LatestVersionLookup.FromResults(new[] { LatestVersionResult.Found("cad_a", "fv_a") });
        Assert.Equal(PlmVersionStatus.Stale, Assess(Managed("cad_a", "fv_a_old"), oracle).Status);
    }

    private static ILatestVersionOracle AnyFound() =>
        LatestVersionLookup.FromResults(new[] { LatestVersionResult.Found("cad_a", "fv_a") });
}
