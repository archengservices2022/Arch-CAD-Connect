using Arch.CadConnect.Core;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.Tests.Workspace;

public class UndoTargetMemoryTests
{
    private const string Root = @"C:\ws\proj";
    private static readonly string PartPath = System.IO.Path.Combine(Root, "part.ipt");

    private static WorkspaceCheckoutBinding Binding(string checkoutId = "co_1", string baseFv = "fv_1") => new()
    {
        CheckoutId = checkoutId,
        BaseFileVersionId = baseFv,
        BaseVersionNumber = 3,
        BaseChecksum = "abc",
        BaseFileSize = 100,
    };

    private static CadDocumentContext CheckedOutDoc(
        string cadId = "cad_1", string docNumber = "PRT-1",
        string checkoutId = "co_1", string baseFv = "fv_1", string path = null!) =>
        CadDocumentContext.Create(path ?? PartPath, displayNameFallback: null, isDirty: false) with
        {
            PlmIdentity = new PlmIdentity(cadId, docNumber, baseFv, 3),
            CheckoutBinding = Binding(checkoutId, baseFv),
            CheckoutState = LocalCheckoutState.CheckedOutByMe,
            WorkspaceRoot = Root,
        };

    private static WorkspaceManifestEntry Entry(
        string cadId = "cad_1", string checkoutId = "co_1", string baseFv = "fv_1", bool checkedOut = true) => new()
    {
        RelativePath = "part.ipt",
        CadDocumentId = cadId,
        DocumentNumber = "PRT-1",
        FileName = "part.ipt",
        FileVersionId = baseFv,
        VersionNumber = 3,
        Checksum = "abc",
        FileSize = 100,
        State = WorkspaceManifestEntryState.Verified,
        Checkout = checkedOut ? Binding(checkoutId, baseFv) : null,
    };

    // ---- capture -----------------------------------------------------

    [Fact]
    public void Observe_captures_a_checked_out_managed_document()
    {
        var m = new UndoTargetMemory();
        m.Observe(CheckedOutDoc());
        Assert.NotNull(m.Current);
        Assert.Equal("cad_1", m.Current!.CadDocumentId);
        Assert.Equal("co_1", m.Current.CheckoutId);
        Assert.Equal("fv_1", m.Current.BaseFileVersionId);
        Assert.Equal("PRT-1", m.Current.DocumentNumber);
        Assert.Equal(PartPath, m.Current.File.AbsoluteFilePath);
    }

    [Fact]
    public void Observe_of_an_unmanaged_or_not_mine_document_never_creates_a_target()
    {
        var m = new UndoTargetMemory();
        m.Observe(CadDocumentContext.None);
        Assert.Null(m.Current);

        m.Observe(CadDocumentContext.Create(PartPath, null, false) with { CheckoutState = LocalCheckoutState.Controlled });
        Assert.Null(m.Current);
    }

    [Fact]
    public void Observe_keeps_the_existing_target_when_a_non_checked_out_doc_becomes_active_or_closes()
    {
        var m = new UndoTargetMemory();
        m.Observe(CheckedOutDoc());
        var captured = m.Current;

        m.Observe(CadDocumentContext.None);                       // document closed
        Assert.Same(captured, m.Current);

        m.Observe(CadDocumentContext.Create(@"C:\other\x.ipt", null, false)); // unrelated doc
        Assert.Same(captured, m.Current);
    }

    [Fact]
    public void Observe_replaces_with_the_most_recently_active_checkout_target()
    {
        var m = new UndoTargetMemory();
        m.Observe(CheckedOutDoc(cadId: "cad_A", docNumber: "A", checkoutId: "co_A", path: System.IO.Path.Combine(Root, "a.ipt")));
        m.Observe(CheckedOutDoc(cadId: "cad_B", docNumber: "B", checkoutId: "co_B", path: System.IO.Path.Combine(Root, "b.ipt")));
        Assert.Equal("cad_B", m.Current!.CadDocumentId);
        Assert.Equal("B", m.Current.DocumentNumber);
    }

    // ---- revalidation ----------------------------------------------

    private static void Revalidate(UndoTargetMemory m, WorkspaceManifestEntry? entry, string? root = Root, bool fileExists = true)
        => m.Revalidate(_ => entry, _ => fileExists, root);

    [Fact]
    public void Revalidate_keeps_a_still_checked_out_exact_binding()
    {
        var m = new UndoTargetMemory();
        m.Observe(CheckedOutDoc());
        Revalidate(m, Entry());
        Assert.NotNull(m.Current);
    }

    [Fact]
    public void Revalidate_drops_the_target_when_the_manifest_entry_is_gone()
    {
        var m = new UndoTargetMemory();
        m.Observe(CheckedOutDoc());
        Revalidate(m, null);
        Assert.Null(m.Current);
    }

    [Fact]
    public void Revalidate_drops_the_target_when_the_checkout_marker_was_cleared_by_a_check_in()
    {
        var m = new UndoTargetMemory();
        m.Observe(CheckedOutDoc());
        Revalidate(m, Entry(checkedOut: false));
        Assert.Null(m.Current);
    }

    [Fact]
    public void Revalidate_drops_the_target_on_a_checkoutId_or_base_identity_mismatch()
    {
        var m1 = new UndoTargetMemory(); m1.Observe(CheckedOutDoc());
        Revalidate(m1, Entry(checkoutId: "co_DIFFERENT"));
        Assert.Null(m1.Current);

        var m2 = new UndoTargetMemory(); m2.Observe(CheckedOutDoc());
        Revalidate(m2, Entry(baseFv: "fv_DIFFERENT"));
        Assert.Null(m2.Current);

        var m3 = new UndoTargetMemory(); m3.Observe(CheckedOutDoc());
        Revalidate(m3, Entry(cadId: "cad_DIFFERENT"));
        Assert.Null(m3.Current);
    }

    [Fact]
    public void Revalidate_drops_the_target_when_the_workspace_root_changed()
    {
        var m = new UndoTargetMemory();
        m.Observe(CheckedOutDoc());
        Revalidate(m, Entry(), root: @"C:\ws\other-project");
        Assert.Null(m.Current);
    }

    [Fact]
    public void Revalidate_drops_the_target_when_the_local_file_is_missing()
    {
        var m = new UndoTargetMemory();
        m.Observe(CheckedOutDoc());
        Revalidate(m, Entry(), fileExists: false);
        Assert.Null(m.Current);
    }

    [Fact]
    public void Clear_forgets_the_target_unconditionally()
    {
        var m = new UndoTargetMemory();
        m.Observe(CheckedOutDoc());
        m.Clear();
        Assert.Null(m.Current);
    }

    [Fact]
    public void RootsEqual_tolerates_a_trailing_separator_but_not_a_different_folder()
    {
        var t = new RememberedCheckoutTarget(
            ManagedFileRef.Create(Root, PartPath), "cad_1", "co_1", "fv_1", "PRT-1");
        Assert.True(t.IsStillUndoable(Entry(), Root + System.IO.Path.DirectorySeparatorChar));
        Assert.False(t.IsStillUndoable(Entry(), @"C:\ws\proj-2"));
        Assert.False(t.IsStillUndoable(Entry(), null));
    }
}
