using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core;

/// <summary>
/// A COM-free snapshot of the active Inventor document, safe to pass into
/// Core / Api / tests. The Inventor project builds this from a live
/// <c>Inventor.Document</c> and then throws the COM object away - no Inventor
/// or COM type ever crosses this boundary.
/// </summary>
public sealed record CadDocumentContext
{
    /// <summary>The context used when no document is active.</summary>
    public static readonly CadDocumentContext None = new()
    {
        FullPath = null,
        FileName = null,
        DocumentType = CadDocumentType.Unknown,
        IsSaved = false,
        HasBeenSavedToDisk = false,
    };

    /// <summary>
    /// Absolute local path of the document, or null when the document has
    /// never been saved (Inventor reports an empty path for a brand-new,
    /// unsaved document).
    /// </summary>
    public required string? FullPath { get; init; }

    /// <summary>File name including extension, or null when unsaved.</summary>
    public required string? FileName { get; init; }

    public required CadDocumentType DocumentType { get; init; }

    /// <summary>
    /// True when the document has NO unsaved changes (Inventor's
    /// <c>Document.Dirty == false</c>). A dirty document must be saved before
    /// any PDM operation - the client never uploads unsaved in-memory state.
    /// </summary>
    public required bool IsSaved { get; init; }

    /// <summary>
    /// True once the document exists as a file on disk (has a real path).
    /// A never-saved document has no local identity at all.
    /// </summary>
    public required bool HasBeenSavedToDisk { get; init; }

    /// <summary>
    /// Server identity, if the client has learned it. Always null in P4A.
    /// The file name is NEVER used as identity - see <see cref="PlmIdentity"/>.
    /// </summary>
    public PlmIdentity? PlmIdentity { get; init; }

    /// <summary>
    /// P4C local checkout situation for this exact managed file, derived from
    /// the verified workspace-manifest binding (+ an optional live server
    /// status). <see cref="LocalCheckoutState.Unmanaged"/> when there is no
    /// manifest binding. Drives ribbon enablement only.
    /// </summary>
    public LocalCheckoutState CheckoutState { get; init; } = LocalCheckoutState.Unmanaged;

    /// <summary>The base-version snapshot recorded when THIS client checked the
    ///  file out (present only while <see cref="CheckoutState"/> ==
    ///  <see cref="LocalCheckoutState.CheckedOutByMe"/> via a local marker).</summary>
    public WorkspaceCheckoutBinding? CheckoutBinding { get; init; }

    /// <summary>The absolute workspace root the manifest binding was found
    ///  under (null when <see cref="PlmIdentity"/> is null).</summary>
    public string? WorkspaceRoot { get; init; }

    /// <summary>
    /// Best-known PLM status. Always <see cref="CadDocumentStatus.Unknown"/>
    /// in P4A (the server does not expose per-document status to the desktop
    /// client yet).
    /// </summary>
    public CadDocumentStatus Status { get; init; } = CadDocumentStatus.Unknown;

    /// <summary>True when a recognized CAD document is actually open.</summary>
    public bool HasDocument => DocumentType != CadDocumentType.Unknown || HasBeenSavedToDisk;

    /// <summary>
    /// True when this document is in a state where a (future) PDM operation
    /// could even be attempted: it is a recognized type, saved to disk, and
    /// has no unsaved edits. P4A uses this only to decide button enablement -
    /// the server remains the real authority.
    /// </summary>
    public bool IsEligibleForPdmOperation =>
        DocumentType != CadDocumentType.Unknown && HasBeenSavedToDisk && IsSaved;

    /// <summary>
    /// Build a context from already-extracted primitive values. Kept as the
    /// single construction path so the Inventor layer has one obvious place to
    /// map from COM, and tests have one obvious place to fake.
    /// </summary>
    public static CadDocumentContext Create(
        string? fullPath,
        string? displayNameFallback,
        bool isDirty)
    {
        var hasPath = !string.IsNullOrWhiteSpace(fullPath);
        var fileName = hasPath
            ? Path.GetFileName(fullPath!.Trim())
            : NormalizeFallbackName(displayNameFallback);

        return new CadDocumentContext
        {
            FullPath = hasPath ? fullPath!.Trim() : null,
            FileName = fileName,
            DocumentType = CadDocumentTypes.FromPath(hasPath ? fullPath : fileName),
            IsSaved = !isDirty,
            HasBeenSavedToDisk = hasPath,
        };
    }

    private static string? NormalizeFallbackName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
