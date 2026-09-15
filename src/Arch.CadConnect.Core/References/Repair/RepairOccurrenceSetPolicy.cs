namespace Arch.CadConnect.Core.References;

/// <summary>
/// A COM-free, stable identity for ONE assembly component occurrence that is
/// bound to the authorized old file reference: its own reported <c>Name</c>
/// plus the resolved absolute path it currently reports.
///
/// Chosen over Inventor's <c>ComponentOccurrence.GetReferenceKey</c> (an
/// opaque byte blob documented primarily for tracking geometry/face proxies
/// across model rebuilds - its semantics for general occurrence-identity
/// comparison within one live COM session are not clearly specified) because
/// <c>Name</c> is Inventor's own well-documented, unique-within-one-assembly
/// occurrence identifier, and combined with the resolved path it is
/// sufficient to detect every drift this policy must catch: a prepared
/// occurrence disappearing, a new one appearing, or one moving to a different
/// file.
/// </summary>
public sealed record RepairOccurrenceIdentity(string Name, string ResolvedAbsolutePath);

/// <summary>The result of comparing a live occurrence set to the
///  prepared/authorized one immediately before a P5C occurrence mutation.</summary>
public enum RepairOccurrenceSetVerdict
{
    /// <summary>The live set is EXACTLY the prepared set - the mutation may proceed.</summary>
    Equal,

    /// <summary>The prepared set itself is empty - there is nothing to mutate.</summary>
    NoMatches,

    /// <summary>The live set differs from the prepared set (missing, extra,
    ///  duplicate, or drifted identity, or the live set could not be
    ///  established) - the mutation must be refused.</summary>
    Mismatch,
}

public sealed record RepairOccurrenceSetComparison(RepairOccurrenceSetVerdict Verdict, string Detail)
{
    public bool IsEqual => Verdict == RepairOccurrenceSetVerdict.Equal;
}

/// <summary>
/// Pure comparison proving a freshly re-established LIVE set of occurrence
/// identities is EXACTLY EQUAL to the already-prepared/authorized set - no
/// missing prepared occurrence, no new/extra occurrence, no duplicate
/// identity, no path drift.
///
/// WHY THIS EXISTS (Codex round-3 HIGH finding): P5C's assembly-component
/// mutation calls <c>ComponentOccurrence.Replace(target, ReplaceAll: true)</c>,
/// which - per Inventor's own semantics - replaces EVERY occurrence Inventor
/// itself currently considers bound to the referenced document, NOT merely
/// the occurrences this process previously recorded. If preparation had ever
/// silently excluded an occurrence it could not safely read (converting a
/// read failure into "doesn't match" instead of failing closed), or if a NEW
/// matching occurrence appeared after Prepare, ReplaceAll could mutate
/// occurrences P5C never authorized or verified. This policy is the final
/// gate, run immediately before the mutation call: it never authorizes a
/// different FILE reference (that identity - cadDocumentId + current
/// fileVersionId - was already established, unchanged, upstream); it only
/// proves the live occurrence set for the ALREADY-authorized reference has
/// not drifted since Prepare.
/// </summary>
public static class RepairOccurrenceSetPolicy
{
    public static RepairOccurrenceSetComparison VerifyEqual(
        IReadOnlyList<RepairOccurrenceIdentity>? prepared,
        IReadOnlyList<RepairOccurrenceIdentity>? live)
    {
        if (prepared is null || prepared.Count == 0)
        {
            return new RepairOccurrenceSetComparison(RepairOccurrenceSetVerdict.NoMatches,
                "The prepared occurrence set is empty - there is nothing to authorize a mutation for.");
        }

        if (HasDuplicate(prepared, out var duplicatePrepared))
        {
            return new RepairOccurrenceSetComparison(RepairOccurrenceSetVerdict.Mismatch,
                $"The prepared occurrence set reports a duplicate identity ({duplicatePrepared}) - refusing to guess.");
        }

        if (live is null)
        {
            return new RepairOccurrenceSetComparison(RepairOccurrenceSetVerdict.Mismatch,
                "The live assembly occurrence set could not be established immediately before mutation - "
                + "refusing to replace (fail closed).");
        }

        if (HasDuplicate(live, out var duplicateLive))
        {
            return new RepairOccurrenceSetComparison(RepairOccurrenceSetVerdict.Mismatch,
                $"The live occurrence set reports a duplicate identity ({duplicateLive}) - refusing to guess.");
        }

        var preparedSet = new HashSet<RepairOccurrenceIdentity>(prepared);
        var liveSet = new HashSet<RepairOccurrenceIdentity>(live);

        if (preparedSet.SetEquals(liveSet))
        {
            return new RepairOccurrenceSetComparison(RepairOccurrenceSetVerdict.Equal,
                $"The live occurrence set exactly matches the prepared set ({prepared.Count} occurrence(s)).");
        }

        var missing = preparedSet.Except(liveSet).ToArray();
        var extra = liveSet.Except(preparedSet).ToArray();
        var reasons = new List<string>();
        if (missing.Length > 0)
        {
            reasons.Add($"{missing.Length} prepared occurrence(s) are no longer present or no longer resolve to "
                + "the authorized old path.");
        }
        if (extra.Length > 0)
        {
            reasons.Add($"{extra.Length} occurrence(s) now resolve to the authorized old path that were NOT in "
                + "the prepared set.");
        }
        return new RepairOccurrenceSetComparison(RepairOccurrenceSetVerdict.Mismatch, string.Join(" ", reasons));
    }

    private static bool HasDuplicate(IReadOnlyList<RepairOccurrenceIdentity> items, out string duplicate)
    {
        var seen = new HashSet<RepairOccurrenceIdentity>();
        foreach (var item in items)
        {
            if (!seen.Add(item))
            {
                duplicate = $"{item.Name} @ {item.ResolvedAbsolutePath}";
                return true;
            }
        }
        duplicate = "";
        return false;
    }
}
