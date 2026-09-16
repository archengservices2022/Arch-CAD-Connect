using Arch.CadConnect.Core.CopyDesign;

namespace Arch.CadConnect.Core.Tests.CopyDesign;

public class TokenReplaceNameRuleTests
{
    [Fact]
    public void Renames_a_file_name_containing_the_source_token()
    {
        var rule = new TokenReplaceNameRule("10073", "10137");

        Assert.Equal("10137-AS210.iam", rule.Rename("10073-AS210.iam"));
        Assert.Equal("10137-P001.ipt", rule.Rename("10073-P001.ipt"));
    }

    [Fact]
    public void Returns_null_unchanged_for_a_file_name_without_the_source_token()
    {
        var rule = new TokenReplaceNameRule("10073", "10137");

        Assert.Null(rule.Rename("STD-BOLT-M6.ipt"));
    }

    [Fact]
    public void Replaces_every_occurrence_of_the_token()
    {
        var rule = new TokenReplaceNameRule("A", "B");

        Assert.Equal("B-B.ipt", rule.Rename("A-A.ipt"));
    }

    [Theory]
    [InlineData("", "10137")]
    [InlineData("   ", "10137")]
    [InlineData(null, "10137")]
    public void Rejects_an_empty_source_token(string? source, string destination)
    {
        Assert.Throws<ArgumentException>(() => new TokenReplaceNameRule(source!, destination));
    }

    [Theory]
    [InlineData("10073", "")]
    [InlineData("10073", "   ")]
    [InlineData("10073", null)]
    public void Rejects_an_empty_destination_token(string source, string? destination)
    {
        Assert.Throws<ArgumentException>(() => new TokenReplaceNameRule(source, destination!));
    }

    [Fact]
    public void Rejects_identical_source_and_destination_tokens()
    {
        Assert.Throws<ArgumentException>(() => new TokenReplaceNameRule("10073", "10073"));
    }
}
