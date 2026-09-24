using Arch.CadConnect.Core.References;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// P6D PRODUCTION RECOVERY: rebuilds the EXACT original Apply Copy Design
/// attempt (plan + idempotency key) for a DURABLE RESUME - reachable after
/// this session has no <c>_resumableCopyDesignAttempt</c> in memory (e.g.
/// after an Inventor restart) - using the server's authoritative
/// <see cref="CopyDesignDurableResumeStatusResult"/> as the SOLE source of
/// truth for identity/naming, cross-validated against the CURRENT source
/// workspace manifest. PURE - no COM, no HTTP, no I/O of its own.
///
/// SCOPE, DELIBERATELY NARROW: this class produces a <see cref="CopyDesignPlan"/>
/// and reuses the ORIGINAL <c>idempotencyKey</c> - nothing more. It does
/// NOT re-verify a MATERIALIZED entry's local destination hash/size, does
/// NOT re-check a PENDING entry's destination is free, and does NOT check
/// whether a drawing source is Dirty - <c>CopyDesignApplyOrchestrator.ExecuteAsync</c>
/// (unchanged) ALREADY does every one of those checks itself, via its own
/// expectation-based <see cref="ICopyDesignOperationStatusClient"/> query,
/// immediately before any mutation - exactly as it already does for an
/// in-session Resume. Reconstruction's job is ONLY to hand that unchanged
/// pipeline a plan it can trust the IDENTITY of; duplicating its own
/// re-validation here would be redundant, not safer.
///
/// FAIL CLOSED, ALWAYS, on: a non-Found status outcome; a missing operation
/// id / idempotency key; a malformed/gapped/duplicated ordinal sequence; an
/// entry whose action/state combination is inconsistent; an INVALID COPY
/// entry; a COPY entry missing its immutable source identity or destination
/// naming; a destination file name whose stem does not match the immutable
/// document number (the ONE shape <c>CopyDesignApplyRequestMapper</c> could
/// ever produce a request that fails to canonically match what the server
/// already has on file - see that mapper's own "DOCUMENT NUMBER" doc
/// comment); a non-blank original description (this mapper never threads a
/// description through, so a stored non-null one could never be exactly
/// replayed - refusing rather than guessing); a source whose stable id is
/// not bound in the supplied source workspace manifest; a source bound to a
/// DIFFERENT FileVersion than the operation reserved against; or a drawing
/// entry whose authoritative model dependency (supplied by the caller - see
/// <paramref name="modelSourceIdsByDrawingSourceId"/> on <see cref="Reconstruct"/>)
/// cannot be confirmed, or names a model that is not itself part of this
/// operation. NEVER reconstructs from a file name alone - every identity
/// comes from the server status, cross-checked against stable workspace-
/// manifest bindings.
///
/// NO NEW IDEMPOTENCY KEY. NO NEW CadDocument. NO SECOND RESERVATION
/// IDENTITY ALLOCATION - the returned key is the SAME one the operation was
/// originally created with; <c>CopyDesignApplyOrchestrator</c> replays it
/// verbatim, so a well-behaved server recognizes the SAME operation.
/// </summary>
public static class CopyDesignResumeAttemptReconstructor
{
    public sealed record Result(
        bool Success,
        string? FailureReason,
        CopyDesignPlan? Plan,
        string? IdempotencyKey,
        string? CopyDesignOperationId)
    {
        public static Result Fail(string reason) => new(false, reason, null, null, null);
    }

    /// <param name="status">The RAW, structurally-validated authoritative
    ///  status (see <see cref="IDurableCopyDesignOperationStatusClient"/>).</param>
    /// <param name="sourceManifest">The CURRENT source workspace's manifest -
    ///  the ONLY source of local absolute paths this method ever trusts;
    ///  never a same-named file found any other way.</param>
    /// <param name="destinationFolder">The user-confirmed, absolute
    ///  destination folder (see the caller for how it is validated as a
    ///  real, existing folder before this is called).</param>
    /// <param name="modelSourceIdsByDrawingSourceId">For each DRAWING COPY
    ///  entry's <c>sourceCadDocumentId</c>, the AUTHORITATIVE set of model
    ///  <c>sourceCadDocumentId</c>s it depends on (from the server's
    ///  DRAWING_REFERENCE dependency authority - see
    ///  <c>HttpDrawingAssociationClient</c>) - supplied by the caller so this
    ///  class stays pure/HTTP-free. A drawing entry absent from this map, or
    ///  mapped to an empty set, fails closed rather than guessing it has no
    ///  model reference.</param>
    public static Result Reconstruct(
        CopyDesignDurableResumeStatusResult status,
        WorkspaceManifest sourceManifest,
        string destinationFolder,
        IReadOnlyDictionary<string, IReadOnlySet<string>> modelSourceIdsByDrawingSourceId)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(sourceManifest);
        ArgumentNullException.ThrowIfNull(modelSourceIdsByDrawingSourceId);

        if (status.Outcome != CopyDesignDurableResumeStatusOutcome.Found)
        {
            return Result.Fail(
                $"Could not obtain a trustworthy authoritative status for this Copy Design operation ({status.Outcome}) - "
                + "refusing to reconstruct a resume attempt.");
        }
        if (string.IsNullOrWhiteSpace(status.OperationId) || string.IsNullOrWhiteSpace(status.IdempotencyKey))
        {
            return Result.Fail("The operation status response did not carry a complete operation id / idempotency key - refusing to reconstruct.");
        }
        if (status.Entries is null || status.Entries.Count == 0)
        {
            return Result.Fail("The operation has no entries to resume.");
        }
        if (string.IsNullOrWhiteSpace(destinationFolder) || !Path.IsPathFullyQualified(destinationFolder))
        {
            return Result.Fail("The destination folder must be a full, absolute path.");
        }

        // ---- ordinal contiguity: 0..N-1, no gaps, no duplicates -----------
        var byOrdinal = new Dictionary<int, CopyDesignDurableResumeEntry>();
        foreach (var entry in status.Entries)
        {
            if (!byOrdinal.TryAdd(entry.Ordinal, entry))
            {
                return Result.Fail($"The operation status reports more than one entry for ordinal {entry.Ordinal} - refusing to trust an internally inconsistent response.");
            }
        }
        for (var i = 0; i < status.Entries.Count; i++)
        {
            if (!byOrdinal.ContainsKey(i))
            {
                return Result.Fail($"The operation status is missing ordinal {i} - refusing to reconstruct a plan with gaps.");
            }
        }
        var orderedEntries = Enumerable.Range(0, status.Entries.Count).Select(i => byOrdinal[i]).ToArray();

        // ---- build nodes, in ordinal order (this order becomes the
        //      reconstructed request's entry order - it MUST match ordinal
        //      exactly for a replay to canonically equal the original). ----
        var nodes = new List<CopyDesignNode>(orderedEntries.Length);
        var nodeBySourceId = new Dictionary<string, CopyDesignNode>(StringComparer.Ordinal);
        foreach (var entry in orderedEntries)
        {
            if (string.Equals(entry.Action, "REUSE", StringComparison.Ordinal))
            {
                if (!string.Equals(entry.State, "REUSED", StringComparison.Ordinal))
                {
                    return Result.Fail($"Entry {entry.Ordinal} is a REUSE action but reports state \"{entry.State}\" - refusing to trust an inconsistent response.");
                }
                if (string.IsNullOrWhiteSpace(entry.ResultingCadDocumentId))
                {
                    return Result.Fail($"Entry {entry.Ordinal} (REUSE) has no resultingCadDocumentId - refusing to reconstruct.");
                }
                nodes.Add(new CopyDesignNode(
                    entry.ResultingCadDocumentId, null, ParseDocumentType(entry.OriginalDocumentType),
                    SourceAbsolutePath: "", SourceFileName: entry.OriginalFileName,
                    RelationshipToParent: null, IsManaged: true, IsVerified: true, IsResolved: true,
                    CopyDesignAction.Reuse, null, null, Array.Empty<string>()));
                continue;
            }

            if (!string.Equals(entry.Action, "COPY", StringComparison.Ordinal))
            {
                return Result.Fail($"Entry {entry.Ordinal} has an unrecognized action \"{entry.Action}\" - refusing to reconstruct.");
            }
            if (string.Equals(entry.State, "INVALID", StringComparison.Ordinal))
            {
                return Result.Fail($"Entry {entry.Ordinal} (\"{entry.OriginalFileName}\") is reported INVALID by the server - refusing to resume an unsafe operation.");
            }
            if (!string.Equals(entry.State, "PENDING", StringComparison.Ordinal) && !string.Equals(entry.State, "MATERIALIZED", StringComparison.Ordinal))
            {
                return Result.Fail($"Entry {entry.Ordinal} reports an unexpected state \"{entry.State}\" for a COPY action - refusing to reconstruct.");
            }
            if (string.IsNullOrWhiteSpace(entry.SourceCadDocumentId) || string.IsNullOrWhiteSpace(entry.SourceFileVersionId))
            {
                return Result.Fail($"Entry {entry.Ordinal} (\"{entry.OriginalFileName}\") is missing its immutable source identity - refusing to reconstruct.");
            }
            if (string.IsNullOrWhiteSpace(entry.OriginalFileName) || string.IsNullOrWhiteSpace(entry.OriginalDocumentNumber))
            {
                return Result.Fail($"Entry {entry.Ordinal} is missing its immutable destination naming - refusing to reconstruct.");
            }

            // P6D FINAL BLOCKER FIX: originalFileName is server-authoritative
            // but STILL untrusted defensive input here - validated as exactly
            // ONE safe file name component BEFORE anything (including the
            // stem-match check below, which also calls a Path API on it)
            // ever touches it. The server validates the SAME shape at the
            // reservation boundary (see CopyDesignSafeBaseName's own doc
            // comment) - this is never treated as a substitute for that.
            var nameValidity = CopyDesignSafeBaseName.Validate(entry.OriginalFileName);
            if (!nameValidity.Safe)
            {
                return Result.Fail(
                    $"Entry {entry.Ordinal} reports an unsafe original file name ({nameValidity.Reason}): "
                    + $"\"{entry.OriginalFileName}\" - refusing to reconstruct rather than build a local path from it.");
            }
            if (!string.IsNullOrEmpty(entry.OriginalDescription))
            {
                return Result.Fail(
                    $"Entry {entry.Ordinal} (\"{entry.OriginalFileName}\") carries a non-blank original description - "
                    + "durable resume reconstruction never replays a description and refuses to guess one.");
            }

            var documentType = ParseDocumentType(entry.OriginalDocumentType);
            if (documentType == CadDocumentType.Unknown)
            {
                return Result.Fail($"Entry {entry.Ordinal} reports an unrecognized documentType \"{entry.OriginalDocumentType}\" - refusing to reconstruct.");
            }

            // The mapper derives newDocumentNumber SOLELY from the
            // destination file name's stem - if that ever diverges from the
            // immutable originalDocumentNumber, a reconstructed replay could
            // never canonically match the original request. Fail closed here
            // rather than let it surface as a confusing idempotency conflict.
            var stem = Path.GetFileNameWithoutExtension(entry.OriginalFileName);
            if (!string.Equals(stem, entry.OriginalDocumentNumber, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Fail(
                    $"Entry {entry.Ordinal}'s original document number (\"{entry.OriginalDocumentNumber}\") does not match "
                    + $"its file name stem (\"{entry.OriginalFileName}\") - a durable resume cannot safely reconstruct a "
                    + "request guaranteed to replay the original exactly.");
            }

            var located = sourceManifest.FindByCadDocumentId(entry.SourceCadDocumentId);
            if (located is null)
            {
                return Result.Fail(
                    $"The source workspace does not have a bound entry for \"{entry.OriginalFileName}\"'s source "
                    + $"(cadDocumentId {entry.SourceCadDocumentId}) - select the correct source workspace folder.");
            }
            if (!string.Equals(located.Value.Entry.FileVersionId, entry.SourceFileVersionId, StringComparison.Ordinal))
            {
                return Result.Fail(
                    $"The local source workspace binds \"{located.Value.Entry.FileName}\" to a DIFFERENT FileVersion than "
                    + "this operation was reserved against - refusing to resume over a changed source.");
            }

            // P6D FINAL BLOCKER FIX: never a plain Path.Combine on untrusted
            // input - resolve the FULL canonical path and independently
            // prove (path-aware, normalized - never naive string-prefix,
            // which "C:\Approved2" would wrongly pass against a root of
            // "C:\Approved") that it lands DIRECTLY inside destinationFolder,
            // never in a subdirectory and never outside it, BEFORE this
            // becomes the plan's ProposedDestinationAbsolutePath - i.e.
            // before any destination-existence check, physical copy,
            // promotion, or other filesystem mutation ever sees it.
            var containment = CopyDesignSafeBaseName.TryResolveContained(destinationFolder, entry.OriginalFileName);
            if (!containment.Safe)
            {
                return Result.Fail($"Entry {entry.Ordinal}: {containment.FailureReason}");
            }
            var destinationPath = containment.ResolvedPath!;
            var node = new CopyDesignNode(
                entry.SourceCadDocumentId, entry.SourceFileVersionId, documentType,
                located.Value.AbsolutePath, Path.GetFileName(located.Value.AbsolutePath),
                RelationshipToParent: null, IsManaged: true, IsVerified: true, IsResolved: true,
                CopyDesignAction.Copy, entry.OriginalFileName, destinationPath, Array.Empty<string>());
            nodes.Add(node);
            nodeBySourceId[entry.SourceCadDocumentId] = node;
        }

        // ---- edges: ONLY drawing -> model, from the caller-supplied
        //      authority - never guessed, never inferred from file names. --
        var edges = new List<CopyDesignEdge>();
        foreach (var entry in orderedEntries)
        {
            if (!string.Equals(entry.Action, "COPY", StringComparison.Ordinal) || entry.SourceCadDocumentId is null)
            {
                continue;
            }
            var documentType = ParseDocumentType(entry.OriginalDocumentType);
            if (documentType is not (CadDocumentType.Idw or CadDocumentType.Dwg))
            {
                continue;
            }
            if (!modelSourceIdsByDrawingSourceId.TryGetValue(entry.SourceCadDocumentId, out var modelSourceIds)
                || modelSourceIds.Count == 0)
            {
                return Result.Fail(
                    $"No authoritative model dependency could be confirmed for drawing \"{entry.OriginalFileName}\" - "
                    + "refusing to guess its reference target(s).");
            }
            var drawingNode = nodeBySourceId[entry.SourceCadDocumentId];
            foreach (var modelSourceId in modelSourceIds)
            {
                if (!nodeBySourceId.TryGetValue(modelSourceId, out var modelNode))
                {
                    return Result.Fail(
                        $"Drawing \"{entry.OriginalFileName}\" authoritatively depends on a model (cadDocumentId "
                        + $"{modelSourceId}) that is not part of this operation - refusing to reconstruct an incomplete plan.");
                }
                edges.Add(new CopyDesignEdge(
                    drawingNode.SourceAbsolutePath, modelNode.SourceAbsolutePath,
                    CadRelationshipKind.DrawingModel, CopyDesignEdgeDisposition.PointsToNewCopy));
            }
        }

        var rootAbsolutePath = nodes.FirstOrDefault(n => n.DocumentType == CadDocumentType.Iam)?.SourceAbsolutePath
            ?? nodes[0].SourceAbsolutePath;

        var plan = new CopyDesignPlan(
            rootAbsolutePath, nodes, edges, Array.Empty<string>(),
            IsExecutable: true, ScanWasComplete: true, DrawingAssociationAvailable: true, ModelFilesOnlyAcknowledged: false);

        return new Result(true, null, plan, status.IdempotencyKey, status.OperationId);
    }

    /// <summary>
    /// P6D DURABLE RESUME LIVE BLOCKER fix: a PURE, side-effect-free
    /// pre-check the caller runs immediately after a successful
    /// <see cref="Reconstruct"/> and BEFORE showing the confirm dialog or
    /// invoking the real orchestrator - so a destination folder that does
    /// not actually hold the files the server already reports as
    /// MATERIALIZED is reported immediately, plainly, and completely (every
    /// missing file listed, with its full expected path) rather than
    /// surfacing later as a single, terse
    /// <c>CopyDesignApplyOutcome.UnexpectedMaterializationState</c> failure
    /// reason for only the FIRST such entry, shown inside a result dialog
    /// whose non-wrapping text box can make that reason look silently cut
    /// off.
    ///
    /// Deliberately narrower than the orchestrator's own re-verification:
    /// this checks ONLY whether the expected file exists, never its
    /// hash/size - that authoritative check remains
    /// <c>CopyDesignApplyOrchestrator.ExecuteAsync</c>'s job alone,
    /// immediately before any mutation (see this class's own "SCOPE,
    /// DELIBERATELY NARROW" doc comment). This method existing, and finding
    /// nothing missing, is NOT proof the resume will succeed - it is only
    /// proof this one, easy-to-mistype input was not the problem, surfaced
    /// earlier and more legibly than the orchestrator alone can.
    /// </summary>
    /// <param name="destinationExists">Injected rather than calling
    ///  <see cref="File.Exists(string)"/> directly, so this stays pure and
    ///  unit-testable with a fake - the caller supplies the real delegate.</param>
    public static IReadOnlyList<(string OriginalFileName, string ExpectedPath)> FindMissingMaterializedDestinations(
        CopyDesignDurableResumeStatusResult status,
        string destinationFolder,
        Func<string, bool> destinationExists)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(destinationExists);

        var missing = new List<(string, string)>();
        if (status.Entries is null || string.IsNullOrWhiteSpace(destinationFolder) || !Path.IsPathFullyQualified(destinationFolder))
        {
            return missing;
        }
        foreach (var entry in status.Entries)
        {
            if (!string.Equals(entry.Action, "COPY", StringComparison.Ordinal)
                || !string.Equals(entry.State, "MATERIALIZED", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(entry.OriginalFileName))
            {
                continue;
            }
            // P6D FINAL BLOCKER FIX: this method is public and could be
            // called independently of Reconstruct (which already fails
            // closed on an unsafe name for every entry) - defense in depth,
            // never Path.Combine on unchecked input here either. An unsafe
            // name is reported as "missing" too (never silently skipped) so
            // it still surfaces to the caller rather than vanishing.
            var containment = CopyDesignSafeBaseName.TryResolveContained(destinationFolder, entry.OriginalFileName);
            if (!containment.Safe)
            {
                missing.Add((entry.OriginalFileName, $"<{containment.FailureReason}>"));
                continue;
            }
            var expectedPath = containment.ResolvedPath!;
            if (!destinationExists(expectedPath))
            {
                missing.Add((entry.OriginalFileName, expectedPath));
            }
        }
        return missing;
    }

    private static CadDocumentType ParseDocumentType(string wireName) => wireName switch
    {
        "IPT" => CadDocumentType.Ipt,
        "IAM" => CadDocumentType.Iam,
        "IDW" => CadDocumentType.Idw,
        "DWG" => CadDocumentType.Dwg,
        _ => CadDocumentType.Unknown,
    };
}
