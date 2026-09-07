using System.Runtime.InteropServices;

using Arch.CadConnect.Core;
using Arch.CadConnect.Core.References;

using InventorApi = Inventor;

namespace Arch.CadConnect.Inventor.References;

/// <summary>
/// <see cref="IDocumentReferenceSource"/> backed by
/// <c>Document.ReferencedDocumentDescriptors</c>. For a document that is
/// currently loaded in Inventor (top-level or as a child of an open assembly /
/// drawing) it reports that document's DIRECT references - including the
/// missing ones - mapped to COM-free <see cref="RawDocumentReference"/>.
///
/// STRICTLY READ-ONLY. It never calls Open / Save / Save2 / Update, never
/// activates a document, and never touches reference resolution or descriptors.
///
/// HONESTY: a document that is not loaded, or that Inventor throws on while
/// describing, is reported as
/// <see cref="DocumentEnumerationStatus.Unavailable"/> - never as a successful
/// empty result. Only a clean read of every descriptor yields
/// <see cref="DocumentEnumerationStatus.Enumerated"/>.
/// </summary>
internal sealed class InventorDocumentReferenceSource(InventorApi.Application application)
    : IDocumentReferenceSource
{
    public DocumentReferenceEnumeration GetDirectReferences(string absoluteDocumentPath)
    {
        if (string.IsNullOrWhiteSpace(absoluteDocumentPath))
        {
            return DocumentReferenceEnumeration.Unavailable;
        }

        var document = FindLoadedDocument(absoluteDocumentPath);
        if (document is null)
        {
            // Not currently loaded - we cannot describe its direct references.
            return DocumentReferenceEnumeration.Unavailable;
        }

        InventorApi.DocumentDescriptorsEnumerator? descriptors;
        try
        {
            descriptors = document.ReferencedDocumentDescriptors;
        }
        catch (COMException)
        {
            return DocumentReferenceEnumeration.Unavailable;
        }
        catch (InvalidCastException)
        {
            return DocumentReferenceEnumeration.Unavailable;
        }

        if (descriptors is null)
        {
            return DocumentReferenceEnumeration.Unavailable;
        }

        var result = new List<RawDocumentReference>();
        try
        {
            foreach (InventorApi.DocumentDescriptor descriptor in descriptors)
            {
                var mapped = MapDescriptor(descriptor);
                if (mapped is null)
                {
                    // A descriptor we could not read at all - we can no longer
                    // guarantee we saw every reference. Report conservatively.
                    return DocumentReferenceEnumeration.Unavailable;
                }
                result.Add(mapped);
            }
        }
        catch (COMException)
        {
            return DocumentReferenceEnumeration.Unavailable;
        }
        catch (InvalidComObjectException)
        {
            return DocumentReferenceEnumeration.Unavailable;
        }

        return DocumentReferenceEnumeration.Enumerated(result);
    }

    private static RawDocumentReference? MapDescriptor(InventorApi.DocumentDescriptor descriptor)
    {
        try
        {
            var reportedName = TryGet(() => descriptor.FullDocumentName);
            var missing = TryGet(() => (bool?)descriptor.ReferenceMissing) ?? false;

            string? resolved = null;
            if (!missing)
            {
                resolved = TryGet(() => descriptor.ReferencedFileDescriptor?.FullFileName);
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    resolved = reportedName;
                }
            }

            var isResolved = !missing && !string.IsNullOrWhiteSpace(resolved);

            // The descriptor does not expose a document-type enum; the scanner
            // derives the child type from the resolved file's extension (the
            // same "display hint" used everywhere else in this codebase). The
            // RELATIONSHIP kind is then computed from (parent type, child type)
            // - never from a filename token.
            return new RawDocumentReference
            {
                ReportedName = string.IsNullOrWhiteSpace(reportedName)
                    ? "(unnamed reference)"
                    : reportedName!,
                ReportedFullPath = string.IsNullOrWhiteSpace(reportedName) ? null : reportedName,
                ResolvedFullPath = isResolved ? resolved : null,
                IsResolved = isResolved,
                ReportedType = CadDocumentType.Unknown,
            };
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidComObjectException)
        {
            return null;
        }
    }

    private InventorApi.Document? FindLoadedDocument(string absoluteDocumentPath)
    {
        try
        {
            foreach (InventorApi.Document document in application.Documents)
            {
                if (PathMatches(document, absoluteDocumentPath))
                {
                    return document;
                }
            }
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidComObjectException)
        {
            return null;
        }
        return null;
    }

    private static bool PathMatches(InventorApi.Document document, string absoluteDocumentPath)
    {
        try
        {
            var full = document.FullFileName;
            if (string.IsNullOrWhiteSpace(full))
            {
                return false;
            }
            return string.Equals(
                Path.GetFullPath(full),
                Path.GetFullPath(absoluteDocumentPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (COMException) { return false; }
        catch (InvalidComObjectException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static T? TryGet<T>(Func<T?> read)
    {
        try
        {
            return read();
        }
        catch (COMException) { return default; }
        catch (InvalidComObjectException) { return default; }
        catch (InvalidCastException) { return default; }
        catch (ArgumentException) { return default; }
    }
}
