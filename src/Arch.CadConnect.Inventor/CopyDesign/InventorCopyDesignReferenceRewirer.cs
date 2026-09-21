using System.Runtime.InteropServices;

using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;

using InventorApi = Inventor;

namespace Arch.CadConnect.Inventor.CopyDesign;

/// <summary>
/// P6C: rewires the references of ONE already-COPIED assembly - opened fresh
/// from its NEW destination path (never the source; the source is never
/// opened, activated, or mutated by this class at all).
///
/// Mirrors the P5C-established pattern (<see cref="Arch.CadConnect.Inventor.References.InventorReferenceReplacer"/>)
/// exactly: an assembly's component references are NOT safely mutated
/// through the document-level <c>FileDescriptor.ReplaceReference</c> (a real
/// Inventor 2025 acceptance run for P5C proved it edits document-level
/// bookkeeping only and does not rebind already-instantiated
/// <c>ComponentOccurrence</c> objects) - they are mutated through
/// <c>ComponentOccurrence.Replace(FileName, ReplaceAll: true)</c> instead,
/// confirmed against the actual Autodesk.Inventor.Interop 29.0.0.0
/// signature <c>Replace(String FileName, Boolean ReplaceAll)</c>. Per
/// Inventor's documented semantics, <c>ReplaceAll: true</c> replaces EVERY
/// occurrence currently bound to that same referenced document in one call.
///
/// Only COPY-child targets are ever mutated. A REUSE-child target's edge
/// deliberately remains untouched (SaveCopyAs already preserved the exact
/// original reference) - this class does not even need to "confirm" it
/// here; that is <see cref="Arch.CadConnect.Core.CopyDesign.Apply.CopyDesignVerificationEvaluator"/>'s
/// job, using the SAME descriptor read this class performs for its own
/// target matching.
///
/// Targeting is by STABLE plan identity + the exact resolved absolute path
/// the plan recorded for that child (<see cref="CopyDesignReferenceTarget.OriginalChildAbsolutePath"/>)
/// - never a file name, never a folder, never a display name. Zero or more
/// than one live reference resolving to that same path fails the WHOLE
/// rewire closed rather than guessing.
/// </summary>
internal sealed class InventorCopyDesignReferenceRewirer(InventorApi.Application application) : ICopyDesignReferenceRewirer
{
    public Task<CopyDesignRewireResult> RewireAsync(
        CopyDesignNode copiedIamNode, IReadOnlyList<CopyDesignReferenceTarget> targets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(copiedIamNode);
        ArgumentNullException.ThrowIfNull(targets);
        ct.ThrowIfCancellationRequested();

        var destination = copiedIamNode.ProposedDestinationAbsolutePath;
        if (string.IsNullOrWhiteSpace(destination) || !File.Exists(destination))
        {
            return Task.FromResult(new CopyDesignRewireResult(false, "The copied assembly's destination file does not exist."));
        }

        InventorApi.Document? document = null;
        var silentBefore = application.SilentOperation;
        try
        {
            application.SilentOperation = true;
            document = application.Documents.Open(destination, OpenVisible: false);
            if (document is not InventorApi.AssemblyDocument assembly)
            {
                return Task.FromResult(new CopyDesignRewireResult(false, "The copied file did not open as an assembly document."));
            }

            var copyTargets = targets.Where(t => t.ChildIsCopy).ToArray();
            if (copyTargets.Length == 0)
            {
                // Nothing to mutate (every child is REUSE, or there are no
                // children at all) - still a legitimate, successful rewire.
                return Task.FromResult(new CopyDesignRewireResult(true, null));
            }

            InventorApi.ComponentOccurrences occurrences;
            try
            {
                occurrences = assembly.ComponentDefinition.Occurrences;
            }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException)
            {
                return Task.FromResult(new CopyDesignRewireResult(false, "Inventor could not enumerate the copied assembly's component occurrences."));
            }

            // Snapshot EVERY occurrence's currently-resolved path ONCE, fail
            // closed on any read failure (mirrors InventorReferenceReplacer's
            // fail-closed enumeration - an undercounted set is unsafe once a
            // ReplaceAll mutation begins).
            var occurrenceByResolvedPath = new Dictionary<string, List<InventorApi.ComponentOccurrence>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (InventorApi.ComponentOccurrence occurrence in occurrences)
                {
                    var resolved = ResolvedFileOrLegitimatelyUnbound(occurrence);
                    if (resolved is null)
                    {
                        continue;
                    }
                    if (!occurrenceByResolvedPath.TryGetValue(resolved, out var list))
                    {
                        list = new List<InventorApi.ComponentOccurrence>();
                        occurrenceByResolvedPath[resolved] = list;
                    }
                    list.Add(occurrence);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException or InvalidCastException)
            {
                return Task.FromResult(new CopyDesignRewireResult(
                    false, "Inventor threw while reading the copied assembly's component occurrences - refusing to guess the complete set (fail closed)."));
            }

            foreach (var target in copyTargets)
            {
                if (!occurrenceByResolvedPath.TryGetValue(target.OriginalChildAbsolutePath, out var matched) || matched.Count == 0)
                {
                    return Task.FromResult(new CopyDesignRewireResult(
                        false, $"No component occurrence in the copied assembly currently resolves to the expected original " +
                               $"reference \"{Path.GetFileName(target.OriginalChildAbsolutePath)}\" - refusing to guess."));
                }

                var representative = matched[0];
                try
                {
                    // ReplaceAll: true replaces EVERY occurrence Inventor
                    // itself currently considers bound to this document, in
                    // ONE call - exactly matching "one file-level reference,
                    // N occurrences" (matched.Count here).
                    representative.Replace(target.ExpectedTargetAbsolutePath, true);
                }
                catch (COMException ex)
                {
                    return Task.FromResult(new CopyDesignRewireResult(
                        false, $"Inventor reported an error while replacing a reference (COMException, 0x{ex.HResult:X8}): {SafeMessage(ex)}"));
                }
                catch (Exception ex) when (ex is InvalidComObjectException or ArgumentException)
                {
                    return Task.FromResult(new CopyDesignRewireResult(false, $"Reference replacement failed: {ex.Message}"));
                }
            }

            document.Save();
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

    /// <summary>Mirrors <c>InventorReferenceReplacer</c>'s occurrence-level
    ///  resolved-file read: a clean "unbound" answer is legitimate and
    ///  returns <c>null</c> without throwing; any COM read failure is left
    ///  UNCAUGHT so the caller's enumeration fails closed instead of
    ///  silently skipping this one occurrence.</summary>
    private static string? ResolvedFileOrLegitimatelyUnbound(InventorApi.ComponentOccurrence occurrence)
    {
        var rfd = occurrence.ReferencedFileDescriptor;
        if (rfd is null || !rfd.DocumentFound)
        {
            return null;
        }
        var full = rfd.FullFileName;
        return string.IsNullOrWhiteSpace(full) ? null : full;
    }

    private static string SafeMessage(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message) ? "(no exception message)" : ex.Message;
}
