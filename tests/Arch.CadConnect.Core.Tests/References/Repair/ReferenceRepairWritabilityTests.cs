using Arch.CadConnect.Core.References;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.Tests.References.Repair;

/// <summary>
/// The referencing document must already be modifiable under existing Arch
/// rules - P5C never checks it out and never clears read-only protection.
/// </summary>
public class ReferenceRepairWritabilityTests
{
    private const string Path = @"C:\ws\job1\ROOT.iam";

    private static WorkspaceManifestEntry CheckedOutEntry(string checkoutId = "co_1", string baseVersion = "fv_1") => new()
    {
        CadDocumentId = "cad_root",
        FileVersionId = "fv_1",
        State = WorkspaceManifestEntryState.Verified,
        Checkout = new WorkspaceCheckoutBinding
        {
            CheckoutId = checkoutId,
            BaseFileVersionId = baseVersion,
        },
    };

    [Fact]
    public void Checked_out_by_me_is_writable()
    {
        var ctx = ReferenceRepairWritability.Evaluate(Path, LocalCheckoutState.CheckedOutByMe,
            onDiskWritable: true, authoritativeCheckoutConfirmed: true);
        Assert.True(ctx.IsWritable);
    }

    [Fact]
    public void Local_checkout_marker_without_server_confirmation_is_not_writable()
    {
        Assert.False(ReferenceRepairWritability.Evaluate(Path, LocalCheckoutState.CheckedOutByMe,
            onDiskWritable: true, authoritativeCheckoutConfirmed: false).IsWritable);
    }

    [Fact]
    public void Server_confirmed_checkout_does_not_override_read_only_file()
    {
        Assert.False(ReferenceRepairWritability.Evaluate(Path, LocalCheckoutState.CheckedOutByMe,
            onDiskWritable: false, authoritativeCheckoutConfirmed: true).IsWritable);
    }

    [Theory]
    [InlineData(LocalCheckoutState.Controlled)]
    [InlineData(LocalCheckoutState.CheckedOutByOther)]
    [InlineData(LocalCheckoutState.Unverified)]
    public void A_managed_document_that_is_not_checked_out_by_me_is_not_writable(LocalCheckoutState state)
    {
        var ctx = ReferenceRepairWritability.Evaluate(Path, state, onDiskWritable: true);
        Assert.False(ctx.IsWritable);
        Assert.False(string.IsNullOrWhiteSpace(ctx.WritabilityDetail));
    }

    [Fact]
    public void An_unmanaged_document_is_writable_only_when_its_file_is_writable_on_disk()
    {
        Assert.True(ReferenceRepairWritability.Evaluate(Path, LocalCheckoutState.Unmanaged, onDiskWritable: true).IsWritable);
        Assert.False(ReferenceRepairWritability.Evaluate(Path, LocalCheckoutState.Unmanaged, onDiskWritable: false).IsWritable);
    }

    [Fact]
    public void Authoritative_checkout_requires_server_mine_and_matching_checkout_and_base()
    {
        var entry = CheckedOutEntry();
        Assert.True(ReferenceRepairWritability.AuthoritativeCheckoutMatches(entry,
            new ServerCheckoutStatus(ServerCheckoutState.Mine, CheckoutId: "co_1", BaseFileVersionId: "fv_1")));
        Assert.False(ReferenceRepairWritability.AuthoritativeCheckoutMatches(entry,
            new ServerCheckoutStatus(ServerCheckoutState.Available)));
        Assert.False(ReferenceRepairWritability.AuthoritativeCheckoutMatches(entry,
            new ServerCheckoutStatus(ServerCheckoutState.Mine, CheckoutId: "co_other", BaseFileVersionId: "fv_1")));
        Assert.False(ReferenceRepairWritability.AuthoritativeCheckoutMatches(entry,
            new ServerCheckoutStatus(ServerCheckoutState.Mine, CheckoutId: "co_1", BaseFileVersionId: "fv_other")));
        Assert.False(ReferenceRepairWritability.AuthoritativeCheckoutMatches(null,
            new ServerCheckoutStatus(ServerCheckoutState.Mine)));
    }

    // ---- FINDING 2: a partial "mine" is never authoritative -------

    [Fact]
    public void A_server_mine_missing_the_checkout_id_is_not_authoritative()
    {
        var entry = CheckedOutEntry();
        Assert.False(ReferenceRepairWritability.AuthoritativeCheckoutMatches(entry,
            new ServerCheckoutStatus(ServerCheckoutState.Mine, CheckoutId: null, BaseFileVersionId: "fv_1")));
    }

    [Fact]
    public void A_server_mine_with_a_blank_checkout_id_is_not_authoritative()
    {
        var entry = CheckedOutEntry();
        Assert.False(ReferenceRepairWritability.AuthoritativeCheckoutMatches(entry,
            new ServerCheckoutStatus(ServerCheckoutState.Mine, CheckoutId: "   ", BaseFileVersionId: "fv_1")));
    }

    [Fact]
    public void A_server_mine_missing_the_base_fileVersionId_is_not_authoritative()
    {
        var entry = CheckedOutEntry();
        Assert.False(ReferenceRepairWritability.AuthoritativeCheckoutMatches(entry,
            new ServerCheckoutStatus(ServerCheckoutState.Mine, CheckoutId: "co_1", BaseFileVersionId: null)));
    }

    [Fact]
    public void A_server_mine_with_a_blank_base_fileVersionId_is_not_authoritative()
    {
        var entry = CheckedOutEntry();
        Assert.False(ReferenceRepairWritability.AuthoritativeCheckoutMatches(entry,
            new ServerCheckoutStatus(ServerCheckoutState.Mine, CheckoutId: "co_1", BaseFileVersionId: "")));
    }

    [Fact]
    public void A_local_binding_missing_its_checkout_id_or_base_is_not_authoritative()
    {
        var noCheckoutId = CheckedOutEntry(checkoutId: "", baseVersion: "fv_1");
        Assert.False(ReferenceRepairWritability.AuthoritativeCheckoutMatches(noCheckoutId,
            new ServerCheckoutStatus(ServerCheckoutState.Mine, CheckoutId: "co_1", BaseFileVersionId: "fv_1")));
    }
}
