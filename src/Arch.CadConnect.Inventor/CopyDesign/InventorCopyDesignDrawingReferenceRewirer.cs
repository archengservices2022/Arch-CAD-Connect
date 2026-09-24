using System.Runtime.InteropServices;

using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;

using InventorApi = Inventor;

namespace Arch.CadConnect.Inventor.CopyDesign;

/// <summary>
/// P6D: rewires the DRAWING -&gt; MODEL reference(s) of ONE already-COPIED
/// IDW/DWG - opened fresh from its NEW destination path (never the source;
/// the source is never opened, activated, or mutated by this class at all -
/// mirrors <see cref="InventorCopyDesignReferenceRewirer"/>'s own
/// established pattern exactly).
///
/// API CHOSEN, AND WHY (investigated independently against the actual
/// Autodesk.Inventor.Interop 29.0.0.0 (Inventor 2025) assembly on this
/// machine via .NET reflection - not assumed): a drawing has NO
/// <c>ComponentOccurrence</c> collection at all (that concept belongs solely
/// to <c>AssemblyDocument.ComponentDefinition</c> - reflection confirms
/// <c>DrawingDocument</c> exposes no such member), so
/// <see cref="InventorCopyDesignReferenceRewirer"/>'s
/// <c>ComponentOccurrence.Replace(FileName, ReplaceAll)</c> approach
/// STRUCTURALLY cannot apply here - the task's own instruction "do not
/// assume ComponentOccurrence.Replace applies to drawings" is confirmed
/// correct by the object model itself, not merely by policy.
///
/// Instead: <c>Document.ReferencedDocumentDescriptors</c> (present on the
/// base <c>Document</c>/<c>DrawingDocument</c> interface, reflection-
/// confirmed) returns ONE <c>DocumentDescriptor</c> per document this
/// drawing directly references - reflection also confirms
/// <c>DrawingView.ReferencedDocumentDescriptor</c> returns THIS SAME
/// document-level <c>DocumentDescriptor</c> object for the model a given
/// view is based on, so there is exactly ONE reference-holding object per
/// referenced model regardless of how many sheets/views display it (unlike
/// an assembly, where each occurrence is its own separately-instantiated,
/// separately-bound COM object). Each <c>DocumentDescriptor</c> exposes
/// <c>.ReferencedFileDescriptor</c> (property, reflection-confirmed return
/// type <c>FileDescriptor</c>) which in turn exposes
/// <c>ReplaceReference(String FullFileName)</c> - THE mutation method. A
/// SEPARATE, similarly-named type - the plural
/// <c>Document.ReferencedFileDescriptors</c> collection of (singular)
/// <c>ReferencedFileDescriptor</c> items - was also inspected and
/// deliberately NOT used: that type has NO <c>ReplaceReference</c> method at
/// all (only lower-level <c>Put*LogicalFileName</c> members for relative/
/// logical-name bookkeeping), so it cannot mutate anything here.
///
/// Because every view/sheet referencing a given model shares the SAME
/// document-level <c>DocumentDescriptor</c>/<c>FileDescriptor</c> pair, ONE
/// <c>ReplaceReference</c> call per referenced model updates every sheet and
/// view that displays it in a single mutation - there is no per-view
/// "instantiated binding" the way an assembly occurrence has, so no analogue
/// of the P5C/P6C ComponentOccurrence quirk (document-level bookkeeping vs.
/// a separately-bound live occurrence) exists for a drawing's model
/// reference.
///
/// Only COPY-model targets are ever mutated. A REUSE-model target's edge
/// deliberately remains untouched (SaveCopyAs already preserved the exact
/// original reference) - this class does not even need to "confirm" it
/// here; that is <see cref="CopyDesignVerificationEvaluator"/>'s job (via
/// <see cref="InventorCopyDesignDrawingVerifier"/>), using the SAME
/// descriptor read this class performs for its own target matching.
///
/// Targeting is by STABLE plan identity + the exact resolved absolute path
/// the plan recorded for that model (<see cref="CopyDesignReferenceTarget.OriginalChildAbsolutePath"/>)
/// - never a file name, never a folder, never a display name. Zero or more
/// than one live descriptor resolving to that same path fails the WHOLE
/// rewire closed rather than guessing - exactly like
/// <see cref="InventorCopyDesignReferenceRewirer"/>'s occurrence matching.
///
/// P6D ROUND 2, CRITICAL fix: the final save that commits the
/// <c>ReplaceReference</c> mutations is <c>DrawingDocument.Save2(SaveDependents: false, ...)</c>,
/// never plain <c>Save()</c> - reflection against the real interop assembly
/// confirms <c>Save2</c>'s own <c>SaveDependents</c> parameter DEFAULTS to
/// <c>true</c> (i.e. Inventor's own default behavior for this call IS to
/// cascade-save dependents), so this is an explicit, deliberate override,
/// never a behavior this class merely relies on by omission. This is
/// defense-in-depth ONLY: the PRIMARY defense is
/// <see cref="InventorDrawingDependentSafety.Evaluate"/>, invoked
/// immediately before Save2 (after ReplaceReference, since the drawing's
/// referenced set has just changed to the FINAL COPY/REUSE targets) via
/// <see cref="CopyDesignDrawingDependentSafetyGuard"/> (Core, PURE, no COM) -
/// if ANY referenced document Inventor currently has loaded is Dirty, this
/// method fails closed and Save2 is never reached at all.
/// </summary>
internal sealed class InventorCopyDesignDrawingReferenceRewirer(InventorApi.Application application) : ICopyDesignReferenceRewirer
{
    public Task<CopyDesignRewireResult> RewireAsync(
        CopyDesignNode copiedDrawingNode, IReadOnlyList<CopyDesignReferenceTarget> targets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(copiedDrawingNode);
        ArgumentNullException.ThrowIfNull(targets);
        ct.ThrowIfCancellationRequested();

        var destination = copiedDrawingNode.ProposedDestinationAbsolutePath;
        if (string.IsNullOrWhiteSpace(destination) || !File.Exists(destination))
        {
            return Task.FromResult(new CopyDesignRewireResult(false, "The copied drawing's destination file does not exist."));
        }

        InventorApi.Document? document = null;
        var silentBefore = application.SilentOperation;
        try
        {
            application.SilentOperation = true;
            document = application.Documents.Open(destination, OpenVisible: false);
            if (document is not InventorApi.DrawingDocument drawing)
            {
                return Task.FromResult(new CopyDesignRewireResult(false, "The copied file did not open as a drawing document."));
            }

            var copyTargets = targets.Where(t => t.ChildIsCopy).ToArray();
            if (copyTargets.Length == 0)
            {
                // Nothing to mutate (every model reference is REUSE, or
                // there are no model references at all - structurally
                // unlikely for a real drawing but never assumed impossible)
                // - still a legitimate, successful rewire.
                return Task.FromResult(new CopyDesignRewireResult(true, null));
            }

            InventorApi.DocumentDescriptorsEnumerator descriptors;
            try
            {
                descriptors = drawing.ReferencedDocumentDescriptors;
            }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException)
            {
                return Task.FromResult(new CopyDesignRewireResult(false, "Inventor could not enumerate the copied drawing's referenced document descriptors."));
            }

            // Snapshot EVERY descriptor's currently-resolved path ONCE, fail
            // closed on any read failure (mirrors InventorCopyDesignReferenceRewirer's
            // fail-closed enumeration - an undercounted set is unsafe once a
            // ReplaceReference mutation begins).
            var descriptorsByResolvedPath = new Dictionary<string, List<InventorApi.FileDescriptor>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (InventorApi.DocumentDescriptor descriptor in descriptors)
                {
                    var resolved = ResolvedFileOrLegitimatelyUnbound(descriptor);
                    if (resolved is null)
                    {
                        continue;
                    }
                    if (!descriptorsByResolvedPath.TryGetValue(resolved.FullFileName, out var list))
                    {
                        list = new List<InventorApi.FileDescriptor>();
                        descriptorsByResolvedPath[resolved.FullFileName] = list;
                    }
                    list.Add(resolved);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException or InvalidCastException)
            {
                return Task.FromResult(new CopyDesignRewireResult(
                    false, "Inventor threw while reading the copied drawing's referenced document descriptors - refusing to guess the complete set (fail closed)."));
            }

            foreach (var target in copyTargets)
            {
                if (!descriptorsByResolvedPath.TryGetValue(target.OriginalChildAbsolutePath, out var matched) || matched.Count == 0)
                {
                    return Task.FromResult(new CopyDesignRewireResult(
                        false, $"No referenced document descriptor in the copied drawing currently resolves to the expected original " +
                               $"model reference \"{Path.GetFileName(target.OriginalChildAbsolutePath)}\" - refusing to guess."));
                }
                if (matched.Count > 1)
                {
                    // Structurally should not happen (one document-level
                    // descriptor per unique referenced file), but never
                    // trusted blindly - an ambiguous match is never mutated.
                    return Task.FromResult(new CopyDesignRewireResult(
                        false, $"More than one referenced document descriptor in the copied drawing resolves to the same expected original " +
                               $"model reference \"{Path.GetFileName(target.OriginalChildAbsolutePath)}\" - refusing to guess which to mutate."));
                }

                try
                {
                    // ONE call rewires every sheet/view bound to this model -
                    // see the class doc comment for why that is safe here
                    // (unlike an assembly occurrence, a drawing's model
                    // reference has no separately-instantiated per-view
                    // binding).
                    matched[0].ReplaceReference(target.ExpectedTargetAbsolutePath);
                }
                catch (COMException ex)
                {
                    return Task.FromResult(new CopyDesignRewireResult(
                        false, $"Inventor reported an error while replacing a drawing model reference (COMException, 0x{ex.HResult:X8}): {SafeMessage(ex)}"));
                }
                catch (Exception ex) when (ex is InvalidComObjectException or ArgumentException)
                {
                    return Task.FromResult(new CopyDesignRewireResult(false, $"Drawing model reference replacement failed: {ex.Message}"));
                }
            }

            // P6D ROUND 2, CRITICAL fix: re-checked HERE (after ReplaceReference,
            // immediately before Save) since the drawing's referenced set has
            // just changed to point at the FINAL (COPY destination / REUSE
            // original) targets - see the class doc comment.
            var dependentSafety = InventorDrawingDependentSafety.Evaluate((InventorApi.Document)drawing);
            if (!dependentSafety.Safe)
            {
                return Task.FromResult(new CopyDesignRewireResult(false, dependentSafety.FailureReason));
            }

            // ROUND 2, CRITICAL fix: plain Save()/Save2's own default both
            // save dependents (confirmed via reflection: Save2's SaveDependents
            // parameter defaults to true) - explicitly pass SaveDependents:
            // false so Inventor is NEVER instructed to persist anything but
            // THIS drawing, even as defense-in-depth on top of the dirty-
            // dependent guard immediately above.
            drawing.Save2(SaveDependents: false, DocumentsToSave: Type.Missing);
        }
        catch (COMException ex)
        {
            return Task.FromResult(new CopyDesignRewireResult(false, $"Inventor reported an error (COMException, 0x{ex.HResult:X8}): {SafeMessage(ex)}"));
        }
        catch (Exception ex) when (ex is InvalidComObjectException or IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new CopyDesignRewireResult(false, $"Rewiring failed: {ex.Message}"));
        }
        finally
        {
            try { application.SilentOperation = silentBefore; } catch { /* best effort */ }
            if (document is not null)
            {
                // Already saved above on the success path; SkipSave here just
                // avoids any residual "save changes?" prompt on a failure
                // path where Save was never reached - it never touches the
                // SOURCE, only this freshly-opened COPY.
                try { document.Close(SkipSave: true); } catch { /* best effort */ }
            }
        }

        return Task.FromResult(new CopyDesignRewireResult(true, null));
    }

    /// <summary>Mirrors <c>InventorCopyDesignReferenceRewirer</c>'s
    ///  occurrence-level resolved-file read, at the DOCUMENT-DESCRIPTOR
    ///  level instead: a clean "reference missing/unbound" answer is
    ///  legitimate and returns <c>null</c> without throwing; any COM read
    ///  failure is left UNCAUGHT so the caller's enumeration fails closed
    ///  instead of silently skipping this one descriptor.</summary>
    private static InventorApi.FileDescriptor? ResolvedFileOrLegitimatelyUnbound(InventorApi.DocumentDescriptor descriptor)
    {
        if (descriptor.ReferenceMissing)
        {
            return null;
        }
        var fileDescriptor = descriptor.ReferencedFileDescriptor;
        if (fileDescriptor is null || string.IsNullOrWhiteSpace(fileDescriptor.FullFileName))
        {
            return null;
        }
        return fileDescriptor;
    }

    private static string SafeMessage(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message) ? "(no exception message)" : ex.Message;
}
