using Arch.CadConnect.Core;
using Arch.CadConnect.Core.References;

namespace Arch.CadConnect.Core.Tests.References.Repair;

/// <summary>
/// Shared deterministic, COM-free builders for the P5C repair tests: a managed
/// reference edge, its P5B-A health entry, and its P5B-B version assessment.
/// </summary>
internal static class RepairFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A canonical (64 lowercase hex) server-authoritative SHA-256 for
    ///  the authoritative target FileVersion in the fixtures.</summary>
    public static readonly string TargetSha = new('a', 64);
    public const long TargetSize = 4096;

    private static readonly string Base = OperatingSystem.IsWindows() ? @"C:\" : "/";

    public static string P(params string[] segments) =>
        Path.GetFullPath(Path.Combine(Base, Path.Combine(segments)));

    public static string RootIam => P("Arch", "Job1", "ROOT.iam");

    public static CadReference ManagedRef(
        string cad = "cad_a", string fv = "fv_v1", string rel = "PART-A.ipt", string? parent = null,
        bool verified = true)
        => new()
        {
            ParentAbsolutePath = parent ?? RootIam,
            InventorReportedName = rel,
            ResolvedAbsolutePath = P("Arch", "Job1", rel),
            ReferenceType = CadDocumentType.Ipt,
            RelationshipKind = CadRelationshipKind.Component,
            Resolution = CadReferenceResolution.Resolved,
            Scope = ReferenceWorkspaceScope.InsideWorkspace,
            ManifestIdentity = new CadManifestIdentity(cad, fv, "DOC-" + cad, rel, verified),
        };

    public static CadReference UnmanagedRef()
        => new()
        {
            ParentAbsolutePath = RootIam,
            InventorReportedName = "SCRATCH.ipt",
            ResolvedAbsolutePath = P("Arch", "Job1", "SCRATCH.ipt"),
            ReferenceType = CadDocumentType.Ipt,
            RelationshipKind = CadRelationshipKind.Component,
            Resolution = CadReferenceResolution.Resolved,
            Scope = ReferenceWorkspaceScope.InsideWorkspace,
            ManifestIdentity = null,
        };

    public static CadReference MissingRef()
        => new()
        {
            ParentAbsolutePath = RootIam,
            InventorReportedName = "MOTOR.ipt",
            ResolvedAbsolutePath = null,
            ReferenceType = CadDocumentType.Ipt,
            RelationshipKind = CadRelationshipKind.Component,
            Resolution = CadReferenceResolution.Unresolved,
            Scope = ReferenceWorkspaceScope.Unknown,
            ManifestIdentity = null,
        };

    public static ReferenceVersionAssessment Assess(CadReference reference, ILatestVersionOracle oracle)
        => ReferenceVersionClassifier.Assess(
            ReferenceHealthDiagnoser.Evaluate(reference, childEnumerated: true), oracle);

    /// <summary>A STALE assessment for <paramref name="reference"/>: pinned
    ///  local <c>fv_v1</c>, authoritative latest <c>latest</c>.</summary>
    public static ReferenceVersionAssessment Stale(
        string cad = "cad_a", string pinned = "fv_v1", string latest = "fv_v3",
        long serverSize = TargetSize, string? serverSha = null)
        => Assess(ManagedRef(cad, pinned),
            LatestVersionLookup.FromResults(new[]
            {
                LatestVersionResult.Found(cad, latest, serverSize, serverSha ?? TargetSha),
            }));

    public static ReferenceVersionAssessment Current(string cad = "cad_a", string fv = "fv_v3")
        => Assess(ManagedRef(cad, fv),
            LatestVersionLookup.FromResults(new[] { LatestVersionResult.Found(cad, fv, TargetSize, TargetSha) }));

    public static ReferenceVersionAssessment UnknownVersion(string cad = "cad_a", string pinned = "fv_v1")
        => Assess(ManagedRef(cad, pinned),
            LatestVersionLookup.WholeFailure(LatestVersionOutcome.ServerUnavailable));

    public static RepairReferencingContext WritableReferencing(string? path = null)
        => new(path ?? RootIam, true, "checked out by you (test).");

    public static RepairReferencingContext ReadOnlyReferencing(string? path = null)
        => new(path ?? RootIam, false, "The referencing document is a controlled managed file and is NOT checked out.");

    public static RepairTargetResolution VerifiedTarget(
        string cad = "cad_a", string fv = "fv_v3", string? path = null, bool exists = true,
        long serverSize = TargetSize, string? serverSha = null)
        => RepairTargetResolution.Found(new RepairTargetCandidate(
            cad, fv, path ?? P("Arch", "Latest", "PART-A.ipt"),
            RepairTargetIdentitySource.WorkspaceManifestVerified, exists, P("Arch", "Latest"),
            BinaryIntegrityVerified: true,
            ManifestAgreesWithServer: true,
            ServerFileSize: serverSize,
            ServerSha256: serverSha ?? TargetSha));
}
