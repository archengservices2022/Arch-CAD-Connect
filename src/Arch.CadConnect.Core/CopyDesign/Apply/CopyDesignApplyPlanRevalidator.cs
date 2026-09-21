namespace Arch.CadConnect.Core.CopyDesign.Apply;

public sealed record CopyDesignRevalidationResult(bool Success, string? FailureReason)
{
    public static readonly CopyDesignRevalidationResult Ok = new(true, null);
    public static CopyDesignRevalidationResult Fail(string reason) => new(false, reason);
}

/// <summary>
/// P6C: re-checks safety-sensitive facts about a CONFIRMED, already-mapped
/// apply request IMMEDIATELY before physical mutation begins - because the
/// world may have changed since the P6A preview was shown (a file moved, a
/// destination appeared, another process wrote something). This NEVER
/// rediscovers or re-derives the plan itself (that authority stays with the
/// confirmed <see cref="CopyDesignPlan"/> P6A produced) - it only re-proves
/// the small set of facts that must still hold true for THIS exact request
/// to be safe to execute.
///
/// FAIL-CLOSED DISCIPLINE (the entire point of this class): every probe
/// delegate is invoked inside a try/catch, and a THROW is treated EXACTLY
/// like the worst-case answer for that check (source missing; destination
/// present) - never optimistically ignored, never retried, never treated as
/// "assume it's fine". This deliberately does NOT reuse P6A preview's own
/// <c>destinationExists</c> predicate, which is documented as fail-OPEN
/// (best-effort, "a false negative does not make the plan mean more than
/// 'no collision detected'") - that is correct for a non-mutating preview,
/// but is the OPPOSITE of what apply-time revalidation requires.
///
/// PURE with respect to the probes themselves (which are injected) - this
/// class does no I/O or COM of its own.
/// </summary>
public static class CopyDesignApplyPlanRevalidator
{
    public static CopyDesignRevalidationResult Revalidate(
        CopyDesignApplyRequest request,
        Func<string, bool> sourceExists,
        Func<string, bool> destinationExists)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sourceExists);
        ArgumentNullException.ThrowIfNull(destinationExists);

        var copyEntries = request.Entries.Where(e => e.Entry.Action == CopyDesignApplyEntryAction.Copy).ToArray();

        // Duplicate destination within THIS request - reject before touching
        // any probe at all (a purely structural check).
        var destinationGroups = copyEntries
            .GroupBy(e => e.Node.ProposedDestinationAbsolutePath, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToArray();
        if (destinationGroups.Length > 0)
        {
            return CopyDesignRevalidationResult.Fail(
                $"Two or more entries propose the same destination \"{destinationGroups[0].Key}\" - refusing to execute.");
        }

        foreach (var entry in copyEntries)
        {
            var source = entry.Node.SourceAbsolutePath;
            var destination = entry.Node.ProposedDestinationAbsolutePath!;

            if (string.Equals(
                    NormalizeOrNull(source), NormalizeOrNull(destination), StringComparison.OrdinalIgnoreCase))
            {
                return CopyDesignRevalidationResult.Fail(
                    $"The proposed destination for \"{entry.Node.SourceFileName}\" is identical to its source path - refusing to execute.");
            }

            bool sourceStillExists;
            try
            {
                sourceStillExists = sourceExists(source);
            }
            catch
            {
                // FAIL CLOSED: an existence check that throws means the
                // state is uncertain, never "assume it exists".
                return CopyDesignRevalidationResult.Fail(
                    $"Could not verify that the source \"{entry.Node.SourceFileName}\" still exists (the check itself failed) - refusing to execute.");
            }
            if (!sourceStillExists)
            {
                return CopyDesignRevalidationResult.Fail(
                    $"The source \"{entry.Node.SourceFileName}\" no longer exists at its expected path - refusing to execute.");
            }

            bool destinationAlreadyExists;
            try
            {
                destinationAlreadyExists = destinationExists(destination);
            }
            catch
            {
                // FAIL CLOSED: never "assume the destination does not exist".
                return CopyDesignRevalidationResult.Fail(
                    $"Could not verify that the destination for \"{entry.Node.SourceFileName}\" is free (the check itself failed) - refusing to execute.");
            }
            if (destinationAlreadyExists)
            {
                return CopyDesignRevalidationResult.Fail(
                    $"The destination for \"{entry.Node.SourceFileName}\" already exists - refusing to overwrite.");
            }
        }

        return CopyDesignRevalidationResult.Ok;
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
