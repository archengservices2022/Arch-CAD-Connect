namespace Arch.CadConnect.Core;

/// <summary>
/// PLM status of the active CAD document, as far as the client can currently
/// tell. P4A does not compute most of these - the server side does not yet
/// expose per-document status to the desktop client - so the honest P4A value
/// for any managed document is <see cref="Unknown"/> and for anything not
/// found on the server is <see cref="Unmanaged"/>.
///
/// The client MUST NOT invent server state. A status is only ever as good as
/// what the server actually told it.
/// </summary>
public enum CadDocumentStatus
{
    /// <summary>
    /// The client cannot determine status yet (no server call made, server
    /// does not expose it, or the call failed). Distinct from
    /// <see cref="Unmanaged"/>: "we do not know" is not "it is not in PLM".
    /// </summary>
    Unknown = 0,

    /// <summary>Not tracked by Arch PLM (no server document identity).</summary>
    Unmanaged,

    /// <summary>Local copy matches the latest server version.</summary>
    Current,

    /// <summary>A newer server version exists than the local copy.</summary>
    OutOfDate,

    /// <summary>Checked out to the signed-in user on this server.</summary>
    CheckedOutByMe,

    /// <summary>Checked out to a different user.</summary>
    CheckedOutByOther,

    /// <summary>The document's revision is in a RELEASED lifecycle state.</summary>
    Released,

    /// <summary>
    /// The local file differs from the server version AND is not a checkout the
    /// user holds - a conflict that must be resolved before check-in.
    /// </summary>
    LocalConflict,
}

/// <summary>
/// A status value plus the provenance of that value, so the UI can distinguish
/// "server says CURRENT" from "we defaulted to UNKNOWN".
/// </summary>
public sealed record CadDocumentStatusResult(
    CadDocumentStatus Status,
    bool FromServer,
    string? Detail = null)
{
    public static CadDocumentStatusResult NotDetermined(string? detail = null) =>
        new(CadDocumentStatus.Unknown, FromServer: false, detail ?? "Status not available in this milestone.");
}
