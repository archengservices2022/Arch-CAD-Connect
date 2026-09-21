using System.Runtime.InteropServices;

using Arch.CadConnect.Core;
using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;

using InventorApi = Inventor;

namespace Arch.CadConnect.Inventor.CopyDesign;

/// <summary>
/// P6C: physically creates ONE COPY destination via Inventor's documented
/// "Save Copy As" primitive - <c>Document.SaveAs(FileName, SaveCopyAs: true)</c>
/// (confirmed against the actual Autodesk.Inventor.Interop 29.0.0.0
/// (Inventor 2025) assembly: <c>SaveAs(String FileName, Boolean SaveCopyAs)</c>).
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
/// </summary>
internal sealed class InventorCopyDesignPhysicalCopier(InventorApi.Application application) : ICopyDesignPhysicalCopier
{
    public Task<CopyDesignPhysicalCopyResult> CopyAsync(CopyDesignNode node, CancellationToken ct)
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

        var expectedType = node.DocumentType == CadDocumentType.Iam
            ? InventorApi.DocumentTypeEnum.kAssemblyDocumentObject
            : InventorApi.DocumentTypeEnum.kPartDocumentObject;

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

            // MEDIUM 1 fix: reject a Dirty source REGARDLESS of whether P6C
            // itself opened it or found it already open - a document P6C
            // just opened can still come back Dirty (e.g. an automatic
            // out-of-date-reference update Inventor applies on open), and
            // SaveCopyAs would then copy THAT unsaved in-memory state, not
            // the on-disk bytes the captured source SHA-256 baseline
            // describes. Never save the dirty source either way - only
            // close what P6C itself opened (see the `finally` below), and
            // always with SkipSave.
            if (document.Dirty)
            {
                return Task.FromResult(new CopyDesignPhysicalCopyResult(
                    false, "The source document is Dirty (has unsaved changes) - save or close it before copying, " +
                           "so the copy corresponds to the checked-in source, not unsaved in-memory edits."));
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
            document.SaveAs(tempPath, SaveCopyAs: true);
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
