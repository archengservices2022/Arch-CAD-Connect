using Arch.CadConnect.Api.Dtos;
using Arch.CadConnect.Api.Workspace;
using Arch.CadConnect.Core.References;
using Arch.CadConnect.Core.Session;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Api;

/// <summary>
/// The typed boundary between the Inventor add-in and the Arch PLM server.
/// Every server interaction the add-in will ever need is declared here so the
/// rest of the client codes against one seam.
///
/// P4A IMPLEMENTS: <see cref="SignInAsync"/>, <see cref="SignOutAsync"/>,
/// <see cref="GetSessionAsync"/> (the desktop-auth endpoints).
///
/// P4B IMPLEMENTS: <see cref="ResolveCadDocumentAsync"/>,
/// <see cref="GetLatestAsync"/> (real, via the existing P3B workspace-plan +
/// content endpoints, bearer-authenticated).
///
/// P4C IMPLEMENTS: <see cref="CheckoutAsync"/>, <see cref="CheckInAsync"/>,
/// <see cref="UndoCheckoutAsync"/> (real, via the existing P3C checkout /
/// check-in / undo endpoints, bearer-authenticated). Each acts on an EXACT
/// manifest-bound local file; the entry's cadDocumentId is authoritative.
///
/// Still declared-not-implemented (future scope): document status / version /
/// revision / where-used.
/// </summary>
public interface IArchApi
{
    ArchServerUri Server { get; }

    // ---- Authentication (P4A) --------------------------------------------

    /// <summary>
    /// Exchange credentials for a bearer session. The password is used once,
    /// for this request body, and never stored or logged.
    /// </summary>
    Task<IArchSession> SignInAsync(
        string email,
        string password,
        string? clientLabel,
        CancellationToken ct = default);

    /// <summary>Revoke <paramref name="session"/> server-side. Never throws for an already-dead session.</summary>
    Task SignOutAsync(IArchSession session, CancellationToken ct = default);

    /// <summary>
    /// Validate a session and fetch current identity + server clock. Throws
    /// <see cref="ArchApiException"/> (<see cref="ArchApiFailureKind.Unauthorized"/>)
    /// if the session is no longer valid.
    /// </summary>
    Task<SessionResponseDto> GetSessionAsync(IArchSession session, CancellationToken ct = default);

    // ---- Get Latest (P4B) ----------------------------------------------

    /// <summary>
    /// Resolve an explicit selection (a tenant document number, or a stable
    /// id from a verified workspace manifest) to its authoritative
    /// <see cref="ResolvedCadDocument"/>. Throws
    /// <see cref="ArchApiException"/> (<see cref="ArchApiFailureKind.NotFound"/>)
    /// when nothing matches in the caller's organization.
    /// </summary>
    Task<ResolvedCadDocument> ResolveCadDocumentAsync(IArchSession session, CadDocumentLookup lookup, CancellationToken ct = default);

    /// <summary>
    /// Perform a real Get Latest: resolve -> fetch the version-pinned
    /// workspace plan -> download the exact FileVersions (size + SHA-256
    /// verified) -> materialize a safe managed local workspace -> update the
    /// workspace manifest. Never overwrites an existing user file; an unsafe
    /// plan is refused wholesale (<see cref="Workspace.UnsafeWorkspacePlanException"/>).
    /// Per-entry outcomes are in the returned report, never thrown.
    /// </summary>
    Task<MaterializationReport> GetLatestAsync(IArchSession session, GetLatestRequest request, CancellationToken ct = default);

    // ---- PDM checkout / check-in / undo (P4C) --------------------------

    /// <summary>
    /// Take an exclusive server checkout of the managed file at
    /// <paramref name="file"/> and make its local copy writable. The file must
    /// be bound in the verified workspace manifest and Verified. Throws
    /// <see cref="Workspace.CheckoutConflictException"/> when another user holds
    /// it, <see cref="Workspace.NotManagedException"/> /
    /// <see cref="Workspace.NotVerifiedException"/> for an ineligible target.
    /// If the server succeeds but the local update partially fails, the result
    /// carries <c>ReconcileNeeded</c> - it NEVER reports the checkout as failed.
    /// </summary>
    Task<CheckoutOperationResult> CheckoutAsync(IArchSession session, ManagedFileRef file, CancellationToken ct = default);

    /// <summary>
    /// Upload the saved local file as the next immutable FileVersion and
    /// release the checkout, then rebind the workspace manifest and set the
    /// file read-only. Creates a FileVersion ONLY - never an Engineering
    /// Revision. A pre-201 failure leaves the checkout and the writable file
    /// untouched; a 201 whose bytes cannot be verified returns
    /// <c>Verified == false</c> (manifest rebound as Unverified).
    /// </summary>
    Task<CheckInOperationResult> CheckInAsync(IArchSession session, ManagedFileRef file, CancellationToken ct = default);

    /// <summary>
    /// Release the checkout and restore the working file to the exact base
    /// FileVersion (discarding local changes). The base version is downloaded
    /// and fully verified WHILE the checkout is still held - a bad download
    /// throws and the checkout is NEVER released.
    /// </summary>
    Task<UndoOperationResult> UndoCheckoutAsync(IArchSession session, ManagedFileRef file, string? reason, CancellationToken ct = default);

    // ---- Authoritative version intelligence (P5B-B) ------------------

    /// <summary>
    /// Fetch the AUTHORITATIVE latest FileVersion identity for each of
    /// <paramref name="cadDocumentIds"/> (stable server ids only). READ-ONLY -
    /// no file bytes, no mutation, no checkout.
    ///
    /// Never throws for an unreachable server, a rejected session, an unknown
    /// id, an unsupported endpoint, or a malformed body - those are per-id (or
    /// whole-lookup) outcomes in the returned <see cref="LatestVersionLookup"/>
    /// so P5B-B version classification fails closed to UNKNOWN VERSION.
    /// </summary>
    Task<LatestVersionLookup> GetLatestVersionsAsync(
        IArchSession session, IReadOnlyCollection<string> cadDocumentIds, CancellationToken ct = default);

    // ---- future scope (declared, not implemented) ---------------------

    Task<PlmDocumentStatusDto> GetDocumentStatusAsync(IArchSession session, string cadDocumentId, CancellationToken ct = default);

    Task<WhereUsedDto> GetWhereUsedAsync(IArchSession session, string cadDocumentId, CancellationToken ct = default);

    Task<ReleaseInfoDto> GetReleaseInfoAsync(IArchSession session, string itemRevisionId, CancellationToken ct = default);
}

// Placeholder DTOs for the not-yet-implemented operations. Shaped now so the
// interface is stable; filled in when the operations land.
public sealed record PlmDocumentStatusDto(string CadDocumentId, string Status);
public sealed record WhereUsedDto(IReadOnlyList<string> ParentDocumentIds);
public sealed record ReleaseInfoDto(string ItemRevisionId, string Lifecycle);
