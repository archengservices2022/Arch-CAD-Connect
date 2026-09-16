using Arch.CadConnect.Core;
using Arch.CadConnect.Core.References;

namespace Arch.CadConnect.Core.Tests.CopyDesign;

/// <summary>Shared deterministic, COM-free builders for P6A Copy Design
///  planner tests - mirrors the style of References/Repair/RepairFixtures.cs.</summary>
internal static class CopyDesignFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly string Base = OperatingSystem.IsWindows() ? @"C:\" : "/";

    public static string P(params string[] segments) =>
        Path.GetFullPath(Path.Combine(Base, Path.Combine(segments)));

    /// <summary>
    /// A scan root. Pass <paramref name="cadDocumentId"/> as <c>null</c> for an
    /// UNMANAGED root (no manifest identity at all); pass
    /// <paramref name="verified"/> as <c>false</c> for a MANAGED but UNVERIFIED
    /// root (an Unverified manifest entry - P6A Round 2, Blocker 1); pass
    /// <paramref name="fv"/> as <c>null</c>/empty/whitespace for a managed,
    /// verified root with an INCOMPLETE identity (P6A Round 2, Blocker 2).
    /// </summary>
    public static CadReferenceRoot Root(
        string path, string? cadDocumentId = "cad_root", string? fv = "fv_root1", CadDocumentType type = CadDocumentType.Iam,
        bool verified = true) =>
        new(path, type, cadDocumentId is null ? null : new PlmIdentity(cadDocumentId, null, fv), verified);

    public static CadReferenceNode Node(string path, CadDocumentType type, bool enumerated = true) =>
        new(path, type, enumerated);

    /// <summary>A RESOLVED, VERIFIED-managed direct reference. Pass
    ///  <paramref name="fv"/> as <c>null</c>/empty/whitespace to simulate an
    ///  INCOMPLETE identity (P6A Round 2, Blocker 2). Pass
    ///  <paramref name="parentIdentity"/> when the PARENT's own stable
    ///  identity matters to the test (e.g. P6A Round 5's drawing-owner-
    ///  consensus tests, where the edge's parent key must match the actual
    ///  root/parent node's "id:" key rather than falling back to a "path:"
    ///  key) - the real <c>CadReferenceScanner</c> always threads this
    ///  through; most fixture-built scans do not need to.</summary>
    public static CadReference Managed(
        string parent, string resolvedPath, string cadDocumentId, string? fv,
        CadDocumentType type = CadDocumentType.Ipt, CadRelationshipKind kind = CadRelationshipKind.Component,
        bool verified = true, PlmIdentity? parentIdentity = null) => new()
        {
            ParentAbsolutePath = parent,
            ParentIdentity = parentIdentity,
            InventorReportedName = Path.GetFileName(resolvedPath),
            ResolvedAbsolutePath = resolvedPath,
            ReferenceType = type,
            RelationshipKind = kind,
            Resolution = CadReferenceResolution.Resolved,
            Scope = ReferenceWorkspaceScope.InsideWorkspace,
            ManifestIdentity = new CadManifestIdentity(cadDocumentId, fv!, "DOC-" + cadDocumentId, Path.GetFileName(resolvedPath), verified),
        };

    /// <summary>A RESOLVED reference with NO manifest-matched identity at all.</summary>
    public static CadReference Unmanaged(
        string parent, string resolvedPath, CadDocumentType type = CadDocumentType.Ipt,
        CadRelationshipKind kind = CadRelationshipKind.Component) => new()
        {
            ParentAbsolutePath = parent,
            InventorReportedName = Path.GetFileName(resolvedPath),
            ResolvedAbsolutePath = resolvedPath,
            ReferenceType = type,
            RelationshipKind = kind,
            Resolution = CadReferenceResolution.Resolved,
            Scope = ReferenceWorkspaceScope.InsideWorkspace,
            ManifestIdentity = null,
        };

    /// <summary>An UNRESOLVED reference - Inventor reports it but no file could be located.</summary>
    public static CadReference Unresolved(
        string parent, string reportedName, CadRelationshipKind kind = CadRelationshipKind.Component) => new()
        {
            ParentAbsolutePath = parent,
            InventorReportedName = reportedName,
            InventorReportedFullPath = reportedName,
            ResolvedAbsolutePath = null,
            ReferenceType = CadDocumentType.Unknown,
            RelationshipKind = kind,
            Resolution = CadReferenceResolution.Unresolved,
            Scope = ReferenceWorkspaceScope.Unknown,
            ManifestIdentity = null,
        };

    public static CadReferenceScan Scan(
        CadReferenceRoot root, IReadOnlyList<CadReference> references, IReadOnlyList<CadReferenceNode>? nodes = null) =>
        new(root, references, nodes ?? new[] { new CadReferenceNode(root.AbsolutePath, root.DocumentType, true) }, Now);
}
