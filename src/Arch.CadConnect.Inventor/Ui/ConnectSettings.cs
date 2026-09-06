using System.Text.Json;

namespace Arch.CadConnect.Inventor.Ui;

/// <summary>
/// Small, NON-secret user preferences for the connect dialog - the last
/// server address and email. Stored as plain JSON under %APPDATA% because
/// none of it is sensitive (the bearer token lives elsewhere,
/// DPAPI-encrypted - see <c>Arch.CadConnect.Api.DpapiSessionStore</c>).
///
/// There is deliberately NO "allow insecure transport" preference: the
/// transport policy (<c>ArchServerUri</c>) has no client-side override.
/// </summary>
public sealed record ConnectSettings
{
    public string ServerAddress { get; init; } = "http://localhost:3000";

    public string? LastEmail { get; init; }

    /// <summary>
    /// The local directory the user last chose as a Get Latest workspace root.
    /// A convenience default only - it is NOT authority for anything and the
    /// user re-confirms it every time.
    /// </summary>
    public string? LastWorkspaceRoot { get; init; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ArchEngineering", "CadConnect", "connect.json");

    public static ConnectSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<ConnectSettings>(json) ?? new ConnectSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // fall through to defaults
        }
        return new ConnectSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort - not being able to remember the address is harmless
        }
    }
}
