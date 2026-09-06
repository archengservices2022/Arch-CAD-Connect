using Arch.CadConnect.Core.Connection;

namespace Arch.CadConnect.Core.Ribbon;

/// <summary>
/// Pure decision of whether a ribbon button is enabled, given the current
/// connection state and active document. This is a USABILITY layer only - it
/// disables buttons that could not possibly succeed. It is NOT a security
/// boundary: the server re-authorizes every real operation regardless of what
/// the ribbon allowed the user to click.
///
/// Rule: a command that is not implemented yet
/// (<see cref="ArchCommands.IsImplemented"/> == false) is ALWAYS shown
/// disabled, with a tooltip saying so - never enabled-then-fake-succeed.
/// </summary>
public static class RibbonCommandPolicy
{
    public static bool IsEnabled(ArchCommand command, ConnectionState connection, CadDocumentContext document)
    {
        // Not built yet -> visible but disabled, everywhere.
        if (!command.IsImplemented())
        {
            return false;
        }

        return command switch
        {
            ArchCommand.SignIn => connection.AllowsSignIn(),
            ArchCommand.SignOut => connection.AllowsSignOut(),
            ArchCommand.ServerStatus => connection is not ConnectionState.SignedOut and not ConnectionState.Connecting,

            // P4B: Get Latest resolves the root by an EXPLICIT document number
            // the user enters, so it does NOT need an open document - only a
            // live connection.
            ArchCommand.GetLatest => connection == ConnectionState.Connected,

            _ => false,
        };
    }

    /// <summary>
    /// The enablement of EVERY command for a given state, ready to apply to the
    /// ribbon in one pass. Deterministic order (<see cref="ArchCommands.All"/>).
    /// </summary>
    public static IReadOnlyDictionary<ArchCommand, bool> Evaluate(
        ConnectionState connection,
        CadDocumentContext document)
    {
        var map = new Dictionary<ArchCommand, bool>();
        foreach (var command in ArchCommands.All)
        {
            map[command] = IsEnabled(command, connection, document);
        }
        return map;
    }
}
