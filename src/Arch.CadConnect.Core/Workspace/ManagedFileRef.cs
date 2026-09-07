namespace Arch.CadConnect.Core.Workspace;

/// <summary>
/// Points a P4C operation at ONE managed file: the absolute workspace root and
/// the absolute path of the file itself. The file's stable Arch identity
/// (<c>cadDocumentId</c>) is looked up from the verified workspace manifest at
/// <see cref="WorkspaceRoot"/> - it is NEVER inferred from the filename.
/// </summary>
public sealed record ManagedFileRef
{
    public required string WorkspaceRoot { get; init; }
    public required string AbsoluteFilePath { get; init; }

    public static ManagedFileRef Create(string workspaceRoot, string absoluteFilePath) => new()
    {
        WorkspaceRoot = (workspaceRoot ?? "").Trim(),
        AbsoluteFilePath = (absoluteFilePath ?? "").Trim(),
    };
}
