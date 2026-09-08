namespace Arch.CadConnect.Api.References;

/// <summary>
/// The response contract the (P5B-B) authoritative latest-version endpoint
/// must stamp on its body. The version is embedded in the identifier
/// (<c>.v1</c>); there is no separate version field. A response whose
/// <c>contract</c> is missing or anything else is refused before its identity
/// is trusted (the whole lookup then fails closed to UNKNOWN VERSION).
///
/// See <see cref="HttpLatestVersionProbe"/> for the exact request / response
/// shape the CAD Connect client requires from the Arch PLM server.
/// </summary>
public static class LatestVersionContract
{
    public const string Id = "arch-plm.desktop-latest-versions.v1";

    public static bool IsSupported(string? contract) =>
        string.Equals(contract, Id, StringComparison.Ordinal);
}
