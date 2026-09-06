using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Arch.CadConnect.Core.Session;

namespace Arch.CadConnect.Api;

/// <summary>
/// <see cref="ISessionStore"/> that writes the session as a DPAPI-encrypted
/// blob under <c>%LOCALAPPDATA%\ArchEngineering\CadConnect\session.bin</c>,
/// scoped to the current Windows user (<see cref="DataProtectionScope.CurrentUser"/>).
///
/// - The plaintext (which contains the bearer token) never touches disk.
/// - Another Windows user - or the same user on another machine - cannot
///   decrypt the file.
/// - Any read/decrypt/parse problem is swallowed and reported as "no session".
/// - Nothing here is ever logged.
///
/// P4A limitation: DPAPI CurrentUser protects against other local users and
/// off-box copies, but not against malware already running as this user. A
/// hardware-backed store is production debt (see README).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSessionStore : ISessionStore
{
    // Extra entropy mixed into DPAPI - not a secret, just domain separation so
    // this blob can only be unprotected via this code path.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Arch.CadConnect/desktop-session/v1");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _filePath;

    public DpapiSessionStore(string? overrideDirectory = null)
    {
        var dir = overrideDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArchEngineering",
            "CadConnect");
        _filePath = Path.Combine(dir, "session.bin");
    }

    public string FilePath => _filePath;

    public void Save(PersistedDesktopSession session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(session, Json);
        try
        {
            var encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            var tmp = _filePath + ".tmp";
            File.WriteAllBytes(tmp, encrypted);
            File.Move(tmp, _filePath, overwrite: true);
        }
        finally
        {
            Array.Clear(plaintext);
        }
    }

    public PersistedDesktopSession? TryLoad()
    {
        byte[]? plaintext = null;
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var encrypted = File.ReadAllBytes(_filePath);
            plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<PersistedDesktopSession>(plaintext, Json);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (plaintext is not null)
            {
                Array.Clear(plaintext);
            }
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
