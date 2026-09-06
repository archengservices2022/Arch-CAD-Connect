using System.Text.Json.Serialization;

namespace Arch.CadConnect.Api.Dtos;

// Wire contract with web/app/api/desktop/*. Kept intentionally small; property
// names match the server's JSON exactly (see app/lib/desktop-auth-core.ts).

public sealed record LoginRequestDto(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("clientLabel")] string? ClientLabel);

public sealed record OrganizationDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name);

public sealed record IdentityDto(
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("organization")] OrganizationDto Organization);

public sealed record LoginResponseDto(
    [property: JsonPropertyName("contract")] string Contract,
    [property: JsonPropertyName("tokenType")] string TokenType,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("identity")] IdentityDto Identity);

public sealed record SessionTimingDto(
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("lastUsedAt")] DateTimeOffset LastUsedAt);

public sealed record ServerDto(
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("time")] DateTimeOffset Time);

public sealed record SessionResponseDto(
    [property: JsonPropertyName("contract")] string Contract,
    [property: JsonPropertyName("identity")] IdentityDto Identity,
    [property: JsonPropertyName("session")] SessionTimingDto Session,
    [property: JsonPropertyName("server")] ServerDto Server);

public sealed record LogoutResponseDto(
    [property: JsonPropertyName("revoked")] bool Revoked);

public sealed record ApiErrorDto(
    [property: JsonPropertyName("error")] string? Error);
