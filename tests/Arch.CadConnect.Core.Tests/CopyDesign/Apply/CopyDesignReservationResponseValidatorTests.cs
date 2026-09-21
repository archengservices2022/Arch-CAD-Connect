using Arch.CadConnect.Core.CopyDesign.Apply;

using static Arch.CadConnect.Core.Tests.CopyDesign.Apply.CopyDesignApplyFixtures;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

public class CopyDesignReservationResponseValidatorTests
{
    private static CopyDesignApplyRequest CopyRequest(out CopyDesignApplyMappedEntry entry)
    {
        var node = CopyNode("cad-src", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var mapping = CopyDesignApplyRequestMapper.Map(CopyDesignApplyFixtures.Plan(new[] { node }), "key-1", null);
        entry = mapping.Request!.Entries[0];
        return mapping.Request!;
    }

    private static CopyDesignReservationResponseEntry OkCopyResponseEntry(CopyDesignApplyMappedEntry entry, string resultingId = "cad-new") => new(
        CopyDesignApplyEntryAction.Copy, entry.Entry.Copy!.SourceCadDocumentId, entry.Entry.Copy.SourceFileVersionId,
        resultingId, entry.Entry.Copy.NewDocumentNumber, entry.Entry.Copy.NewFileName, "IPT");

    // 8. COPY returned identity captured
    [Fact]
    public void ValidCopyResponseIsAcceptedAndIdentityCaptured()
    {
        var request = CopyRequest(out var entry);
        var response = new CopyDesignReservationResponse("c", "op-1", "now", new[] { OkCopyResponseEntry(entry) });

        var result = CopyDesignReservationResponseValidator.Validate(request, response);

        Assert.True(result.Success);
        Assert.Equal("op-1", result.CopyDesignOperationId);
        Assert.Equal("cad-new", result.Entries[0].ResultingCadDocumentId);
    }

    // 6. reservation response count mismatch fails
    [Fact]
    public void EntryCountMismatchFails()
    {
        var request = CopyRequest(out var entry);
        var response = new CopyDesignReservationResponse("c", "op-1", "now", Array.Empty<CopyDesignReservationResponseEntry>());

        var result = CopyDesignReservationResponseValidator.Validate(request, response);

        Assert.False(result.Success);
        Assert.Contains("entries", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    // 7. reservation response action mismatch fails
    [Fact]
    public void ActionMismatchFails()
    {
        var request = CopyRequest(out var entry);
        var response = new CopyDesignReservationResponse("c", "op-1", "now", new[]
        {
            new CopyDesignReservationResponseEntry(
                CopyDesignApplyEntryAction.Reuse, null, null, entry.Entry.Copy!.SourceCadDocumentId,
                entry.Entry.Copy.NewDocumentNumber, entry.Entry.Copy.NewFileName, "IPT"),
        });

        var result = CopyDesignReservationResponseValidator.Validate(request, response);

        Assert.False(result.Success);
        Assert.Contains("action", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CopyResultingIdentityEqualToSourceIsRejected()
    {
        var request = CopyRequest(out var entry);
        var response = new CopyDesignReservationResponse("c", "op-1", "now",
            new[] { OkCopyResponseEntry(entry, resultingId: entry.Entry.Copy!.SourceCadDocumentId) });

        var result = CopyDesignReservationResponseValidator.Validate(request, response);

        Assert.False(result.Success);
    }

    [Fact]
    public void CopyEchoedSourceMismatchIsRejected()
    {
        var request = CopyRequest(out var entry);
        var response = new CopyDesignReservationResponse("c", "op-1", "now", new[]
        {
            new CopyDesignReservationResponseEntry(
                CopyDesignApplyEntryAction.Copy, "some-other-source-id", entry.Entry.Copy!.SourceFileVersionId,
                "cad-new", entry.Entry.Copy.NewDocumentNumber, entry.Entry.Copy.NewFileName, "IPT"),
        });

        var result = CopyDesignReservationResponseValidator.Validate(request, response);

        Assert.False(result.Success);
    }

    // 9. REUSE returned identity must match intended identity
    [Fact]
    public void ReuseResultingIdentityMustExactlyMatchRequested()
    {
        var node = ReuseNode("cad-reuse", @"C:\src\std.ipt");
        var mapping = CopyDesignApplyRequestMapper.Map(CopyDesignApplyFixtures.Plan(new[] { node }), "key-1", null);
        var request = mapping.Request!;

        var wrongIdentity = new CopyDesignReservationResponse("c", "op-1", "now", new[]
        {
            new CopyDesignReservationResponseEntry(
                CopyDesignApplyEntryAction.Reuse, null, null, "cad-DIFFERENT", "10073-STD", "std.ipt", "IPT"),
        });
        Assert.False(CopyDesignReservationResponseValidator.Validate(request, wrongIdentity).Success);

        var rightIdentity = new CopyDesignReservationResponse("c", "op-1", "now", new[]
        {
            new CopyDesignReservationResponseEntry(
                CopyDesignApplyEntryAction.Reuse, null, null, "cad-reuse", "10073-STD", "std.ipt", "IPT"),
        });
        var result = CopyDesignReservationResponseValidator.Validate(request, rightIdentity);
        Assert.True(result.Success);
        Assert.Equal("cad-reuse", result.Entries[0].ResultingCadDocumentId);
    }

    // ==================================================================
    // CODEX FINAL AUDIT ROUND 1, MEDIUM 2: GLOBAL identity validation
    // across the WHOLE response, not merely per-entry self-consistency.
    // ==================================================================

    [Fact]
    public void DuplicateCopyResultingIdentityAcrossTwoEntriesIsRejected()
    {
        var nodeA = CopyNode("cad-a", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var nodeB = CopyNode("cad-b", @"C:\src\b.ipt", @"C:\dst\b-new.ipt");
        var mapping = CopyDesignApplyRequestMapper.Map(CopyDesignApplyFixtures.Plan(new[] { nodeA, nodeB }), "key-1", null);
        var request = mapping.Request!;

        var response = new CopyDesignReservationResponse("c", "op-1", "now", new[]
        {
            OkCopyResponseEntry(request.Entries[0], resultingId: "cad-SAME"),
            OkCopyResponseEntry(request.Entries[1], resultingId: "cad-SAME"),
        });

        var result = CopyDesignReservationResponseValidator.Validate(request, response);

        Assert.False(result.Success);
        Assert.Contains("same resultingCadDocumentId", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CopyResultingIdentityAliasingAnotherEntrysSourceIdentityIsRejected()
    {
        var nodeA = CopyNode("cad-a", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var nodeB = CopyNode("cad-b", @"C:\src\b.ipt", @"C:\dst\b-new.ipt");
        var mapping = CopyDesignApplyRequestMapper.Map(CopyDesignApplyFixtures.Plan(new[] { nodeA, nodeB }), "key-1", null);
        var request = mapping.Request!;

        // entry A's "new" identity aliases entry B's OWN source identity.
        var response = new CopyDesignReservationResponse("c", "op-1", "now", new[]
        {
            OkCopyResponseEntry(request.Entries[0], resultingId: "cad-b"),
            OkCopyResponseEntry(request.Entries[1], resultingId: "cad-b-new"),
        });

        var result = CopyDesignReservationResponseValidator.Validate(request, response);

        Assert.False(result.Success);
    }

    [Fact]
    public void CopyResultingIdentityAliasingAReuseEntrysIdentityIsRejected()
    {
        var copyNode = CopyNode("cad-a", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var reuseNode = ReuseNode("cad-reuse", @"C:\src\std.ipt");
        var mapping = CopyDesignApplyRequestMapper.Map(CopyDesignApplyFixtures.Plan(new[] { copyNode, reuseNode }), "key-1", null);
        var request = mapping.Request!;

        var response = new CopyDesignReservationResponse("c", "op-1", "now", new[]
        {
            OkCopyResponseEntry(request.Entries[0], resultingId: "cad-reuse"), // aliases the REUSE identity below
            new CopyDesignReservationResponseEntry(
                CopyDesignApplyEntryAction.Reuse, null, null, "cad-reuse", "10073-STD", "std.ipt", "IPT"),
        });

        var result = CopyDesignReservationResponseValidator.Validate(request, response);

        Assert.False(result.Success);
    }
}
