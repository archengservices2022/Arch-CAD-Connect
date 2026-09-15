namespace Arch.CadConnect.Core.References;

/// <summary>
/// The instruction to replace EXACTLY ONE reference edge of one document with a
/// new target file. Absolute paths only. Produced from an
/// <see cref="ReferenceRepairEligibility.Eligible"/> <see cref="ReferenceRepairPlan"/>.
/// </summary>
public sealed record ReferenceReplaceCommand(
    string ReferencingDocumentAbsolutePath,
    string CurrentReferenceResolvedPath,
    string TargetAbsolutePath,
    string ExpectedCadDocumentId,
    string ExpectedCurrentFileVersionId);

/// <summary>The result of asking an <see cref="IReferenceReplacer"/> to perform
///  one replacement. <see cref="ApiSucceeded"/> means the Inventor API call
///  returned without error - it is NOT proof the repair worked (that needs a
///  fresh scan; see <see cref="ReferenceRepairVerifier"/>).</summary>
public sealed record ReferenceReplaceResult(
    bool ApiSucceeded,
    string Detail,
    /// <summary>True once ReplaceReference was actually called. A failure after
    /// this boundary may still have changed Inventor state.</summary>
    bool MutationInvoked = false);

/// <summary>
/// The COM boundary for a P5C reference replacement, split into TWO phases so
/// that ALL expensive discovery happens BEFORE the final authoritative checkout
/// read - not between it and the COM mutation:
///
///  * <see cref="Prepare"/> does every non-trivial step now: resolve the loaded
///    parent document, enumerate its reference descriptors, select EXACTLY ONE
///    by resolved path + verified stable Arch identity, and validate the
///    relationship. It returns an <see cref="IPreparedReferenceReplacement"/>
///    bound to that exact selected descriptor - or a not-ready result.
///  * <see cref="IPreparedReferenceReplacement.Execute"/> is the MINIMAL final
///    step: an immediate COM-validity check on the ALREADY SELECTED descriptor
///    (it can never select or act on a different one) followed by the single
///    <c>ReferencedFileDescriptor.ReplaceReference</c> call. No re-enumeration,
///    no loaded-document search, no manifest reload.
///
/// An implementation touches no other reference and NEVER saves, checks in,
/// checks out, or otherwise changes PLM state. Zero or more than one matching
/// descriptor -&gt; not ready, no change.
/// </summary>
public interface IReferenceReplacer
{
    IPreparedReferenceReplacement Prepare(ReferenceReplaceCommand command);
}

/// <summary>
/// A P5C replacement whose target descriptor has ALREADY been discovered,
/// selected, and validated. It is bound to that ONE descriptor identity;
/// <see cref="Execute"/> can never rediscover or substitute a different one.
/// </summary>
public interface IPreparedReferenceReplacement
{
    /// <summary>True when a single valid descriptor was selected and the
    ///  replacement is ready to invoke.</summary>
    bool Ready { get; }

    /// <summary>Why it is / is not ready (never a secret).</summary>
    string Detail { get; }

    /// <summary>
    /// The MINIMAL final step, invoked by the coordinator WHILE the protected
    /// target lease is held and AFTER the final authoritative checkout read: a
    /// quick COM-validity re-check on the already-selected descriptor, then the
    /// one <c>ReplaceReference</c> call. Never enumerates, searches, or reloads
    /// anything; never authorizes a different descriptor.
    /// </summary>
    ReferenceReplaceResult Execute();
}

/// <summary>A COM-free snapshot of one of a document's reference descriptors,
///  used to pick the single descriptor to replace without any COM in the
///  decision.</summary>
public sealed record ReferenceDescriptorSnapshot(
    int Index,
    string? ResolvedAbsolutePath,
    string ReportedName,
    string? CadDocumentId = null,
    string? FileVersionId = null,
    bool IdentityVerified = false);

public enum ReferenceReplaceTargetingOutcome { Matched, NoMatch, Ambiguous }

public sealed record ReferenceReplaceTargeting(ReferenceReplaceTargetingOutcome Outcome, int Index, string Detail);

/// <summary>
/// Pure selection of the ONE descriptor a replacement must act on: the
/// descriptor whose currently resolved absolute path equals the plan's current
/// reference path (case-insensitive, normalised). Zero matches -&gt; NoMatch;
/// more than one -&gt; Ambiguous. Never falls back to a name match.
/// </summary>
public static class ReferenceReplaceTargetingResolver
{
    public static ReferenceReplaceTargeting Resolve(
        IReadOnlyList<ReferenceDescriptorSnapshot>? descriptors,
        string currentReferenceResolvedPath,
        string expectedCadDocumentId,
        string expectedCurrentFileVersionId)
    {
        var wanted = Normalize(currentReferenceResolvedPath);
        if (wanted is null)
        {
            return new ReferenceReplaceTargeting(ReferenceReplaceTargetingOutcome.NoMatch, -1,
                "The current reference path is not a usable absolute path.");
        }
        if (string.IsNullOrWhiteSpace(expectedCadDocumentId)
            || string.IsNullOrWhiteSpace(expectedCurrentFileVersionId))
        {
            return new ReferenceReplaceTargeting(ReferenceReplaceTargetingOutcome.NoMatch, -1,
                "The selected reference does not carry a complete stable Arch identity.");
        }

        var matches = (descriptors ?? Array.Empty<ReferenceDescriptorSnapshot>())
            .Where(d => Normalize(d.ResolvedAbsolutePath) is { } p
                && string.Equals(p, wanted, StringComparison.OrdinalIgnoreCase))
            .Where(d => d.IdentityVerified
                && string.Equals(d.CadDocumentId, expectedCadDocumentId, StringComparison.Ordinal)
                && string.Equals(d.FileVersionId, expectedCurrentFileVersionId, StringComparison.Ordinal))
            .ToArray();

        return matches.Length switch
        {
            1 => new ReferenceReplaceTargeting(ReferenceReplaceTargetingOutcome.Matched, matches[0].Index,
                "Exactly one descriptor matches the path and verified stable Arch identity."),
            0 => new ReferenceReplaceTargeting(ReferenceReplaceTargetingOutcome.NoMatch, -1,
                "No reference descriptor currently resolves to the reference being repaired - it may have changed."),
            _ => new ReferenceReplaceTargeting(ReferenceReplaceTargetingOutcome.Ambiguous, -1,
                $"{matches.Length} reference descriptors resolve to the same path - refusing to guess which to replace."),
        };
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }
        try { return Path.GetFullPath(path.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
