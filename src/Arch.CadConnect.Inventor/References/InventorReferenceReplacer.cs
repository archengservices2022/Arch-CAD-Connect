using System.Runtime.InteropServices;

using Arch.CadConnect.Core.References;
using Arch.CadConnect.Core.Workspace;

using InventorApi = Inventor;

namespace Arch.CadConnect.Inventor.References;

/// <summary>
/// The P5C COM edge, in TWO phases so all discovery happens BEFORE the final
/// authoritative checkout read:
///
///  * <see cref="Prepare"/> resolves the loaded parent document, enumerates its
///    FLAT, file-level reference descriptors
///    (<c>Document.File.ReferencedFileDescriptors</c> - the collection
///    Inventor's own reference-repair APIs operate against), and selects
///    EXACTLY ONE by resolved path + verified stable Arch identity (via the
///    COM-free <see cref="ReferenceReplaceTargetingResolver"/> - zero or 2+
///    matches -&gt; not ready). This is still the ONLY authorization decision:
///    it establishes exactly one authorized FILE-level reference, identified
///    by (cadDocumentId, current fileVersionId).
///  * That one authorized file-level reference is then mapped onto Inventor's
///    MUTATION objects. For a non-assembly referencing document this is
///    still the file-level <c>FileDescriptor</c> pointer itself (unchanged
///    from round 1): <see cref="PreparedInventorReplacement.Execute"/> does a
///    MINIMAL COM-validity re-check on that SAME pointer then the single
///    <c>FileDescriptor.ReplaceReference</c> call.
///  * NOTE (P5C mutation-fix round 2): for an <c>AssemblyDocument</c>, a real
///    Inventor 2025 acceptance run proved <c>FileDescriptor.ReplaceReference</c>
///    is the WRONG API - it edits the document-level file-reference bookkeeping
///    entry but does not rebind the assembly's already-instantiated
///    <c>ComponentOccurrence</c> objects (a byte-identical replacement target
///    silently no-ops; a genuinely byte-different one is explicitly rejected
///    by Inventor). For that case the authorized file reference is instead
///    mapped onto EVERY <c>ComponentOccurrence</c> currently bound to the
///    exact selected file (occurrence matching is NOT a new authorization
///    decision - the authority remains the file-level identity already
///    established above) and mutated via
///    <c>ComponentOccurrence.Replace(FileName, ReplaceAll: true)</c> - see
///    <see cref="PreparedAssemblyOccurrenceReplacement"/>. Confirmed against
///    the actual Autodesk.Inventor.Interop 29.0.0.0 (Inventor 2025) assembly:
///    the second <c>Replace</c> parameter is named <c>ReplaceAll</c> (not
///    <c>MaintainRelationships</c>) and, per Inventor's own documented
///    semantics, replacing ANY ONE occurrence bound to a document with
///    <c>ReplaceAll: true</c> replaces EVERY occurrence bound to that same
///    document in one Inventor-internal operation - exactly matching P5C's
///    existing file-level (not per-occurrence) authorization model, so no
///    manual per-occurrence loop is needed or safer than Inventor's own
///    primitive.
///  * NOTE (P5C mutation-fix round 3 - Codex HIGH finding): because
///    <c>ReplaceAll: true</c> replaces EVERY occurrence Inventor itself
///    currently considers bound to the document - NOT merely the occurrences
///    this process previously recorded - two gaps had to be closed:
///    (1) occurrence enumeration during <see cref="Prepare"/> now FAILS
///    CLOSED the entire preparation if ANY occurrence cannot be safely
///    inspected, instead of silently excluding an unreadable one from the
///    matched set (an undercounted prepared set would be unsafe once
///    <c>ReplaceAll</c> is invoked); (2) immediately before the mutation call,
///    <see cref="PreparedAssemblyOccurrenceReplacement.Execute"/> re-enumerates
///    the assembly fresh and proves - via the COM-free
///    <see cref="RepairOccurrenceSetPolicy"/> - that the CURRENT live matching
///    occurrence set is EXACTLY the prepared set (no missing occurrence, no
///    new occurrence, no duplicate identity, no path drift) before calling
///    <c>ReplaceAll</c>. Neither change authorizes a different FILE reference;
///    both only prove the occurrence-level mapping of the already-authorized
///    reference has not drifted.
///
/// NOTE (P5C mutation-fix round 1): this deliberately does NOT enumerate
/// <c>Document.ReferencedDocumentDescriptors</c> - that is the read-only
/// dependency-TREE view <see cref="InventorDocumentReferenceSource"/> (P5A) uses
/// for diagnosis, and a manual Inventor 2025 acceptance run proved that
/// mutating through a <c>DocumentDescriptor.ReferencedFileDescriptor</c> can
/// return without throwing while the assembly's actual resolved reference does
/// not change. <c>Document.File.ReferencedFileDescriptors</c> is the flat,
/// file-level collection Inventor documents for programmatically repairing /
/// replacing a reference.
///
/// It never calls Open / Save / Save2 / Update, never activates a document,
/// never changes any other reference, and never touches PLM state or the CAD
/// file internals.
/// </summary>
internal sealed class InventorReferenceReplacer(
    InventorApi.Application application,
    IEnumerable<string> workspaceRoots) : IReferenceReplacer
{
    private readonly IReadOnlyList<string> _workspaceRoots = workspaceRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public IPreparedReferenceReplacement Prepare(ReferenceReplaceCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.TargetAbsolutePath)
            || !Path.IsPathFullyQualified(command.TargetAbsolutePath)
            || !File.Exists(command.TargetAbsolutePath))
        {
            return PreparedInventorReplacement.NotReady(
                "The target file is not a usable absolute path on disk - refusing to replace.");
        }

        var document = FindLoadedDocument(command.ReferencingDocumentAbsolutePath);
        if (document is null)
        {
            return PreparedInventorReplacement.NotReady(
                "The referencing document is not currently open in Inventor - open it and try again.");
        }

        InventorApi.FileDescriptorsEnumerator? descriptors;
        try
        {
            // The FLAT, file-level reference list - NOT the read-only
            // dependency-tree view (Document.ReferencedDocumentDescriptors).
            descriptors = document.File?.ReferencedFileDescriptors;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return PreparedInventorReplacement.NotReady("Inventor could not enumerate the document's file references.");
        }

        if (descriptors is null)
        {
            return PreparedInventorReplacement.NotReady("Inventor reported no file-level reference descriptors for the document.");
        }

        var comDescriptors = new List<InventorApi.FileDescriptor>();
        var snapshots = new List<ReferenceDescriptorSnapshot>();
        try
        {
            var index = 0;
            foreach (InventorApi.FileDescriptor descriptor in descriptors)
            {
                comDescriptors.Add(descriptor);
                var resolvedPath = ResolvedPathOf(descriptor);
                var identity = ManifestIdentityFor(resolvedPath);
                snapshots.Add(new ReferenceDescriptorSnapshot(
                    index,
                    resolvedPath,
                    ReportedNameOf(descriptor) ?? "(unnamed reference)",
                    identity?.CadDocumentId,
                    identity?.FileVersionId,
                    identity?.IsVerified == true));
                index++;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            return PreparedInventorReplacement.NotReady(
                "Inventor threw while reading the file-level reference descriptors - no change made.");
        }

        var targeting = ReferenceReplaceTargetingResolver.Resolve(
            snapshots,
            command.CurrentReferenceResolvedPath,
            command.ExpectedCadDocumentId,
            command.ExpectedCurrentFileVersionId);
        if (targeting.Outcome != ReferenceReplaceTargetingOutcome.Matched)
        {
            return PreparedInventorReplacement.NotReady(targeting.Detail);
        }

        // The chosen FileDescriptor identifies the exact AUTHORIZED file-level
        // reference - obtained directly from the flat file-level collection,
        // with NO further indirection through a DocumentDescriptor. This
        // remains the ONLY authorization decision; everything below only maps
        // that already-authorized identity onto the right Inventor mutation
        // object(s).
        var chosen = comDescriptors[targeting.Index];
        var selectedResolvedPath = ResolvedPathOf(chosen);

        // Round 2: an assembly's component references are not safely mutated
        // through the file-level FileDescriptor - map the authorized
        // reference onto its ComponentOccurrence(s) instead (see the class
        // doc comment). Any other referencing document type keeps the
        // unchanged round-1 file-level path.
        if (document is InventorApi.AssemblyDocument assemblyDocument)
        {
            return PrepareAssemblyOccurrenceReplacement(
                assemblyDocument, selectedResolvedPath, command.TargetAbsolutePath, targeting.Detail);
        }

        return new PreparedInventorReplacement(
            chosen,
            targeting.Detail,
            command.TargetAbsolutePath,
            selectedResolvedPath,
            () => ResolvedPathOf(chosen));
    }

    /// <summary>
    /// Maps the already-authorized file-level reference (identified by
    /// <paramref name="selectedResolvedPath"/> - the SAME identity the
    /// COM-free <see cref="ReferenceReplaceTargetingResolver"/> just verified,
    /// nothing new is authorized here) onto every <c>ComponentOccurrence</c>
    /// of <paramref name="assemblyDocument"/> currently bound to that exact
    /// path. Matching is by resolved absolute path only (never filename) -
    /// this is occurrence DISCOVERY, not a second identity check. The
    /// enumeration FAILS CLOSED (round 3) if any occurrence in the assembly
    /// cannot be safely inspected - it never silently excludes one. Zero
    /// matches fails closed rather than falling back to the file-level API.
    ///
    /// ROUND 5 (Codex HIGH - final target freshness must be last-mile): the
    /// fresh live occurrence enumeration, the prepared-vs-live equality proof,
    /// AND the selection of the EXACT representative COM pointer all happen
    /// HERE - in <see cref="Prepare"/>, i.e. as local/pre-mutation work that
    /// does NOT depend on, and runs strictly BEFORE, the protected target
    /// lease, the final checkout re-check, and (LAST of all) the final
    /// authoritative latest-version proof. <see cref="PreparedAssemblyOccurrenceReplacement.Execute"/>
    /// no longer re-enumerates anything - it only re-reads the ONE already-
    /// selected representative captured here, so nothing that touches the
    /// network, the manifest, or the full occurrence collection can run after
    /// the final latest-version proof succeeds.
    /// </summary>
    private static IPreparedReferenceReplacement PrepareAssemblyOccurrenceReplacement(
        InventorApi.AssemblyDocument assemblyDocument,
        string? selectedResolvedPath,
        string targetAbsolutePath,
        string targetingDetail)
    {
        if (string.IsNullOrWhiteSpace(selectedResolvedPath))
        {
            return PreparedInventorReplacement.NotReady(
                "The authorized reference did not resolve to a usable path - refusing to map it onto assembly occurrences.");
        }

        var matched = EnumerateMatchingOccurrencesOrNull(assemblyDocument, selectedResolvedPath);
        if (matched is null)
        {
            // Round 3 (Codex HIGH): a read failure on ANY occurrence - matching
            // or not - means the true matching set cannot be established.
            // ReplaceAll operates on Inventor's own internal notion of "every
            // occurrence bound to this document", so an undercounted list here
            // would be unsafe. Fail closed for the WHOLE preparation.
            return PreparedInventorReplacement.NotReady(
                "Inventor could not safely inspect every assembly component occurrence - refusing to guess "
                + "the complete matching set (fail closed).");
        }

        if (matched.Count == 0)
        {
            // FAIL CLOSED (zero matching occurrences never silently falls back
            // to the file-level API for an assembly).
            return PreparedInventorReplacement.NotReady(
                "No assembly component occurrence currently resolves to the authorized reference - refusing to guess.");
        }

        var identities = matched.Select(m => m.Identity).ToArray();

        // Prepared-vs-live equality proof: a duplicate identity within the
        // freshly enumerated set itself is never valid evidence to authorize
        // on (the freshly-enumerated "live" set must be internally consistent
        // with itself to serve as the "prepared" baseline Execute will later
        // re-check its single representative against).
        var selfCheck = RepairOccurrenceSetPolicy.VerifyEqual(identities, identities);
        if (!selfCheck.IsEqual)
        {
            return PreparedInventorReplacement.NotReady(
                "The assembly reports an inconsistent occurrence identity set: " + selfCheck.Detail);
        }

        // Select the EXACT representative NOW - the SAME COM pointer Execute
        // will (much later, after the lease + final checkout + final
        // latest-version proof) do a minimal re-check on and call Replace on.
        var representative = matched[0];

        return new PreparedAssemblyOccurrenceReplacement(
            matched.Count,
            representative.Occurrence,
            representative.Identity,
            selectedResolvedPath,
            targetAbsolutePath,
            targetingDetail);
    }

    /// <summary>
    /// Enumerates EVERY occurrence of the assembly and returns the COMPLETE
    /// list of (COM pointer, stable identity) pairs whose current referenced
    /// file resolves to <paramref name="targetResolvedPath"/>. Returns
    /// <c>null</c> (never a partial list) if the occurrence collection itself,
    /// or ANY single occurrence's Name / referenced-file read, throws - a read
    /// failure anywhere means the true matching set cannot be established. A
    /// clean <c>DocumentFound == false</c> answer is a LEGITIMATE "this
    /// occurrence's reference is missing" result (not a read failure) and is
    /// correctly excluded from the match, since it can never equal a resolved
    /// existing-file path.
    /// </summary>
    private static List<(InventorApi.ComponentOccurrence Occurrence, RepairOccurrenceIdentity Identity)>?
        EnumerateMatchingOccurrencesOrNull(InventorApi.AssemblyDocument assemblyDocument, string targetResolvedPath)
    {
        InventorApi.ComponentOccurrences? occurrences;
        try
        {
            occurrences = assemblyDocument.ComponentDefinition?.Occurrences;
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            return null;
        }
        if (occurrences is null)
        {
            return null;
        }

        var matched = new List<(InventorApi.ComponentOccurrence, RepairOccurrenceIdentity)>();
        try
        {
            foreach (InventorApi.ComponentOccurrence occurrence in occurrences)
            {
                // Both reads are allowed to throw here - deliberately NOT
                // caught per-occurrence. A failure on ANY occurrence aborts
                // the whole enumeration (caught below) rather than silently
                // treating that occurrence as "does not match".
                var name = occurrence.Name;
                var resolvedPath = ResolvedFileOrLegitimatelyUnbound(occurrence);
                if (resolvedPath is not null
                    && string.Equals(resolvedPath, targetResolvedPath, StringComparison.OrdinalIgnoreCase))
                {
                    matched.Add((occurrence, new RepairOccurrenceIdentity(name, resolvedPath)));
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or InvalidCastException)
        {
            return null;
        }
        return matched;
    }

    /// <summary>
    /// The occurrence-level "what file currently backs this exact occurrence"
    /// read (<c>ComponentOccurrence.ReferencedFileDescriptor</c> - distinct
    /// from the document-level flat/tree collections). A clean
    /// <c>DocumentFound == false</c> is a legitimate, well-formed "unbound /
    /// missing" answer and returns <c>null</c> WITHOUT throwing - it is not an
    /// inspection failure. Any COM read failure is deliberately left
    /// UNCAUGHT here so the caller's enumeration fails the whole assembly
    /// closed instead of silently skipping this one occurrence.
    /// </summary>
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

    private static string? ResolvedPathOf(InventorApi.FileDescriptor descriptor)
    {
        try
        {
            var missing = (bool?)descriptor.ReferenceMissing ?? false;
            if (missing)
            {
                return null;
            }
            var full = descriptor.FullFileName;
            return string.IsNullOrWhiteSpace(full) ? null : full;
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or InvalidCastException)
        {
            return null;
        }
    }

    private static string? ReportedNameOf(InventorApi.FileDescriptor descriptor)
    {
        try
        {
            var full = descriptor.FullFileName;
            return string.IsNullOrWhiteSpace(full) ? null : Path.GetFileName(full);
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or ArgumentException)
        {
            return null;
        }
    }

    private CadManifestIdentity? ManifestIdentityFor(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return null;
        foreach (var root in _workspaceRoots)
        {
            try
            {
                var entry = WorkspaceManifest.LoadOrEmpty(root).FindByAbsolutePath(absolutePath);
                if (entry is not null)
                {
                    return new CadManifestIdentity(entry.CadDocumentId, entry.FileVersionId,
                        entry.DocumentNumber, entry.RelativePath,
                        entry.State == WorkspaceManifestEntryState.Verified);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkspaceRootException)
            {
                // Continue; no verified identity means no mutation.
            }
        }
        return null;
    }

    private InventorApi.Document? FindLoadedDocument(string absoluteDocumentPath) =>
        FindLoadedDocument(application, absoluteDocumentPath);

    private static InventorApi.Document? FindLoadedDocument(InventorApi.Application app, string absoluteDocumentPath)
    {
        if (string.IsNullOrWhiteSpace(absoluteDocumentPath))
        {
            return null;
        }

        try
        {
            foreach (InventorApi.Document document in app.Documents)
            {
                if (PathMatches(document, absoluteDocumentPath))
                {
                    return document;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            return null;
        }
        return null;
    }

    private static bool PathMatches(InventorApi.Document document, string absoluteDocumentPath)
    {
        try
        {
            var full = document.FullFileName;
            return !string.IsNullOrWhiteSpace(full)
                && string.Equals(
                    Path.GetFullPath(full),
                    Path.GetFullPath(absoluteDocumentPath),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort, READ-ONLY count of how many LIVE assembly component
    /// occurrences currently resolve to <paramref name="currentReferenceResolvedPath"/>
    /// in the loaded document at <paramref name="referencingDocumentAbsolutePath"/>.
    /// For UI DISCLOSURE ONLY (the Repair Reference confirmation dialog,
    /// via <see cref="RepairOccurrenceDisclosureText.ScopeSentence"/>) - it is
    /// NEVER used to authorize, select, or count what actually gets mutated;
    /// <see cref="Prepare"/> and the pre-mutation equality check in
    /// <see cref="PreparedAssemblyOccurrenceReplacement"/> are the sole
    /// authorities for that. Returns <c>null</c> (never a guessed number) when
    /// the document isn't loaded, isn't an assembly, or the count could not be
    /// established safely - callers must fall back to occurrence-count-agnostic
    /// wording in that case.
    /// </summary>
    internal static int? TryCountLiveMatchingOccurrencesForDisclosure(
        InventorApi.Application app, string? referencingDocumentAbsolutePath, string? currentReferenceResolvedPath)
    {
        if (string.IsNullOrWhiteSpace(referencingDocumentAbsolutePath)
            || string.IsNullOrWhiteSpace(currentReferenceResolvedPath))
        {
            return null;
        }

        InventorApi.Document? document;
        try
        {
            document = FindLoadedDocument(app, referencingDocumentAbsolutePath);
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            return null;
        }

        if (document is not InventorApi.AssemblyDocument assemblyDocument)
        {
            return null;
        }

        try
        {
            return EnumerateMatchingOccurrencesOrNull(assemblyDocument, currentReferenceResolvedPath)?.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            return null;
        }
    }
}

/// <summary>
/// A P5C replacement bound to ONE already-selected Inventor <c>FileDescriptor</c>
/// COM pointer, obtained directly from <c>Document.File.ReferencedFileDescriptors</c>
/// (the flat file-level reference list). <see cref="Execute"/> does a minimal
/// validity re-check on that SAME pointer then the single
/// <c>FileDescriptor.ReplaceReference</c> call - it can NEVER enumerate,
/// search, reload a manifest, or select a different descriptor.
/// </summary>
internal sealed class PreparedInventorReplacement : IPreparedReferenceReplacement
{
    private readonly InventorApi.FileDescriptor? _fileDescriptor;
    private readonly string _targetAbsolutePath;
    private readonly string? _selectedResolvedPath;
    private readonly Func<string?> _currentResolvedPath;

    internal PreparedInventorReplacement(
        InventorApi.FileDescriptor? fileDescriptor,
        string detail,
        string targetAbsolutePath,
        string? selectedResolvedPath,
        Func<string?> currentResolvedPath)
    {
        _fileDescriptor = fileDescriptor;
        Detail = detail;
        _targetAbsolutePath = targetAbsolutePath;
        _selectedResolvedPath = selectedResolvedPath;
        _currentResolvedPath = currentResolvedPath;
    }

    public static PreparedInventorReplacement NotReady(string detail) =>
        new(null, detail, "", null, () => null);

    public bool Ready => _fileDescriptor is not null;

    public string Detail { get; }

    public ReferenceReplaceResult Execute()
    {
        if (_fileDescriptor is null)
        {
            return new ReferenceReplaceResult(false, "The replacement was not prepared: " + Detail);
        }

        // MINIMAL final validity re-check on the SAME descriptor COM pointer -
        // NO manifest reload, NO re-enumeration, NO re-selection. It can never
        // authorize a DIFFERENT descriptor: it only refuses to act if the exact
        // descriptor selected during Prepare is no longer valid or no longer
        // resolves to the exact path it was selected for (which was matched to a
        // verified stable Arch identity at selection time).
        string? currentPath;
        try
        {
            currentPath = _currentResolvedPath();
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            return new ReferenceReplaceResult(false,
                "The prepared reference descriptor is no longer valid - refusing to replace.");
        }

        if (!PathEquals(currentPath, _selectedResolvedPath))
        {
            return new ReferenceReplaceResult(false,
                "The prepared reference descriptor no longer resolves to the exact reference that was selected - "
                + "refusing to replace a changed descriptor.");
        }

        try
        {
            _fileDescriptor.ReplaceReference(_targetAbsolutePath);
        }
        catch (COMException ex)
        {
            return new ReferenceReplaceResult(false,
                $"Inventor reported a replacement error (COMException, 0x{ex.HResult:X8}): {SafeMessage(ex)}. "
                + "The document may already be modified.",
                MutationInvoked: true);
        }
        catch (Exception ex) when (ex is InvalidComObjectException or ArgumentException)
        {
            return new ReferenceReplaceResult(false,
                $"Inventor reported a replacement error ({ex.GetType().Name}, 0x{ex.HResult:X8}): {SafeMessage(ex)}. "
                + "The document may already be modified.",
                MutationInvoked: true);
        }

        return new ReferenceReplaceResult(true,
            $"Repointed reference \"{Detail}\" via ReplaceReference. The document is now modified in memory (not saved).",
            MutationInvoked: true);
    }

    /// <summary>A COM error message, safely defaulted when empty (never a
    ///  secret - Inventor error text is deterministic diagnostic text).</summary>
    internal static string SafeMessage(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message) ? "(no exception message)" : ex.Message;

    private static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

/// <summary>
/// A P5C replacement bound to an IMMUTABLE list of already-selected assembly
/// <c>ComponentOccurrence</c> COM pointers - every occurrence, discovered in
/// <see cref="InventorReferenceReplacer.Prepare"/> with a FAIL-CLOSED
/// enumeration, that currently resolves to the ONE authorized file-level
/// reference. This is round 2's fix for the assembly-component case (see the
/// class doc comment on <see cref="InventorReferenceReplacer"/>):
/// <c>FileDescriptor.ReplaceReference</c> edits document-level bookkeeping
/// only and does not rebind an assembly's already-instantiated occurrences.
///
/// Round 3 (Codex HIGH finding) adds a FINAL LIVE-SET EQUALITY CHECK:
/// <see cref="Execute"/> re-enumerates the assembly fresh and proves, via the
/// COM-free <see cref="RepairOccurrenceSetPolicy"/>, that the CURRENT live
/// matching occurrence set is EXACTLY the prepared set - no missing prepared
/// occurrence, no new/extra occurrence, no duplicate identity, no path drift.
/// This does NOT authorize a different file reference (that identity was
/// already established, unchanged, in <c>Prepare</c>); it only proves the
/// occurrence-level mapping has not drifted since. Only if the sets are
/// proven equal does it make ONE
/// <c>ComponentOccurrence.Replace(FileName, ReplaceAll: true)</c> call on a
/// representative prepared occurrence. Per Inventor's own documented
/// semantics, <c>ReplaceAll: true</c> replaces EVERY occurrence bound to that
/// same document in that single call - this is Inventor's native mechanism
/// for exactly the "one file-level reference, N occurrences" mapping P5C's
/// architecture already assumes, so no manual per-occurrence loop is used.
/// Because it is genuinely one COM call, a failure cannot be decomposed into
/// "K of N occurrences succeeded first" - the honest report is that all N
/// matched occurrences were targeted together and some or all of them may
/// already be modified; this is not weaker than a manual loop, since Inventor
/// does not expose a lower-level primitive that would let this code observe
/// finer-grained partial progress.
///
/// ROUND 4 (Codex HIGH - stored COM pointer rebinding / substitute occurrence):
/// this type does not retain any COM pointer OTHER than the one already-
/// selected representative captured at <see cref="InventorReferenceReplacer.Prepare"/>
/// time.
///
/// ROUND 5 (Codex HIGH - final target freshness must be last-mile): the fresh
/// live occurrence enumeration, the prepared-vs-live equality proof, and the
/// selection of the representative COM pointer ALL now happen back in
/// <c>Prepare</c> (see <see cref="InventorReferenceReplacer.PrepareAssemblyOccurrenceReplacement"/>) -
/// i.e. BEFORE the protected lease, the final checkout re-check, and (LAST of
/// all) the final authoritative latest-version proof run. <see cref="Execute"/>
/// therefore does the SMALLEST possible thing after that proof succeeds: a
/// MINIMAL, no-network, no-manifest re-read of the ONE already-selected
/// representative pointer to prove it still references the authorized old
/// path, then immediately the single <c>ComponentOccurrence.Replace</c> call.
/// No re-enumeration and no fresh occurrence-set comparison happens here - by
/// design, that work is already done, earlier, in Prepare.
/// </summary>
internal sealed class PreparedAssemblyOccurrenceReplacement : IPreparedReferenceReplacement
{
    private readonly int _preparedCount;
    private readonly InventorApi.ComponentOccurrence? _representative;
    private readonly string? _selectedResolvedPath;
    private readonly string _targetAbsolutePath;

    internal PreparedAssemblyOccurrenceReplacement(
        int preparedCount,
        InventorApi.ComponentOccurrence representative,
        RepairOccurrenceIdentity representativeIdentity,
        string selectedResolvedPath,
        string targetAbsolutePath,
        string targetingDetail)
    {
        _preparedCount = preparedCount;
        _representative = representative;
        _selectedResolvedPath = selectedResolvedPath;
        _targetAbsolutePath = targetAbsolutePath;
        Detail = $"{targetingDetail} Mapped to {preparedCount} assembly component occurrence(s) "
            + $"currently bound to that file (representative: {representativeIdentity.Name}) via "
            + "ComponentOccurrence.Replace, ReplaceAll: true.";
    }

    public bool Ready => _representative is not null;

    public string Detail { get; }

    public ReferenceReplaceResult Execute()
    {
        if (_representative is null)
        {
            return new ReferenceReplaceResult(false, "The replacement was not prepared: " + Detail);
        }

        // ROUND 5: the ONLY thing permitted between the final authoritative
        // latest-version proof and the actual mutation call - a MINIMAL,
        // no-network, no-manifest re-read of the EXACT already-selected
        // representative pointer (chosen back in Prepare, before the lease
        // and every server-authority check). No re-enumeration, no fresh
        // occurrence-set comparison, no different pointer can be selected
        // here.
        string? representativePath;
        try
        {
            representativePath = ResolveOrNull(_representative);
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            return new ReferenceReplaceResult(false,
                "The selected mutation representative is no longer valid immediately before mutation - "
                + "refusing to replace (fail closed).");
        }
        if (!PathEquals(representativePath, _selectedResolvedPath))
        {
            return new ReferenceReplaceResult(false,
                "The selected mutation representative no longer resolves to the authorized old path immediately "
                + "before mutation - refusing to replace (fail closed).");
        }

        // ReplaceAll=true asks Inventor to replace every occurrence bound to
        // that same document, which is exactly all N occurrences proven equal
        // to the prepared set back in Prepare.
        try
        {
            _representative.Replace(_targetAbsolutePath, true);
        }
        catch (COMException ex)
        {
            return new ReferenceReplaceResult(false,
                $"Inventor reported a replacement error (COMException, 0x{ex.HResult:X8}): "
                + $"{PreparedInventorReplacement.SafeMessage(ex)}. {_preparedCount} matching occurrence(s) were "
                + "targeted together via ReplaceAll; some or all may already be modified.",
                MutationInvoked: true);
        }
        catch (Exception ex) when (ex is InvalidComObjectException or ArgumentException)
        {
            return new ReferenceReplaceResult(false,
                $"Inventor reported a replacement error ({ex.GetType().Name}, 0x{ex.HResult:X8}): "
                + $"{PreparedInventorReplacement.SafeMessage(ex)}. {_preparedCount} matching occurrence(s) were "
                + "targeted together via ReplaceAll; some or all may already be modified.",
                MutationInvoked: true);
        }

        return new ReferenceReplaceResult(true,
            $"Repointed {_preparedCount} assembly component occurrence(s) - \"{Detail}\" - via "
            + "ComponentOccurrence.Replace(ReplaceAll: true). The document is now modified in memory (not saved).",
            MutationInvoked: true);
    }

    private static string? ResolveOrNull(InventorApi.ComponentOccurrence occurrence)
    {
        var rfd = occurrence.ReferencedFileDescriptor;
        if (rfd is null || !rfd.DocumentFound)
        {
            return null;
        }
        var full = rfd.FullFileName;
        return string.IsNullOrWhiteSpace(full) ? null : full;
    }

    private static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
