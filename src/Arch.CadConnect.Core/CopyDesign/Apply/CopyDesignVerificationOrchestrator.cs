using Arch.CadConnect.Core.References;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// P6E-B: the pure Core engine for an INDEPENDENT, READ-ONLY re-verification
/// of one Copy Design operation, by <c>operationId</c>, at any time after it
/// ran (fully or partially).
///
/// STRICTLY READ-ONLY - structurally, not merely by convention: this class
/// is wired with ONLY an <see cref="ICopyDesignVerifier"/> (facts-gathering,
/// already read-only by its own contract - see that interface's doc
/// comment), an <see cref="ICopyDesignFileHasher"/>, and two plain local
/// file-I/O delegates. It has NO dependency on <c>ICopyDesignPhysicalCopier</c>,
/// <c>ICopyDesignReferenceRewirer</c>, <c>ICopyDesignMaterializer</c>, or
/// <c>ICopyDesignReservationClient</c> AT ALL - there is no seam through
/// which this class could copy, rewire, materialize, or reserve anything
/// even if a caller wanted it to.
///
/// REUSE OVER DUPLICATION:
///   - <see cref="CopyDesignVerificationTopologyBuilder"/> builds the
///     expected graph.
///   - <see cref="CopyDesignReferenceTargetResolver.Resolve"/> (P6C/P6D,
///     UNCHANGED) resolves each IAM/IDW/DWG node's expected children.
///   - <see cref="CopyDesignVerificationReferenceInspector"/> (this round's
///     own adapter) reuses <see cref="CopyDesignVerificationEvaluator.Evaluate"/>
///     (P6C/P6D, UNCHANGED) for reference/destination/type/openable
///     judgment, WITHOUT exposing any synthetic "before operation" evidence
///     as genuine - see that adapter's own extensive doc comment for the
///     full compatibility-boundary reasoning.
///
/// P6E-B SEMANTIC CORRECTION (REQUIRED vs. INFORMATIONAL): every check is
/// tagged <see cref="CopyDesignVerificationCheckRequirement.Required"/> or
/// <see cref="CopyDesignVerificationCheckRequirement.Informational"/> - see
/// that enum's own doc comment. A REUSE entry's historical byte
/// immutability is ALWAYS reported (honestly, NOT_PROVABLE, with a clear
/// reason - P6D never freezes a source FileVersion for REUSE) but is
/// INFORMATIONAL: it never by itself prevents VERIFIED. This is
/// architecture-locked - do not change it back to Required.
///
/// FAIL CLOSED: a structurally untrustworthy operation (see
/// <see cref="CopyDesignVerificationTopologyBuilder"/>'s own "FAILS THE
/// WHOLE BUILD" list) never reaches per-entry evaluation at all - the whole
/// result is INCOMPLETE (P6E-B/D FINAL SEMANTIC ALIGNMENT: NOT FAILED - see
/// <see cref="CopyDesignOperationVerificationResult.StructuralFailure"/>'s
/// own doc comment for why "the topology could not be built" is categorically
/// "verification cannot legitimately conclude", never a proven defect in the
/// copied package). Missing/invalid REQUIRED evidence is NEVER silently
/// treated as a pass - it always becomes an explicit NOT_PROVABLE check,
/// rolling that entry (and therefore the whole operation) up to, at best,
/// INCOMPLETE. A concrete defect PROVEN after a trustworthy topology was
/// already built (a hash/size mismatch, a missing materialized destination, a
/// stale/unplanned reference, a wrong REUSE identity) still rolls up to
/// FAILED exactly as before.
///
/// P6E FINAL SEMANTIC CLEANUP: this SAME "cannot establish the target vs.
/// proven wrong once established" boundary applies ONE LEVEL DOWN, per
/// entry, to destination resolution - an unsafe/uncontained destination
/// name/path (see <see cref="CopyDesignVerificationTopologyBuilder.Result.DestinationProblems"/>)
/// means the expected verification target itself could not be safely
/// established, so it is a REQUIRED <c>DestinationResolution</c> NOT_PROVABLE
/// check (INCOMPLETE), never a <c>DestinationIntegrity</c> FAILED one - and
/// performs ZERO filesystem or COM verification for that entry's
/// destination. Once a destination genuinely resolves, a missing file, a
/// hash mismatch, or a size mismatch remain <c>DestinationIntegrity</c>
/// FAILED exactly as before - this cleanup changes ONLY the "could the
/// target be established at all" step.
/// </summary>
public sealed class CopyDesignVerificationOrchestrator(
    /// <summary>Gathers facts for a copied/reused IAM or IPT - in
    ///  production, <c>InventorCopyDesignVerifier</c> (P6D, UNMODIFIED).</summary>
    ICopyDesignVerifier verifier,
    ICopyDesignFileHasher hasher,
    /// <summary>True iff a local absolute path exists - used for BOTH
    ///  source and destination paths alike (a plain existence probe has no
    ///  reason to differ between them). Injected (never a bare
    ///  <c>File.Exists</c> call inline) so this class stays testable with
    ///  fakes, matching every other file-touching seam in this codebase.</summary>
    Func<string, bool> localFileExists,
    /// <summary>Gathers facts for a copied/reused IDW/DWG - in production,
    ///  <c>InventorCopyDesignDrawingVerifier</c> (P6D, UNMODIFIED). Required
    ///  (non-null) ONLY when the operation actually includes a drawing -
    ///  see the config guard in <see cref="VerifyAsync"/>.</summary>
    ICopyDesignVerifier? drawingVerifier = null,
    /// <summary>Reads a local file's byte length - injected, matching
    ///  <c>CopyDesignApplyOrchestrator</c>'s own identical seam. Defaults to
    ///  the real filesystem when omitted.</summary>
    Func<string, long>? getFileSize = null)
{
    private readonly Func<string, long> _getFileSize = getFileSize ?? (path => new FileInfo(path).Length);

    /// <param name="support">MUST already be <see cref="CopyDesignVerificationSupportResult.Success"/> -
    ///  the caller checks this (and reports whatever transport/auth/not-
    ///  found problem prevented it) before ever calling this method,
    ///  exactly like durable resume's own <c>!status.Success</c> gate.</param>
    /// <param name="sourceManifest">The CURRENT source workspace's manifest.</param>
    /// <param name="destinationFolder">The absolute folder the copied
    ///  package was materialized into.</param>
    /// <param name="modelSourceIdsByDrawingSourceId">See
    ///  <see cref="CopyDesignVerificationTopologyBuilder.Build"/>'s own doc
    ///  comment for the exact "missing key vs. present-but-empty" contract.</param>
    public async Task<CopyDesignOperationVerificationResult> VerifyAsync(
        CopyDesignVerificationSupportResult support,
        WorkspaceManifest sourceManifest,
        string destinationFolder,
        IReadOnlyDictionary<string, IReadOnlySet<string>> modelSourceIdsByDrawingSourceId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(support);
        ArgumentNullException.ThrowIfNull(sourceManifest);
        ArgumentNullException.ThrowIfNull(modelSourceIdsByDrawingSourceId);

        var topology = CopyDesignVerificationTopologyBuilder.Build(support, sourceManifest, destinationFolder, modelSourceIdsByDrawingSourceId);
        if (!topology.Success)
        {
            return CopyDesignOperationVerificationResult.StructuralFailure(topology.FailureReason!, support.OperationId);
        }
        var plan = topology.Plan!;

        var drawingTypesPresent = plan.Nodes.Any(n => n.DocumentType is CadDocumentType.Idw or CadDocumentType.Dwg);
        if (drawingTypesPresent && drawingVerifier is null)
        {
            return CopyDesignOperationVerificationResult.StructuralFailure(
                "The operation includes a drawing (IDW/DWG), but this orchestrator was not configured with a drawing verification adapter - refusing to produce a partial/misleading result.",
                support.OperationId);
        }

        var nodeByResultingId = plan.Nodes.ToDictionary(n => n.CadDocumentId!, StringComparer.Ordinal);
        var integrityByFileVersionId = (support.SourceIntegrity ?? Array.Empty<CopyDesignSourceIntegrityEvidence>())
            .ToDictionary(e => e.SourceFileVersionId, StringComparer.Ordinal);
        var orderedEntries = support.Entries!.OrderBy(e => e.Ordinal).ToArray();

        var entryResults = new List<CopyDesignVerificationEntryResult>(orderedEntries.Length);
        foreach (var entry in orderedEntries)
        {
            if (!nodeByResultingId.TryGetValue(entry.ResultingCadDocumentId, out var node))
            {
                // Structurally impossible given a successful topology build
                // (every entry produces exactly one node), but never assumed.
                return CopyDesignOperationVerificationResult.StructuralFailure(
                    $"Entry {entry.Ordinal} (\"{entry.OriginalFileName}\") has no corresponding topology node - internal inconsistency.",
                    support.OperationId);
            }

            entryResults.Add(entry.Action == "REUSE"
                ? await VerifyReuseEntryAsync(entry, node, plan, topology, ct).ConfigureAwait(false)
                : await VerifyCopyEntryAsync(entry, node, plan, topology, integrityByFileVersionId, ct).ConfigureAwait(false));
        }

        return new CopyDesignOperationVerificationResult(CopyDesignOperationVerificationResult.Rollup(entryResults), support.OperationId, entryResults);
    }

    /// <summary>
    /// P6E-B SEMANTIC CORRECTION: a REUSE entry now has TWO REQUIRED checks
    /// (<c>IntendedReuseIdentity</c>, and <c>ReferenceTopology</c> when
    /// applicable) plus ONE INFORMATIONAL one
    /// (<c>SourceByteImmutability</c>). The informational check is ALWAYS
    /// NOT_PROVABLE (P6D never freezes a source FileVersion for REUSE) but,
    /// being informational, NEVER by itself prevents this entry - or an
    /// otherwise-correct operation containing it - from reaching VERIFIED.
    /// This is architecture-locked: do not change it back to Required, and
    /// do not remove it from the output - it must remain visibly,
    /// truthfully reported.
    /// </summary>
    private async Task<CopyDesignVerificationEntryResult> VerifyReuseEntryAsync(
        CopyDesignDurableResumeEntry entry,
        CopyDesignNode node,
        CopyDesignPlan plan,
        CopyDesignVerificationTopologyBuilder.Result topology,
        CancellationToken ct)
    {
        var checks = new List<CopyDesignVerificationCheck>();

        // ---- IntendedReuseIdentity (Required) ----------------------------
        // The topology build already proved this identity resolves in the
        // local workspace manifest; this check adds the one thing that
        // COULD have changed since - whether the file it points at is still
        // actually there.
        checks.Add(localFileExists(node.SourceAbsolutePath)
            ? CopyDesignVerificationCheck.Pass("IntendedReuseIdentity")
            : CopyDesignVerificationCheck.Fail("IntendedReuseIdentity", "The reused document's expected local file no longer exists."));

        // ---- ReferenceTopology (Required, when applicable to this
        //      document type) - ALWAYS attempted for IAM/IDW/DWG, exactly
        //      like a materialized COPY entry (never gated on "does this
        //      operation's edge list happen to be non-empty for it" - a
        //      DRAWING with a missing model-dependency authority must be
        //      flagged as NotProvable EVEN WITH ZERO edges reaching the
        //      topology graph, and an IAM/DRAWING with genuinely zero
        //      expected children still deserves confirmation that its
        //      ACTUAL reference set is also empty, not silently skipped. ---
        if (node.DocumentType is CadDocumentType.Iam or CadDocumentType.Idw or CadDocumentType.Dwg)
        {
            var relationshipKind = node.DocumentType == CadDocumentType.Iam ? CadRelationshipKind.Component : CadRelationshipKind.DrawingModel;
            checks.Add(await VerifyReferenceTopologyAsync(node, plan, topology, entry.ResultingCadDocumentId, relationshipKind,
                // A REUSE node has no destination of its own (nothing was
                // ever copied) - its OWN, unchanged source file is the
                // only thing that exists to open and inspect. A probe
                // node borrows the identity but redirects
                // ProposedDestinationAbsolutePath to SourceAbsolutePath
                // PURELY so ICopyDesignVerifier.GatherFactsAsync (which
                // always opens ProposedDestinationAbsolutePath) has
                // something real to open - never treated as this node
                // having actually been "copied" anywhere.
                probeNode: node with { ProposedDestinationAbsolutePath = node.SourceAbsolutePath },
                currentSourceSha256: null, ct).ConfigureAwait(false));
        }

        // ---- SourceByteImmutability (INFORMATIONAL - architecture-locked) -
        checks.Add(CopyDesignVerificationCheck.NotProvable(
            "SourceByteImmutability",
            "P6D does not freeze a historical FileVersion baseline for REUSE entries - byte immutability since the operation was performed cannot be proven.",
            CopyDesignVerificationCheckRequirement.Informational));

        return new CopyDesignVerificationEntryResult(
            entry.Ordinal, entry.ResultingCadDocumentId, entry.Action, entry.OriginalFileName, entry.OriginalDocumentType,
            CopyDesignVerificationEntryResult.Rollup(checks), checks);
    }

    private async Task<CopyDesignVerificationEntryResult> VerifyCopyEntryAsync(
        CopyDesignDurableResumeEntry entry,
        CopyDesignNode node,
        CopyDesignPlan plan,
        CopyDesignVerificationTopologyBuilder.Result topology,
        IReadOnlyDictionary<string, CopyDesignSourceIntegrityEvidence> integrityByFileVersionId,
        CancellationToken ct)
    {
        var checks = new List<CopyDesignVerificationCheck>();

        // ---- MaterializationState (Required) -----------------------------
        var materialized = false;
        switch (entry.State)
        {
            case "PENDING":
                checks.Add(CopyDesignVerificationCheck.NotProvable("MaterializationState", "Not yet materialized (server reports PENDING)."));
                break;
            case "INVALID":
                checks.Add(CopyDesignVerificationCheck.NotProvable("MaterializationState", "The server reports this entry's materialization state as INVALID - cannot verify against inconsistent data."));
                break;
            case "MATERIALIZED":
                if (entry.VersionNumber != 1 || string.IsNullOrWhiteSpace(entry.FileVersionId)
                    || !FileVersionIntegrity.IsCanonicalSha256(entry.Sha256)
                    || entry.FileSize is null || !FileVersionIntegrity.IsRepresentableFileSize(entry.FileSize.Value))
                {
                    checks.Add(CopyDesignVerificationCheck.Fail("MaterializationState", "The server reports MATERIALIZED but its own FileVersion identity is incomplete or non-canonical."));
                }
                else
                {
                    checks.Add(CopyDesignVerificationCheck.Pass("MaterializationState"));
                    materialized = true;
                }
                break;
            default:
                checks.Add(CopyDesignVerificationCheck.Fail("MaterializationState", $"Unrecognized entry state \"{entry.State}\" for a COPY action."));
                break;
        }

        // ---- SourceIntegrity (Required, always attempted, independent of
        //      materialization) - the SAME current-source-hash value is
        //      reused below for ReferenceTopology's intra-scan probe, so the
        //      source is hashed AT MOST once per entry. ---------------------
        string? currentSourceSha256 = null;
        if (localFileExists(node.SourceAbsolutePath))
        {
            try { currentSourceSha256 = hasher.ComputeSha256(node.SourceAbsolutePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { currentSourceSha256 = null; }
        }

        var evidence = entry.SourceFileVersionId is not null && integrityByFileVersionId.TryGetValue(entry.SourceFileVersionId, out var foundEvidence)
            ? foundEvidence
            : null;
        if (evidence is null)
        {
            checks.Add(CopyDesignVerificationCheck.NotProvable("SourceIntegrity", "No source integrity evidence was returned for this entry."));
        }
        else if (evidence.State == CopyDesignSourceIntegrityState.Unavailable)
        {
            checks.Add(CopyDesignVerificationCheck.NotProvable("SourceIntegrity", "No authoritative source FileVersion could be found - immutability since the operation cannot be proven."));
        }
        else if (evidence.State == CopyDesignSourceIntegrityState.Invalid)
        {
            checks.Add(CopyDesignVerificationCheck.NotProvable("SourceIntegrity", $"Source FileVersion evidence is invalid: {evidence.Reason}"));
        }
        else if (currentSourceSha256 is null)
        {
            checks.Add(CopyDesignVerificationCheck.NotProvable("SourceIntegrity", "The local source file no longer exists (or could not be read) - immutability cannot be confirmed."));
        }
        else
        {
            long? currentSourceSize = null;
            try { currentSourceSize = _getFileSize(node.SourceAbsolutePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { currentSourceSize = null; }

            if (currentSourceSize is not null
                && string.Equals(currentSourceSha256, evidence.Sha256, StringComparison.OrdinalIgnoreCase)
                && currentSourceSize == evidence.FileSize)
            {
                checks.Add(CopyDesignVerificationCheck.Pass("SourceIntegrity"));
            }
            else
            {
                checks.Add(CopyDesignVerificationCheck.Fail(
                    "SourceIntegrity",
                    "The local source file's bytes no longer match the authoritative checked-in FileVersion - the source appears to have been modified."));
            }
        }

        // ---- DestinationResolution (can the expected destination even be
        //      established? - P6E FINAL SEMANTIC CLEANUP: NotProvable/
        //      INCOMPLETE, never Failed) + DestinationIntegrity (once
        //      resolved: does the actual file match? - Failed on a proven
        //      mismatch/absence) + ReferenceTopology (Required, only
        //      meaningful once MATERIALIZED). ---------------------------
        if (materialized)
        {
            await VerifyDestinationAndTopologyAsync(entry, node, plan, topology, checks, currentSourceSha256, ct).ConfigureAwait(false);
        }

        var outcome = CopyDesignVerificationEntryResult.Rollup(checks);
        return new CopyDesignVerificationEntryResult(entry.Ordinal, entry.ResultingCadDocumentId, entry.Action, entry.OriginalFileName, entry.OriginalDocumentType, outcome, checks);
    }

    private async Task VerifyDestinationAndTopologyAsync(
        CopyDesignDurableResumeEntry entry,
        CopyDesignNode node,
        CopyDesignPlan plan,
        CopyDesignVerificationTopologyBuilder.Result topology,
        List<CopyDesignVerificationCheck> checks,
        string? currentSourceSha256,
        CancellationToken ct)
    {
        string? destination = node.ProposedDestinationAbsolutePath;
        if (topology.DestinationProblems is not null && topology.DestinationProblems.TryGetValue(entry.ResultingCadDocumentId, out var destProblem))
        {
            // P6E FINAL SEMANTIC CLEANUP: an unsafe/uncontained destination
            // name/path means P6E cannot safely ESTABLISH what it was even
            // supposed to verify - this is categorically INCOMPLETE (cannot
            // establish the expected target), never a proven FAILED defect
            // (that would wrongly claim "the copied package was proven
            // wrong" when nothing about the actual copy was ever inspected).
            // No filesystem or COM verification is performed against this
            // entry's destination - `destination` stays null, so neither the
            // hash/size branch below nor ReferenceTopology's COM inspection
            // further down is ever reached for it.
            checks.Add(CopyDesignVerificationCheck.NotProvable("DestinationResolution", destProblem));
            destination = null;
        }
        else if (destination is null)
        {
            // Defensive only - CopyDesignVerificationTopologyBuilder.Build
            // only ever leaves ProposedDestinationAbsolutePath null when it
            // ALSO recorded a DestinationProblems entry (the branch above),
            // so this is unreachable in practice; kept, and given the SAME
            // "cannot establish the target" INCOMPLETE treatment, for the
            // "never assumed" safety this codebase applies everywhere else.
            checks.Add(CopyDesignVerificationCheck.NotProvable("DestinationResolution", "No safe destination path could be resolved for this entry."));
        }
        else if (!localFileExists(destination))
        {
            checks.Add(CopyDesignVerificationCheck.Fail("DestinationIntegrity", $"The expected destination file does not exist: \"{destination}\"."));
            destination = null;
        }
        else
        {
            try
            {
                var destHash = hasher.ComputeSha256(destination);
                var destSize = _getFileSize(destination);
                if (string.Equals(destHash, entry.Sha256, StringComparison.OrdinalIgnoreCase) && destSize == entry.FileSize)
                {
                    checks.Add(CopyDesignVerificationCheck.Pass("DestinationIntegrity"));
                }
                else
                {
                    checks.Add(CopyDesignVerificationCheck.Fail("DestinationIntegrity", "The local destination file's bytes do not match the server-authoritative materialized FileVersion."));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                checks.Add(CopyDesignVerificationCheck.Fail("DestinationIntegrity", $"Could not read the local destination file: {ex.Message}"));
                destination = null;
            }
        }

        if (node.DocumentType is not (CadDocumentType.Iam or CadDocumentType.Idw or CadDocumentType.Dwg))
        {
            return; // an IPT has no children to verify
        }
        if (destination is null)
        {
            checks.Add(CopyDesignVerificationCheck.NotProvable("ReferenceTopology", "The destination could not be confirmed - reference topology cannot be inspected."));
            return;
        }

        var relationshipKind = node.DocumentType == CadDocumentType.Iam ? CadRelationshipKind.Component : CadRelationshipKind.DrawingModel;
        checks.Add(await VerifyReferenceTopologyAsync(node, plan, topology, entry.ResultingCadDocumentId, relationshipKind, node, currentSourceSha256, ct).ConfigureAwait(false));
    }

    /// <summary>Shared by both COPY and REUSE entries - resolves this
    ///  node's expected children (<see cref="CopyDesignReferenceTargetResolver.Resolve"/>,
    ///  P6C/P6D, UNCHANGED), then inspects the ACTUAL references through
    ///  <paramref name="probeNode"/> (for COPY, the real copied node; for
    ///  REUSE, a node redirected to open its own unchanged source - see
    ///  <see cref="VerifyReuseEntryAsync"/>'s own comment) via
    ///  <see cref="CopyDesignVerificationReferenceInspector"/> - never the
    ///  P6D evaluator called directly, so no synthetic "before operation"
    ///  evidence is ever represented as genuine.</summary>
    private async Task<CopyDesignVerificationCheck> VerifyReferenceTopologyAsync(
        CopyDesignNode node,
        CopyDesignPlan plan,
        CopyDesignVerificationTopologyBuilder.Result topology,
        string resultingCadDocumentId,
        CadRelationshipKind relationshipKind,
        CopyDesignNode probeNode,
        string? currentSourceSha256,
        CancellationToken ct)
    {
        if (topology.DrawingAuthorityProblems is not null && topology.DrawingAuthorityProblems.TryGetValue(resultingCadDocumentId, out var authorityProblem))
        {
            return CopyDesignVerificationCheck.NotProvable("ReferenceTopology", authorityProblem);
        }

        var targetsResult = CopyDesignReferenceTargetResolver.Resolve(node, plan.Edges, plan.Nodes, relationshipKind);
        if (!targetsResult.Success)
        {
            return CopyDesignVerificationCheck.Fail("ReferenceTopology", targetsResult.FailureReason!);
        }

        var verifierForNode = node.DocumentType == CadDocumentType.Iam ? verifier : drawingVerifier!;
        try
        {
            var inspection = await CopyDesignVerificationReferenceInspector.InspectAsync(
                verifierForNode, probeNode, targetsResult.Targets, currentSourceSha256, ct).ConfigureAwait(false);
            return inspection.Passed
                ? CopyDesignVerificationCheck.Pass("ReferenceTopology")
                : CopyDesignVerificationCheck.Fail("ReferenceTopology", string.Join(" ", inspection.Reasons));
        }
        catch (Exception ex)
        {
            return CopyDesignVerificationCheck.Fail("ReferenceTopology", $"Could not inspect references through Inventor: {ex.Message}");
        }
    }
}
