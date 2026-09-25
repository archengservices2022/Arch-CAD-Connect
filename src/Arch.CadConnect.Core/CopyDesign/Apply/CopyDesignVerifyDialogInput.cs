namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// P6E-D: pure, COM/WinForms-free validation of the "Verify Copy Design"
/// dialog's three inputs (operation id, source workspace folder, destination
/// folder) - extracted out of <c>CopyDesignVerifyDialog</c> (Inventor
/// project, not unit-testable there since no test project references it) so
/// this fail-closed input gate is directly unit-testable from
/// <c>Arch.CadConnect.Core.Tests</c>.
///
/// Deliberately STRICTER than the sibling <c>CopyDesignDurableResumeDialog</c>'s
/// own input handling (which only checks path SHAPE): this task's own
/// contract requires both folders to already EXIST, so a wrong path fails
/// before any HTTP/COM call, never merely a full-path-format check.
///
/// Never guesses/finds an operation id from the filesystem - that is
/// explicitly out of scope; this only validates what the user typed/chose.
/// </summary>
public static class CopyDesignVerifyDialogInput
{
    public sealed record ValidationResult(
        bool IsValid,
        string? ErrorMessage,
        string OperationId = "",
        string SourceWorkspaceRoot = "",
        string DestinationFolder = "")
    {
        public static ValidationResult Invalid(string message) => new(false, message);

        public static ValidationResult Valid(string operationId, string sourceWorkspaceRoot, string destinationFolder) =>
            new(true, null, operationId, sourceWorkspaceRoot, destinationFolder);
    }

    /// <param name="directoryExists">Injected so this stays pure/unit-testable
    ///  with a fake filesystem - production callers pass <see cref="Directory.Exists(string?)"/>.</param>
    public static ValidationResult Validate(
        string? operationId,
        string? sourceWorkspaceRoot,
        string? destinationFolder,
        Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);

        var id = (operationId ?? string.Empty).Trim();
        var source = (sourceWorkspaceRoot ?? string.Empty).Trim();
        var destination = (destinationFolder ?? string.Empty).Trim();

        if (id.Length == 0)
        {
            return ValidationResult.Invalid("Enter the Copy Design operation id to verify.");
        }
        if (source.Length == 0)
        {
            return ValidationResult.Invalid("Choose the source workspace folder.");
        }
        if (!Path.IsPathFullyQualified(source))
        {
            return ValidationResult.Invalid("The source workspace folder must be a full path (e.g. C:\\Work\\ProjectX).");
        }
        if (!directoryExists(source))
        {
            return ValidationResult.Invalid("The source workspace folder does not exist.");
        }
        if (destination.Length == 0)
        {
            return ValidationResult.Invalid("Choose the destination folder.");
        }
        if (!Path.IsPathFullyQualified(destination))
        {
            return ValidationResult.Invalid("The destination folder must be a full path (e.g. C:\\Work\\ProjectX-New).");
        }
        if (!directoryExists(destination))
        {
            return ValidationResult.Invalid("The destination folder does not exist.");
        }

        return ValidationResult.Valid(id, source, destination);
    }
}
