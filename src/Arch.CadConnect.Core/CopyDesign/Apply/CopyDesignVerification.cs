namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>ONE managed reference a copied IAM was expected to resolve, and
///  what it ACTUALLY resolved to (gathered by the Inventor adapter) -
///  <c>null</c> <see cref="ActualResolvedAbsolutePath"/> means the reference
///  is unresolved/missing. <see cref="OriginalSourceAbsolutePath"/> (HIGH 3
///  fix) is the child's PRE-copy path - the ONE other path an occurrence is
///  allowed to have come from (a REUSE child, where it equals
///  <see cref="ExpectedTargetAbsolutePath"/>) - so a STALE occurrence still
///  pointing at it can be told apart from a genuinely unplanned one.</summary>
public sealed record CopyDesignReferenceResolutionFact(
    string ChildCadDocumentId,
    string ExpectedTargetAbsolutePath,
    string OriginalSourceAbsolutePath,
    string? ActualResolvedAbsolutePath,
    bool ChildIsCopy);

/// <summary>HIGH 3 fix: ONE ACTUAL component occurrence Inventor found in the
///  copied/rewired assembly - the COMPLETE set (every occurrence, not merely
///  the ones the plan expected) is what <see cref="CopyDesignVerificationEvaluator"/>
///  now checks, so a stale leftover reference or a wholly unplanned
///  reference can never hide behind an expected one that ALSO happens to be
///  present. <c>null</c> <see cref="ResolvedAbsolutePath"/> means Inventor
///  reports this occurrence as unresolved.</summary>
public sealed record CopyDesignActualComponentOccurrence(string? ResolvedAbsolutePath);

/// <summary>
/// Every fact <see cref="CopyDesignVerificationEvaluator"/> needs to decide
/// whether ONE COPY node's result is trustworthy - gathered entirely by the
/// Inventor adapter (COM, hashing, I/O); this record itself is COM-free so
/// the decision logic can be tested without Inventor.
/// </summary>
public sealed record CopyDesignNodeVerificationFacts(
    string CadDocumentId,
    string SourceAbsolutePath,
    string DestinationAbsolutePath,
    bool DestinationExists,
    bool DocumentTypeMatches,
    bool OpenableThroughInventor,
    string? ResultingSha256,
    /// <summary>The FINAL destination binary's actual byte length, captured
    ///  by the SAME verification pass as <see cref="ResultingSha256"/> - the
    ///  authoritative "locally verified final file size" materialization
    ///  reports to the server (never a pre-copy or stale value).</summary>
    long? ResultingFileSize,
    string SourceSha256BeforeOperation,
    string? SourceSha256AfterOperation,
    /// <summary>Empty for an IPT (nothing to rewire); one entry per direct
    ///  COMPONENT child EXPECTED by the confirmed plan for a copied IAM -
    ///  used to report a completely MISSING expected reference and for
    ///  per-target messaging. See <see cref="ActualComponentOccurrences"/>
    ///  for the COMPLETE actual set this is now cross-checked against.</summary>
    IReadOnlyList<CopyDesignReferenceResolutionFact> ReferenceResolutions,
    /// <summary>HIGH 3 fix: the COMPLETE set of component occurrences
    ///  Inventor ACTUALLY found in the copied/rewired assembly - empty for
    ///  an IPT. Every occurrence in this list is judged; none may be
    ///  unresolved, stale (at a child's <see cref="CopyDesignReferenceResolutionFact.OriginalSourceAbsolutePath"/>
    ///  when it differs from its expected target), or unplanned (resolving
    ///  somewhere not named by ANY entry in <see cref="ReferenceResolutions"/>).
    ///  Duplicates are fine as long as EVERY one of them resolves to its
    ///  expected target.</summary>
    IReadOnlyList<CopyDesignActualComponentOccurrence> ActualComponentOccurrences,
    /// <summary>CODEX ROUND 3, HIGH fix: <c>true</c> unless enumerating
    ///  <see cref="ActualComponentOccurrences"/> for an IAM was ATTEMPTED and
    ///  FAILED (Inventor threw, the document could not be opened as an
    ///  assembly, or the destination never existed at all) - an IPT never
    ///  enumerates occurrences at all, so this is always <c>true</c> for one.
    ///  REQUIRED to be <c>true</c> by <see cref="CopyDesignVerificationEvaluator"/>
    ///  for EVERY node, independent of expected-reference count: an IAM with
    ///  ZERO expected references must never pass verification merely because
    ///  an empty <see cref="ActualComponentOccurrences"/> looks identical to
    ///  "there was nothing to enumerate" when it actually means "enumeration
    ///  failed and lost the real, possibly non-empty, set".</summary>
    bool OccurrenceEnumerationSucceeded = true);

public sealed record CopyDesignNodeVerificationResult(string CadDocumentId, bool Passed, IReadOnlyList<string> Reasons);

public sealed record CopyDesignVerificationResult(bool Passed, IReadOnlyList<CopyDesignNodeVerificationResult> NodeResults)
{
    public IEnumerable<string> AllReasons => NodeResults.SelectMany(r => r.Reasons);
}

/// <summary>
/// P6C: PURE evaluation of gathered verification facts into a pass/fail
/// decision with human-readable reasons. Never calls Inventor, never does
/// I/O or hashing itself - it only judges facts someone else already
/// gathered. "SaveAs/Copy returned without throwing" is NEVER sufficient on
/// its own for success; every one of these checks must independently pass.
/// </summary>
public static class CopyDesignVerificationEvaluator
{
    public static CopyDesignNodeVerificationResult Evaluate(CopyDesignNodeVerificationFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var reasons = new List<string>();

        if (!facts.DestinationExists)
        {
            reasons.Add("The destination file does not exist.");
        }
        if (PathsEqual(facts.SourceAbsolutePath, facts.DestinationAbsolutePath))
        {
            reasons.Add("The source and destination paths are identical.");
        }
        if (!facts.DocumentTypeMatches)
        {
            reasons.Add("The resulting document's type does not match the expected document type.");
        }
        if (!facts.OpenableThroughInventor)
        {
            reasons.Add("The resulting file could not be opened/verified through Inventor.");
        }
        if (string.IsNullOrWhiteSpace(facts.ResultingSha256))
        {
            reasons.Add("No resulting SHA-256 was captured for the destination file.");
        }
        if (facts.ResultingFileSize is null || facts.ResultingFileSize.Value <= 0)
        {
            reasons.Add("No resulting file size was captured for the destination file.");
        }
        if (string.IsNullOrWhiteSpace(facts.SourceSha256AfterOperation)
            || !string.Equals(facts.SourceSha256BeforeOperation, facts.SourceSha256AfterOperation, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("The source file's SHA-256 could not be re-verified as unchanged - the source may have been mutated.");
        }

        // CODEX ROUND 3, HIGH fix: occurrence ENUMERATION itself must have
        // succeeded, independent of how many references were expected or
        // actually found - an IAM with zero expected references must never
        // pass merely because "enumeration failed" and "there was nothing
        // to enumerate" both leave ActualComponentOccurrences empty. An IPT
        // never enumerates occurrences at all, so its gatherer always
        // reports this as true - this check is unconditional and safe for
        // every node.
        if (!facts.OccurrenceEnumerationSucceeded)
        {
            reasons.Add("Component occurrence enumeration for the resulting document could not be completed - " +
                "refusing to trust an incomplete/undercounted occurrence set.");
        }

        // HIGH 3 fix: evaluate the COMPLETE actual occurrence set, not
        // merely "does each expected target exist somewhere" - existence of
        // the expected target says nothing about whether a STALE or
        // wholly UNPLANNED occurrence ALSO remains alongside it.
        var byExpectedPath = new Dictionary<string, List<CopyDesignReferenceResolutionFact>>(StringComparer.OrdinalIgnoreCase);
        var byOriginalPath = new Dictionary<string, List<CopyDesignReferenceResolutionFact>>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in facts.ReferenceResolutions)
        {
            AddTarget(byExpectedPath, NormalizeOrNull(target.ExpectedTargetAbsolutePath), target);
            AddTarget(byOriginalPath, NormalizeOrNull(target.OriginalSourceAbsolutePath), target);
        }

        var foundAtExpected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var occurrence in facts.ActualComponentOccurrences)
        {
            if (string.IsNullOrWhiteSpace(occurrence.ResolvedAbsolutePath))
            {
                reasons.Add("A component occurrence in the resulting assembly did not resolve (unresolved managed reference).");
                continue;
            }

            var normalized = NormalizeOrNull(occurrence.ResolvedAbsolutePath);
            if (normalized is not null && byExpectedPath.TryGetValue(normalized, out var expectedMatches))
            {
                // Resolves exactly where the plan says it should - fine,
                // including when MULTIPLE occurrences legitimately land here.
                foreach (var match in expectedMatches)
                {
                    foundAtExpected.Add(match.ChildCadDocumentId);
                }
                continue;
            }

            if (normalized is not null && byOriginalPath.TryGetValue(normalized, out var staleMatches))
            {
                // Present at the child's OWN pre-copy path while that child's
                // EXPECTED target is a DIFFERENT path (a REUSE child whose
                // original path IS its expected target already matched
                // above and never reaches here) - a stale leftover
                // reference, even if another occurrence ALSO correctly
                // resolves to the expected destination.
                foreach (var match in staleMatches)
                {
                    reasons.Add($"A stale reference to child \"{match.ChildCadDocumentId}\"'s ORIGINAL (pre-copy) path " +
                        $"remains (\"{occurrence.ResolvedAbsolutePath}\") - the original source path must not remain referenced anywhere.");
                }
                continue;
            }

            reasons.Add($"An unexpected/unplanned component reference resolves to \"{occurrence.ResolvedAbsolutePath}\", " +
                "which is not part of the confirmed plan for this assembly.");
        }

        foreach (var target in facts.ReferenceResolutions)
        {
            if (!foundAtExpected.Contains(target.ChildCadDocumentId))
            {
                reasons.Add(target.ChildIsCopy
                    ? $"A reference to COPY child \"{target.ChildCadDocumentId}\" does not resolve to its copied destination (unexpected reference)."
                    : $"A reference to REUSE child \"{target.ChildCadDocumentId}\" does not resolve to its original document (unexpected source-project reference).");
            }
        }

        return new CopyDesignNodeVerificationResult(facts.CadDocumentId, reasons.Count == 0, reasons);
    }

    public static CopyDesignVerificationResult EvaluateAll(IReadOnlyList<CopyDesignNodeVerificationFacts> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var results = facts.Select(Evaluate).ToArray();
        return new CopyDesignVerificationResult(results.All(r => r.Passed), results);
    }

    private static void AddTarget(
        Dictionary<string, List<CopyDesignReferenceResolutionFact>> byPath, string? path, CopyDesignReferenceResolutionFact target)
    {
        if (path is null)
        {
            return;
        }
        if (!byPath.TryGetValue(path, out var list))
        {
            list = new List<CopyDesignReferenceResolutionFact>();
            byPath[path] = list;
        }
        list.Add(target);
    }

    private static bool PathsEqual(string? left, string? right)
    {
        var l = NormalizeOrNull(left);
        var r = NormalizeOrNull(right);
        return l is not null && r is not null && string.Equals(l, r, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }
        try { return Path.GetFullPath(path.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
