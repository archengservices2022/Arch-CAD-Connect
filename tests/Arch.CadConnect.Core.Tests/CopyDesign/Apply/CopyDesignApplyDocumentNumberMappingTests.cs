using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;

using static Arch.CadConnect.Core.Tests.CopyDesign.CopyDesignFixtures;
using static Arch.CadConnect.Core.Tests.CopyDesign.Apply.CopyDesignApplyFixtures;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

/// <summary>P6C real-acceptance follow-up: proves EXACTLY what
///  <c>newDocumentNumber</c>/<c>newFileName</c> the CAD client sends to
///  <c>POST /api/desktop/copy-design/apply</c> for the real P4C-&gt;P6C
///  acceptance scenario, using the REAL <see cref="CopyDesignPlanner"/> (not a
///  hand-built fixture) so the FULL pipeline - naming rule, explicit COPY
///  decisions, mapping - is exercised exactly as the Ribbon controller runs
///  it. See <see cref="CopyDesignApplyRequestMapper"/>'s own "DOCUMENT
///  NUMBER" doc comment for the derivation rule this locks in.</summary>
public class CopyDesignApplyDocumentNumberMappingTests
{
    private static readonly string RootPath = P("Design", "P4C-REAL-ROOT.iam");
    private static readonly string PartAPath = P("Design", "P4C-REAL-PART-A.ipt");
    private static readonly string PartBPath = P("Design", "P4C-REAL-PART-B.ipt");

    private sealed class AlwaysFoundEmptyDrawingSource : IDrawingAssociationSource
    {
        public static readonly AlwaysFoundEmptyDrawingSource Instance = new();

        public DrawingAssociationResult GetAssociatedDrawings(string cadDocumentId) =>
            new(DrawingAssociationOutcome.Found, Array.Empty<AssociatedDrawing>());
    }

    private static CopyDesignApplyRequest MapAcceptScan(CopyDesignAction partBAction = CopyDesignAction.Copy)
    {
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, PartAPath, "cad_a", "fv_a1"),
            Managed(RootPath, PartBPath, "cad_b", "fv_b1"),
        });
        var tokenRule = new TokenReplaceNameRule("P4C", "P6C");
        var decisions = new Dictionary<string, CopyDesignAction>
        {
            ["cad_a"] = CopyDesignAction.Copy,
            ["cad_b"] = partBAction,
        };

        var plan = CopyDesignPlanner.Plan(
            scan, tokenRule, P("Dest"), null, AlwaysFoundEmptyDrawingSource.Instance, null, decisions);
        Assert.True(plan.IsExecutable);

        var mapping = CopyDesignApplyRequestMapper.Map(plan, "key-accept-1", null);
        Assert.True(mapping.Success, mapping.FailureReason);
        return mapping.Request!;
    }

    // ---- items 1-3: the exact acceptance destinations -------------------

    [Fact]
    public void Item1_Root_P4C_REAL_ROOT_iam_to_P6C_REAL_ROOT_iam_sends_P6C_REAL_ROOT()
    {
        var request = MapAcceptScan();
        var root = Assert.Single(request.Entries, e => e.Node.IsRoot);

        Assert.Equal(CopyDesignApplyEntryAction.Copy, root.Entry.Action);
        Assert.Equal("P6C-REAL-ROOT", root.Entry.Copy!.NewDocumentNumber);
        Assert.Equal("P6C-REAL-ROOT.iam", root.Entry.Copy.NewFileName);
    }

    [Fact]
    public void Item2_PartA_sends_P6C_REAL_PART_A()
    {
        var request = MapAcceptScan();
        var a = Assert.Single(request.Entries, e => e.Node.CadDocumentId == "cad_a");

        Assert.Equal("P6C-REAL-PART-A", a.Entry.Copy!.NewDocumentNumber);
        Assert.Equal("P6C-REAL-PART-A.ipt", a.Entry.Copy.NewFileName);
    }

    [Fact]
    public void Item3_PartB_sends_P6C_REAL_PART_B()
    {
        var request = MapAcceptScan();
        var b = Assert.Single(request.Entries, e => e.Node.CadDocumentId == "cad_b");

        Assert.Equal("P6C-REAL-PART-B", b.Entry.Copy!.NewDocumentNumber);
        Assert.Equal("P6C-REAL-PART-B.ipt", b.Entry.Copy.NewFileName);
    }

    // ---- items 4-5: never a source or fixture document number -----------

    [Fact]
    public void Item4And5_Source_and_fixture_document_numbers_are_NEVER_sent_as_newDocumentNumber()
    {
        var request = MapAcceptScan();
        var sentNumbers = request.Entries
            .Where(e => e.Entry.Action == CopyDesignApplyEntryAction.Copy)
            .Select(e => e.Entry.Copy!.NewDocumentNumber)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // The real acceptance DB already has exactly these six numbers -
        // none of them may ever appear as a destination number for THIS
        // operation (item 4: the disposable P6C-ACCEPT-* fixture identity;
        // item 5: the P4C-REAL-* source identity).
        string[] forbidden =
        {
            "P4C-REAL-ROOT", "P4C-REAL-PART-A", "P4C-REAL-PART-B",
            "P6C-ACCEPT-ROOT", "P6C-ACCEPT-PART-A", "P6C-ACCEPT-PART-B",
        };
        foreach (var number in sentNumbers)
        {
            Assert.DoesNotContain(number, forbidden);
        }
        Assert.Equal(new[] { "P6C-REAL-PART-A", "P6C-REAL-PART-B", "P6C-REAL-ROOT" }, sentNumbers);
    }

    // ---- item 6: newFileName is the exact final destination file name ---

    [Fact]
    public void Item6_NewFileName_is_the_exact_final_destination_file_name_never_the_source_name()
    {
        var request = MapAcceptScan();
        foreach (var entry in request.Entries.Where(e => e.Entry.Action == CopyDesignApplyEntryAction.Copy))
        {
            Assert.Equal(entry.Node.ProposedDestinationFileName, entry.Entry.Copy!.NewFileName);
            Assert.NotEqual(entry.Node.SourceFileName, entry.Entry.Copy.NewFileName);
        }
    }

    // ---- item 7: a nested/path-containing destination never leaks
    //      directory text into the document number ------------------------

    [Fact]
    public void Item7_A_destination_under_nested_subfolders_derives_the_document_number_from_the_file_name_only()
    {
        var node = CopyNode("cad-1", @"C:\src\A.ipt", @"C:\dst\sub\folder\FOO.ipt");
        var plan = Plan(new[] { node });

        var mapping = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.True(mapping.Success);
        var entry = Assert.Single(mapping.Request!.Entries);
        Assert.Equal("FOO", entry.Entry.Copy!.NewDocumentNumber);
        Assert.DoesNotContain("\\", entry.Entry.Copy.NewDocumentNumber, StringComparison.Ordinal);
        Assert.DoesNotContain("dst", entry.Entry.Copy.NewDocumentNumber, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("folder", entry.Entry.Copy.NewDocumentNumber, StringComparison.OrdinalIgnoreCase);
    }

    // ---- item 8: a blank/invalid destination cannot create a request -----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Item8_A_blank_destination_file_name_cannot_create_a_reservation_request(string? blankFileName)
    {
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt") with { ProposedDestinationFileName = blankFileName };
        var plan = Plan(new[] { node });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.False(result.Success);
        Assert.Null(result.Request);
    }

    [Fact]
    public void Item8_A_null_destination_absolute_path_cannot_create_a_reservation_request()
    {
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt") with { ProposedDestinationAbsolutePath = null };
        var plan = Plan(new[] { node });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.False(result.Success);
        Assert.Null(result.Request);
    }

    // ---- item 9: mapping is deterministic --------------------------------

    [Fact]
    public void Item9_Mapping_the_SAME_confirmed_plan_twice_produces_IDENTICAL_document_numbers_and_file_names()
    {
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, PartAPath, "cad_a", "fv_a1"),
            Managed(RootPath, PartBPath, "cad_b", "fv_b1"),
        });
        var tokenRule = new TokenReplaceNameRule("P4C", "P6C");
        var decisions = new Dictionary<string, CopyDesignAction> { ["cad_a"] = CopyDesignAction.Copy, ["cad_b"] = CopyDesignAction.Copy };
        var plan = CopyDesignPlanner.Plan(scan, tokenRule, P("Dest"), null, AlwaysFoundEmptyDrawingSource.Instance, null, decisions);

        var mapping1 = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);
        var mapping2 = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.True(mapping1.Success);
        Assert.True(mapping2.Success);
        var numbers1 = mapping1.Request!.Entries.Select(e => (e.Node.CadDocumentId, e.Entry.Copy?.NewDocumentNumber, e.Entry.Copy?.NewFileName)).ToArray();
        var numbers2 = mapping2.Request!.Entries.Select(e => (e.Node.CadDocumentId, e.Entry.Copy?.NewDocumentNumber, e.Entry.Copy?.NewFileName)).ToArray();
        Assert.Equal(numbers1, numbers2);
    }

    // ---- item 10: REUSE entries never create a newDocumentNumber ---------

    [Fact]
    public void Item10_A_REUSE_decided_entry_carries_no_Copy_payload_and_therefore_no_newDocumentNumber()
    {
        var request = MapAcceptScan(partBAction: CopyDesignAction.Reuse);
        var b = Assert.Single(request.Entries, e => e.Node.CadDocumentId == "cad_b");

        Assert.Equal(CopyDesignApplyEntryAction.Reuse, b.Entry.Action);
        Assert.Null(b.Entry.Copy); // no newDocumentNumber field is even present
        Assert.Equal("cad_b", b.Entry.Reuse!.CadDocumentId); // identity preserved, not renumbered
    }

    // ---- item 11: mapping is bound to stable node identity, not row/order
    //      or path text ----------------------------------------------------

    [Fact]
    public void Item11_Each_mapped_entry_stays_bound_to_its_OWN_node_identity_regardless_of_input_order()
    {
        var nodeA = CopyNode("cad_a", @"C:\src\AAA.ipt", @"C:\dst\AAA-new.ipt");
        var nodeB = CopyNode("cad_b", @"C:\src\BBB.ipt", @"C:\dst\BBB-new.ipt");

        var forward = CopyDesignApplyRequestMapper.Map(Plan(new[] { nodeA, nodeB }), "key-1", null);
        var reversed = CopyDesignApplyRequestMapper.Map(Plan(new[] { nodeB, nodeA }), "key-1", null);

        Assert.True(forward.Success);
        Assert.True(reversed.Success);
        foreach (var mapping in new[] { forward, reversed })
        {
            var a = Assert.Single(mapping.Request!.Entries, e => e.Node.CadDocumentId == "cad_a");
            var b = Assert.Single(mapping.Request!.Entries, e => e.Node.CadDocumentId == "cad_b");
            Assert.Equal("AAA-new", a.Entry.Copy!.NewDocumentNumber);
            Assert.Equal("BBB-new", b.Entry.Copy!.NewDocumentNumber);
        }
    }

    // (Item 12 - no physical copier runs if reservation fails - is already
    // proven for CopyDesignApplyOutcome.ReservationCallFailed by
    // CopyDesignApplyOrchestratorTests.ReservationCallFailureIsReportedWithoutAnOperationId
    // ("Assert.Empty(h.Copier.Calls)"), the EXACT outcome this real
    // acceptance run observed - preserved unchanged.)

    // ---- local, pre-HTTP fail-closed on an intra-request document-number
    //      collision (the "also inspect" follow-up) ------------------------

    // P6D ROUND 2, HIGH fix: was previously rejected here (the server's OLD
    // org-wide-unique-regardless-of-type documentNumber constraint made
    // this a genuine collision). Now that the server scopes uniqueness by
    // (documentNumber, documentType), two DIFFERENT document types sharing
    // one destination stem are explicitly ALLOWED - see
    // 20260921183016_scope_document_number_uniqueness_by_type in the web
    // repo.
    [Fact]
    public void An_IAM_and_an_IPT_sharing_the_same_destination_stem_in_different_paths_is_ALLOWED()
    {
        var iam = CopyNode("cad-iam", @"C:\src\a.iam", @"C:\dst\one\FOO.iam", type: CadDocumentType.Iam);
        var ipt = CopyNode("cad-ipt", @"C:\src\b.ipt", @"C:\dst\two\FOO.ipt", type: CadDocumentType.Ipt);
        var plan = Plan(new[] { iam, ipt });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.True(result.Success);
        Assert.Equal(2, result.Request!.Entries.Count);
    }

    [Fact]
    public void Two_IAMs_sharing_the_same_destination_stem_in_different_paths_is_STILL_rejected_LOCALLY()
    {
        // Two DIFFERENT destination paths (different sub-folder, SAME
        // extension/type) that would still collide on (document number,
        // document type) - never caught by CopyDesignPlanner's own
        // duplicate-destination check (which compares full paths).
        var iamA = CopyNode("cad-iam-a", @"C:\src\a.iam", @"C:\dst\one\FOO.iam", type: CadDocumentType.Iam);
        var iamB = CopyNode("cad-iam-b", @"C:\src\b.iam", @"C:\dst\two\FOO.iam", type: CadDocumentType.Iam);
        var plan = Plan(new[] { iamA, iamB });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.False(result.Success);
        Assert.Contains("same destination document number", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FOO", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_number_collision_is_detected_case_insensitively()
    {
        var a = CopyNode("cad-a", @"C:\src\a.ipt", @"C:\dst\FOO.ipt");
        var b = CopyNode("cad-b", @"C:\src\b.iam", @"C:\dst\sub\foo.iam");
        var plan = Plan(new[] { a, b });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.False(result.Success);
    }
}
