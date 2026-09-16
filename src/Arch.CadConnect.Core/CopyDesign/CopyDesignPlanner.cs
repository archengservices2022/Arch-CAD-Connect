using Arch.CadConnect.Core.References;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.CopyDesign;

/// <summary>
/// P6A: builds a deterministic, read-only Copy Design plan from an existing
/// <see cref="CadReferenceScan"/> (P5A). PURE - no COM, no HTTP, no database,
/// and filesystem-light (the only optional filesystem input is a caller-
/// supplied <c>destinationExists</c> predicate for local collision detection;
/// by default no filesystem is touched at all). It never copies, renames,
/// saves, replaces a reference, checks anything out/in, or creates a
/// FileVersion / CadDocument - it only PROPOSES a plan for a later phase.
///
/// Identity discipline: every node is keyed by its STABLE Arch
/// <c>cadDocumentId</c> when one is known (so a document reachable from
/// multiple parents is represented exactly ONCE - "shared dependency
/// represented once by stable identity" - and two nodes that merely share a
/// file name but carry different cadDocumentIds always stay distinct). A
/// resolved reference with NO manifest-matched identity, or an unresolved
/// reference, still becomes a node (so the plan never silently drops it) but
/// can only ever end up <see cref="CopyDesignAction.NeedsDecision"/> - it can
/// never be a safe COPY or REUSE candidate, and its presence makes the whole
/// plan non-executable.
///
/// Round 3 hardening added a SECOND reconciliation direction (same
/// cadDocumentId observed twice, forward; same normalized path claimed by two
/// identities, inverse), folded associated drawings into the SAME
/// observation pipeline instead of a hardcoded-safe side channel, and made
/// canonical representative selection a full ordinal field order.
///
/// Round 4 corrected the drawing edge DIRECTION (a drawing depends on its
/// model, never the reverse), made a multi-owner drawing's forced action
/// require ALL owners to agree, keyed EVERY observation by cadDocumentId
/// FIRST regardless of resolution/path availability, enforced that
/// cadDocumentId/FileVersionId are opaque EXACT values (a padded id fails
/// closed, never trimmed-and-accepted), and sorted the final Nodes/Edges/
/// Warnings deterministically before returning.
///
/// Round 5 fixes two remaining gaps: (1) drawing-owner consensus now comes
/// from EVERY <see cref="CadRelationshipKind.DrawingModel"/> edge in the
/// COMPLETE edge set - including edges the P5A scanner itself already
/// produced (e.g. a scan rooted at a drawing, referencing its models
/// directly) - not only edges an <see cref="IDrawingAssociationSource"/>
/// added; decision order is now an explicit two-phase pass (every non-
/// drawing node first, then every drawing node using the now-complete,
/// already-decided owner set) so a drawing that happens to be discovered
/// BEFORE its model owners (e.g. the drawing IS the selected root) is never
/// decided on stale/missing owner information. (2) The association source is
/// now QUERIED ONLY for reconciled IAM/IPT model nodes, and every
/// <see cref="AssociatedDrawing"/> it returns is validated before it can ever
/// become a trusted graph edge: its DocumentType must be exactly Idw or Dwg,
/// and its AbsolutePath - when nonblank - must be a genuinely resolvable,
/// fully-qualified path. Evidence that fails either check is never
/// "resolved" and never produces a DrawingModel edge; malformed evidence
/// (a bad DocumentType, or a nonblank-but-unresolvable path) is surfaced as
/// an explicit, deterministic warning and forces the plan non-executable -
/// never silently treated as "no drawing exists".
/// </summary>
public static class CopyDesignPlanner
{
    public static CopyDesignPlan Plan(
        CadReferenceScan scan,
        IDestinationNameRule nameRule,
        string destinationWorkspaceRoot,
        IComponentClassificationSource? classificationSource = null,
        IDrawingAssociationSource? drawingSource = null,
        Func<string, bool>? destinationExists = null)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(nameRule);

        if (string.IsNullOrWhiteSpace(destinationWorkspaceRoot) || !Path.IsPathFullyQualified(destinationWorkspaceRoot))
        {
            return CopyDesignPlan.Empty(scan.Root.AbsolutePath,
                "The destination workspace root must be an absolute path.");
        }

        classificationSource ??= UnknownComponentClassificationSource.Instance;
        drawingSource ??= NoDrawingAssociationSource.Instance;
        destinationExists ??= static _ => false;

        var warnings = new List<string>();
        var scanWasComplete = scan.IsComplete;
        if (!scanWasComplete)
        {
            warnings.Add("The reference scan did not fully enumerate every document it visited - the plan below "
                + "reflects only what was actually observed and cannot be executable.");
        }

        // ---- 1. discover RAW OBSERVATIONS (root + every direct reference),
        //         grouped by stable identity, in DETERMINISTIC discovery
        //         order. This ALREADY includes any scanner-native
        //         DrawingModel edges (e.g. a scan rooted at an IDW/DWG,
        //         referencing its IAM/IPT models directly) - the P5A scanner
        //         derives that relationship kind purely from parent/child
        //         document TYPES. ------------------------------------------
        var observationsByKey = new Dictionary<string, List<Draft>>(StringComparer.Ordinal);
        var order = new List<string>();
        var edgeSpecs = new List<EdgeSpec>();

        void RecordObservation(string key, Draft draft)
        {
            if (!observationsByKey.TryGetValue(key, out var list))
            {
                list = new List<Draft>();
                observationsByKey[key] = list;
                order.Add(key);
            }
            list.Add(draft);
        }

        var rootKey = KeyFor(scan.Root.Identity?.CadDocumentId, scan.Root.AbsolutePath);
        RecordObservation(rootKey, new Draft(
            scan.Root.Identity?.CadDocumentId,
            scan.Root.Identity?.FileVersionId,
            scan.Root.DocumentType,
            scan.Root.AbsolutePath,
            Path.GetFileName(scan.Root.AbsolutePath),
            RelationshipToParent: null,
            IsManaged: scan.Root.Identity is not null,
            // A non-null root Identity is NOT proof of a verified binding -
            // the manifest entry it came from may itself be Unverified. Only
            // a manifest-VERIFIED root identity is safe for an automatic
            // Copy Design root action.
            IsVerified: scan.Root.Identity is not null && scan.Root.IsVerified,
            IsResolved: true,
            IsRoot: true));

        foreach (var reference in scan.References)
        {
            var isResolved = reference.Resolution == CadReferenceResolution.Resolved
                && !string.IsNullOrWhiteSpace(reference.ResolvedAbsolutePath);
            // ID-first: a nonblank cadDocumentId ALWAYS keys by identity,
            // regardless of resolution state.
            var childKey = !string.IsNullOrWhiteSpace(reference.ManifestIdentity?.CadDocumentId)
                ? "id:" + reference.ManifestIdentity!.CadDocumentId
                : (isResolved
                    ? "path:" + NormalizePath(reference.ResolvedAbsolutePath!)
                    : UnresolvedKey(reference.ParentAbsolutePath, reference.InventorReportedFullPath ?? reference.InventorReportedName));

            var sourcePath = isResolved ? reference.ResolvedAbsolutePath! : reference.InventorReportedFullPath
                ?? reference.InventorReportedName;
            RecordObservation(childKey, new Draft(
                reference.ManifestIdentity?.CadDocumentId,
                reference.ManifestIdentity?.FileVersionId,
                reference.ReferenceType,
                sourcePath,
                SafeFileName(sourcePath, reference.InventorReportedName),
                reference.RelationshipKind,
                IsManaged: reference.ManifestIdentity is not null,
                IsVerified: reference.ManifestIdentity?.IsVerified == true,
                IsResolved: isResolved,
                IsRoot: false));

            var parentKey = KeyFor(reference.ParentIdentity?.CadDocumentId, reference.ParentAbsolutePath);
            // Scanner-native edges keep whatever relationship kind the P5A
            // classifier assigned from actual parent/child document TYPES -
            // this is ALREADY a correctly-directed DrawingModel edge
            // (drawing parent -> model child) whenever the scan discovered
            // one directly (e.g. the selected root IS a drawing).
            edgeSpecs.Add(new EdgeSpec(parentKey, childKey, reference.RelationshipKind, reference.ParentAbsolutePath));
        }

        // ---- 1b. FIRST reconciliation pass, over the scan-only observation
        //          set - just enough to know each candidate model's
        //          reconciled cadDocumentId / document type / absolute path
        //          for the drawing-association query below. The FINAL
        //          reconciliation (used for every actual decision) happens
        //          AFTER drawings are folded in. --------------------------
        var (byKeyForDrawingLookup, _) = ReconcileAll(observationsByKey, order);

        // ---- 2. drawing association - ONLY from an authoritative source,
        //         NEVER a filename guess. BLOCKER 2 (Round 5): the source is
        //         queried ONLY for reconciled nodes whose type is IAM or IPT
        //         - never for a drawing, Unknown, or unsupported type. Every
        //         AssociatedDrawing it returns is VALIDATED before it can
        //         become a trusted observation/edge: DocumentType must be
        //         exactly Idw/Dwg, and a nonblank AbsolutePath must be a
        //         genuinely resolvable, fully-qualified path. Invalid
        //         evidence never produces a DrawingModel edge and is
        //         surfaced as an explicit warning instead of being silently
        //         discarded. -------------------------------------------------
        // AVAILABLE vs COMPLETE (Round 7, HIGH) - these are NOT the same fact:
        //   AVAILABLE: at least one eligible IAM/IPT model's lookup returned
        //     Found (some real evidence exists).
        //   COMPLETE:  EVERY eligible IAM/IPT model's lookup returned Found -
        //     i.e. the planner actually knows the COMPLETE drawing-owner
        //     picture. A single NotAvailable/null/thrown lookup among many
        //     Found ones still means the TRUE owner set for some drawing may
        //     be missing an owner the planner never learned about - that is
        //     GLOBAL incompleteness, never a fact about one specific drawing
        //     identity (Round 6's taintedDrawingKeys stays reserved for
        //     malformed evidence that POSITIVELY names a drawing; this is the
        //     opposite - an ABSENCE of evidence, attributable to no drawing
        //     in particular).
        var drawingAssociationAvailable = false;
        var drawingAssociationComplete = true;
        var anyInvalidAssociationEvidence = false;
        // ROUND 6, HIGH: a REJECTED association claim that positively names a
        // drawing identity (a usable, nonblank cadDocumentId) must TAINT that
        // exact identity - never silently vanish. Without this, a drawing
        // with N valid owners and ONE rejected owner claim would compute
        // consensus from only the N SURVIVING owners, potentially showing a
        // confident Copy/Reuse when the TRUE (unknowable) owner set might
        // have been mixed. Keyed with the SAME "id:" scheme DrawingKeyFor
        // uses (no trim) so a tainted claim always matches the exact key a
        // valid observation of the same raw id would use.
        var taintedDrawingKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var modelKey in order.ToArray()) // snapshot - drawings extend `order` below
        {
            var modelDraft = byKeyForDrawingLookup[modelKey];
            if (string.IsNullOrWhiteSpace(modelDraft.CadDocumentId))
            {
                continue;
            }
            if (modelDraft.DocumentType is not (CadDocumentType.Iam or CadDocumentType.Ipt))
            {
                // QUERY SCOPE: never ask "what drawings reference this" about
                // something that is not itself a model (a drawing, Unknown,
                // or any unsupported type).
                continue;
            }

            DrawingAssociationResult result;
            try
            {
                // CASE 3: a null result from the source is treated exactly
                // like an explicit NotAvailable - never as "Found, zero
                // drawings".
                result = drawingSource.GetAssociatedDrawings(modelDraft.CadDocumentId) ?? DrawingAssociationResult.NotAvailable;
            }
            catch
            {
                // CASE 4: an exception proves NOTHING about this model's
                // drawings - never silently treated as "no drawings".
                result = DrawingAssociationResult.NotAvailable;
            }

            if (result.Outcome != DrawingAssociationOutcome.Found)
            {
                // CASE 2 (and 3/4 above, both folded into NotAvailable): this
                // eligible model's lookup did NOT complete authoritatively -
                // the planner does not know whether it owns some drawing.
                // Whatever the FUTURE DrawingAssociationOutcome values might
                // be, only Found proves completion; everything else fails
                // this lookup closed.
                drawingAssociationComplete = false;
                continue;
            }
            drawingAssociationAvailable = true;

            foreach (var drawing in result.Drawings ?? Array.Empty<AssociatedDrawing>())
            {
                if (drawing.DocumentType is not (CadDocumentType.Idw or CadDocumentType.Dwg))
                {
                    // TYPE VALIDATION: an association claiming to be a
                    // "drawing" that isn't actually IDW/DWG is malformed
                    // evidence - never represented as a node or an edge, but
                    // never silently dropped either.
                    anyInvalidAssociationEvidence = true;
                    warnings.Add($"Drawing association evidence for cadDocumentId \"{modelDraft.CadDocumentId}\" "
                        + $"reported an unsupported document type ({drawing.DocumentType}) - a drawing association "
                        + "must be an IDW or DWG. This association is not represented in the plan.");
                    if (!string.IsNullOrWhiteSpace(drawing.CadDocumentId))
                    {
                        // TAINT: this rejected claim positively names a
                        // drawing identity - that identity's consensus can
                        // never be proven complete, even if OTHER owners for
                        // it were valid.
                        taintedDrawingKeys.Add("id:" + drawing.CadDocumentId);
                    }
                    continue;
                }

                // PATH VALIDATION: a nonblank path must be a genuinely
                // resolvable, fully-qualified absolute path - never
                // arbitrary nonblank text. A BLANK path is a legitimate,
                // honest "could not locate the file" claim (no warning); a
                // NONBLANK-but-unresolvable path is malformed evidence.
                var isResolved = IsResolvedAbsolutePath(drawing.AbsolutePath);
                if (!string.IsNullOrWhiteSpace(drawing.AbsolutePath) && !isResolved)
                {
                    anyInvalidAssociationEvidence = true;
                    warnings.Add($"Drawing association evidence for cadDocumentId \"{modelDraft.CadDocumentId}\" "
                        + $"reported a path that is not a valid, fully-qualified absolute path "
                        + $"(\"{drawing.AbsolutePath}\") - it is treated as unresolved and no DrawingModel edge is "
                        + "recorded for it.");
                    if (!string.IsNullOrWhiteSpace(drawing.CadDocumentId))
                    {
                        // TAINT: same rationale as the type-invalid case
                        // above - this rejected claim still positively names
                        // a drawing identity.
                        taintedDrawingKeys.Add("id:" + drawing.CadDocumentId);
                    }
                }

                var drawingKey = DrawingKeyFor(drawing, modelDraft.SourceAbsolutePath, isResolved);
                var drawingDraft = BuildDrawingDraft(drawing, modelDraft.SourceAbsolutePath, isResolved);
                RecordObservation(drawingKey, drawingDraft);

                if (isResolved)
                {
                    // Only VALIDATED, trusted evidence produces a graph edge.
                    // DRAWING -> MODEL: the drawing is the parent.
                    edgeSpecs.Add(new EdgeSpec(drawingKey, modelKey, CadRelationshipKind.DrawingModel, drawingDraft.SourceAbsolutePath));
                }
            }
        }

        if (!drawingAssociationAvailable)
        {
            // Correct the wording for the case where the SCAN ITSELF already
            // discovered one or more drawing nodes directly (e.g. the
            // selected root is a drawing, or a scanner-native DrawingModel
            // edge was found) - association contributing nothing must never
            // be phrased as "no drawings are represented", since drawing
            // nodes may already be sitting right there in the plan.
            var anyScannerDrawingNodePresent = byKeyForDrawingLookup.Values
                .Any(d => d.DocumentType is CadDocumentType.Idw or CadDocumentType.Dwg);
            warnings.Add(anyScannerDrawingNodePresent
                ? "Drawing association authority did not confirm any drawings for the models in this plan - any "
                    + "drawing node(s) already present come from directly scanned references, not from association "
                    + "data. This is a known limitation of the available authority, not evidence about the true "
                    + "complete drawing set."
                : "Drawing association could not be established from currently available Arch/server/"
                    + "workspace information - no drawings are represented in this plan. This is a known limitation, "
                    + "not evidence that no drawings exist for this design.");
        }

        if (!drawingAssociationComplete)
        {
            // GLOBAL incompleteness (Round 7, HIGH): at least one eligible
            // model's drawing-owner evidence could not be obtained
            // authoritatively - the COMPLETE drawing-owner picture was never
            // proven, even if every lookup that DID succeed returned Found.
            // This is deliberately NOT attributed to any specific drawing
            // identity (unlike Round 6's taintedDrawingKeys, which is for
            // malformed evidence that POSITIVELY names a drawing) - an
            // unavailable lookup proves nothing about which drawing, if any,
            // that model owns.
            warnings.Add("Drawing association authority was unavailable for one or more model documents; the "
                + "complete drawing set and drawing-owner relationships cannot be proven.");
        }

        // ---- 3. FINAL reconciliation, over the COMPLETE observation set
        //         (models AND drawings together) - both directions: forward
        //         (same cadDocumentId observed more than once) AND inverse
        //         (same normalized path claimed by more than one identity).
        var (byKey, conflictsByKey) = ReconcileAll(observationsByKey, order);

        // ---- 3b. BLOCKER 1 (Round 5): drawing-owner consensus MUST use
        //          EVERY DrawingModel edge in the COMPLETE edge set - the
        //          scanner's own direct edges AND every association-sourced
        //          edge - never only the latter. Built as one pass AFTER
        //          every edge (scan + association) exists, so it never
        //          depends on discovery order. A HashSet naturally dedupes a
        //          (drawing, model) pair proven by BOTH a scanner edge and an
        //          association edge - no duplicate owner influence. --------
        var drawingOwnerKeys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var spec in edgeSpecs)
        {
            if (spec.RelationshipKind != CadRelationshipKind.DrawingModel)
            {
                continue;
            }
            if (!drawingOwnerKeys.TryGetValue(spec.ParentKey, out var owners))
            {
                owners = new HashSet<string>(StringComparer.Ordinal);
                drawingOwnerKeys[spec.ParentKey] = owners;
            }
            owners.Add(spec.ChildKey);
        }

        // ---- 4. decide every node's action in TWO EXPLICIT PHASES:
        //
        //         PHASE 1 - every NON-DRAWING node (IAM, IPT, unmanaged/
        //         unresolved/other) - never depends on any drawing's action.
        //
        //         PHASE 2 - every DRAWING node (IDW/DWG), using the COMPLETE,
        //         already-final owner set from step 3b. Because Phase 1 has
        //         ALREADY decided every non-drawing node by the time Phase 2
        //         runs, a drawing's owners are always available - even when
        //         the drawing itself was discovered BEFORE its owners (e.g.
        //         the selected root IS a drawing) and `order` would
        //         otherwise list it first. -----------------------------
        var nodesByKey = new Dictionary<string, CopyDesignNode>(StringComparer.Ordinal);

        foreach (var key in order)
        {
            var draft = byKey[key];
            if (draft.DocumentType is CadDocumentType.Idw or CadDocumentType.Dwg)
            {
                continue; // decided in Phase 2 below
            }
            nodesByKey[key] = conflictsByKey.TryGetValue(key, out var conflictReasons)
                ? ConflictedNode(draft, conflictReasons)
                : DecideNode(draft, nameRule, destinationWorkspaceRoot, classificationSource, forcedAction: null);
        }

        foreach (var key in order)
        {
            var draft = byKey[key];
            if (draft.DocumentType is not (CadDocumentType.Idw or CadDocumentType.Dwg))
            {
                continue; // already decided in Phase 1
            }
            if (conflictsByKey.TryGetValue(key, out var conflictReasons))
            {
                nodesByKey[key] = ConflictedNode(draft, conflictReasons);
                continue;
            }

            CopyDesignAction? forcedAction = null;
            string? forcedReason = null;
            if (taintedDrawingKeys.Contains(key))
            {
                // ROUND 6, HIGH: at least one association claim for THIS
                // exact drawing identity was rejected before a trusted edge
                // could be created - the owner set can never be proven
                // complete. This taint takes ABSOLUTE precedence over
                // owner-consensus (all-Copy / all-Reuse) AND over the
                // ordinary root-Copy fallback: a drawing must never display a
                // confident action merely because the surviving, VALID
                // owners happened to agree - the rejected owner might not
                // have.
                forcedAction = CopyDesignAction.NeedsDecision;
                forcedReason = "At least one association claim for this drawing identity could not be validated "
                    + "(malformed evidence, no trusted DrawingModel edge could be created for it) - fail closed: "
                    + "complete owner consensus cannot be proven from an incomplete/rejected owner set.";
            }
            else if (drawingOwnerKeys.TryGetValue(key, out var owners) && owners.Count > 0)
            {
                // Every owner is a model (IAM/IPT), already decided in
                // Phase 1 above - never stale, never dependent on order.
                var ownerActions = owners
                    .Select(ok => nodesByKey.TryGetValue(ok, out var ownerNode) ? ownerNode.ProposedAction : (CopyDesignAction?)null)
                    .ToArray();

                if (ownerActions.All(a => a == CopyDesignAction.Copy))
                {
                    forcedAction = CopyDesignAction.Copy;
                }
                else if (ownerActions.All(a => a == CopyDesignAction.Reuse))
                {
                    forcedAction = CopyDesignAction.Reuse;
                }
                else
                {
                    // Mixed Copy+Reuse, an owner that is itself NeedsDecision,
                    // or an owner whose action could not be determined - fail
                    // closed rather than silently picking one owner's
                    // action. This applies EVEN WHEN the drawing is the
                    // selected root - root status never bypasses mixed-owner
                    // consensus.
                    forcedAction = CopyDesignAction.NeedsDecision;
                    forcedReason = owners.Count > 1
                        ? "This drawing is associated with more than one model and their proposed actions do not "
                            + "all agree (or at least one owner is itself NeedsDecision/undetermined) - fail closed "
                            + "rather than silently picking one owner's action."
                        : "This drawing's associated model's proposed action is NeedsDecision or could not be "
                            + "determined - fail closed: a drawing cannot safely mirror an unsafe owner.";
                }
            }
            // Zero proven model owners: no forcing at all - DecideNode falls
            // through to its ordinary rules (root -> Copy if this drawing IS
            // the selected root; otherwise ordinary classification). A
            // drawing root with no proven owner is not penalized for a
            // limitation of the available reference/association data, but
            // gaining even ONE owner immediately subjects it to full
            // owner-consensus safety above.

            nodesByKey[key] = DecideNode(draft, nameRule, destinationWorkspaceRoot, classificationSource, forcedAction, forcedReason);
        }

        // ---- 4b. RelationshipToParent is derived CONSISTENTLY from the
        //          actual incoming edges after graph construction - never
        //          carried along as a side-channel guess. A node's
        //          relationship is the single kind every edge INTO it agrees
        //          on, or null when it has none or they disagree. A drawing
        //          itself has NO incoming edges under the DRAWING -> MODEL
        //          direction, so it always resolves to null here. ----------
        var incomingKindsByChildKey = new Dictionary<string, HashSet<CadRelationshipKind>>(StringComparer.Ordinal);
        foreach (var spec in edgeSpecs)
        {
            if (!incomingKindsByChildKey.TryGetValue(spec.ChildKey, out var kinds))
            {
                kinds = new HashSet<CadRelationshipKind>();
                incomingKindsByChildKey[spec.ChildKey] = kinds;
            }
            kinds.Add(spec.RelationshipKind);
        }

        foreach (var key in order)
        {
            var resolvedRelationship = incomingKindsByChildKey.TryGetValue(key, out var kinds) && kinds.Count == 1
                ? kinds.First()
                : (CadRelationshipKind?)null;
            if (nodesByKey[key].RelationshipToParent != resolvedRelationship)
            {
                nodesByKey[key] = nodesByKey[key] with { RelationshipToParent = resolvedRelationship };
            }
        }

        // ---- 5. build every edge DIRECTLY from EdgeSpec.ParentKey/ChildKey -
        //         NEVER by re-searching plan nodes for a matching path. -----
        var edges = new List<CopyDesignEdge>();
        foreach (var spec in edgeSpecs)
        {
            var childAbs = byKey.TryGetValue(spec.ChildKey, out var childDraft)
                ? childDraft.SourceAbsolutePath
                : spec.FallbackParentAbsolutePath; // structurally unreachable - every child key is always recorded
            var parentAbs = byKey.TryGetValue(spec.ParentKey, out var parentDraft)
                ? parentDraft.SourceAbsolutePath
                : spec.FallbackParentAbsolutePath;

            // The disposition reflects the TARGET (child) of the edge - for a
            // DrawingModel edge that target is the MODEL: if the model is
            // being copied, the drawing's reference to it is expected to be
            // repointed at the new copy; if the model is reused, the
            // reference remains exactly as it is.
            var disposition = nodesByKey.TryGetValue(spec.ChildKey, out var childNode)
                ? childNode.ProposedAction switch
                {
                    CopyDesignAction.Copy => CopyDesignEdgeDisposition.PointsToNewCopy,
                    CopyDesignAction.Reuse => CopyDesignEdgeDisposition.RemainsOnReusedSource,
                    _ => CopyDesignEdgeDisposition.UnresolvedOrUnsafe,
                }
                : CopyDesignEdgeDisposition.UnresolvedOrUnsafe;

            edges.Add(new CopyDesignEdge(parentAbs, childAbs, spec.RelationshipKind, disposition));
        }

        // ---- 6. plan-level validation: duplicate / colliding / unsafe
        //         destinations across every COPY node. ---------------------
        var copyNodes = order.Select(k => nodesByKey[k]).Where(n => n.ProposedAction == CopyDesignAction.Copy).ToArray();
        var byDestination = copyNodes
            .Where(n => n.ProposedDestinationAbsolutePath is not null)
            .GroupBy(n => n.ProposedDestinationAbsolutePath!, StringComparer.OrdinalIgnoreCase);
        var anyDuplicateDestination = false;
        foreach (var group in byDestination)
        {
            if (group.Count() > 1)
            {
                anyDuplicateDestination = true;
                // sort the embedded name list ordinally so the rendered
                // warning text never depends on discovery order.
                var sources = string.Join(", ", group.Select(n => n.SourceFileName).OrderBy(n => n, StringComparer.Ordinal));
                warnings.Add($"Duplicate proposed destination \"{Path.GetFileName(group.Key)}\" - "
                    + $"{group.Count()} COPY nodes would collide ({sources}). No silent overwrite is permitted.");
            }
        }

        // Destination collision detectable from CURRENT LOCAL managed
        // workspace information (best-effort - `destinationExists` is
        // whatever the caller can determine locally; it is never a
        // guarantee, so a false negative here does not make an otherwise-
        // clean plan mean anything more than "no collision detected").
        var anyLocalCollision = false;
        foreach (var node in copyNodes)
        {
            if (node.ProposedDestinationAbsolutePath is not { } path)
            {
                continue;
            }
            bool exists;
            try { exists = destinationExists(path); }
            catch { exists = false; }
            if (exists)
            {
                anyLocalCollision = true;
                warnings.Add($"The proposed destination \"{Path.GetFileName(path)}\" already exists in the local "
                    + "managed workspace - no silent overwrite is permitted.");
            }
        }

        var decidedNodes = order.Select(k => nodesByKey[k]).ToArray();
        var anyNodeBlocksExecution = decidedNodes.Any(n =>
            n.ProposedAction == CopyDesignAction.NeedsDecision
            || (n.ProposedAction == CopyDesignAction.Copy && n.ProposedDestinationAbsolutePath is null));

        var isExecutable = scanWasComplete && !anyNodeBlocksExecution && !anyDuplicateDestination
            && !anyLocalCollision && !anyInvalidAssociationEvidence && drawingAssociationComplete;

        // ---- 7. sort the FINAL returned collections with a complete,
        //         ordinal, total order - never the raw discovery/observation
        //         order, and never a LINQ stable-sort fallback to input
        //         order. -------------------------------------------------
        var finalNodes = decidedNodes.OrderBy(NodeOrderKey, StringComparer.Ordinal).ToArray();
        var finalEdges = edges.OrderBy(e => e.ParentAbsolutePath, StringComparer.Ordinal)
            .ThenBy(e => e.ChildAbsolutePath, StringComparer.Ordinal)
            .ThenBy(e => e.RelationshipKind)
            .ThenBy(e => e.Disposition)
            .ToArray();
        var finalWarnings = warnings.OrderBy(w => w, StringComparer.Ordinal).ToArray();

        return new CopyDesignPlan(
            scan.Root.AbsolutePath,
            finalNodes,
            finalEdges,
            finalWarnings,
            isExecutable,
            scanWasComplete,
            drawingAssociationAvailable);
    }

    /// <summary>A complete, total, ordinal sort key for a plan node - prefers
    ///  the stable cadDocumentId when available, otherwise the normalized
    ///  source path, then exact source path/name/type as further tie-
    ///  breakers so the key is unique per distinct node regardless of
    ///  discovery order.</summary>
    private static string NodeOrderKey(CopyDesignNode n)
    {
        var identitySegment = !string.IsNullOrWhiteSpace(n.CadDocumentId)
            ? "0 " + n.CadDocumentId
            : "1 " + NormalizePath(n.SourceAbsolutePath);
        return identitySegment + " " + n.SourceAbsolutePath + " " + n.SourceFileName + " " + n.DocumentType;
    }

    /// <summary>BLOCKER 2D (Round 5): a path is "resolved" evidence ONLY when
    ///  it is nonblank, fully qualified, and normalizes without throwing -
    ///  the SAME safe-path rule <see cref="NormalizePath"/> relies on
    ///  elsewhere in the planner. Arbitrary nonblank text (a relative path, a
    ///  malformed/unsupported path string) is NEVER treated as resolved.</summary>
    private static bool IsResolvedAbsolutePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }
        try
        {
            _ = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Turns an authoritative, ALREADY-VALIDATED <see cref="AssociatedDrawing"/>
    ///  into an ordinary observation. Every fact comes from the association
    ///  source itself - nothing is assumed true. <paramref name="isResolved"/>
    ///  is the caller's own <see cref="IsResolvedAbsolutePath"/> verdict, so
    ///  the drawing/key/draft construction never re-derives (and cannot
    ///  disagree on) whether the path is trustworthy. A drawing with no
    ///  resolvable path falls back to an "unresolved" observation, exactly
    ///  like an unresolved CAD reference. It never carries a
    ///  RelationshipToParent of its own - under the DRAWING -&gt; MODEL
    ///  direction a drawing has no incoming edge.</summary>
    private static Draft BuildDrawingDraft(AssociatedDrawing drawing, string ownerAbsolutePath, bool isResolved)
    {
        var isManaged = !string.IsNullOrWhiteSpace(drawing.CadDocumentId);
        var sourcePath = isResolved ? drawing.AbsolutePath! : ownerAbsolutePath;
        var displayName = isResolved
            ? SafeFileName(drawing.AbsolutePath, drawing.CadDocumentId ?? "(unresolved drawing)")
            : "(unresolved drawing: " + (drawing.CadDocumentId ?? "unknown") + ")";

        return new Draft(
            drawing.CadDocumentId,
            drawing.CurrentFileVersionId,
            drawing.DocumentType,
            sourcePath,
            displayName,
            RelationshipToParent: null,
            IsManaged: isManaged,
            // Verification is only meaningful for a managed identity, and can
            // NEVER be inferred - it comes verbatim from the source.
            IsVerified: isManaged && drawing.IsVerified,
            IsResolved: isResolved,
            IsRoot: false);
    }

    /// <summary>ID-FIRST keying - a nonblank cadDocumentId ALWAYS wins
    ///  regardless of whether the drawing's path resolved, so an unresolved
    ///  observation of a cadDocumentId reconciles against a LATER (or
    ///  earlier) resolved observation of the SAME id instead of silently
    ///  keying past it under a fallback bucket their evidence never
    ///  meets.</summary>
    private static string DrawingKeyFor(AssociatedDrawing drawing, string ownerAbsolutePath, bool isResolved) =>
        !string.IsNullOrWhiteSpace(drawing.CadDocumentId)
            ? "id:" + drawing.CadDocumentId
            : (isResolved
                ? "path:" + NormalizePath(drawing.AbsolutePath!)
                : UnresolvedKey(ownerAbsolutePath, "(unresolved drawing, no cadDocumentId)"));

    /// <summary>cadDocumentId/FileVersionId are opaque, exact, persisted ids.
    ///  Canonical means nonblank AND identical to its own trimmed form - a
    ///  padded value ("  cad_a  ") is NEVER trimmed and accepted; it is
    ///  treated as untrustworthy evidence.</summary>
    private static bool IsCanonicalId(string? id) => !string.IsNullOrWhiteSpace(id) && id == id.Trim();

    private static CopyDesignNode DecideNode(
        Draft draft,
        IDestinationNameRule nameRule,
        string destinationWorkspaceRoot,
        IComponentClassificationSource classificationSource,
        CopyDesignAction? forcedAction,
        string? forcedReason = null)
    {
        var reasons = new List<string>();
        CopyDesignAction action;

        if (!draft.IsResolved)
        {
            action = CopyDesignAction.NeedsDecision;
            reasons.Add("Unresolved reference - the target could not be located. Fail closed: this node cannot "
                + "be safely planned as COPY or REUSE.");
        }
        else if (!draft.IsManaged || string.IsNullOrWhiteSpace(draft.CadDocumentId))
        {
            action = CopyDesignAction.NeedsDecision;
            reasons.Add("No stable Arch identity is available for this reference (unmanaged). Fail closed: "
                + "identity - never a file name - is required to plan a copy.");
        }
        else if (!IsCanonicalId(draft.CadDocumentId))
        {
            // A padded cadDocumentId ("  cad_a  ") is noncanonical evidence -
            // never trimmed and accepted as if it equaled the clean id. Fail
            // closed exactly like a missing id.
            action = CopyDesignAction.NeedsDecision;
            reasons.Add("This cadDocumentId is noncanonical - it does not exactly equal its own trimmed form. "
                + "An opaque stable id must be used EXACTLY as persisted, never padded or reformatted. Fail closed.");
        }
        else if (!draft.IsVerified)
        {
            action = CopyDesignAction.NeedsDecision;
            reasons.Add("This managed identity is Unverified. Fail closed: run Get Latest to re-establish a "
                + "trusted binding before planning a copy.");
        }
        else if (string.IsNullOrWhiteSpace(draft.CurrentFileVersionId))
        {
            // A stable Copy Design source identity requires BOTH the
            // cadDocumentId AND the current FileVersionId. A document id with
            // no pinned FileVersionId is not a complete lineage source for a
            // future P6B copy/reuse.
            action = CopyDesignAction.NeedsDecision;
            reasons.Add("This managed identity has no current FileVersionId. Fail closed: a complete stable "
                + "identity requires both the CadDocumentId and the FileVersionId before planning a copy or reuse.");
        }
        else if (!IsCanonicalId(draft.CurrentFileVersionId))
        {
            // Same canonical-exact-value rule for FileVersionId.
            action = CopyDesignAction.NeedsDecision;
            reasons.Add("This FileVersionId is noncanonical - it does not exactly equal its own trimmed form. "
                + "An opaque stable id must be used EXACTLY as persisted, never padded or reformatted. Fail closed.");
        }
        else if (forcedAction is { } fa)
        {
            action = fa;
            reasons.Add(forcedReason ?? $"Associated drawing - proposed action mirrors its model ({fa}).");
        }
        else if (draft.IsRoot)
        {
            action = CopyDesignAction.Copy;
            reasons.Add("Selected root of the Copy Design operation.");
        }
        else
        {
            var classification = classificationSource.Classify(draft.CadDocumentId!);
            switch (classification)
            {
                case ComponentClassification.LibraryOrShared:
                    action = CopyDesignAction.Reuse;
                    reasons.Add("Classified as a standard/library/shared component from existing Arch information.");
                    break;

                case ComponentClassification.ProjectSpecific:
                    action = CopyDesignAction.Copy;
                    reasons.Add("Classified as project-specific from existing Arch information.");
                    break;

                default:
                    // ARCHITECTURE CORRECTION: no authoritative classification
                    // signal is available - do NOT guess COPY. Surface
                    // NeedsDecision and let the plan fail closed. The
                    // suggestion below is NON-AUTHORITATIVE - it is plain text
                    // in Reasons, never assigned to ProposedAction, and can
                    // never become the effective action without an explicit
                    // engineer decision (there is no auto-apply path for it).
                    action = CopyDesignAction.NeedsDecision;
                    reasons.Add("No library/shared classification signal is available for this managed component - "
                        + "refusing to guess. Suggested: COPY (not automatically applied - requires an explicit "
                        + "engineer decision).");
                    break;
            }
        }

        string? destFileName = null;
        string? destPath = null;
        if (action == CopyDesignAction.Copy)
        {
            destFileName = nameRule.Rename(draft.SourceFileName) ?? draft.SourceFileName;
            try
            {
                destPath = SafeWorkspacePath.ResolveWithinRoot(destinationWorkspaceRoot, destFileName);
                if (string.Equals(Path.GetFullPath(draft.SourceAbsolutePath), destPath, StringComparison.OrdinalIgnoreCase))
                {
                    reasons.Add("The proposed destination is identical to the source path - refusing to plan a "
                        + "self-overwriting copy.");
                    action = CopyDesignAction.NeedsDecision;
                    destPath = null;
                }
            }
            catch (UnsafeWorkspacePathException ex)
            {
                reasons.Add($"The proposed destination file name is not safe ({ex.Reason}). Fail closed.");
                action = CopyDesignAction.NeedsDecision;
                destFileName = null;
            }
            catch (WorkspaceRootException ex)
            {
                reasons.Add($"The destination workspace root is not usable ({ex.Message}). Fail closed.");
                action = CopyDesignAction.NeedsDecision;
                destFileName = null;
            }
        }

        return new CopyDesignNode(
            draft.CadDocumentId,
            draft.CurrentFileVersionId,
            draft.DocumentType,
            draft.SourceAbsolutePath,
            draft.SourceFileName,
            draft.RelationshipToParent,
            draft.IsManaged,
            draft.IsVerified,
            draft.IsResolved,
            action,
            action == CopyDesignAction.Copy ? destFileName : null,
            action == CopyDesignAction.Copy ? destPath : null,
            reasons,
            draft.IsRoot);
    }

    /// <summary>
    /// Builds the node for a key whose observations DISAGREE on a safety-
    /// relevant identity fact (forward, same-cadDocumentId conflict) OR whose
    /// normalized path is claimed by more than one identity (inverse). Always
    /// <see cref="CopyDesignAction.NeedsDecision"/> with no proposed
    /// destination - reconciling conflicting facts into a single COPY/REUSE
    /// decision is never safe.
    /// </summary>
    private static CopyDesignNode ConflictedNode(Draft draft, IReadOnlyList<string> conflictReasons)
    {
        var reasons = new List<string>
        {
            "Multiple scan observations disagree on safety-relevant identity facts. Fail closed: this cannot be "
                + "reconciled into one COPY/REUSE decision without an explicit engineer decision.",
        };
        reasons.AddRange(conflictReasons);

        return new CopyDesignNode(
            draft.CadDocumentId,
            draft.CurrentFileVersionId,
            draft.DocumentType,
            draft.SourceAbsolutePath,
            draft.SourceFileName,
            draft.RelationshipToParent,
            draft.IsManaged,
            draft.IsVerified,
            draft.IsResolved,
            CopyDesignAction.NeedsDecision,
            ProposedDestinationFileName: null,
            ProposedDestinationAbsolutePath: null,
            reasons,
            draft.IsRoot);
    }

    /// <summary>
    /// Reconciles the COMPLETE observation set in both directions:
    ///
    /// FORWARD: observations sharing the same dedupe KEY (same cadDocumentId,
    /// or same unmanaged path, or same unresolved descriptor) dedupe into one
    /// node only when every safety-relevant fact agrees.
    ///
    /// INVERSE: observations sharing the same NORMALIZED RESOLVED PATH but
    /// living under DIFFERENT keys (different, or differently-managed,
    /// cadDocumentIds) are flagged conflicted too - one physical file can
    /// never legitimately claim two identities. IDs are compared EXACTLY
    /// (ordinal, no trimming): a padded id is a DIFFERENT value from its
    /// clean form, never silently folded together.
    ///
    /// Returns the canonical <see cref="Draft"/> for every key plus, for any
    /// key touched by EITHER direction, the reasons (in a fixed, sorted
    /// order) explaining why it must fail closed. Fully deterministic and
    /// independent of input/traversal order: canonical selection never falls
    /// back to "whichever observation was seen first".
    /// </summary>
    private static (Dictionary<string, Draft> ByKey, Dictionary<string, IReadOnlyList<string>> Conflicts) ReconcileAll(
        Dictionary<string, List<Draft>> observationsByKey, List<string> order)
    {
        var byKey = new Dictionary<string, Draft>(StringComparer.Ordinal);
        var conflicts = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void AddConflict(string key, string reason)
        {
            if (!conflicts.TryGetValue(key, out var list))
            {
                list = new List<string>();
                conflicts[key] = list;
            }
            list.Add(reason);
        }

        // ---- forward: per-key reconciliation -----------------------------
        foreach (var key in order)
        {
            var (draft, reasons) = ReconcileKey(observationsByKey[key]);
            byKey[key] = draft;
            foreach (var reason in reasons)
            {
                AddConflict(key, reason);
            }
        }

        // ---- inverse: normalized path -> identity ------------------------
        // Grouped from the RAW observations (never the post-reconciliation
        // canonical drafts) so a path conflict is caught even when each
        // individual key would otherwise reconcile "cleanly" on its own.
        var byNormalizedPath = new Dictionary<string, List<(string Key, Draft Draft)>>(StringComparer.Ordinal);
        foreach (var key in order)
        {
            foreach (var draft in observationsByKey[key])
            {
                if (!draft.IsResolved)
                {
                    continue; // only a RESOLVED source path is a real physical-file claim
                }
                var normalized = NormalizePath(draft.SourceAbsolutePath);
                if (!byNormalizedPath.TryGetValue(normalized, out var list))
                {
                    list = new List<(string, Draft)>();
                    byNormalizedPath[normalized] = list;
                }
                list.Add((key, draft));
            }
        }

        foreach (var group in byNormalizedPath.Values)
        {
            // EXACT (ordinal, untrimmed) comparison - "cad_a" and " cad_a "
            // are DIFFERENT opaque values, never folded together.
            var distinctIds = group
                .Select(g => g.Draft.CadDocumentId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            var hasUnmanagedClaim = group.Any(g => string.IsNullOrWhiteSpace(g.Draft.CadDocumentId));
            var conflicted = distinctIds.Length > 1 || (distinctIds.Length == 1 && hasUnmanagedClaim);
            if (!conflicted)
            {
                continue; // zero or one identity, consistently claimed - no ambiguity
            }

            var displayName = group
                .Select(g => g.Draft.SourceFileName)
                .OrderBy(n => n, StringComparer.Ordinal)
                .First();
            var idSummary = string.Join(", ", hasUnmanagedClaim ? distinctIds.Append("(unmanaged)") : distinctIds);
            var reason = $"The same source path (\"{displayName}\") is claimed by more than one identity: "
                + $"{idSummary}. Fail closed: one physical file cannot resolve to more than one stable identity.";

            foreach (var affectedKey in group.Select(g => g.Key).Distinct(StringComparer.Ordinal))
            {
                AddConflict(affectedKey, reason);
            }
        }

        // sort each key's accumulated conflict reasons ordinally so their
        // relative order can never depend on which direction (forward vs
        // inverse) or which raw-observation processing order happened to
        // contribute them first.
        return (byKey, conflicts.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value.OrderBy(r => r, StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal));
    }

    /// <summary>
    /// Reconciles every observation recorded under ONE dedupe key. A single
    /// observation always "reconciles" to itself. Two or more observations
    /// dedupe cleanly into ONE node only when every safety-relevant identity
    /// fact agrees across ALL of them (cadDocumentId is already guaranteed
    /// equal by construction of the key itself; CurrentFileVersionId,
    /// document type, managed/verified/RESOLVED state, and the normalized
    /// resolved path are compared explicitly).
    ///
    /// RelationshipToParent is NOT part of this reconciliation at all (moved
    /// to a post-construction, edge-derived computation) since it
    /// legitimately varies per parent and is not an identity fact.
    ///
    /// The chosen canonical representative is picked by a TOTAL, fully
    /// ordinal field order over every value that gets copied onto the plan
    /// node - never "whichever observation was seen first" - so the result,
    /// compatible or conflicting, never depends on scan/traversal order.
    /// </summary>
    private static (Draft Draft, IReadOnlyList<string> ConflictReasons) ReconcileKey(List<Draft> observations)
    {
        if (observations.Count == 1)
        {
            return (observations[0], Array.Empty<string>());
        }

        var isRoot = observations.Any(o => o.IsRoot);
        var fvValues = observations.Select(o => o.CurrentFileVersionId).ToArray();
        var typeValues = observations.Select(o => o.DocumentType).ToArray();
        var managedValues = observations.Select(o => o.IsManaged).ToArray();
        var verifiedValues = observations.Select(o => o.IsVerified).ToArray();
        var resolvedValues = observations.Select(o => o.IsResolved).ToArray();
        var pathValues = observations.Select(o => NormalizePath(o.SourceAbsolutePath)).ToArray();

        var fvMatch = AllEqual(fvValues, StringComparer.Ordinal);
        var typeMatch = AllEqual(typeValues);
        var managedMatch = AllEqual(managedValues);
        var verifiedMatch = AllEqual(verifiedValues);
        var resolvedMatch = AllEqual(resolvedValues);
        var pathMatch = AllEqual(pathValues, StringComparer.Ordinal);

        // A TOTAL deterministic order over every field that ends up copied
        // onto the plan node - normalized path first, then the EXACT
        // (ordinal) source path text as a tie-break so even a case-only
        // difference resolves the same way regardless of input order, then
        // every remaining output-bearing field (including IsResolved) in
        // turn. Two observations that still tie after all of these are, by
        // definition, indistinguishable in every field the plan node exposes.
        var canonicalObservation = observations
            .OrderBy(o => NormalizePath(o.SourceAbsolutePath), StringComparer.Ordinal)
            .ThenBy(o => o.SourceAbsolutePath, StringComparer.Ordinal)
            .ThenBy(o => o.CadDocumentId, StringComparer.Ordinal)
            .ThenBy(o => o.CurrentFileVersionId, StringComparer.Ordinal)
            .ThenBy(o => o.DocumentType)
            .ThenBy(o => o.IsManaged)
            .ThenBy(o => o.IsVerified)
            .ThenBy(o => o.IsResolved)
            .ThenBy(o => o.SourceFileName, StringComparer.Ordinal)
            .First();
        // RelationshipToParent is deliberately NOT set here - see the
        // post-construction, edge-derived computation in Plan().
        var canonical = canonicalObservation with { IsRoot = isRoot };

        if (fvMatch && typeMatch && managedMatch && verifiedMatch && resolvedMatch && pathMatch)
        {
            return (canonical, Array.Empty<string>());
        }

        var conflictReasons = new List<string>();
        if (!fvMatch)
        {
            conflictReasons.Add(DescribeConflict("FileVersionId", fvValues));
        }
        if (!typeMatch)
        {
            conflictReasons.Add(DescribeConflict("document type", typeValues.Select(t => t.ToString())));
        }
        if (!managedMatch)
        {
            conflictReasons.Add(DescribeConflict("managed state", managedValues.Select(v => v.ToString())));
        }
        if (!verifiedMatch)
        {
            conflictReasons.Add(DescribeConflict("verification state", verifiedValues.Select(v => v.ToString())));
        }
        if (!resolvedMatch)
        {
            conflictReasons.Add(DescribeConflict("resolved state", resolvedValues.Select(v => v.ToString())));
        }
        if (!pathMatch)
        {
            conflictReasons.Add(DescribeConflict("resolved path", pathValues));
        }

        return (canonical, conflictReasons);
    }

    private static bool AllEqual<T>(IReadOnlyList<T> values, IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;
        for (var i = 1; i < values.Count; i++)
        {
            if (!comparer.Equals(values[0], values[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static string DescribeConflict(string label, IEnumerable<string?> rawValues)
    {
        // Ordinal (case- AND padding-SENSITIVE) distinct + sort - NO
        // trimming. FileVersionId and cadDocumentId are opaque, exact
        // persisted ids (like a cuid) - "FV-ABC" and "fv-abc", or "cad_a" and
        // " cad_a ", are DIFFERENT values, never silently folded together.
        // Ordinal sort+dedup also makes the rendered message fully
        // independent of which observation happened to be seen first.
        var distinctValues = rawValues
            .Select(v => string.IsNullOrEmpty(v) ? "(none)" : v!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal);
        return $"Conflicting {label} observed for the same identity: {string.Join(", ", distinctValues)}.";
    }

    private sealed record EdgeSpec(
        string ParentKey,
        string ChildKey,
        CadRelationshipKind RelationshipKind,
        string FallbackParentAbsolutePath);

    private static string KeyFor(string? cadDocumentId, string absolutePath) =>
        string.IsNullOrWhiteSpace(cadDocumentId)
            ? "path:" + NormalizePath(absolutePath)
            : "id:" + cadDocumentId;

    private static string UnresolvedKey(string parentAbsolutePath, string reportedNameOrPath) =>
        "unresolved:" + NormalizePath(parentAbsolutePath) + "|" + (reportedNameOrPath ?? "").Trim().ToLowerInvariant();

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path).ToLowerInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim().ToLowerInvariant();
        }
    }

    private static string SafeFileName(string? path, string fallback)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.IsNullOrWhiteSpace(fallback) ? "(unnamed reference)" : fallback.Trim();
        }
        try
        {
            var name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? path.Trim() : name;
        }
        catch (ArgumentException)
        {
            return string.IsNullOrWhiteSpace(fallback) ? path.Trim() : fallback.Trim();
        }
    }

    private sealed record Draft(
        string? CadDocumentId,
        string? CurrentFileVersionId,
        CadDocumentType DocumentType,
        string SourceAbsolutePath,
        string SourceFileName,
        CadRelationshipKind? RelationshipToParent,
        bool IsManaged,
        bool IsVerified,
        bool IsResolved,
        bool IsRoot);
}
