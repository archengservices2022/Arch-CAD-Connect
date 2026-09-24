using Arch.CadConnect.Core;
using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;
using Arch.CadConnect.Core.References;

using static Arch.CadConnect.Core.Tests.CopyDesign.Apply.CopyDesignApplyFixtures;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

public class CopyDesignApplyRequestMapperTests
{
    // 1. non-executable plan cannot apply
    [Fact]
    public void NonExecutablePlanCannotBeMapped()
    {
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var plan = Plan(new[] { node }, isExecutable: false);

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.False(result.Success);
        Assert.Contains("not executable", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    // 3. COPY maps to P6B COPY request correctly
    [Fact]
    public void CopyNodeMapsToCopyEntryWithExactFields()
    {
        var node = CopyNode("cad-1", @"C:\src\10073-P001.ipt", @"C:\dst\10137-P001.ipt", fileVersionId: "fv-9");
        var plan = Plan(new[] { node });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.True(result.Success);
        var entry = Assert.Single(result.Request!.Entries);
        Assert.Equal(CopyDesignApplyEntryAction.Copy, entry.Entry.Action);
        Assert.Equal("cad-1", entry.Entry.Copy!.SourceCadDocumentId);
        Assert.Equal("fv-9", entry.Entry.Copy.SourceFileVersionId);
        Assert.Equal("10137-P001", entry.Entry.Copy.NewDocumentNumber);
        Assert.Equal("10137-P001.ipt", entry.Entry.Copy.NewFileName);
        Assert.Equal(CadDocumentType.Ipt, entry.Entry.Copy.DocumentType);
    }

    // 4. REUSE maps correctly
    [Fact]
    public void ReuseNodeMapsToReuseEntry()
    {
        var node = ReuseNode("cad-2", @"C:\src\std-bolt.ipt");
        var plan = Plan(new[] { node });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.True(result.Success);
        var entry = Assert.Single(result.Request!.Entries);
        Assert.Equal(CopyDesignApplyEntryAction.Reuse, entry.Entry.Action);
        Assert.Equal("cad-2", entry.Entry.Reuse!.CadDocumentId);
        Assert.Null(entry.Entry.Copy);
    }

    // P6D: drawings are mapped ALONGSIDE models by default (includeDrawings
    // defaults to true) - P6C's original "never mapped regardless of
    // proposed action" boundary is now conditional on includeDrawings:false
    // (the plan's explicit model-files-only acknowledgement) - see the two
    // tests immediately below.
    [Fact]
    public void DrawingNodesAreMappedAlongsideModelsByDefault()
    {
        var copyIpt = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var drawing = new CopyDesignNode(
            "cad-drawing", "fv-1", CadDocumentType.Idw, @"C:\src\a.idw", "a.idw",
            CadRelationshipKind.DrawingModel, true, true, true,
            CopyDesignAction.Copy, "b.idw", @"C:\dst\b.idw", Array.Empty<string>());
        var plan = Plan(new[] { copyIpt, drawing });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.True(result.Success);
        Assert.Equal(2, result.Request!.Entries.Count);
        var drawingEntry = Assert.Single(result.Request.Entries, e => e.Node.DocumentType == CadDocumentType.Idw);
        Assert.Equal(CopyDesignApplyEntryAction.Copy, drawingEntry.Entry.Action);
        Assert.Equal("cad-drawing", drawingEntry.Entry.Copy!.SourceCadDocumentId);
        Assert.Equal("b", drawingEntry.Entry.Copy.NewDocumentNumber); // derived from destination file name stem
        Assert.Equal(CadDocumentType.Idw, drawingEntry.Entry.Copy.DocumentType);
    }

    // required test list item 6: DWG -> IPT relationship maps correctly too
    // (an Inventor DWG drawing referencing an IPT part).
    [Fact]
    public void DwgCopyNodeMapsWithDwgDocumentType()
    {
        var drawing = new CopyDesignNode(
            "cad-dwg", "fv-1", CadDocumentType.Dwg, @"C:\src\a.dwg", "a.dwg",
            CadRelationshipKind.DrawingModel, true, true, true,
            CopyDesignAction.Copy, "b.dwg", @"C:\dst\b.dwg", Array.Empty<string>());
        var plan = Plan(new[] { drawing });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.True(result.Success);
        var entry = Assert.Single(result.Request!.Entries);
        Assert.Equal(CadDocumentType.Dwg, entry.Entry.Copy!.DocumentType);
    }

    // required test list item 2: drawing REUSE maps correctly.
    [Fact]
    public void DrawingReuseNodeMapsToReuseEntry()
    {
        var drawing = new CopyDesignNode(
            "cad-drawing", "fv-1", CadDocumentType.Idw, @"C:\src\a.idw", "a.idw",
            null, true, true, true, CopyDesignAction.Reuse, null, null, Array.Empty<string>());
        var plan = Plan(new[] { drawing });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.True(result.Success);
        var entry = Assert.Single(result.Request!.Entries);
        Assert.Equal(CopyDesignApplyEntryAction.Reuse, entry.Entry.Action);
        Assert.Equal("cad-drawing", entry.Entry.Reuse!.CadDocumentId);
    }

    [Fact]
    public void ExplicitIncludeDrawingsFalseOmitsDrawingsExactlyLikeP6COriginallyDid()
    {
        var copyIpt = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var drawing = new CopyDesignNode(
            "cad-drawing", "fv-1", CadDocumentType.Idw, @"C:\src\a.idw", "a.idw",
            CadRelationshipKind.DrawingModel, true, true, true,
            CopyDesignAction.Copy, "b.idw", @"C:\dst\b.idw", Array.Empty<string>());
        var plan = Plan(new[] { copyIpt, drawing });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null, includeDrawings: false);

        Assert.True(result.Success);
        Assert.Single(result.Request!.Entries);
        Assert.DoesNotContain(result.Request.Entries, e => e.Node.DocumentType is CadDocumentType.Idw or CadDocumentType.Dwg);
    }

    [Fact]
    public void OnlyDrawingsInPlan_WithIncludeDrawingsFalse_MeansNothingToApply()
    {
        var drawing = new CopyDesignNode(
            "cad-drawing", "fv-1", CadDocumentType.Idw, @"C:\src\a.idw", "a.idw",
            null, true, true, true, CopyDesignAction.Copy, "b.idw", @"C:\dst\b.idw", Array.Empty<string>(), IsRoot: true);
        var plan = Plan(new[] { drawing });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null, includeDrawings: false);

        Assert.False(result.Success);
        Assert.Contains("no in-scope", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnlyDrawingsInPlan_WithDefaultIncludeDrawings_MapsSuccessfully()
    {
        var drawing = new CopyDesignNode(
            "cad-drawing", "fv-1", CadDocumentType.Idw, @"C:\src\a.idw", "a.idw",
            null, true, true, true, CopyDesignAction.Copy, "b.idw", @"C:\dst\b.idw", Array.Empty<string>(), IsRoot: true);
        var plan = Plan(new[] { drawing });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.True(result.Success);
        Assert.Single(result.Request!.Entries);
    }

    // P6D ROUND 2, HIGH fix: was previously rejected here (the server's OLD
    // org-wide-unique-regardless-of-type documentNumber constraint made this
    // a genuine collision). Now that the server scopes uniqueness by
    // (documentNumber, documentType) - see the web repo's
    // 20260921183016_scope_document_number_uniqueness_by_type migration -
    // this is the NORMAL Inventor model/drawing pair and must succeed.
    [Fact]
    public void ADrawingAndAModelCopyRequestingTheSameDocumentNumberStemButDifferentTypesIsAllowed()
    {
        var model = CopyNode("cad-model", @"C:\src\FOO.ipt", @"C:\dst\FOO.ipt");
        var drawing = new CopyDesignNode(
            "cad-drawing", "fv-1", CadDocumentType.Idw, @"C:\src\FOO.idw", "FOO.idw",
            null, true, true, true, CopyDesignAction.Copy, "FOO.idw", @"C:\dst\FOO.idw", Array.Empty<string>());
        var plan = Plan(new[] { model, drawing });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.True(result.Success);
        Assert.Equal(2, result.Request!.Entries.Count);
        Assert.Contains(result.Request.Entries, e => e.Entry.Copy!.DocumentType == CadDocumentType.Ipt && e.Entry.Copy.NewDocumentNumber == "FOO");
        Assert.Contains(result.Request.Entries, e => e.Entry.Copy!.DocumentType == CadDocumentType.Idw && e.Entry.Copy.NewDocumentNumber == "FOO");
    }

    [Fact]
    public void TwoCopyEntriesWithTheSameDocumentNumberAndTheSameDocumentTypeAreStillRejected()
    {
        var modelA = CopyNode("cad-model-a", @"C:\src\a\FOO.ipt", @"C:\dst\a\FOO.ipt");
        var modelB = CopyNode("cad-model-b", @"C:\src\b\FOO.ipt", @"C:\dst\b\FOO.ipt");
        var plan = Plan(new[] { modelA, modelB });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.False(result.Success);
        Assert.Contains("same destination document number", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FOO", result.FailureReason);
    }

    [Fact]
    public void NeedsDecisionNodeFailsClosedEvenOnANominallyExecutablePlan()
    {
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt") with { ProposedAction = CopyDesignAction.NeedsDecision };
        var plan = Plan(new[] { node });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.False(result.Success);
    }

    [Fact]
    public void ExcludeNodeFailsClosed()
    {
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt") with { ProposedAction = CopyDesignAction.Exclude };
        var plan = Plan(new[] { node });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.False(result.Success);
    }

    [Fact]
    public void CopyNodeMissingStableIdentityFailsClosed()
    {
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt") with { CurrentFileVersionId = null };
        var plan = Plan(new[] { node });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.False(result.Success);
    }

    [Fact]
    public void BlankIdempotencyKeyIsRejected()
    {
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var plan = Plan(new[] { node });

        var result = CopyDesignApplyRequestMapper.Map(plan, "  ", null);

        Assert.False(result.Success);
    }
}
