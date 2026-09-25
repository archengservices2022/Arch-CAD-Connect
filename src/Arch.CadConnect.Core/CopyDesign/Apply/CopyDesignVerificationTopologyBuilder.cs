using Arch.CadConnect.Core.References;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// P6E-B: rebuilds the EXPECTED topology of a Copy Design operation's
/// copied/reused package - a <see cref="CopyDesignPlan"/>-shaped node/edge
/// graph - from DURABLE server data (<see cref="CopyDesignVerificationSupportResult"/>)
/// plus the CURRENT local source workspace manifest, for INDEPENDENT,
/// READ-ONLY re-verification. PURE - no COM, no HTTP, no mutation of any
/// kind.
///
/// MODELED ON, BUT DELIBERATELY SEPARATE FROM, <see cref="CopyDesignResumeAttemptReconstructor"/>
/// (P6D, released, never modified by this class): that class reconstructs
/// an attempt to RESUME executing an operation (so it fails closed, whole-
/// build, on ANY unsafe shape - there is about to be real mutation). This
/// class reconstructs a graph to independently OBSERVE an operation that
/// already happened (nothing is about to be mutated), so it is
/// intentionally MORE LENIENT per-entry: a problem specific to ONE entry
/// (an unsafe destination file name, a missing piece of evidence) is
/// recorded AGAINST that entry and surfaces later as a normal per-entry
/// FAILED/INCOMPLETE verification result - it does not abort the whole
/// build, so the run stays maximally diagnostic. Only a problem that makes
/// the ENTIRE graph structurally untrustworthy (see <see cref="Build"/>'s
/// own "FAILS THE WHOLE BUILD" list) aborts everything.
///
/// SAFE DESTINATION CONTAINMENT: every COPY entry's destination path is
/// resolved through the EXISTING, unmodified <see cref="CopyDesignSafeBaseName.TryResolveContained"/>
/// (P6D FINAL BLOCKER FIX) - never a bare <c>Path.Combine</c> on
/// server-authoritative input. An unsafe/uncontained name is a PER-ENTRY
/// problem (see <see cref="DestinationProblems"/>), never a whole-build
/// abort - the rest of the operation can still be verified.
/// </summary>
public static class CopyDesignVerificationTopologyBuilder
{
    public sealed record Result(
        bool Success,
        string? FailureReason,
        CopyDesignPlan? Plan,
        /// <summary>Keyed by <c>resultingCadDocumentId</c> - present ONLY for
        ///  a COPY entry whose destination file name is unsafe or does not
        ///  resolve directly inside <c>destinationFolder</c>. That entry's
        ///  node still exists in <see cref="Plan"/>, with a <c>null</c>
        ///  <c>ProposedDestinationAbsolutePath</c> - P6E FINAL SEMANTIC
        ///  CLEANUP: the orchestrator turns this into a REQUIRED
        ///  <c>DestinationResolution</c> NOT_PROVABLE check (INCOMPLETE) for
        ///  that ONE entry, never a FAILED <c>DestinationIntegrity</c> one -
        ///  "cannot safely establish the expected verification target" is
        ///  categorically different from "proven wrong", and no filesystem
        ///  or COM verification is attempted against it either way.</summary>
        IReadOnlyDictionary<string, string>? DestinationProblems,
        /// <summary>Keyed by <c>resultingCadDocumentId</c> - present ONLY
        ///  for a COPY/REUSE drawing (IDW/DWG) entry whose source identity
        ///  is NOT a key at all in <c>modelSourceIdsByDrawingSourceId</c> -
        ///  i.e. the caller never confirmed (or explicitly denied) a
        ///  model-dependency authority for it. Deliberately DIFFERENT from
        ///  "the key is present but maps to an empty set" (which is
        ///  trusted as "authoritatively zero associated models" and is NOT
        ///  a problem) - see this class's own "empty vs unknown" reasoning
        ///  in <see cref="Build"/>.</summary>
        IReadOnlyDictionary<string, string>? DrawingAuthorityProblems)
    {
        public static Result Fail(string reason) => new(false, reason, null, null, null);
    }

    /// <param name="support">MUST already be <see cref="CopyDesignVerificationSupportResult.Success"/> -
    ///  the caller checks this before calling (mirrors
    ///  <see cref="CopyDesignResumeAttemptReconstructor.Reconstruct"/>'s own
    ///  contract).</param>
    /// <param name="sourceManifest">The CURRENT source workspace's manifest -
    ///  the ONLY source of local absolute paths this method ever trusts.</param>
    /// <param name="destinationFolder">The absolute folder the copied
    ///  package was materialized into.</param>
    /// <param name="modelSourceIdsByDrawingSourceId">For each drawing
    ///  entry's OWN source identity (COPY: <c>sourceCadDocumentId</c>;
    ///  REUSE: <c>resultingCadDocumentId</c>), the AUTHORITATIVE set of
    ///  model source identities it depends on - supplied by the caller
    ///  (e.g. the SAME drawing-association authority durable resume already
    ///  uses), so this class stays HTTP-free. See <see cref="Result.DrawingAuthorityProblems"/>
    ///  for how a MISSING key (never asked about) is distinguished from a
    ///  present-but-empty one (authoritatively confirmed zero).</param>
    public static Result Build(
        CopyDesignVerificationSupportResult support,
        WorkspaceManifest sourceManifest,
        string destinationFolder,
        IReadOnlyDictionary<string, IReadOnlySet<string>> modelSourceIdsByDrawingSourceId)
    {
        ArgumentNullException.ThrowIfNull(support);
        ArgumentNullException.ThrowIfNull(sourceManifest);
        ArgumentNullException.ThrowIfNull(modelSourceIdsByDrawingSourceId);

        if (!support.Success || support.Entries is null)
        {
            return Result.Fail("The verification-support data is not a successful (Found) result - refusing to build a topology from it.");
        }
        if (string.IsNullOrWhiteSpace(destinationFolder) || !Path.IsPathFullyQualified(destinationFolder))
        {
            return Result.Fail("The destination folder must be a full, absolute path.");
        }

        // ---- FAILS THE WHOLE BUILD: ordinal contiguity -----------------------
        var byOrdinal = new Dictionary<int, CopyDesignDurableResumeEntry>();
        foreach (var entry in support.Entries)
        {
            if (!byOrdinal.TryAdd(entry.Ordinal, entry))
            {
                return Result.Fail($"More than one entry reports ordinal {entry.Ordinal} - refusing to trust an internally inconsistent operation.");
            }
        }
        for (var i = 0; i < support.Entries.Count; i++)
        {
            if (!byOrdinal.ContainsKey(i))
            {
                return Result.Fail($"The operation is missing ordinal {i} - refusing to build a topology with gaps.");
            }
        }
        var orderedEntries = Enumerable.Range(0, support.Entries.Count).Select(i => byOrdinal[i]).ToArray();

        var nodes = new List<CopyDesignNode>(orderedEntries.Length);
        var nodeBySourceIdentity = new Dictionary<string, CopyDesignNode>(StringComparer.Ordinal);
        var destinationProblems = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in orderedEntries)
        {
            // ---- FAILS THE WHOLE BUILD: unrecognized/inconsistent action/state
            if (entry.Action is not ("COPY" or "REUSE"))
            {
                return Result.Fail($"Entry {entry.Ordinal} has an unrecognized action \"{entry.Action}\" - refusing to build a topology from it.");
            }
            if (string.IsNullOrWhiteSpace(entry.OriginalFileName) || string.IsNullOrWhiteSpace(entry.OriginalDocumentNumber))
            {
                return Result.Fail($"Entry {entry.Ordinal} is missing its immutable naming - refusing to build a topology from it.");
            }
            var documentType = ParseDocumentType(entry.OriginalDocumentType);
            if (documentType == CadDocumentType.Unknown)
            {
                return Result.Fail($"Entry {entry.Ordinal} reports an unrecognized documentType \"{entry.OriginalDocumentType}\" - refusing to build a topology from it.");
            }

            string sourceIdentity;
            if (entry.Action == "COPY")
            {
                if (string.IsNullOrWhiteSpace(entry.SourceCadDocumentId) || string.IsNullOrWhiteSpace(entry.SourceFileVersionId))
                {
                    return Result.Fail($"Entry {entry.Ordinal} (\"{entry.OriginalFileName}\") is a COPY action missing its immutable source identity.");
                }
                sourceIdentity = entry.SourceCadDocumentId;
            }
            else
            {
                sourceIdentity = entry.ResultingCadDocumentId;
            }

            // ---- FAILS THE WHOLE BUILD: this participant's physical location
            //      cannot be established at all - nothing built on top of it
            //      (its own checks, or any edge naming it as parent/child)
            //      could ever be trustworthy. -------------------------------
            var located = sourceManifest.FindByCadDocumentId(sourceIdentity);
            if (located is null)
            {
                return Result.Fail(
                    $"The source workspace does not have a bound entry for \"{entry.OriginalFileName}\" "
                    + $"(cadDocumentId {sourceIdentity}) - select the correct source workspace folder.");
            }
            if (entry.Action == "COPY" && !string.Equals(located.Value.Entry.FileVersionId, entry.SourceFileVersionId, StringComparison.Ordinal))
            {
                return Result.Fail(
                    $"The local source workspace binds \"{located.Value.Entry.FileName}\" to a DIFFERENT FileVersion than "
                    + "this operation was performed against - refusing to verify over a changed/wrong source workspace.");
            }

            string? destinationPath = null;
            if (entry.Action == "COPY")
            {
                // Per-entry, NEVER whole-build - see class doc comment.
                var containment = CopyDesignSafeBaseName.TryResolveContained(destinationFolder, entry.OriginalFileName);
                if (containment.Safe)
                {
                    destinationPath = containment.ResolvedPath;
                }
                else
                {
                    destinationProblems[entry.ResultingCadDocumentId] = containment.FailureReason!;
                }
            }

            var node = new CopyDesignNode(
                entry.ResultingCadDocumentId,
                entry.Action == "COPY" ? entry.SourceFileVersionId : located.Value.Entry.FileVersionId,
                documentType,
                located.Value.AbsolutePath,
                Path.GetFileName(located.Value.AbsolutePath),
                RelationshipToParent: null,
                IsManaged: true, IsVerified: true, IsResolved: true,
                entry.Action == "COPY" ? CopyDesignAction.Copy : CopyDesignAction.Reuse,
                entry.Action == "COPY" ? entry.OriginalFileName : null,
                destinationPath,
                Array.Empty<string>());
            nodes.Add(node);
            nodeBySourceIdentity[sourceIdentity] = node;
        }

        // ---- edges: COMPONENT (from the durable topology set) + DrawingModel
        //      (from the caller-supplied authority) - NEVER guessed. -----------
        var edges = new List<CopyDesignEdge>();
        foreach (var componentEdge in support.ComponentEdges ?? Array.Empty<CopyDesignComponentEdge>())
        {
            if (!nodeBySourceIdentity.TryGetValue(componentEdge.ParentCadDocumentId, out var parentNode)
                || !nodeBySourceIdentity.TryGetValue(componentEdge.ChildCadDocumentId, out var childNode))
            {
                // Defensive only - P6E-A's own contract already scopes
                // componentEdges to this operation's topology identity set,
                // so this should never actually happen. Never guessed at;
                // simply not represented as an edge.
                continue;
            }
            edges.Add(new CopyDesignEdge(
                parentNode.SourceAbsolutePath, childNode.SourceAbsolutePath,
                CadRelationshipKind.Component,
                childNode.ProposedAction == CopyDesignAction.Copy
                    ? CopyDesignEdgeDisposition.PointsToNewCopy
                    : CopyDesignEdgeDisposition.RemainsOnReusedSource));
        }

        var drawingAuthorityProblems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in orderedEntries)
        {
            var documentType = ParseDocumentType(entry.OriginalDocumentType);
            if (documentType is not (CadDocumentType.Idw or CadDocumentType.Dwg))
            {
                continue;
            }
            var drawingSourceIdentity = entry.Action == "COPY" ? entry.SourceCadDocumentId! : entry.ResultingCadDocumentId;
            if (!nodeBySourceIdentity.TryGetValue(drawingSourceIdentity, out var drawingNode))
            {
                continue; // structurally impossible given the loop above, but never assumed
            }
            if (!modelSourceIdsByDrawingSourceId.TryGetValue(drawingSourceIdentity, out var modelSourceIds))
            {
                // MISSING key = "never asked about this drawing" - an
                // UNKNOWN authority, never treated as "authoritatively zero".
                drawingAuthorityProblems[entry.ResultingCadDocumentId] =
                    "No model-dependency authority was confirmed for this drawing - its expected reference set is unknown, not proven empty.";
                continue;
            }
            // A PRESENT key mapping to an empty set is trusted as
            // "authoritatively zero associated models" - genuinely nothing
            // to add as an edge, not a problem.
            foreach (var modelSourceId in modelSourceIds)
            {
                if (!nodeBySourceIdentity.TryGetValue(modelSourceId, out var modelNode))
                {
                    // The authority names a model that is not part of THIS
                    // operation's own entries - cannot represent it as an
                    // edge into this graph. Recorded as an authority
                    // problem for the drawing rather than silently dropped,
                    // so it surfaces as NOT_PROVABLE rather than looking
                    // like a clean, fully-resolved reference set.
                    drawingAuthorityProblems[entry.ResultingCadDocumentId] =
                        $"The model-dependency authority names a model (cadDocumentId {modelSourceId}) that is not part of this operation's own entries.";
                    continue;
                }
                edges.Add(new CopyDesignEdge(
                    drawingNode.SourceAbsolutePath, modelNode.SourceAbsolutePath,
                    CadRelationshipKind.DrawingModel,
                    modelNode.ProposedAction == CopyDesignAction.Copy
                        ? CopyDesignEdgeDisposition.PointsToNewCopy
                        : CopyDesignEdgeDisposition.RemainsOnReusedSource));
            }
        }

        var rootAbsolutePath = nodes.FirstOrDefault(n => n.DocumentType == CadDocumentType.Iam)?.SourceAbsolutePath
            ?? nodes.FirstOrDefault()?.SourceAbsolutePath ?? "";

        var plan = new CopyDesignPlan(
            rootAbsolutePath, nodes, edges, Array.Empty<string>(),
            IsExecutable: false, // this plan is never executed - verification only
            ScanWasComplete: true, DrawingAssociationAvailable: true, ModelFilesOnlyAcknowledged: false);

        return new Result(true, null, plan, destinationProblems, drawingAuthorityProblems);
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
