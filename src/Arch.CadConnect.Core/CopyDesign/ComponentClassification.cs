namespace Arch.CadConnect.Core.CopyDesign;

/// <summary>
/// Whether a managed CAD document is known to be a project-specific design
/// (should be COPIED) or a standard/library/shared component (should be
/// REUSED), from EXISTING Arch information only - NEVER inferred from a
/// filename.
/// </summary>
public enum ComponentClassification
{
    /// <summary>No safe classification signal is available. The planner
    ///  (ARCHITECTURE CORRECTION) does NOT guess COPY or REUSE for this case -
    ///  it surfaces <see cref="CopyDesignAction.NeedsDecision"/> and fails the
    ///  plan closed, with a non-authoritative "Suggested: COPY" note in the
    ///  node's reasons. Neither a silent COPY nor a silent REUSE is safe here:
    ///  an unconfirmed REUSE could alias two projects onto the SAME identity,
    ///  and an unconfirmed COPY could duplicate something meant to be shared -
    ///  so this case always requires an explicit engineer decision.</summary>
    Unknown,

    /// <summary>Confirmed project-specific - a COPY candidate.</summary>
    ProjectSpecific,

    /// <summary>Confirmed standard / library / shared - a REUSE candidate.</summary>
    LibraryOrShared,
}

/// <summary>
/// The seam a later phase can implement once Arch exposes a real
/// "library/standard part" classification (e.g. a server-side CadDocument
/// flag). P6A ships only <see cref="UnknownComponentClassificationSource"/> -
/// no such authority exists yet, so P6A NEVER classifies anything as
/// <see cref="ComponentClassification.LibraryOrShared"/> and therefore never
/// proposes REUSE from classification alone (REUSE in P6A can still occur for
/// other reasons a later phase may add - this seam exists purely so the
/// planner does not need to change shape when that data becomes available).
/// </summary>
public interface IComponentClassificationSource
{
    ComponentClassification Classify(string cadDocumentId);
}

/// <summary>The P6A default: no classification authority exists yet, so every
///  lookup honestly reports <see cref="ComponentClassification.Unknown"/> -
///  never a filename-based guess.</summary>
public sealed class UnknownComponentClassificationSource : IComponentClassificationSource
{
    public static readonly UnknownComponentClassificationSource Instance = new();

    public ComponentClassification Classify(string cadDocumentId) => ComponentClassification.Unknown;
}
