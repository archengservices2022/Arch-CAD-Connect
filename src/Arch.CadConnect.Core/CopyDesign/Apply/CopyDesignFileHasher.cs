using System.Security.Cryptography;

namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// The REAL <see cref="ICopyDesignFileHasher"/> - plain file I/O + SHA-256,
/// no Inventor COM involved, so it lives in Core (mirrors
/// <see cref="Api.Workspace"/>-style hashing already used for check-in) and
/// is directly unit-testable with real temp files.
/// </summary>
public sealed class CopyDesignFileHasher : ICopyDesignFileHasher
{
    public string ComputeSha256(string absolutePath)
    {
        using var stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: false);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
