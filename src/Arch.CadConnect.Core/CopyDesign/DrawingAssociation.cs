namespace Arch.CadConnect.Core.CopyDesign;

/// <summary>Why <see cref="IDrawingAssociationSource.GetAssociatedDrawings"/>
///  did or did not return drawings.</summary>
public enum DrawingAssociationOutcome
{
    /// <summary>No authoritative source of "which drawings reference this
    ///  model" exists yet (P5A's scanner only walks references OUTWARD from a
    ///  document - i.e. what a document depends on - never INWARD - what
    ///  depends on it - which is what finding "the drawing for this model"
    ///  actually needs; that requires a reverse-reference / Where-Used
    ///  authority P6A does not have). This is reported explicitly - it is
    ///  NEVER papered over with a filename guess.</summary>
    NotAvailable,

    /// <summary>An authoritative source found (zero or more) associated
    ///  drawings for the model.</summary>
    Found,
}

/// <summary>
/// ONE drawing (.idw / Inventor .dwg) an authoritative source associates with
/// a given model. BLOCKER 2 (P6A Round 3): every field the planner needs to
/// make a SAFE decision is carried here explicitly - the planner NEVER
/// hardcodes managed/verified/resolved for a drawing the way earlier rounds
/// did. An association source that cannot prove one of these facts must
/// report it honestly (null/false) rather than let the planner assume the
/// best case; an incomplete or unverified drawing observation is planned
/// EXACTLY like an incomplete/unverified managed reference - it fails closed
/// to <see cref="CopyDesignAction.NeedsDecision"/>, never silently to COPY.
/// </summary>
public sealed record AssociatedDrawing(
    /// <summary>The drawing's stable Arch document id, if the source can
    ///  prove one. Null/blank means NO stable identity - planned exactly like
    ///  an unmanaged CAD reference (never Copy/Reuse).</summary>
    string? CadDocumentId,
    /// <summary>The drawing's pinned FileVersion id, if known. Null/blank
    ///  means an INCOMPLETE identity - fails closed exactly like a managed
    ///  reference with no FileVersionId.</summary>
    string? CurrentFileVersionId,
    /// <summary>The drawing's resolved absolute path, if the source could
    ///  locate the actual file. Null/blank means UNRESOLVED - the association
    ///  is claimed but the file itself could not be found, exactly like an
    ///  unresolved CAD reference.</summary>
    string? AbsolutePath,
    CadDocumentType DocumentType,
    /// <summary>Whether the association source's binding for THIS drawing is
    ///  currently verified/trusted. This can NEVER be inferred from anything
    ///  else the source provides - a source that cannot prove it must report
    ///  <c>false</c>, not default to "verified".</summary>
    bool IsVerified,
    /// <summary>Free-text description of the evidence/authority behind this
    ///  association (e.g. "Arch server reverse-reference"), for diagnostics
    ///  only. Never changes any safety decision - the planner's action
    ///  selection depends only on the typed fields above.</summary>
    string? AssociationEvidence = null);

public sealed record DrawingAssociationResult(
    DrawingAssociationOutcome Outcome,
    IReadOnlyList<AssociatedDrawing> Drawings)
{
    public static readonly DrawingAssociationResult NotAvailable =
        new(DrawingAssociationOutcome.NotAvailable, Array.Empty<AssociatedDrawing>());
}

/// <summary>
/// The seam a later phase can implement once Arch exposes a real reverse-
/// reference / "Where Used" authority for drawings. P6A ships only
/// <see cref="NoDrawingAssociationSource"/>: it always reports
/// <see cref="DrawingAssociationOutcome.NotAvailable"/> - the planner then
/// represents that limitation explicitly in the plan's warnings instead of
/// guessing a drawing association from a similar file name.
/// </summary>
public interface IDrawingAssociationSource
{
    DrawingAssociationResult GetAssociatedDrawings(string cadDocumentId);
}

public sealed class NoDrawingAssociationSource : IDrawingAssociationSource
{
    public static readonly NoDrawingAssociationSource Instance = new();

    public DrawingAssociationResult GetAssociatedDrawings(string cadDocumentId) =>
        DrawingAssociationResult.NotAvailable;
}

/// <summary>
/// P6D: the real authority the class doc comment above anticipated, now that
/// Arch exposes one (the server's <c>DRAWING_REFERENCE</c> reverse-reference
/// lookup - see <c>Arch.CadConnect.Api.CopyDesign.HttpDrawingAssociationClient</c>).
/// <see cref="IDrawingAssociationSource.GetAssociatedDrawings"/> is SYNCHRONOUS
/// (the planner is pure and calls it inline during reconciliation), so the
/// actual HTTP round trip must happen BEFORE the planner runs - the caller
/// fetches every candidate model's association result ONCE, up front, and
/// wraps the result in this class. A model id the caller never asked about
/// (and therefore never populated) is reported EXACTLY like an explicit
/// <see cref="DrawingAssociationResult.NotAvailable"/> - never silently
/// "Found, zero drawings" - so a caller that forgot to prefetch an id fails
/// closed instead of understating the true (unknown) drawing set.
/// </summary>
public sealed class PrefetchedDrawingAssociationSource : IDrawingAssociationSource
{
    private readonly IReadOnlyDictionary<string, DrawingAssociationResult> _byModelId;

    public PrefetchedDrawingAssociationSource(IReadOnlyDictionary<string, DrawingAssociationResult> byModelId)
    {
        _byModelId = byModelId ?? throw new ArgumentNullException(nameof(byModelId));
    }

    public DrawingAssociationResult GetAssociatedDrawings(string cadDocumentId) =>
        _byModelId.TryGetValue(cadDocumentId, out var result) ? result : DrawingAssociationResult.NotAvailable;
}
