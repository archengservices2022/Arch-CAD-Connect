using System.Runtime.InteropServices;

using Arch.CadConnect.Core;
using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;

using InventorApi = Inventor;

namespace Arch.CadConnect.Inventor.CopyDesign;

/// <summary>
/// P6D: gathers every fact <see cref="CopyDesignVerificationEvaluator"/>
/// needs for ONE copied IDW/DWG, AFTER its physical copy and drawing-model
/// reference rewiring have completed - opening the destination FRESH (a
/// separate, dedicated open/close, never reusing whatever
/// <see cref="InventorCopyDesignDrawingReferenceRewirer"/> had open) so
/// "openable through Inventor" is a genuine, independent proof, not just
/// "SaveAs/ReplaceReference didn't throw". Mirrors
/// <see cref="InventorCopyDesignVerifier"/>'s established pattern exactly,
/// substituting the drawing's <c>ReferencedDocumentDescriptors</c> for an
/// assembly's <c>ComponentDefinition.Occurrences</c> (a drawing has no
/// component occurrences at all - see
/// <see cref="InventorCopyDesignDrawingReferenceRewirer"/>'s own doc comment
/// for why).
///
/// Reuses <see cref="CopyDesignVerificationEvaluator"/> UNCHANGED: its
/// <see cref="CopyDesignActualComponentOccurrence"/> shape (despite the
/// "ComponentOccurrence" name) is really just "one actually-resolved
/// reference target" and is document-type-agnostic - it already checks
/// EXACTLY the properties the P6D spec requires for a drawing's model
/// reference set: every COPY model resolves ONLY to its copied destination,
/// every REUSE model resolves ONLY to its original source, a stale
/// original-path reference anywhere is a failure, an unplanned/unexpected
/// reference is a failure, an unresolved reference is a failure, and a
/// completely missing expected reference is a failure - across the
/// drawing's COMPLETE reference set (every sheet/view that references a
/// given model shares the SAME document-level descriptor - see the rewirer's
/// doc comment - so this ALREADY inherently covers "if a drawing has
/// multiple sheets/views referencing the same model, all must be
/// consistent": there is exactly one authoritative resolved path per model,
/// not one per view, so there is no way for two views of the SAME model to
/// disagree).
/// </summary>
internal sealed class InventorCopyDesignDrawingVerifier(InventorApi.Application application, ICopyDesignFileHasher hasher) : ICopyDesignVerifier
{
    public Task<CopyDesignNodeVerificationFacts> GatherFactsAsync(
        CopyDesignNode node, IReadOnlyList<CopyDesignReferenceTarget> referenceTargets, string sourceSha256BeforeOperation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(referenceTargets);
        ct.ThrowIfCancellationRequested();

        var destination = node.ProposedDestinationAbsolutePath ?? "";
        var destinationExists = !string.IsNullOrWhiteSpace(destination) && File.Exists(destination);

        string? resultingSha256 = null;
        long? resultingFileSize = null;
        if (destinationExists)
        {
            try
            {
                resultingSha256 = hasher.ComputeSha256(destination);
                resultingFileSize = new FileInfo(destination).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                resultingSha256 = null;
                resultingFileSize = null;
            }
        }

        string? sourceSha256After = null;
        try { sourceSha256After = hasher.ComputeSha256(node.SourceAbsolutePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) { sourceSha256After = null; }

        var documentTypeMatches = false;
        var openableThroughInventor = false;
        var referenceResolutions = referenceTargets
            .Select(t => new CopyDesignReferenceResolutionFact(t.ChildCadDocumentId, t.ExpectedTargetAbsolutePath, t.OriginalChildAbsolutePath, null, t.ChildIsCopy))
            .ToList();
        var actualOccurrences = new List<CopyDesignActualComponentOccurrence>();
        var descriptorsGathered = false;

        if (destinationExists)
        {
            InventorApi.Document? document = null;
            var silentBefore = application.SilentOperation;
            try
            {
                application.SilentOperation = true;
                document = application.Documents.Open(destination, OpenVisible: false);
                if (document is not null)
                {
                    openableThroughInventor = true;
                    documentTypeMatches = document.DocumentType == InventorApi.DocumentTypeEnum.kDrawingDocumentObject
                        && document is InventorApi.DrawingDocument drawingCheck
                        && drawingCheck.IsInventorDWG == (node.DocumentType == CadDocumentType.Dwg);

                    if (document is InventorApi.DrawingDocument drawing)
                    {
                        descriptorsGathered = TryGatherActualReferences(drawing, actualOccurrences);
                        if (descriptorsGathered)
                        {
                            referenceResolutions = ResolveReferences(referenceTargets, actualOccurrences);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException)
            {
                openableThroughInventor = false;
            }
            finally
            {
                try { application.SilentOperation = silentBefore; } catch { /* best effort */ }
                if (document is not null)
                {
                    try { document.Close(SkipSave: true); } catch { /* best effort */ }
                }
            }
        }

        // A node with reference targets that could not even be inspected
        // (not a drawing, open failed, or enumeration itself failed) leaves
        // `actualOccurrences` EMPTY (never partially populated - see
        // TryGatherActualReferences) while `referenceResolutions` still
        // names every expected target - exactly what the evaluator already
        // treats as "missing expected reference", never as "nothing to
        // check, so verification passed".

        // Every IDW/DWG node enumerates its reference set (unlike an IPT,
        // which never does) - trustworthy ONLY when enumeration was actually
        // attempted AND succeeded, regardless of whether zero or many
        // references were expected, exactly mirroring
        // InventorCopyDesignVerifier's IAM enumeration-succeeded discipline.
        var occurrenceEnumerationSucceeded = descriptorsGathered;

        return Task.FromResult(new CopyDesignNodeVerificationFacts(
            node.CadDocumentId!, node.SourceAbsolutePath, destination,
            destinationExists, documentTypeMatches, openableThroughInventor,
            resultingSha256, resultingFileSize, sourceSha256BeforeOperation, sourceSha256After,
            referenceResolutions, actualOccurrences, occurrenceEnumerationSucceeded));
    }

    /// <summary>Snapshots EVERY current referenced-document descriptor in
    ///  <paramref name="drawing"/> - the COMPLETE set, not merely the ones
    ///  matching an expected target - into <paramref name="occurrences"/>.
    ///  Returns <c>false</c> (and leaves <paramref name="occurrences"/>
    ///  untouched) if Inventor throws while enumerating, so the caller can
    ///  fail closed rather than trust a partial/undercounted set.</summary>
    private static bool TryGatherActualReferences(
        InventorApi.DrawingDocument drawing, List<CopyDesignActualComponentOccurrence> occurrences)
    {
        var gathered = new List<CopyDesignActualComponentOccurrence>();
        try
        {
            foreach (InventorApi.DocumentDescriptor descriptor in drawing.ReferencedDocumentDescriptors)
            {
                string? resolved = null;
                if (!descriptor.ReferenceMissing)
                {
                    var fileDescriptor = descriptor.ReferencedFileDescriptor;
                    resolved = fileDescriptor is not null && !string.IsNullOrWhiteSpace(fileDescriptor.FullFileName)
                        ? fileDescriptor.FullFileName
                        : null;
                }
                gathered.Add(new CopyDesignActualComponentOccurrence(resolved));
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or InvalidCastException)
        {
            return false;
        }
        occurrences.AddRange(gathered);
        return true;
    }

    /// <summary>Best-effort per-target "what did this ONE expected model
    ///  reference primarily resolve to" summary, kept for messaging/back-
    ///  compat - the evaluator's own pass/fail decision comes from the
    ///  COMPLETE <see cref="CopyDesignActualComponentOccurrence"/> set, not
    ///  from this. Identical in shape to
    ///  <see cref="InventorCopyDesignVerifier"/>'s own helper.</summary>
    private static List<CopyDesignReferenceResolutionFact> ResolveReferences(
        IReadOnlyList<CopyDesignReferenceTarget> targets, IReadOnlyList<CopyDesignActualComponentOccurrence> actual)
    {
        var resolvedPaths = new HashSet<string>(
            actual.Where(o => !string.IsNullOrWhiteSpace(o.ResolvedAbsolutePath)).Select(o => o.ResolvedAbsolutePath!),
            StringComparer.OrdinalIgnoreCase);

        var results = new List<CopyDesignReferenceResolutionFact>(targets.Count);
        foreach (var target in targets)
        {
            string? primary = resolvedPaths.Contains(target.ExpectedTargetAbsolutePath)
                ? target.ExpectedTargetAbsolutePath
                : resolvedPaths.Contains(target.OriginalChildAbsolutePath)
                    ? target.OriginalChildAbsolutePath
                    : null;
            results.Add(new CopyDesignReferenceResolutionFact(
                target.ChildCadDocumentId, target.ExpectedTargetAbsolutePath, target.OriginalChildAbsolutePath, primary, target.ChildIsCopy));
        }
        return results;
    }
}
