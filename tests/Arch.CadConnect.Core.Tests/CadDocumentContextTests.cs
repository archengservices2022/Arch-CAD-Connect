using Arch.CadConnect.Core;

namespace Arch.CadConnect.Core.Tests;

public class CadDocumentContextTests
{
    [Fact]
    public void Saved_document_on_disk_has_path_type_and_is_eligible()
    {
        var ctx = CadDocumentContext.Create(
            fullPath: @"C:\Work\10073\bracket.ipt",
            displayNameFallback: "bracket",
            isDirty: false);

        Assert.Equal(@"C:\Work\10073\bracket.ipt", ctx.FullPath);
        Assert.Equal("bracket.ipt", ctx.FileName);
        Assert.Equal(CadDocumentType.Ipt, ctx.DocumentType);
        Assert.True(ctx.IsSaved);
        Assert.True(ctx.HasBeenSavedToDisk);
        Assert.True(ctx.IsEligibleForPdmOperation);
        Assert.Null(ctx.PlmIdentity);
        Assert.Equal(CadDocumentStatus.Unknown, ctx.Status);
    }

    [Fact]
    public void Dirty_document_is_not_eligible_even_when_on_disk()
    {
        var ctx = CadDocumentContext.Create(@"C:\Work\a.iam", "a", isDirty: true);

        Assert.False(ctx.IsSaved);
        Assert.True(ctx.HasBeenSavedToDisk);
        Assert.False(ctx.IsEligibleForPdmOperation);
    }

    [Fact]
    public void Never_saved_document_has_no_path_and_no_local_identity()
    {
        var ctx = CadDocumentContext.Create(fullPath: "   ", displayNameFallback: "Part1", isDirty: true);

        Assert.Null(ctx.FullPath);
        Assert.Equal("Part1", ctx.FileName);
        Assert.False(ctx.HasBeenSavedToDisk);
        Assert.False(ctx.IsEligibleForPdmOperation);
    }

    [Fact]
    public void None_represents_no_active_document()
    {
        Assert.Equal(CadDocumentType.Unknown, CadDocumentContext.None.DocumentType);
        Assert.False(CadDocumentContext.None.HasDocument);
        Assert.False(CadDocumentContext.None.IsEligibleForPdmOperation);
    }

    [Fact]
    public void Context_equality_is_by_value_so_redundant_events_are_no_ops()
    {
        var a = CadDocumentContext.Create(@"C:\w\p.ipt", "p", false);
        var b = CadDocumentContext.Create(@"C:\w\p.ipt", "p", false);
        Assert.Equal(a, b);
    }
}
