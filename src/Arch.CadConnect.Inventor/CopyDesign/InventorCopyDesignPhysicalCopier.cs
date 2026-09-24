using System.Runtime.InteropServices;

using Arch.CadConnect.Core;
using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;

using InventorApi = Inventor;

namespace Arch.CadConnect.Inventor.CopyDesign;

/// <summary>
/// P6C/P6D: physically creates ONE COPY destination via Inventor's documented
/// "Save Copy As" primitives - <c>Document.SaveAs(FileName, SaveCopyAs: true)</c>
/// for an IPT/IAM/IDW, and <c>DrawingDocument.SaveAsInventorDWG(FileName, SaveCopyAs: true)</c>
/// for an Inventor DWG drawing (both confirmed against the actual
/// Autodesk.Inventor.Interop 29.0.0.0 (Inventor 2025) assembly via reflection
/// against the real interop DLL: <c>DrawingDocument</c> exposes a DEDICATED
/// <c>SaveAsInventorDWG(String FullFileName, Boolean SaveCopyAs)</c> method
/// alongside the ordinary <c>SaveAs</c>/<c>SaveAs2</c> - Autodesk's own
/// distinct entry point for producing a DWG-format save, so an Inventor DWG
/// source is NEVER routed through the plain <c>SaveAs</c> that IPT/IAM/IDW
/// use).
///
/// IDW vs DWG: Inventor's own <c>DocumentTypeEnum</c> reports BOTH as
/// <c>kDrawingDocumentObject</c> - there is no separate "IDW document type"
/// enum value. The two are distinguished ONLY by <c>DrawingDocument.IsInventorDWG</c>
/// (also confirmed via reflection) - <see cref="ExpectedDrawingIsInventorDwg"/>
/// is checked in ADDITION to <c>DocumentType</c> for every IDW/DWG node, so a
/// plan expecting one drawing format never silently accepts the other.
///
/// WHY SaveCopyAs, not a byte copy: per Inventor's own documented semantics,
/// <c>SaveCopyAs: true</c> writes a COPY of the CURRENTLY-LOADED in-memory
/// document to the new path - the ALREADY-OPEN <c>Document</c> object that
/// performed the call remains bound to its ORIGINAL file (its
/// <c>FullFileName</c>, dirty state, and on-disk bytes are all UNCHANGED). A
/// raw byte copy would risk copying an internally-inconsistent file for any
/// Inventor document format where on-disk state and Inventor's own internal
/// document identity/graphics-cache bookkeeping are not simple mirrors of
/// each other; SaveAs is Inventor's own supported mechanism for exactly this
/// "preserve the source, produce a distinct destination" operation and is
/// the API Autodesk documents for programmatic "Save Copy As".
///
/// NEVER calls plain <c>Save</c>/<c>Save2</c> on the source, NEVER calls
/// <c>ReplaceReference</c> on the source (this class never mutates the
/// source's own references at all - see <see cref="InventorCopyDesignReferenceRewirer"/>,
/// which only ever opens the FRESH COPY), and NEVER overwrites an existing
/// destination.
///
/// CODEX FINAL AUDIT ROUND 1:
///  MEDIUM 1 - a Dirty source (<c>Document.Dirty == true</c>) is refused
///   UNCONDITIONALLY, regardless of whether this class itself opened the
///   source or found it already open - SaveCopyAs would otherwise copy the
///   CURRENT in-memory (unsaved) state, not the on-disk bytes the
///   operation's captured source SHA-256 baseline describes. The source is
///   never saved either way; only a document THIS class itself opened is
///   ever closed (always with SkipSave).
///  HIGH 4 - SaveAs never targets the unclaimed FINAL destination directly
///   (a <c>File.Exists</c> check immediately before SaveAs is not an atomic
///   no-overwrite claim). SaveAs instead targets an operation-owned TEMP
///   path in the SAME destination directory (see
///   <see cref="CopyDesignAtomicPromotion"/>), which is then promoted to the
///   final destination via an atomic move that FAILS (never overwrites,
///   never auto-renames) if the final path already exists. On ANY failure,
///   only the temp file is ever removed - a racing/pre-existing file at the
///   final path is never touched. The temp path is never opened as a live
///   Inventor document, so no later phase can ever bind to a "temp"
///   identity - reference rewiring and verification both re-open the FINAL
///   path fresh.
///
/// CODEX ROUND 3, MEDIUM - a SaveAs failure attempts temp cleanup WHILE the
///  document is still open (it may fail there, e.g. a transient lock) and,
///  if that attempt did not CONFIRM removal, retries ONCE more AFTER the
///  document is closed (only a document THIS class itself opened is ever
///  closed). The outcome is never assumed: <see cref="CopyDesignPhysicalCopyResult.UnremovedTempPath"/>
///  carries the exact temp path whenever cleanup could not confirm it was
///  removed, for every failure path that could leave one behind (SaveAs
///  failure, a lost destination race, or a failed promotion) - a
///  pre-existing/racing file at the FINAL path is never touched by any of
///  this, and a successful operation always leaves zero temp artifacts.
///
/// P6D ROUND 2, CRITICAL fix - a DRAWING's SaveAs/SaveAsInventorDWG is
///  DIFFERENT from an IPT/IAM's SaveAs in one crucial way: a drawing's
///  in-memory document graph directly holds live references to its model
///  document(s) (see <see cref="Inventor.CopyDesign.InventorCopyDesignDrawingReferenceRewirer"/>'s
///  own doc comment), and Inventor's Save engine can, for a document with
///  out-of-date or otherwise "needs saving" references, cascade-save those
///  REFERENCED documents too - even for a SaveCopyAs. If one of those
///  referenced documents happens to be Dirty (unsaved user edits) - most
///  dangerously a REUSE target, which Copy Design must NEVER mutate at all,
///  or a COPY source, whose pre-mutation SHA-256 baseline would then
///  silently describe stale bytes - that would be a genuine source-
///  immutability violation. <c>Application.SilentOperation</c> only
///  suppresses UI prompts; it says NOTHING about which documents the Save
///  engine decides to persist, so it is NEVER treated as a safety guarantee
///  here. Confirmed via reflection against the real interop assembly that
///  NEITHER <c>SaveAs</c> NOR <c>SaveAsInventorDWG</c> exposes a
///  "SaveDependents" parameter at all (unlike <c>Save2</c>, which
///  <see cref="Inventor.CopyDesign.InventorCopyDesignDrawingReferenceRewirer"/>
///  uses for exactly this reason) - so for THESE two calls the only
///  available defense is PREVENTION: <see cref="InventorDrawingDependentSafety.Evaluate"/>
///  enumerates every document Inventor currently has LOADED that this
///  drawing references (<c>Document.AllReferencedDocuments</c> - the
///  complete closure, not merely the direct set) and fails closed via
///  <see cref="CopyDesignDrawingDependentSafetyGuard"/> (PURE, Core, no COM)
///  if ANY of them is Dirty - BEFORE SaveAs/SaveAsInventorDWG is ever
///  called. Only checked for an IDW/DWG node - an IPT never references
///  anything, and P6C's existing IAM copy path is unchanged/out of this
///  round's scope.
///
/// P6D LIVE BLOCKER fix ("drawing remains Dirty after source IDW is
///  closed") - a drawing this class opens itself (<c>wasAlreadyOpen ==
///  false</c>) can come back <c>Dirty</c> SOLELY because Inventor's own
///  document loader resolves/loads the drawing's referenced model
///  documents and evaluates whether cached view representations are out of
///  date - a load-time side effect, never a user action, and never
///  something this class's own code causes (nothing between
///  <c>Documents.Open</c> and the Dirty check below mutates anything).
///  Unconditionally rejecting this (as before this fix) blocks a
///  perfectly-safe resume for no real reason. The Dirty check now defers
///  to <see cref="CopyDesignSourceDirtyProvenanceGuard"/> (PURE, Core, no
///  COM) - see its own doc comment for the exact, non-weakening
///  distinction: a document already open before this operation touched it
///  is STILL rejected unconditionally while Dirty (unchanged); a document
///  this class opened itself may proceed ONLY when the on-disk bytes,
///  re-hashed from disk immediately after open, still exactly match the
///  orchestrator's own pre-mutation SHA-256 baseline for this node.
/// </summary>
internal sealed class InventorCopyDesignPhysicalCopier(InventorApi.Application application, ICopyDesignFileHasher hasher) : ICopyDesignPhysicalCopier
{
    public Task<CopyDesignPhysicalCopyResult> CopyAsync(CopyDesignNode node, string sourceSha256BeforeOperation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(node);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(node.SourceAbsolutePath) || !File.Exists(node.SourceAbsolutePath))
        {
            return Task.FromResult(new CopyDesignPhysicalCopyResult(false, "The source file does not exist."));
        }
        var destination = node.ProposedDestinationAbsolutePath;
        if (string.IsNullOrWhiteSpace(destination))
        {
            return Task.FromResult(new CopyDesignPhysicalCopyResult(false, "No destination path was proposed."));
        }
        if (File.Exists(destination))
        {
            // Last line of defense immediately before mutation - never
            // overwrite, even though the orchestrator's own revalidation
            // already checked this.
            return Task.FromResult(new CopyDesignPhysicalCopyResult(false, "The destination already exists."));
        }
        var destinationDir = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(destinationDir))
        {
            return Task.FromResult(new CopyDesignPhysicalCopyResult(false, "The destination path has no usable directory."));
        }

        var expectedType = ExpectedDocumentType(node.DocumentType);

        var wasAlreadyOpen = TryFindLoadedDocument(node.SourceAbsolutePath, out var document);
        var silentBefore = application.SilentOperation;
        string? tempPath = null;
        string? saveFailureMessage = null;
        // CODEX ROUND 3, MEDIUM fix: true once a cleanup attempt has
        // CONFIRMED the temp file is gone - starts true (nothing to clean up
        // yet) so a SaveAs success never triggers a spurious retry below.
        var tempConfirmedGone = true;
        try
        {
            if (!wasAlreadyOpen)
            {
                application.SilentOperation = true;
                document = application.Documents.Open(node.SourceAbsolutePath, OpenVisible: false);
            }
            if (document is null)
            {
                return Task.FromResult(new CopyDesignPhysicalCopyResult(false, "Inventor could not open the source document."));
            }

            if (document.DocumentType != expectedType)
            {
                return Task.FromResult(new CopyDesignPhysicalCopyResult(
                    false, "The source document's actual type does not match the plan's expected document type."));
            }
            if (node.DocumentType is CadDocumentType.Idw or CadDocumentType.Dwg)
            {
                // kDrawingDocumentObject alone does not distinguish IDW from
                // Inventor DWG - see the class doc comment. A mismatch here
                // (e.g. the plan expected an IDW but the actual on-disk file
                // is an Inventor DWG, or vice versa) is exactly the same kind
                // of "actual type does not match expected" failure as above,
                // fails closed the same way, and is never silently coerced by
                // just picking whichever SaveAs variant matches the ACTUAL
                // file - the plan's expectation is authoritative.
                if (document is not InventorApi.DrawingDocument drawingSource
                    || drawingSource.IsInventorDWG != (node.DocumentType == CadDocumentType.Dwg))
                {
                    return Task.FromResult(new CopyDesignPhysicalCopyResult(
                        false, "The source drawing's actual format (IDW vs Inventor DWG) does not match the plan's expected document type."));
                }
            }

            // P6D LIVE BLOCKER fix: a document already open BEFORE this
            // operation touched it is STILL rejected unconditionally while
            // Dirty - never weakened, see CopyDesignSourceDirtyProvenanceGuard's
            // own doc comment. A document THIS class opened itself may
            // proceed ONLY when the on-disk bytes, re-hashed from disk
            // (never from Inventor's in-memory state) right now, still
            // exactly match the orchestrator's own pre-mutation baseline -
            // proof the Dirty flag is Inventor's own load-time side effect
            // (e.g. resolving/loading this drawing's referenced models),
            // never a content change and never a user edit (a closed file
            // has no in-memory session that could hold one). Never saves
            // the dirty source either way - only closes what this class
            // itself opened (see the `finally` below), and always with
            // SkipSave.
            if (document.Dirty)
            {
                string? postOpenHash = null;
                try { postOpenHash = hasher.ComputeSha256(node.SourceAbsolutePath); }
                catch { /* left null - the guard treats this as unproven, never a pass */ }

                var provenance = CopyDesignSourceDirtyProvenanceGuard.Evaluate(
                    node.SourceFileName, wasAlreadyOpen, dirty: true, postOpenHash, sourceSha256BeforeOperation);
                if (!provenance.Safe)
                {
                    return Task.FromResult(new CopyDesignPhysicalCopyResult(false, provenance.FailureReason));
                }
                // else: proven load-time-only Dirty side effect on a
                // document this class itself opened from provably-
                // untouched bytes - fall through and proceed exactly as if
                // the document were clean.
            }

            // P6D ROUND 2, CRITICAL fix: see the class doc comment. Checked
            // ONLY for a drawing - its referenced model document(s) could be
            // saved as a side effect of SaveAs/SaveAsInventorDWG.
            if (node.DocumentType is CadDocumentType.Idw or CadDocumentType.Dwg)
            {
                var dependentSafety = InventorDrawingDependentSafety.Evaluate(document);
                if (!dependentSafety.Safe)
                {
                    return Task.FromResult(new CopyDesignPhysicalCopyResult(false, dependentSafety.FailureReason));
                }
            }

            Directory.CreateDirectory(destinationDir);

            // HIGH 4 fix: never SaveAs directly onto the unclaimed FINAL
            // destination - a File.Exists check immediately before SaveAs is
            // not an atomic no-overwrite claim, and a concurrent writer
            // could create the final path between that check and this call.
            // Instead: SaveAs to an operation-owned TEMP path in the SAME
            // destination directory (so promotion below is a same-volume
            // rename), then promote temp -> final with an atomic move that
            // FAILS if the final path already exists. The temp path is
            // NEVER opened as a live Inventor document (SaveCopyAs only
            // writes bytes to it), so no later step (reference rewiring,
            // verification - both re-open the FINAL path fresh) can ever
            // observe or bind to the temp identity.
            tempPath = CopyDesignAtomicPromotion.NewTempPath(destination);
            if (node.DocumentType == CadDocumentType.Dwg)
            {
                // Autodesk's dedicated DWG-format save entry point - see the
                // class doc comment. Only ever reached after the IsInventorDWG
                // check above confirmed the source IS actually a DWG-format
                // drawing.
                ((InventorApi.DrawingDocument)document).SaveAsInventorDWG(tempPath, SaveCopyAs: true);
            }
            else
            {
                document.SaveAs(tempPath, SaveCopyAs: true);
            }
        }
        catch (COMException ex)
        {
            saveFailureMessage = $"Inventor reported an error while copying (COMException, 0x{ex.HResult:X8}): {SafeMessage(ex)}";
            // CODEX ROUND 3, MEDIUM fix: attempt cleanup NOW, WHILE the
            // document is still open - this may legitimately fail if
            // Inventor (or the OS) still holds a transient lock on the temp
            // file. Recorded, never trusted blindly; a confirmed-failed
            // attempt is retried AFTER the document is closed (see below the
            // `finally`), never abandoned here.
            if (tempPath is not null) { tempConfirmedGone = CopyDesignAtomicPromotion.CleanUpTempOnly(tempPath); }
        }
        catch (Exception ex) when (ex is InvalidComObjectException or IOException or UnauthorizedAccessException)
        {
            saveFailureMessage = $"Copy failed: {ex.Message}";
            if (tempPath is not null) { tempConfirmedGone = CopyDesignAtomicPromotion.CleanUpTempOnly(tempPath); }
        }
        finally
        {
            try { application.SilentOperation = silentBefore; } catch { /* best effort */ }
            if (!wasAlreadyOpen && document is not null)
            {
                // Never save what we opened ourselves - it is (or was) the
                // source, and a plain Close never touches the on-disk file
                // when SkipSave is true.
                try { document.Close(SkipSave: true); } catch { /* best effort */ }
            }
        }

        if (saveFailureMessage is not null)
        {
            // CODEX ROUND 3, MEDIUM fix: the document (if P6C itself opened
            // it) is now closed - retry cleanup ONCE more if the earlier,
            // while-open attempt did not confirm removal. Whatever transient
            // lock caused that attempt to fail may now be released; if the
            // retry ALSO fails, the exact temp path is surfaced for manual
            // cleanup - never silently claimed as removed.
            string? unremovedTempPath = null;
            if (tempPath is not null && !tempConfirmedGone)
            {
                var confirmedGoneAfterClose = CopyDesignAtomicPromotion.CleanUpTempOnly(tempPath);
                unremovedTempPath = confirmedGoneAfterClose ? null : tempPath;
            }
            return Task.FromResult(new CopyDesignPhysicalCopyResult(false, saveFailureMessage, unremovedTempPath));
        }

        if (tempPath is null || !File.Exists(tempPath))
        {
            return Task.FromResult(new CopyDesignPhysicalCopyResult(
                false, "SaveAs returned without an exception, but the operation-owned temporary file does not exist - refusing to report success."));
        }
        if (File.Exists(destination))
        {
            // Lost the race: something else created the final destination
            // WHILE this copy was in progress - fail closed, clean up ONLY
            // our own temp file (the document is already closed by this
            // point), never touch the racing file.
            var raceCleanedUp = CopyDesignAtomicPromotion.CleanUpTempOnly(tempPath);
            return Task.FromResult(new CopyDesignPhysicalCopyResult(
                false, "The destination was created by another process while copying - refusing to overwrite it.",
                raceCleanedUp ? null : tempPath));
        }
        var promotion = CopyDesignAtomicPromotion.PromoteToFinal(tempPath, destination);
        if (!promotion.Success)
        {
            return Task.FromResult(new CopyDesignPhysicalCopyResult(false, promotion.FailureReason, promotion.UnremovedTempPath));
        }
        if (!File.Exists(destination))
        {
            return Task.FromResult(new CopyDesignPhysicalCopyResult(
                false, "The copy was promoted without an exception, but the destination file does not exist - refusing to report success."));
        }
        return Task.FromResult(new CopyDesignPhysicalCopyResult(true, null));
    }

    /// <summary>P6D: IDW and Inventor DWG both report
    ///  <c>kDrawingDocumentObject</c> - see the class doc comment for why a
    ///  SECOND check (<c>IsInventorDWG</c>) is required in addition to
    ///  this.</summary>
    private static InventorApi.DocumentTypeEnum ExpectedDocumentType(CadDocumentType type) => type switch
    {
        CadDocumentType.Iam => InventorApi.DocumentTypeEnum.kAssemblyDocumentObject,
        CadDocumentType.Idw or CadDocumentType.Dwg => InventorApi.DocumentTypeEnum.kDrawingDocumentObject,
        _ => InventorApi.DocumentTypeEnum.kPartDocumentObject,
    };

    private bool TryFindLoadedDocument(string absolutePath, out InventorApi.Document? document)
    {
        document = null;
        try
        {
            foreach (InventorApi.Document candidate in application.Documents)
            {
                if (PathMatches(candidate, absolutePath))
                {
                    document = candidate;
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            return false;
        }
        return false;
    }

    private static bool PathMatches(InventorApi.Document document, string absolutePath)
    {
        try
        {
            var full = document.FullFileName;
            return !string.IsNullOrWhiteSpace(full)
                && string.Equals(Path.GetFullPath(full), Path.GetFullPath(absolutePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or ArgumentException)
        {
            return false;
        }
    }

    private static string SafeMessage(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message) ? "(no exception message)" : ex.Message;
}
