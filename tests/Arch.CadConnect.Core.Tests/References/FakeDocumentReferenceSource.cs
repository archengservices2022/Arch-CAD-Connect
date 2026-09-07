using Arch.CadConnect.Core.References;

namespace Arch.CadConnect.Core.Tests.References;

/// <summary>
/// An in-memory <see cref="IDocumentReferenceSource"/>: maps a document's
/// absolute path to the direct references it "reports" and whether the
/// enumeration succeeded. Path lookup is case-insensitive and normalises via
/// <see cref="Path.GetFullPath(string)"/>, matching the scanner's key handling.
///
/// A path that was never registered defaults to
/// <see cref="DocumentEnumerationStatus.Enumerated"/> with zero references - a
/// proven leaf. Use <see cref="Unavailable"/> to model a document the scanner
/// cannot inspect.
/// </summary>
internal sealed class FakeDocumentReferenceSource : IDocumentReferenceSource
{
    private readonly Dictionary<string, DocumentReferenceEnumeration> _byDocument =
        new(StringComparer.OrdinalIgnoreCase);

    public int Calls { get; private set; }
    public List<string> CallLog { get; } = new();

    public FakeDocumentReferenceSource Add(string parentAbsolutePath, params RawDocumentReference[] references)
    {
        _byDocument[Key(parentAbsolutePath)] = DocumentReferenceEnumeration.Enumerated(references);
        return this;
    }

    public FakeDocumentReferenceSource Component(string parent, string child, bool resolved = true)
        => Add(parent, Ref(child, resolved));

    /// <summary>Register <paramref name="documentAbsolutePath"/> as a document
    ///  Inventor could not describe (not loaded / threw).</summary>
    public FakeDocumentReferenceSource Unavailable(string documentAbsolutePath)
    {
        _byDocument[Key(documentAbsolutePath)] = DocumentReferenceEnumeration.Unavailable;
        return this;
    }

    public static RawDocumentReference Ref(string childPath, bool resolved = true) => new()
    {
        ReportedName = childPath,
        ReportedFullPath = childPath,
        ResolvedFullPath = resolved ? childPath : null,
        IsResolved = resolved,
    };

    public static RawDocumentReference Missing(string reportedName) => new()
    {
        ReportedName = reportedName,
        ReportedFullPath = reportedName,
        ResolvedFullPath = null,
        IsResolved = false,
    };

    public DocumentReferenceEnumeration GetDirectReferences(string absoluteDocumentPath)
    {
        Calls++;
        CallLog.Add(Key(absoluteDocumentPath));
        return _byDocument.TryGetValue(Key(absoluteDocumentPath), out var enumeration)
            ? enumeration
            : DocumentReferenceEnumeration.EnumeratedEmpty;
    }

    private static string Key(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
