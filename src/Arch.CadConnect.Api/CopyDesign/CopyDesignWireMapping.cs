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
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "P6C only ever requests IPT/IAM copies."),
    };
}
