namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// P6D LIVE BLOCKER fix ("drawing remains Dirty after source IDW is
/// closed"): the PURE decision half of distinguishing a genuine
/// pre-existing unsaved user edit (MUST block) from Inventor's own
/// load-time side effect on a document THIS operation itself just opened
/// from provably-untouched, authoritative bytes (may proceed) - see
/// <c>InventorCopyDesignPhysicalCopier</c> (the COM-gathering half, which
/// reads <c>Document.Dirty</c>/<c>Application.Documents</c> and calls this
/// class) for the runtime evidence backing this design: opening a drawing
/// via <c>Documents.Open</c> makes Inventor resolve/load its referenced
/// model documents and evaluate whether cached view representations are
/// out of date - and merely evaluating that (never any user action) can
/// set <c>Document.Dirty</c> true immediately after open, before this
/// operation has mutated anything.
///
/// NEVER weakens the pre-existing rule: a document that was ALREADY open
/// (in this Inventor session) BEFORE this operation touched it is ALWAYS
/// rejected while Dirty - a document with a live in-memory session could
/// carry a genuine unsaved user edit, and there is no way to prove
/// otherwise. This class only ever relaxes the rule for a document THIS
/// operation itself opened from a CLOSED state (a closed file has no
/// in-memory session that could hold a genuine unsaved edit at all), and
/// only when the on-disk bytes - re-read from disk (never from Inventor's
/// in-memory state) immediately after Inventor opened it - still exactly
/// match the SHA-256 the orchestrator captured BEFORE this operation
/// touched Inventor at all (its own pre-mutation baseline, computed in
/// <c>CopyDesignApplyOrchestrator</c> step 4, long before any physical
/// copy is attempted). That combination - never previously open, plus
/// authoritative on-disk bytes unchanged - proves the Dirty flag reflects
/// ONLY Inventor's own load-time processing, never file content, and never
/// a user edit (there was nothing open to have made one).
/// </summary>
public static class CopyDesignSourceDirtyProvenanceGuard
{
    public sealed record Result(bool Safe, string? FailureReason)
    {
        public static Result Proceed() => new(true, null);
        public static Result Block(string reason) => new(false, reason);
    }

    /// <param name="sourceFileName">For the failure message only.</param>
    /// <param name="wasAlreadyOpen">Whether the document was present in
    ///  <c>Application.Documents</c> BEFORE this operation opened/touched
    ///  it - <c>true</c> means a live session pre-dates this operation and
    ///  could hold a genuine unsaved edit.</param>
    /// <param name="dirty"><c>Document.Dirty</c>, read immediately after
    ///  open (or immediately, if it was already open).</param>
    /// <param name="postOpenSha256">The source file's SHA-256, computed
    ///  from DISK (never from Inventor's in-memory state) AFTER Inventor
    ///  opened it. <c>null</c>/unavailable is treated as UNPROVEN - never
    ///  as a pass.</param>
    /// <param name="sourceSha256BeforeOperation">The SAME file's SHA-256,
    ///  captured by the orchestrator BEFORE this operation touched
    ///  Inventor at all (its own pre-mutation baseline).</param>
    public static Result Evaluate(
        string sourceFileName,
        bool wasAlreadyOpen,
        bool dirty,
        string? postOpenSha256,
        string sourceSha256BeforeOperation)
    {
        if (!dirty)
        {
            return Result.Proceed();
        }
        if (wasAlreadyOpen)
        {
            // Open in an Inventor session BEFORE this operation touched it
            // at all - Dirty here can ONLY mean a genuine in-memory edit
            // that predates this operation. Unconditional block, exactly as
            // before this fix - never weakened.
            return Result.Block(
                $"The source document \"{sourceFileName}\" is Dirty (has unsaved changes) - save or close it before " +
                "copying, so the copy corresponds to the checked-in source, not unsaved in-memory edits.");
        }
        if (string.IsNullOrWhiteSpace(postOpenSha256))
        {
            return Result.Block(
                $"Could not re-verify \"{sourceFileName}\"'s on-disk integrity after Inventor opened it - refusing to " +
                "treat an unproven Dirty document as a safe, load-time-only side effect.");
        }
        if (!string.Equals(postOpenSha256, sourceSha256BeforeOperation, StringComparison.OrdinalIgnoreCase))
        {
            // The on-disk bytes no longer match this operation's OWN
            // pre-mutation baseline - this is NOT the load-time-only case
            // (something changed the file), so it fails closed exactly like
            // any other integrity violation.
            return Result.Block(
                $"The source document \"{sourceFileName}\" is Dirty, and its on-disk bytes no longer match this " +
                "operation's own pre-mutation baseline - refusing to resume over a changed or corrupt source.");
        }

        // Not previously open (no in-memory session could hold a genuine
        // unsaved edit) AND the on-disk bytes, re-read after Inventor
        // opened it, still exactly match this operation's own authoritative
        // pre-mutation baseline. The Dirty flag reflects ONLY Inventor's
        // own load-time processing - safe to proceed.
        return Result.Proceed();
    }
}
