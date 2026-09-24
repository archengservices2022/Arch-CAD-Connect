using Arch.CadConnect.Core;

namespace Arch.CadConnect.Api.CopyDesign;

/// <summary>Shared wire-format mapping between <see cref="CadDocumentType"/>
///  and the string values the P6B/P6C server contracts use - used by both
///  <see cref="CopyDesignApplyHttpClient"/> and
///  <see cref="CopyDesignMaterializeHttpClient"/>.</summary>
internal static class CopyDesignWireMapping
{
    public static string DocumentTypeWireName(CadDocumentType type) => type switch
    {
        CadDocumentType.Ipt => "IPT",
        CadDocumentType.Iam => "IAM",
        // P6D: the server's CadDocumentType enum (web/prisma/schema.prisma)
        // and CAD_DOCUMENT_TYPES (web/app/lib/copy-design-lineage-core.ts)
        // already carry IDW/DWG generically for both the apply/reservation
        // and materialize contracts - confirmed no server contract gap.
        CadDocumentType.Idw => "IDW",
        CadDocumentType.Dwg => "DWG",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Copy Design only ever requests IPT/IAM/IDW/DWG copies."),
    };

    /// <summary>The reverse of <see cref="DocumentTypeWireName"/> - EXACT,
    ///  case-sensitive match against the server's own canonical uppercase
    ///  wire values only (never case-insensitive, never a filename guess).
    ///  An unrecognized wire value returns <see cref="CadDocumentType.Unknown"/>
    ///  rather than throwing - the caller (e.g. drawing-association evidence
    ///  validation) already treats an unexpected type as malformed evidence,
    ///  not a hard failure.</summary>
    public static CadDocumentType ParseDocumentTypeWireName(string? wireName) => wireName switch
    {
        "IPT" => CadDocumentType.Ipt,
        "IAM" => CadDocumentType.Iam,
        "IDW" => CadDocumentType.Idw,
        "DWG" => CadDocumentType.Dwg,
        _ => CadDocumentType.Unknown,
    };
}
