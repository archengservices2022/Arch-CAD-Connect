namespace Arch.CadConnect.Core;

/// <summary>
/// The CAD document formats Arch CAD Connect recognizes. Mirrors the server's
/// <c>CadDocumentType</c> enum (see web/prisma/schema.prisma) but is an
/// independent client-side type - the client never imports server code.
/// </summary>
public enum CadDocumentType
{
    /// <summary>Not a recognized CAD document (or no document open).</summary>
    Unknown = 0,

    /// <summary>Inventor part (.ipt).</summary>
    Ipt,

    /// <summary>Inventor assembly (.iam).</summary>
    Iam,

    /// <summary>Inventor drawing (.idw).</summary>
    Idw,

    /// <summary>AutoCAD / Inventor DWG drawing (.dwg).</summary>
    Dwg,
}

/// <summary>
/// Maps a local file name or path to a <see cref="CadDocumentType"/> using ONLY
/// the file extension. This is a display/routing hint - never an identity. The
/// authoritative Arch identity for a document is a server-issued id, never the
/// file name (which users rename freely).
/// </summary>
public static class CadDocumentTypes
{
    /// <summary>The four extensions P4A must recognize, lower-case, with dot.</summary>
    public static readonly IReadOnlyList<string> RecognizedExtensions =
        new[] { ".ipt", ".iam", ".idw", ".dwg" };

    public static CadDocumentType FromPath(string? pathOrFileName)
    {
        if (string.IsNullOrWhiteSpace(pathOrFileName))
        {
            return CadDocumentType.Unknown;
        }

        // Path.GetExtension tolerates a bare file name and a full path alike.
        var ext = Path.GetExtension(pathOrFileName.Trim()).ToLowerInvariant();
        return ext switch
        {
            ".ipt" => CadDocumentType.Ipt,
            ".iam" => CadDocumentType.Iam,
            ".idw" => CadDocumentType.Idw,
            ".dwg" => CadDocumentType.Dwg,
            _ => CadDocumentType.Unknown,
        };
    }

    public static bool IsRecognized(string? pathOrFileName) =>
        FromPath(pathOrFileName) != CadDocumentType.Unknown;
}
