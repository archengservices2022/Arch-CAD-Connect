namespace Arch.CadConnect.Core.Documents;

/// <summary>
/// Answers "is this exact local file currently open in the CAD editor?" - used
/// as an Undo pre-flight so the working file is known to be replaceable BEFORE
/// the server checkout is released. The Inventor layer implements this against
/// <c>Application.Documents</c>; Core / Api / tests use
/// <see cref="NoEditorProbe"/> (nothing is open) or a fake.
/// </summary>
public interface IEditorDocumentProbe
{
    /// <summary>True when a document whose on-disk path equals
    ///  <paramref name="absoluteFilePath"/> (case-insensitive) is open in the
    ///  editor right now.</summary>
    bool IsOpenForEditing(string absoluteFilePath);
}

/// <summary>The default: assume nothing is open (non-Inventor contexts and
///  tests). The filesystem exclusive-open probe in
///  <c>ManagedFileGuard.CanReplaceInPlace</c> still guards the real case.</summary>
public sealed class NoEditorProbe : IEditorDocumentProbe
{
    public static readonly NoEditorProbe Instance = new();
    public bool IsOpenForEditing(string absoluteFilePath) => false;
}
