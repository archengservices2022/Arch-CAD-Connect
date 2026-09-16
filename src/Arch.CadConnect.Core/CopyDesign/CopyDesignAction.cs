namespace Arch.CadConnect.Core.CopyDesign;

/// <summary>
/// The proposed disposition of ONE node in a P6A Copy Design plan. P6A only
/// ever PROPOSES an action - it never executes one (no file copy, no SaveAs,
/// no reference replacement, no FileVersion/CadDocument creation).
/// </summary>
public enum CopyDesignAction
{
    /// <summary>Later phases will create a NEW CAD document identity and
    ///  physical file for this node. P6A never creates it - it only proposes
    ///  the destination name/path and preserves the source cadDocumentId /
    ///  fileVersionId as lineage input for P6B.</summary>
    Copy,

    /// <summary>Keep the EXACT SAME cadDocumentId / FileVersion identity - no
    ///  renamed duplicate is ever created for a REUSE node.</summary>
    Reuse,

    /// <summary>Leave this node out of the copy entirely. The PLANNER never
    ///  selects this automatically (it could silently break an assembly or
    ///  drawing relationship) - it exists only so a later, explicit human
    ///  decision can be represented in the SAME plan shape. See
    ///  <see cref="CopyDesignPlanner"/>'s own tests: EXCLUDE is representable,
    ///  never auto-selected.</summary>
    Exclude,

    /// <summary>The planner could not safely decide COPY vs REUSE (or could
    ///  not establish a usable stable identity at all) and refuses to guess.
    ///  A plan containing any <c>NeedsDecision</c> node is NOT executable.</summary>
    NeedsDecision,
}
