namespace Arch.CadConnect.Core.References;

/// <summary>
/// The COM boundary for a P5A reference scan. The Inventor layer implements
/// this against <c>Document.ReferencedDocumentDescriptors</c> - a READ-ONLY
/// view of one document's DIRECT references, including the missing ones.
/// Core / tests use <see cref="NoDocumentReferenceSource"/> or a fake.
///
/// Contract: an implementation NEVER opens, activates, saves, updates, or
/// repairs a document, and NEVER changes reference resolution. It only reports
/// what Inventor already knows - and it MUST say, via
/// <see cref="DocumentReferenceEnumeration.Status"/>, whether it actually
/// managed to inspect the document. "Could not inspect" is never reported as
/// "successfully found zero references".
/// </summary>
public interface IDocumentReferenceSource
{
    /// <summary>
    /// The direct references of the document at
    /// <paramref name="absoluteDocumentPath"/>, together with whether the
    /// enumeration actually succeeded. A document that is not currently loaded
    /// (or that Inventor refuses to describe) is
    /// <see cref="DocumentEnumerationStatus.Unavailable"/> - NOT an empty
    /// <see cref="DocumentEnumerationStatus.Enumerated"/> result.
    /// </summary>
    DocumentReferenceEnumeration GetDirectReferences(string absoluteDocumentPath);
}

/// <summary>Whether a document's direct references could be read at all.</summary>
public enum DocumentEnumerationStatus
{
    /// <summary>Inventor successfully described this document's direct
    ///  references. Zero references here means a proven leaf.</summary>
    Enumerated,

    /// <summary>The document's direct references could not be read (it is not
    ///  loaded, or Inventor threw while describing it). This says NOTHING about
    ///  how many children it has - the sub-graph below it is unknown.</summary>
    Unavailable,
}

/// <summary>
/// The outcome of asking an <see cref="IDocumentReferenceSource"/> for one
/// document's direct references.
/// </summary>
public sealed record DocumentReferenceEnumeration(
    DocumentEnumerationStatus Status,
    IReadOnlyList<RawDocumentReference> References)
{
    public static DocumentReferenceEnumeration Enumerated(IEnumerable<RawDocumentReference> references) =>
        new(DocumentEnumerationStatus.Enumerated, references.ToArray());

    public static readonly DocumentReferenceEnumeration EnumeratedEmpty =
        new(DocumentEnumerationStatus.Enumerated, Array.Empty<RawDocumentReference>());

    public static readonly DocumentReferenceEnumeration Unavailable =
        new(DocumentEnumerationStatus.Unavailable, Array.Empty<RawDocumentReference>());
}

/// <summary>
/// One direct reference as reported by Inventor, mapped to primitives before
/// it crosses out of the Inventor project. No COM type here.
/// </summary>
public sealed record RawDocumentReference
{
    /// <summary>The descriptor's name exactly as Inventor gives it (usually the
    ///  full stored path of the reference target).</summary>
    public required string ReportedName { get; init; }

    /// <summary>A fuller path form of the descriptor, if a distinct one exists.</summary>
    public string? ReportedFullPath { get; init; }

    /// <summary>The path Inventor actually resolves this reference to. Null /
    ///  blank when the reference is missing.</summary>
    public string? ResolvedFullPath { get; init; }

    /// <summary>True when Inventor resolves the reference to a real file.</summary>
    public required bool IsResolved { get; init; }

    /// <summary>The referenced document's type if the descriptor reports it
    ///  directly; <see cref="CadDocumentType.Unknown"/> otherwise (the scanner
    ///  then falls back to the resolved file's extension).</summary>
    public CadDocumentType ReportedType { get; init; } = CadDocumentType.Unknown;
}

/// <summary>The default source: no document can be introspected (non-Inventor
///  contexts and tests). Every lookup is
///  <see cref="DocumentEnumerationStatus.Unavailable"/> - it never pretends a
///  document is a proven leaf.</summary>
public sealed class NoDocumentReferenceSource : IDocumentReferenceSource
{
    public static readonly NoDocumentReferenceSource Instance = new();

    public DocumentReferenceEnumeration GetDirectReferences(string absoluteDocumentPath) =>
        DocumentReferenceEnumeration.Unavailable;
}
