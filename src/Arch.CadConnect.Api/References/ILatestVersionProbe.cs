using Arch.CadConnect.Core.References;
using Arch.CadConnect.Core.Session;

namespace Arch.CadConnect.Api.References;

/// <summary>
/// Retrieves the AUTHORITATIVE latest FileVersion identity for one or more
/// stable <c>cadDocumentId</c>s from the authenticated Arch PLM server.
///
/// READ-ONLY and FAIL-CLOSED: an unreachable server, a rejected session, an
/// unknown id, an unsupported endpoint, or a malformed body is never thrown -
/// it is a (per-id or whole-lookup) outcome in the returned
/// <see cref="LatestVersionLookup"/>, so P5B-B version classification degrades
/// safely to UNKNOWN VERSION.
/// </summary>
public interface ILatestVersionProbe
{
    Task<LatestVersionLookup> LookupAsync(
        IArchSession session,
        IReadOnlyCollection<string> cadDocumentIds,
        CancellationToken ct = default);
}
