namespace Arch.CadConnect.Core.Ribbon;

/// <summary>
/// Every ribbon button the add-in defines. Split into two groups:
///
///   * <see cref="IsImplemented"/> == true  - the command actually performs a
///     real operation (CONNECTION commands since P4A; Get Latest since P4B);
///   * everything else - declared so the ribbon is complete, but wired to a
///     handler that tells the user the operation is not implemented yet. The
///     add-in NEVER reports a fake successful PDM result.
/// </summary>
public enum ArchCommand
{
    // CONNECTION panel
    SignIn,
    SignOut,
    ServerStatus,

    // PDM panel  (Get Latest: P4B; the rest: P4C)
    GetLatest,
    Checkout,
    CheckIn,
    UndoCheckout,

    // INFORMATION panel
    ScanReferences, // P5A
    Status,
    Version,
    Revision,
    WhereUsed,
}

public static class ArchCommands
{
    /// <summary>Internal-id namespace for every ribbon control this add-in creates.</summary>
    public const string InternalIdPrefix = "Arch.CadConnect";

    /// <summary>The ribbon tab label.</summary>
    public const string RibbonTabName = "ARCH ENGINEERING";

    /// <summary>True when the command performs a real operation today (vs.
    ///  "declared but not implemented yet").</summary>
    public static bool IsImplemented(this ArchCommand command) => command switch
    {
        ArchCommand.SignIn => true,
        ArchCommand.SignOut => true,
        ArchCommand.ServerStatus => true,
        ArchCommand.GetLatest => true, // P4B
        ArchCommand.Checkout => true, // P4C
        ArchCommand.CheckIn => true, // P4C
        ArchCommand.UndoCheckout => true, // P4C
        ArchCommand.ScanReferences => true, // P5A
        _ => false,
    };

    /// <summary>True when the command only makes sense with an active, eligible document.</summary>
    public static bool RequiresActiveDocument(this ArchCommand command) => command switch
    {
        ArchCommand.GetLatest => true,
        ArchCommand.Checkout => true,
        ArchCommand.CheckIn => true,
        ArchCommand.UndoCheckout => true,
        ArchCommand.ScanReferences => true,
        ArchCommand.Status => true,
        ArchCommand.Version => true,
        ArchCommand.Revision => true,
        ArchCommand.WhereUsed => true,
        _ => false,
    };

    /// <summary>True when the command needs a live connection to the server.</summary>
    public static bool RequiresConnection(this ArchCommand command) =>
        command != ArchCommand.SignIn;

    public static string DisplayName(this ArchCommand command) => command switch
    {
        ArchCommand.SignIn => "Sign In",
        ArchCommand.SignOut => "Sign Out",
        ArchCommand.ServerStatus => "Server Status",
        ArchCommand.GetLatest => "Get Latest",
        ArchCommand.Checkout => "Checkout",
        ArchCommand.CheckIn => "Check In",
        ArchCommand.UndoCheckout => "Undo Checkout",
        ArchCommand.ScanReferences => "Scan References",
        ArchCommand.Status => "Status",
        ArchCommand.Version => "Version",
        ArchCommand.Revision => "Revision",
        ArchCommand.WhereUsed => "Where Used",
        _ => command.ToString(),
    };

    public static string InternalName(this ArchCommand command) =>
        $"{InternalIdPrefix}.Cmd.{command}";

    public static string Panel(this ArchCommand command) => command switch
    {
        ArchCommand.SignIn or ArchCommand.SignOut or ArchCommand.ServerStatus => "CONNECTION",
        ArchCommand.GetLatest or ArchCommand.Checkout or ArchCommand.CheckIn or ArchCommand.UndoCheckout => "PDM",
        _ => "INFORMATION",
    };

    // ScanReferences (P5A) falls through to INFORMATION - it is read-only
    // intelligence, not a PDM state change.

    public static string ToolTip(this ArchCommand command) =>
        command.IsImplemented()
            ? command switch
            {
                ArchCommand.SignIn => "Sign in to an Arch PLM server.",
                ArchCommand.SignOut => "Sign out and revoke this machine's session.",
                ArchCommand.ServerStatus => "Check the Arch PLM connection.",
                ArchCommand.GetLatest => "Download the latest version of a managed CAD document and its dependencies into a local workspace.",
                ArchCommand.Checkout => "Take an exclusive server checkout of the active managed document and make its local file editable.",
                ArchCommand.CheckIn => "Upload the saved local file as the next version and release your checkout.",
                ArchCommand.UndoCheckout => "Release your checkout and restore the local file to the checked-out version, discarding local changes.",
                ArchCommand.ScanReferences => "Report the references Inventor knows about for the active document. Read-only - never changes, saves, or repairs anything.",
                _ => command.DisplayName(),
            }
            : $"{command.DisplayName()} is not available yet. Coming in a later release.";

    /// <summary>Deterministic panel order.</summary>
    public static readonly IReadOnlyList<string> PanelOrder = new[] { "CONNECTION", "PDM", "INFORMATION" };

    /// <summary>All commands, in ribbon layout order.</summary>
    public static readonly IReadOnlyList<ArchCommand> All = new[]
    {
        ArchCommand.SignIn, ArchCommand.SignOut, ArchCommand.ServerStatus,
        ArchCommand.GetLatest, ArchCommand.Checkout, ArchCommand.CheckIn, ArchCommand.UndoCheckout,
        ArchCommand.ScanReferences,
        ArchCommand.Status, ArchCommand.Version, ArchCommand.Revision, ArchCommand.WhereUsed,
    };
}
