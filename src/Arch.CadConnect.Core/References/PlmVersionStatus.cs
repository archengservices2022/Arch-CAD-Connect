namespace Arch.CadConnect.Core.References;

/// <summary>
/// The AUTHORITATIVE Arch PLM version status of one managed local CAD
/// reference (P5B-B). This is an ADDITIONAL dimension layered on top of the
/// P5B-A local reference health - it never replaces or weakens
/// <see cref="ReferenceHealth"/>, <see cref="ReferenceManagement"/>, or any
/// other P5A / P5B-A fact.
///
/// AUTHORITATIVE IDENTITY RULE: <see cref="Current"/> / <see cref="Stale"/>
/// are established ONLY by comparing
///   - the exact stable <c>cadDocumentId</c>, and
///   - the pinned local <c>fileVersionId</c> (from the verified workspace
///     manifest), against
///   - the authenticated authoritative Arch PLM server's latest
///     <c>fileVersionId</c> for that same <c>cadDocumentId</c>.
/// It is NEVER derived from a filename, a display name, a path, a file
/// timestamp, a local modification time, a version number alone, or the local
/// workspace manifest alone.
///
/// FAIL CLOSED: any doubt maps to <see cref="UnknownVersion"/>. CURRENT is
/// never guessed.
/// </summary>
public enum PlmVersionStatus
{
    /// <summary>Version status could not be established safely or
    ///  authoritatively - the fail-closed default.</summary>
    UnknownVersion = 0,

    /// <summary>The authenticated authoritative server reports the pinned
    ///  local fileVersionId as the latest version for that exact
    ///  cadDocumentId.</summary>
    Current = 1,

    /// <summary>The authenticated authoritative server reports a DIFFERENT
    ///  latest fileVersionId for that exact cadDocumentId - the local
    ///  reference is out of date.</summary>
    Stale = 2,
}
