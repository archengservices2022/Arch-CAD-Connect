using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.Tests.Workspace;

public class DesktopCadDocumentContractTests
{
    [Fact]
    public void The_id_matches_the_server_contract_string_exactly()
    {
        Assert.Equal("arch-plm.desktop-cad-document.v1", DesktopCadDocumentContract.Id);
    }

    [Theory]
    [InlineData("arch-plm.desktop-cad-document.v1", true)]
    [InlineData("arch-plm.desktop-cad-document.v2", false)]
    [InlineData("arch-plm.cad-workspace-plan", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSupported_accepts_only_the_exact_v1_identifier(string? contract, bool expected)
    {
        Assert.Equal(expected, DesktopCadDocumentContract.IsSupported(contract));
    }
}
