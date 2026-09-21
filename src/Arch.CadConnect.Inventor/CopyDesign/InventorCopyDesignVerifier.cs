using System.Runtime.InteropServices;

using Arch.CadConnect.Core;
using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;

using InventorApi = Inventor;

namespace Arch.CadConnect.Inventor.CopyDesign;

/// <summary>
/// P6C: gathers every fact <see cref="CopyDesignVerificationEvaluator"/> needs
/// for ONE COPY node, AFTER its physical copy (and, for an IAM, its
/// reference rewiring) has completed - opening the destination FRESH (a
/// separate, dedicated open/close, never reusing whatever
/// <see cref="InventorCopyDesignReferenceRewirer"/> had open) so "openable
/// through Inventor" is a genuine, independent proof, not just "SaveAs/
/// Replace didn't throw".
///
/// HIGH 3 fix (CODEX FINAL AUDIT ROUND 1): this now reports the COMPLETE
/// component occurrence set (every occurrence Inventor currently enumerates,
/// resolved or not - <see cref="CopyDesignActualComponentOccurrence"/>), not
/// merely a per-expected-target existence check. The evaluator is what turns
/// that complete set into a pass/fail decision (stale-original, unplanned,
/// unresolved, missing-expected); this class only gathers facts, never
/// judges them.
///
/// CODEX ROUND 3, HIGH fix: whether enumeration itself was ATTEMPTED and
/// SUCCEEDED for an IAM is now its own reported fact
/// (<see cref="CopyDesignNodeVerificationFacts.OccurrenceEnumerationSucceeded"/>),
/// never inferred from an empty occurrence list - an empty list previously
/// meant EITHER "nothing to enumerate" OR "enumeration failed", and the
/// evaluator could not tell them apart.
/// </summary>
internal sealed class InventorCopyDesignVerifier(InventorApi.Application application, ICopyDesignFileHasher hasher) : ICopyDesignVerifier
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

        var expectedType = node.DocumentType == CadDocumentType.Iam
            ? InventorApi.DocumentTypeEnum.kAssemblyDocumentObject
            : InventorApi.DocumentTypeEnum.kPartDocumentObject;

        var documentTypeMatches = false;
        var openableThroughInventor = false;
        var referenceResolutions = referenceTargets
            .Select(t => new CopyDesignReferenceResolutionFact(t.ChildCadDocumentId, t.ExpectedTargetAbsolutePath, t.OriginalChildAbsolutePath, null, t.ChildIsCopy))
            .ToList();
        var actualOccurrences = new List<CopyDesignActualComponentOccurrence>();
        var occurrencesGathered = false;

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
                    documentTypeMatches = document.DocumentType == expectedType;

                    if (document is InventorApi.AssemblyDocument assembly)
                    {
                        occurrencesGathered = TryGatherActualOccurrences(assembly, actualOccurrences);
                        if (occurrencesGathered)
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
        // (not an assembly, open failed, or enumeration itself failed)
        // leaves `actualOccurrences` EMPTY (never partially populated - see
        // TryGatherActualOccurrences) while `referenceResolutions` still
        // names every expected target - exactly what the evaluator already
        // treats as "missing expected reference", never as "nothing to
        // check, so verification passed".

        // CODEX ROUND 3, HIGH fix: an IPT never enumerates occurrences at
        // all - always true. An IAM's enumeration is trustworthy ONLY when
        // it was actually attempted AND succeeded (occurrencesGathered);
        // this stays false for an IAM whose destination didn't exist,
        // couldn't be opened, didn't open as an assembly, or threw while
        // enumerating - regardless of whether it had zero or many expected
        // references, so a zero-expected-reference IAM can never pass on a
        // silently-lost enumeration failure.
        var occurrenceEnumerationSucceeded = node.DocumentType != CadDocumentType.Iam || occurrencesGathered;

        return Task.FromResult(new CopyDesignNodeVerificationFacts(
            node.CadDocumentId!, node.SourceAbsolutePath, destination,
            destinationExists, documentTypeMatches, openableThroughInventor,
            resultingSha256, resultingFileSize, sourceSha256BeforeOperation, sourceSha256After,
            referenceResolutions, actualOccurrences, occurrenceEnumerationSucceeded));
    }

    /// <summary>Snapshots EVERY current component occurrence in
    ///  <paramref name="assembly"/> - the COMPLETE set, not merely the ones
    ///  matching an expected target - into <paramref name="occurrences"/>.
    ///  Returns <c>false</c> (and leaves <paramref name="occurrences"/>
    ///  untouched) if Inventor throws while enumerating, so the caller can
    ///  fail closed rather than trust a partial/undercounted set.</summary>
    private static bool TryGatherActualOccurrences(
        InventorApi.AssemblyDocument assembly, List<CopyDesignActualComponentOccurrence> occurrences)
    {
        var gathered = new List<CopyDesignActualComponentOccurrence>();
        try
        {
            foreach (InventorApi.ComponentOccurrence occurrence in assembly.ComponentDefinition.Occurrences)
            {
                var rfd = occurrence.ReferencedFileDescriptor;
                var resolved = rfd is not null && rfd.DocumentFound && !string.IsNullOrWhiteSpace(rfd.FullFileName)
                    ? rfd.FullFileName
                    : null;
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

    /// <summary>Best-effort per-target "what did this ONE expected target
    ///  primarily resolve to" summary, kept for messaging/back-compat - the
    ///  evaluator's own pass/fail decision comes from the COMPLETE
    ///  <see cref="CopyDesignActualComponentOccurrence"/> set, not from
    ///  this.</summary>
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
