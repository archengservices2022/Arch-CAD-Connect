namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// P6C/P6D: maps a CONFIRMED, executable P6A <see cref="CopyDesignPlan"/> to a
/// P6B <see cref="CopyDesignApplyRequest"/>. PURE - no COM, no HTTP, no I/O.
///
/// SCOPE: <see cref="CadDocumentType.Ipt"/> and <see cref="CadDocumentType.Iam"/>
/// nodes are ALWAYS mapped (P6C's original, unchanged boundary). A drawing
/// node (Idw/Dwg) is mapped alongside them - in the SAME reservation request,
/// using the SAME Copy/Reuse entry shape - whenever <paramref name="includeDrawings"/>
/// is <c>true</c> (the default: P6D now owns drawing execution, so a normal
/// Copy Design operation maps everything the confirmed plan represents). The
/// orchestrator passes <c>includeDrawings: false</c> ONLY when the plan
/// itself carries an explicit <see cref="CopyDesignPlan.ModelFilesOnlyAcknowledged"/>
/// acknowledgement - in that one case a drawing node is skipped entirely:
/// never sent to P6B, never physically touched, exactly mirroring P6C's
/// original boundary for that specific, explicit, non-default mode.
///
/// FAIL CLOSED (returns a failure, never a partial/best-effort request) when:
///   - the plan itself is not executable;
///   - an in-scope node's action is anything other than Copy/Reuse (an
///     executable plan should never contain NeedsDecision/Exclude, but this
///     is never trusted blindly);
///   - a COPY node lacks a complete stable identity, or a proposed
///     destination file name/path;
///   - a REUSE node lacks a stable cadDocumentId;
///   - after filtering, there is nothing in-scope to apply at all;
///   - two or more COPY entries would request the SAME destination document
///     number AND the SAME document type (P6C acceptance follow-up; scoped
///     by type since P6D ROUND 2 - see "DOCUMENT NUMBER" below) - a full-
///     PATH collision is already caught by <see cref="CopyDesignPlanner"/>'s
///     own duplicate-destination check, but two DIFFERENT destination paths
///     (different destination sub-folders, SAME extension) can still share
///     the SAME file-name STEM; this is caught HERE, locally, before any
///     HTTP call is made - never left to a server-side 409 to be the only
///     line of defense. The SAME stem with a DIFFERENT document type (e.g.
///     "1001.ipt" and "1001.idw") is explicitly NOT a collision.
///
/// DOCUMENT NUMBER: <see cref="CopyDesignNode"/> has no explicit destination
/// "document number" field (it only proposes a destination FILE NAME) - this
/// mapper derives <c>newDocumentNumber</c> as the destination file name's
/// stem (extension removed, via <see cref="Path.GetFileNameWithoutExtension(string)"/>
/// on the destination file name ONLY - never a directory, never the source
/// file name, never the source/fixture's OWN document number, and never the
/// stable <c>CadDocumentId</c>), matching the shop convention already used
/// throughout the P6B server's own fixtures/tests (e.g. fileName
/// "10137-P001.ipt" -&gt; documentNumber "10137-P001"; "P6C-REAL-PART-A.ipt" -&gt;
/// "P6C-REAL-PART-A"). If document numbers and file names ever diverge in
/// practice, <c>CopyDesignPlan</c> would need an explicit destination-
/// document-number field - out of scope for P6C since P6A is frozen/released
/// (see the P6C report's "known limitations").
/// </summary>
public static class CopyDesignApplyRequestMapper
{
    private static readonly HashSet<CadDocumentType> ModelTypes = new() { CadDocumentType.Ipt, CadDocumentType.Iam };
    private static readonly HashSet<CadDocumentType> DrawingTypes = new() { CadDocumentType.Idw, CadDocumentType.Dwg };

    public static CopyDesignApplyMappingResult Map(
        CopyDesignPlan plan, string idempotencyKey, string? label, bool includeDrawings = true)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return CopyDesignApplyMappingResult.Fail("An idempotency key is required to apply a Copy Design plan.");
        }
        if (!plan.IsExecutable)
        {
            return CopyDesignApplyMappingResult.Fail("The plan is not executable - it cannot be applied.");
        }

        var mapped = new List<CopyDesignApplyMappedEntry>();
        foreach (var node in plan.Nodes)
        {
            var inScope = ModelTypes.Contains(node.DocumentType)
                || (includeDrawings && DrawingTypes.Contains(node.DocumentType));
            if (!inScope)
            {
                // Out of scope for THIS mapping call - either an unrecognized
                // type, or a drawing while includeDrawings is false (the
                // explicit model-files-only acknowledgement) - never mapped,
                // never blocks the rest of the plan's execution.
                continue;
            }

            switch (node.ProposedAction)
            {
                case CopyDesignAction.Copy:
                {
                    if (!node.HasStableIdentity)
                    {
                        return CopyDesignApplyMappingResult.Fail(
                            $"COPY node \"{node.SourceFileName}\" does not carry a complete stable identity - refusing to apply.");
                    }
                    if (string.IsNullOrWhiteSpace(node.ProposedDestinationFileName)
                        || string.IsNullOrWhiteSpace(node.ProposedDestinationAbsolutePath))
                    {
                        return CopyDesignApplyMappingResult.Fail(
                            $"COPY node \"{node.SourceFileName}\" has no proposed destination - refusing to apply.");
                    }

                    var documentNumber = DocumentNumberFromFileName(node.ProposedDestinationFileName);
                    if (string.IsNullOrWhiteSpace(documentNumber))
                    {
                        return CopyDesignApplyMappingResult.Fail(
                            $"Could not derive a destination document number from \"{node.ProposedDestinationFileName}\".");
                    }

                    mapped.Add(new CopyDesignApplyMappedEntry(
                        node,
                        new CopyDesignApplyEntry(
                            CopyDesignApplyEntryAction.Copy,
                            new CopyDesignApplyCopyEntry(
                                node.CadDocumentId!,
                                node.CurrentFileVersionId!,
                                documentNumber,
                                node.ProposedDestinationFileName!,
                                node.DocumentType,
                                Description: null),
                            Reuse: null)));
                    break;
                }
                case CopyDesignAction.Reuse:
                {
                    if (string.IsNullOrWhiteSpace(node.CadDocumentId))
                    {
                        return CopyDesignApplyMappingResult.Fail(
                            $"REUSE node \"{node.SourceFileName}\" does not carry a stable cadDocumentId - refusing to apply.");
                    }

                    mapped.Add(new CopyDesignApplyMappedEntry(
                        node,
                        new CopyDesignApplyEntry(
                            CopyDesignApplyEntryAction.Reuse,
                            Copy: null,
                            new CopyDesignApplyReuseEntry(node.CadDocumentId))));
                    break;
                }
                default:
                    // NeedsDecision / Exclude on an in-scope node. An
                    // executable plan should never contain one - fail closed
                    // rather than trust that invariant blindly.
                    return CopyDesignApplyMappingResult.Fail(
                        $"Node \"{node.SourceFileName}\" has action {node.ProposedAction}, which P6C cannot apply.");
            }
        }

        if (mapped.Count == 0)
        {
            return CopyDesignApplyMappingResult.Fail(
                "The plan has no in-scope (IAM/IPT) COPY or REUSE entries to apply.");
        }

        // FAIL CLOSED, LOCALLY, BEFORE HTTP: two COPY entries requesting the
        // SAME destination document number would collide server-side no
        // matter how correctly each one was individually derived - e.g. an
        // IAM and an IPT landing on the same stem in different destination
        // sub-paths ("FOO.iam" and "FOO.ipt", or two different destination
        // folders both producing "FOO") share a document number even though
        // CopyDesignPlanner's own duplicate-destination check (which compares
        // full ABSOLUTE PATHS, extension included) would not catch it. Never
        // rely on the server's own uniqueness constraint / a 409 response to
        // catch what this client already knows before sending anything.
        //
        // P6D ROUND 2, HIGH fix: the server's uniqueness key is now SCOPED BY
        // documentType too (CadDocument's @@unique([organizationId,
        // documentNumber, documentType]) - see the web repo's
        // 20260921183016_scope_document_number_uniqueness_by_type migration)
        // - the SAME documentNumber with a DIFFERENT documentType (e.g.
        // "1001.ipt" and "1001.idw", the normal Inventor model/drawing pair)
        // is explicitly NOT a collision and must never be rejected here. Only
        // the SAME (documentNumber, documentType) pair twice is a real
        // collision.
        var duplicateDocumentNumbers = mapped
            .Where(m => m.Entry.Action == CopyDesignApplyEntryAction.Copy)
            .GroupBy(m => (m.Entry.Copy!.NewDocumentNumber, m.Entry.Copy!.DocumentType), DocumentDestinationKeyComparer.Instance)
            .Where(g => g.Count() > 1)
            .ToArray();
        if (duplicateDocumentNumbers.Length > 0)
        {
            var group = duplicateDocumentNumbers[0];
            var sources = string.Join(", ", group.Select(m => m.Node.SourceFileName).OrderBy(n => n, StringComparer.Ordinal));
            return CopyDesignApplyMappingResult.Fail(
                $"Two or more COPY entries would request the same destination document number \"{group.Key.NewDocumentNumber}\" "
                + $"for document type {group.Key.DocumentType} ({sources}) - refusing to send a request that could collide. "
                + "No silent overwrite/collision is permitted.");
        }

        return CopyDesignApplyMappingResult.Ok(new CopyDesignApplyRequest(idempotencyKey.Trim(), NormalizeLabel(label), mapped));
    }

    private static string? DocumentNumberFromFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName)?.Trim();
        return string.IsNullOrWhiteSpace(stem) ? null : stem;
    }

    /// <summary>P6D ROUND 2: groups a (documentNumber, documentType) pair the
    ///  SAME way the server's scoped unique constraint does - ordinal,
    ///  case-INSENSITIVE on the document number (matching every other
    ///  document-number comparison already in this class), exact on the
    ///  document type.</summary>
    private sealed class DocumentDestinationKeyComparer : IEqualityComparer<(string NewDocumentNumber, CadDocumentType DocumentType)>
    {
        public static readonly DocumentDestinationKeyComparer Instance = new();

        public bool Equals((string NewDocumentNumber, CadDocumentType DocumentType) x, (string NewDocumentNumber, CadDocumentType DocumentType) y) =>
            string.Equals(x.NewDocumentNumber, y.NewDocumentNumber, StringComparison.OrdinalIgnoreCase) && x.DocumentType == y.DocumentType;

        public int GetHashCode((string NewDocumentNumber, CadDocumentType DocumentType) obj) =>
            HashCode.Combine(obj.NewDocumentNumber.ToUpperInvariant(), obj.DocumentType);
    }

    private static string? NormalizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }
        var trimmed = label.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
