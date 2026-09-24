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

    /// <summary>
    /// The fixed set of managed CAD document types a Get Latest bootstrap
    /// lookup may disambiguate with (P6D: a documentNumber is no longer
    /// unique by itself - a model and its drawing may share one engineering
    /// number). This is the SINGLE SOURCE OF TRUTH for "supported types" -
    /// the Get Latest dialog's type selector is populated from this exact
    /// list, so the UI and this validation can never drift apart. Mirrors
    /// the server's `CAD_DOCUMENT_TYPES` minus the non-managed `PDF`/`OTHER`
    /// values, which are never a valid Get Latest root.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedDocumentTypes = new[] { "IAM", "IPT", "IDW", "DWG" };

    public required LookupKind Kind { get; init; }
    public required string Value { get; init; }

    /// <summary>
    /// Only meaningful when <see cref="Kind"/> is
    /// <see cref="LookupKind.ByDocumentNumber"/> (enforced by
    /// <see cref="ByNumber"/> being the only way to set it - <see cref="ById"/>
    /// has no parameter for it, so a cadDocumentId lookup can never carry
    /// one). Disambiguates a documentNumber that legitimately matches more
    /// than one document. Null means the caller did not supply one - the
    /// server then fails closed (409) if more than one document shares the
    /// number, rather than silently picking one.
    /// </summary>
    public string? DocumentType { get; private init; }

    public static CadDocumentLookup ByNumber(string documentNumber, string? documentType = null) => new()
    {
        Kind = LookupKind.ByDocumentNumber,
        Value = Require(documentNumber, nameof(documentNumber)),
        DocumentType = NormalizeDocumentType(documentType),
    };

    public static CadDocumentLookup ById(string cadDocumentId) => new()
    {
        Kind = LookupKind.ByCadDocumentId,
        Value = Require(cadDocumentId, nameof(cadDocumentId)),
    };

    /// <summary>
    /// Builds the exact, ordered `(name, value)` query parameters for this
    /// lookup (values NOT yet URI-escaped). This is the ONE place that
    /// decides which value goes under which parameter name, so a caller
    /// building the HTTP request can never transpose documentNumber and
    /// documentType into the wrong query parameter - it just iterates
    /// whatever this returns.
    /// </summary>
    public IReadOnlyList<(string Name, string Value)> BuildQueryParameters()
    {
        if (Kind == LookupKind.ByCadDocumentId)
        {
            return new[] { ("cadDocumentId", Value) };
        }

        if (DocumentType is null)
        {
            return new[] { ("documentNumber", Value) };
        }
        return new[] { ("documentNumber", Value), ("documentType", DocumentType) };
    }

    private static string? NormalizeDocumentType(string? documentType)
    {
        if (documentType is null)
        {
            return null;
        }
        var trimmed = documentType.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("documentType must not be blank when provided.", nameof(documentType));
        }
        if (!SupportedDocumentTypes.Contains(trimmed, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"documentType must be one of {string.Join(", ", SupportedDocumentTypes)}.",
                nameof(documentType));
        }
        return trimmed;
    }

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
