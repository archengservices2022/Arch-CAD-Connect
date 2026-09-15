using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.References;

/// <summary>
/// Pure decision of whether the document that owns a reference may be modified
/// + saved under EXISTING Arch rules, so a reference replacement (which dirties
/// it in memory) is allowed. No I/O, no COM.
///
///   CheckedOutByMe                        -> writable
///   Controlled / CheckedOutByOther /
///     Unverified                          -> NOT writable (do not auto-checkout)
///   Unmanaged                             -> writable ONLY if the on-disk file
///                                             is not read-only (never make a
///                                             writable copy to bypass a guard)
/// </summary>
public static class ReferenceRepairWritability
{
    /// <summary>
    /// True ONLY when the authenticated server reports an authoritative "mine"
    /// checkout that is COMPLETE and matches the verified local checkout binding
    /// EXACTLY. Fail closed:
    ///  * the manifest entry must be Verified and carry a local checkout marker;
    ///  * the local marker's checkout id and base fileVersionId must be non-blank,
    ///    and the base fileVersionId must equal the entry's pinned fileVersionId;
    ///  * the SERVER status must be <see cref="ServerCheckoutState.Mine"/> with a
    ///    NON-BLANK checkout id AND a NON-BLANK base fileVersionId - a partial
    ///    "mine" is never sufficient;
    ///  * the server checkout id and base fileVersionId must equal the local
    ///    marker's exactly (ordinal).
    /// </summary>
    public static bool AuthoritativeCheckoutMatches(
        WorkspaceManifestEntry? entry, ServerCheckoutStatus? status)
    {
        if (entry is not { State: WorkspaceManifestEntryState.Verified, Checkout: { } local }
            || status is not { State: ServerCheckoutState.Mine })
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(local.CheckoutId)
            || string.IsNullOrWhiteSpace(local.BaseFileVersionId)
            || !string.Equals(local.BaseFileVersionId, entry.FileVersionId, StringComparison.Ordinal))
        {
            return false;
        }

        // A server "mine" MUST be complete - never treat a partial "mine" as
        // authoritative - and must match the verified local binding exactly.
        if (string.IsNullOrWhiteSpace(status.CheckoutId)
            || string.IsNullOrWhiteSpace(status.BaseFileVersionId))
        {
            return false;
        }
        if (!string.Equals(status.CheckoutId, local.CheckoutId, StringComparison.Ordinal))
        {
            return false;
        }
        return string.Equals(status.BaseFileVersionId, local.BaseFileVersionId, StringComparison.Ordinal);
    }

    public static RepairReferencingContext Evaluate(
        string documentAbsolutePath,
        LocalCheckoutState checkoutState,
        bool onDiskWritable,
        bool authoritativeCheckoutConfirmed = false)
    {
        var path = (documentAbsolutePath ?? "").Trim();

        return checkoutState switch
        {
            LocalCheckoutState.CheckedOutByMe when authoritativeCheckoutConfirmed && onDiskWritable =>
                new RepairReferencingContext(path, true,
                    "The server confirms this checkout is yours and the referencing file is writable."),

            LocalCheckoutState.CheckedOutByMe when !authoritativeCheckoutConfirmed =>
                new RepairReferencingContext(path, false,
                    "The local checkout marker is not sufficient; the server did not confirm that this checkout is yours."),

            LocalCheckoutState.CheckedOutByMe =>
                new RepairReferencingContext(path, false,
                    "The server confirms this checkout is yours, but the referencing file is read-only on disk."),

            LocalCheckoutState.Controlled =>
                new RepairReferencingContext(path, false,
                    "The referencing document is a controlled managed file and is NOT checked out. "
                    + "Check it out first - P5C never checks out automatically."),

            LocalCheckoutState.CheckedOutByOther =>
                new RepairReferencingContext(path, false,
                    "The referencing document is checked out by another user - it cannot be modified here."),

            LocalCheckoutState.Unverified =>
                new RepairReferencingContext(path, false,
                    "The referencing document's local copy is not verified against the server. "
                    + "Run Get Latest, then check it out, before repairing its references."),

            _ => onDiskWritable
                ? new RepairReferencingContext(path, true,
                    "The referencing document is not a managed file and its local copy is writable.")
                : new RepairReferencingContext(path, false,
                    "The referencing document's local file is read-only. Make it writable through the "
                    + "normal Arch / Vault workflow - P5C never clears read-only protection or copies around it."),
        };
    }
}
