using Arch.CadConnect.Api.Dtos;
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
/// P4C: the write operations below are declared but every implementation
/// throws <see cref="ArchApiException"/> with
/// <see cref="ArchApiFailureKind.NotImplemented"/> - the client never fakes a
/// successful PDM result.
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

    // ---- PDM write operations (declared for P4C; not implemented yet) ----

    Task<PlmDocumentStatusDto> GetDocumentStatusAsync(IArchSession session, string cadDocumentId, CancellationToken ct = default);

    Task CheckoutAsync(IArchSession session, string cadDocumentId, CancellationToken ct = default);

    Task CheckInAsync(IArchSession session, string cadDocumentId, string localFilePath, CancellationToken ct = default);

    Task UndoCheckoutAsync(IArchSession session, string cadDocumentId, CancellationToken ct = default);

    Task<WhereUsedDto> GetWhereUsedAsync(IArchSession session, string cadDocumentId, CancellationToken ct = default);

    Task<ReleaseInfoDto> GetReleaseInfoAsync(IArchSession session, string itemRevisionId, CancellationToken ct = default);
}

// Placeholder DTOs for the not-yet-implemented operations. Shaped now so the
// interface is stable; filled in when the operations land.
public sealed record PlmDocumentStatusDto(string CadDocumentId, string Status);
public sealed record WhereUsedDto(IReadOnlyList<string> ParentDocumentIds);
public sealed record ReleaseInfoDto(string ItemRevisionId, string Lifecycle);
