namespace Arch.CadConnect.Core.Session;

/// <summary>
/// The signed-in identity, exactly as the SERVER derived it. The client
/// displays these values but never trusts a locally-held copy for any
/// authorization decision - every request is re-authorized server-side.
/// In particular <see cref="OrganizationId"/> is display-only; the client
/// never sends an organization id back to the server.
/// </summary>
public sealed record ArchIdentity(
    string UserId,
    string Name,
    string Email,
    string Role,
    string OrganizationId,
    string OrganizationCode,
    string OrganizationName);
