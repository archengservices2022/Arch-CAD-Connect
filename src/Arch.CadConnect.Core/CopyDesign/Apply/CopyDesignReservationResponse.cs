namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// P6C: the response shape the P6B server contract returns. Mirrors the
/// server's <c>CopyDesignApplyResultDto</c> exactly (see
/// web/app/lib/copy-design-lineage-core.ts).
/// </summary>
public sealed record CopyDesignReservationResponseEntry(
    CopyDesignApplyEntryAction Action,
    string? SourceCadDocumentId,
    string? SourceFileVersionId,
    string ResultingCadDocumentId,
    string DocumentNumber,
    string FileName,
    string DocumentType);

public sealed record CopyDesignReservationResponse(
    string Contract,
    string CopyDesignOperationId,
    string PerformedAtUtc,
    IReadOnlyList<CopyDesignReservationResponseEntry> Entries);

/// <summary>
/// ONE validated (request entry, plan node, server-reserved identity)
/// triple - the ONLY thing the rest of P6C's execution is allowed to act on.
/// </summary>
public sealed record CopyDesignReservationMapEntry(
    CopyDesignNode Node,
    CopyDesignApplyEntryAction Action,
    string ResultingCadDocumentId,
    string DocumentNumber,
    string FileName);

public sealed record CopyDesignReservationValidationResult(
    bool Success,
    string? FailureReason,
    string? CopyDesignOperationId,
    IReadOnlyList<CopyDesignReservationMapEntry> Entries)
{
    public static CopyDesignReservationValidationResult Fail(string reason) => new(false, reason, null, Array.Empty<CopyDesignReservationMapEntry>());
}

/// <summary>
/// P6C: validates a P6B reservation response against the EXACT request that
/// was sent, before any physical mutation is permitted. PURE.
///
/// FAIL CLOSED (before physical mutation) on:
///   - entry count mismatch;
///   - order/action mismatch at any index (P6B's contract is order-preserving
///     - see the server's own CopyDesignOperationEntry.ordinal design);
///   - a COPY entry whose echoed source ids do not exactly match what was
///     requested, or whose resultingCadDocumentId is blank or equal to the
///     source (a copy must be a genuinely NEW identity);
///   - a REUSE entry whose resultingCadDocumentId does not EXACTLY equal the
///     cadDocumentId that was requested (the intended existing identity);
///   - MEDIUM 2 (CODEX FINAL AUDIT ROUND 1): GLOBALLY, across the WHOLE
///     response - two COPY entries sharing the same resultingCadDocumentId,
///     a COPY resultingCadDocumentId equal to ANY source cadDocumentId in
///     this request, or a COPY resultingCadDocumentId equal to ANY REUSE
///     entry's cadDocumentId in this request.
/// </summary>
public static class CopyDesignReservationResponseValidator
{
    public static CopyDesignReservationValidationResult Validate(
        CopyDesignApplyRequest request, CopyDesignReservationResponse response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        if (string.IsNullOrWhiteSpace(response.CopyDesignOperationId))
        {
            return CopyDesignReservationValidationResult.Fail("The server response did not carry a CopyDesignOperationId.");
        }
        if (response.Entries.Count != request.Entries.Count)
        {
            return CopyDesignReservationValidationResult.Fail(
                $"The server returned {response.Entries.Count} entries, but {request.Entries.Count} were requested.");
        }

        var mapped = new List<CopyDesignReservationMapEntry>(request.Entries.Count);
        for (var i = 0; i < request.Entries.Count; i++)
        {
            var requested = request.Entries[i];
            var returned = response.Entries[i];

            if (returned.Action != requested.Entry.Action)
            {
                return CopyDesignReservationValidationResult.Fail(
                    $"Entry {i}: requested action {requested.Entry.Action} but the server returned {returned.Action}.");
            }

            if (requested.Entry.Action == CopyDesignApplyEntryAction.Copy)
            {
                var copy = requested.Entry.Copy!;
                if (!string.Equals(returned.SourceCadDocumentId, copy.SourceCadDocumentId, StringComparison.Ordinal)
                    || !string.Equals(returned.SourceFileVersionId, copy.SourceFileVersionId, StringComparison.Ordinal))
                {
                    return CopyDesignReservationValidationResult.Fail(
                        $"Entry {i}: the server's echoed COPY source identity does not match what was requested.");
                }
                if (string.IsNullOrWhiteSpace(returned.ResultingCadDocumentId))
                {
                    return CopyDesignReservationValidationResult.Fail($"Entry {i}: the server returned no resultingCadDocumentId for a COPY.");
                }
                if (string.Equals(returned.ResultingCadDocumentId, copy.SourceCadDocumentId, StringComparison.Ordinal))
                {
                    return CopyDesignReservationValidationResult.Fail(
                        $"Entry {i}: the server returned the SOURCE identity as the COPY result - a copy must be a distinct identity.");
                }
            }
            else
            {
                var reuse = requested.Entry.Reuse!;
                if (!string.Equals(returned.ResultingCadDocumentId, reuse.CadDocumentId, StringComparison.Ordinal))
                {
                    return CopyDesignReservationValidationResult.Fail(
                        $"Entry {i}: REUSE must resolve to the exact intended identity \"{reuse.CadDocumentId}\", " +
                        $"but the server returned \"{returned.ResultingCadDocumentId}\".");
                }
            }

            mapped.Add(new CopyDesignReservationMapEntry(
                requested.Node, returned.Action, returned.ResultingCadDocumentId, returned.DocumentNumber, returned.FileName));
        }

        // MEDIUM 2 fix: the per-index checks above only ever compare an
        // entry against ITSELF (its own requested source/identity) - they
        // never catch a malformed response that is internally consistent
        // per-entry but GLOBALLY ambiguous across entries. Validate the
        // COMPLETE result set, once, before any physical mutation is ever
        // permitted to read `mapped`.
        var copyResultingIds = mapped
            .Where(m => m.Action == CopyDesignApplyEntryAction.Copy)
            .Select(m => m.ResultingCadDocumentId)
            .ToArray();

        var duplicateCopyResultingId = copyResultingIds
            .GroupBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateCopyResultingId is not null)
        {
            return CopyDesignReservationValidationResult.Fail(
                $"The server returned the same resultingCadDocumentId \"{duplicateCopyResultingId.Key}\" for two or "
                + "more COPY entries - refusing to trust an ambiguous reservation response.");
        }

        var allRequestedCopySourceIds = new HashSet<string>(
            request.Entries.Where(e => e.Entry.Action == CopyDesignApplyEntryAction.Copy).Select(e => e.Entry.Copy!.SourceCadDocumentId),
            StringComparer.Ordinal);
        var resultingAliasesAnySource = copyResultingIds.FirstOrDefault(allRequestedCopySourceIds.Contains);
        if (resultingAliasesAnySource is not null)
        {
            return CopyDesignReservationValidationResult.Fail(
                $"The server returned resultingCadDocumentId \"{resultingAliasesAnySource}\" for a COPY entry, but that "
                + "identity is also a SOURCE cadDocumentId in this same request - refusing to trust an ambiguous "
                + "reservation response.");
        }

        var allRequestedReuseIds = new HashSet<string>(
            request.Entries.Where(e => e.Entry.Action == CopyDesignApplyEntryAction.Reuse).Select(e => e.Entry.Reuse!.CadDocumentId),
            StringComparer.Ordinal);
        var resultingAliasesAnyReuse = copyResultingIds.FirstOrDefault(allRequestedReuseIds.Contains);
        if (resultingAliasesAnyReuse is not null)
        {
            return CopyDesignReservationValidationResult.Fail(
                $"The server returned resultingCadDocumentId \"{resultingAliasesAnyReuse}\" for a COPY entry, but that "
                + "SAME identity is also a REUSE entry's cadDocumentId in this same request - refusing to trust an "
                + "ambiguous reservation response.");
        }

        return new CopyDesignReservationValidationResult(true, null, response.CopyDesignOperationId, mapped);
    }
}
