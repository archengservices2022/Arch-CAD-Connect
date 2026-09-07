using System.Runtime.InteropServices;

using Arch.CadConnect.Core.Documents;

using InventorApi = Inventor;

namespace Arch.CadConnect.Inventor.Documents;

/// <summary>
/// <see cref="IEditorDocumentProbe"/> backed by <c>Application.Documents</c>.
/// Used as an Undo Checkout pre-flight: if the managed file is open in Inventor
/// (as a top-level document OR a referenced child of an open assembly), the
/// working file cannot be safely replaced, so Undo is refused BEFORE the
/// server checkout is released.
/// </summary>
internal sealed class InventorEditorDocumentProbe(InventorApi.Application application) : IEditorDocumentProbe
{
    public bool IsOpenForEditing(string absoluteFilePath)
    {
        if (string.IsNullOrWhiteSpace(absoluteFilePath))
        {
            return false;
        }

        try
        {
            foreach (InventorApi.Document doc in application.Documents)
            {
                if (PathMatches(doc, absoluteFilePath))
                {
                    return true;
                }

                // A part can be open only as a reference inside an assembly /
                // drawing - check those too.
                InventorApi.DocumentsEnumerator? referenced = null;
                try { referenced = doc.ReferencedDocuments; }
                catch (COMException) { /* some doc types have none */ }
                catch (InvalidCastException) { }

                if (referenced is not null)
                {
                    foreach (InventorApi.Document child in referenced)
                    {
                        if (PathMatches(child, absoluteFilePath))
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch (COMException)
        {
            // Inventor busy / mid-operation - be conservative and say "open"
            // so Undo refuses rather than risking a replace under a live edit.
            return true;
        }
        catch (InvalidComObjectException)
        {
            return true;
        }

        return false;
    }

    private static bool PathMatches(InventorApi.Document doc, string absoluteFilePath)
    {
        try
        {
            var full = doc.FullFileName;
            return !string.IsNullOrWhiteSpace(full)
                && string.Equals(
                    Path.GetFullPath(full),
                    Path.GetFullPath(absoluteFilePath),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (COMException) { return false; }
        catch (InvalidComObjectException) { return false; }
        catch (ArgumentException) { return false; }
    }
}
