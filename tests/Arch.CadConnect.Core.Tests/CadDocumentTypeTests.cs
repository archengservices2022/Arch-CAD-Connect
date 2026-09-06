using Arch.CadConnect.Core;

namespace Arch.CadConnect.Core.Tests;

public class CadDocumentTypeTests
{
    [Theory]
    [InlineData("Part1.ipt", CadDocumentType.Ipt)]
    [InlineData("Assembly.IAM", CadDocumentType.Iam)]
    [InlineData("Sheet.Idw", CadDocumentType.Idw)]
    [InlineData("Legacy.dwg", CadDocumentType.Dwg)]
    [InlineData(@"C:\Work\10073\10073-MFD-AS210.iam", CadDocumentType.Iam)]
    [InlineData("notes.txt", CadDocumentType.Unknown)]
    [InlineData("no-extension", CadDocumentType.Unknown)]
    [InlineData("", CadDocumentType.Unknown)]
    [InlineData(null, CadDocumentType.Unknown)]
    public void FromPath_maps_by_extension_case_insensitively(string? input, CadDocumentType expected)
    {
        Assert.Equal(expected, CadDocumentTypes.FromPath(input));
    }

    [Fact]
    public void RecognizedExtensions_is_exactly_the_four_P4A_formats()
    {
        Assert.Equal(new[] { ".ipt", ".iam", ".idw", ".dwg" }, CadDocumentTypes.RecognizedExtensions);
    }

    [Fact]
    public void IsRecognized_true_only_for_cad_formats()
    {
        Assert.True(CadDocumentTypes.IsRecognized("x.ipt"));
        Assert.False(CadDocumentTypes.IsRecognized("x.sldprt"));
    }
}
