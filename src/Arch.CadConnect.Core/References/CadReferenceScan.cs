namespace Arch.CadConnect.Core.References;

/// <summary>
/// The COM-free result of a P5A reference scan: what references Inventor
/// actually reports for a root document, and how each one relates to the
/// managed workspace. This is OBSERVED INVENTOR REFERENCE DATA - it is NOT
/// authoritative dependency truth. The server's <c>CadDependency</c> graph
/// remains the EXPECTED graph; a later P5 milestone compares the two and
/// decides how to reconcile. Nothing here ever mutates a document, a file, or
/// a PLM record.
/// </summary>
public sealed record CadReferenceScan(
    CadReferenceRoot Root,
    IReadOnlyList<CadReference> References,
    IReadOnlyList<CadReferenceNode> Nodes,
    DateTimeOffset ScannedAtUtc)
{
    public CadReferenceScanSummary Summary => CadReferenceScanSummary.From(this);

    /// <summary>
    /// True when EVERY document the scan visited had its direct references
    /// successfully enumerated. False when at least one visited document could
    /// not be inspected - its sub-graph is unknown and the scan is PARTIAL.
    /// </summary>
    public bool IsComplete => Nodes.All(n => n.Enumerated);

    /// <summary>The visited documents whose direct references could NOT be
    ///  read. A user must not treat any of these as a proven leaf.</summary>
    public IReadOnlyList<CadReferenceNode> UnenumeratedNodes =>
        Nodes.Where(n => !n.Enumerated).ToArray();

    /// <summary>True when the resolved target of <paramref name="reference"/> is
    ///  a document the scan visited but could not enumerate.</summary>
    public bool TargetIsUnenumerated(CadReference reference) =>
        reference.ResolvedAbsolutePath is { } path
        && Nodes.Any(n => !n.Enumerated
            && string.Equals(n.AbsolutePath, path, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One document the scan actually visited (the root, or a resolved reference
/// target it dequeued), and whether Inventor could describe its DIRECT
/// references. Recorded once per document - the traversal's cycle / repeated
/// sub-assembly guard makes the set deterministic and free of duplicates.
/// </summary>
public sealed record CadReferenceNode(
    string AbsolutePath,
    CadDocumentType DocumentType,
    bool Enumerated);

/// <summary>The document the scan started from.</summary>
public sealed record CadReferenceRoot(
    string AbsolutePath,
    CadDocumentType DocumentType,
    /// <summary>The stable Arch identity of the root, IF its exact path is bound
    ///  in a verified workspace manifest. Never filename-inferred.</summary>
    PlmIdentity? Identity);

/// <summary>
/// One direct parent -> child reference edge exactly as Inventor reports it.
/// The scan result is a graph of these edges (each direct edge recorded once),
/// never a flattened list.
/// </summary>
public sealed record CadReference
{
    /// <summary>Canonical absolute path of the document that owns this reference.</summary>
    public required string ParentAbsolutePath { get; init; }

    /// <summary>The parent's stable Arch identity, if known (manifest-bound).</summary>
    public PlmIdentity? ParentIdentity { get; init; }

    /// <summary>The reference name / path descriptor exactly as Inventor named it.</summary>
    public required string InventorReportedName { get; init; }

    /// <summary>A fuller path form of the descriptor, when Inventor exposes one.</summary>
    public string? InventorReportedFullPath { get; init; }

    /// <summary>The canonical absolute path Inventor actually resolves this
    ///  reference to. Null when the reference is unresolved - never fabricated.</summary>
    public string? ResolvedAbsolutePath { get; init; }

    /// <summary>The referenced document's type (from the resolved file, else the
    ///  Inventor descriptor). <see cref="CadDocumentType.Unknown"/> when neither
    ///  is available.</summary>
    public required CadDocumentType ReferenceType { get; init; }

    public required CadRelationshipKind RelationshipKind { get; init; }

    public required CadReferenceResolution Resolution { get; init; }

    public required ReferenceWorkspaceScope Scope { get; init; }

    /// <summary>
    /// The EXACT managed identity of the resolved target - present ONLY when the
    /// resolved canonical path matches an entry in the containing workspace
    /// manifest by <c>root + relativePath</c>. A same-named file OUTSIDE the
    /// workspace, or any filename coincidence, never produces this.
    /// </summary>
    public CadManifestIdentity? ManifestIdentity { get; init; }

    public bool IsResolved => Resolution == CadReferenceResolution.Resolved;

    public bool IsDirectChildOf(string rootAbsolutePath) =>
        string.Equals(ParentAbsolutePath, rootAbsolutePath, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The exact managed identity a resolved reference maps to. Every field comes
/// verbatim from the matched workspace-manifest entry - none is guessed.
/// </summary>
public sealed record CadManifestIdentity(
    string CadDocumentId,
    string FileVersionId,
    string DocumentNumber,
    string RelativePath);

public enum CadReferenceResolution
{
    /// <summary>Inventor resolves the reference to a real file on disk.</summary>
    Resolved,

    /// <summary>Inventor reports the reference but cannot resolve a file for it
    ///  (missing / broken link). A first-class scan result, never omitted.</summary>
    Unresolved,
}

public enum ReferenceWorkspaceScope
{
    /// <summary>Unresolved, or no managed workspace root is known - containment
    ///  cannot be decided.</summary>
    Unknown,

    /// <summary>The resolved path sits strictly under a known workspace root
    ///  (separator-safe, case-insensitive).</summary>
    InsideWorkspace,

    /// <summary>The resolved path is outside every known workspace root.</summary>
    OutsideWorkspace,
}

/// <summary>
/// The kind of relationship an edge represents, derived ONLY from the parent
/// and child document TYPES - never from a filename token.
/// </summary>
public enum CadRelationshipKind
{
    /// <summary>An assembly component: IAM -> IPT or IAM -> IAM.</summary>
    Component,

    /// <summary>A drawing's model reference: IDW/DWG -> IPT or IDW/DWG -> IAM.</summary>
    DrawingModel,

    /// <summary>A reference exists but cannot be safely classified.</summary>
    Other,
}

/// <summary>Counts derived from a <see cref="CadReferenceScan"/>.</summary>
public sealed record CadReferenceScanSummary(
    int DirectReferences,
    int TotalReferences,
    int Resolved,
    int Unresolved,
    int InsideWorkspace,
    int OutsideWorkspace,
    int WithManifestIdentity,
    bool IsComplete,
    int DocumentsNotEnumerated)
{
    public static CadReferenceScanSummary From(CadReferenceScan scan)
    {
        var refs = scan.References;
        return new CadReferenceScanSummary(
            DirectReferences: refs.Count(r => r.IsDirectChildOf(scan.Root.AbsolutePath)),
            TotalReferences: refs.Count,
            Resolved: refs.Count(r => r.Resolution == CadReferenceResolution.Resolved),
            Unresolved: refs.Count(r => r.Resolution == CadReferenceResolution.Unresolved),
            InsideWorkspace: refs.Count(r => r.Scope == ReferenceWorkspaceScope.InsideWorkspace),
            OutsideWorkspace: refs.Count(r => r.Scope == ReferenceWorkspaceScope.OutsideWorkspace),
            WithManifestIdentity: refs.Count(r => r.ManifestIdentity is not null),
            IsComplete: scan.IsComplete,
            DocumentsNotEnumerated: scan.Nodes.Count(n => !n.Enumerated));
    }
}
