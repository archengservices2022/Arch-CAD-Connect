namespace Arch.CadConnect.Core.CopyDesign;

/// <summary>
/// A PURE, deterministic rule that proposes a destination file name for one
/// source file name. No filesystem, no COM, no I/O of any kind - it is a pure
/// string transform so it is heavily unit-testable and so later phases can add
/// new rule kinds without touching the planner.
/// </summary>
public interface IDestinationNameRule
{
    /// <summary>
    /// The proposed destination file name for <paramref name="sourceFileName"/>,
    /// or <c>null</c> when this rule does not apply to it (the file name is
    /// left COMPLETELY UNCHANGED in that case - never partially transformed,
    /// never guessed).
    /// </summary>
    string? Rename(string sourceFileName);
}

/// <summary>
/// The ONLY rule P6A ships: an exact, case-sensitive, ordinal search/replace
/// of one literal token, e.g. <c>10073</c> -&gt; <c>10137</c> so
/// <c>10073-AS210.iam</c> previews as <c>10137-AS210.iam</c>. A file name that
/// does not contain <see cref="SourceToken"/> is left unchanged
/// (<see cref="Rename"/> returns <c>null</c>) - never modified "just in case".
/// </summary>
public sealed class TokenReplaceNameRule : IDestinationNameRule
{
    public string SourceToken { get; }
    public string DestinationToken { get; }

    /// <exception cref="ArgumentException">the source token is empty/blank,
    ///  the destination token is empty/blank, or source == destination
    ///  (a rule that would never change anything, or that P6A's own
    ///  requirements explicitly reject).</exception>
    public TokenReplaceNameRule(string sourceToken, string destinationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceToken))
        {
            throw new ArgumentException("The source token must not be empty.", nameof(sourceToken));
        }
        if (string.IsNullOrWhiteSpace(destinationToken))
        {
            throw new ArgumentException("The destination token must not be empty.", nameof(destinationToken));
        }
        if (string.Equals(sourceToken, destinationToken, StringComparison.Ordinal))
        {
            throw new ArgumentException("The source and destination tokens must not be identical.", nameof(destinationToken));
        }

        SourceToken = sourceToken;
        DestinationToken = destinationToken;
    }

    public string? Rename(string sourceFileName)
    {
        if (string.IsNullOrEmpty(sourceFileName)
            || !sourceFileName.Contains(SourceToken, StringComparison.Ordinal))
        {
            return null;
        }
        return sourceFileName.Replace(SourceToken, DestinationToken, StringComparison.Ordinal);
    }
}
