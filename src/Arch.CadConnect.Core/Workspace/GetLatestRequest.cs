namespace Arch.CadConnect.Core.Workspace;

/// <summary>
/// How the user selected the ROOT document for a Get Latest. This is an
/// EXPLICIT bootstrap selection - a document number the user typed/confirmed,
/// or a stable id carried from a verified workspace manifest. It is NEVER
/// inferred from the open file's name or path.
/// </summary>
public sealed record CadDocumentLookup
{
    public enum LookupKind { ByDocumentNumber, ByCadDocumentId }

    public required LookupKind Kind { get; init; }
    public required string Value { get; init; }

    public static CadDocumentLookup ByNumber(string documentNumber) => new()
    {
        Kind = LookupKind.ByDocumentNumber,
        Value = Require(documentNumber, nameof(documentNumber)),
    };

    public static CadDocumentLookup ById(string cadDocumentId) => new()
    {
        Kind = LookupKind.ByCadDocumentId,
        Value = Require(cadDocumentId, nameof(cadDocumentId)),
    };

    /// <summary>The query-string parameter name for this lookup.</summary>
    public string QueryParameter =>
        Kind == LookupKind.ByDocumentNumber ? "documentNumber" : "cadDocumentId";

    private static string Require(string value, string name)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("A lookup value is required.", name);
        }
        return trimmed;
    }
}

/// <summary>The authoritative identity + display fields the server returned
///  for a <see cref="CadDocumentLookup"/>. After this, <see cref="CadDocumentId"/>
///  is authoritative.</summary>
public sealed record ResolvedCadDocument(
    string CadDocumentId,
    string DocumentNumber,
    string FileName,
    string CadType);

/// <summary>
/// The contract the desktop CAD-document resolve endpoint
/// (<c>GET /api/desktop/cad-documents/resolve</c>) stamps on its response
/// body. Mirrors <c>DESKTOP_CAD_DOC_CONTRACT</c> in
/// <c>web/app/lib/desktop-cad-documents-core.ts</c> - the version is embedded
/// in the identifier (<c>.v1</c>), there is no separate version field. A
/// resolve response whose <c>contract</c> is missing or anything else is
/// refused before its identity is trusted.
/// </summary>
public static class DesktopCadDocumentContract
{
    public const string Id = "arch-plm.desktop-cad-document.v1";

    public static bool IsSupported(string? contract) =>
        string.Equals(contract, Id, StringComparison.Ordinal);
}

/// <summary>One Get Latest: which root, into which local workspace.</summary>
public sealed record GetLatestRequest
{
    public required CadDocumentLookup Root { get; init; }

    /// <summary>Caller-supplied, TRUSTED, absolute workspace directory. Never
    ///  derived from the plan or any server response.</summary>
    public required string WorkspaceRoot { get; init; }
}
