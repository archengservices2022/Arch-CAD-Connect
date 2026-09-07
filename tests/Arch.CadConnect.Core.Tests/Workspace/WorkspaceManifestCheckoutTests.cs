using System.Text.Json;

using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.Tests.Workspace;

public class WorkspaceManifestCheckoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arch-cc-mfco-" + Guid.NewGuid().ToString("N"));

    public WorkspaceManifestCheckoutTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private string PartPath => Path.Combine(_root, "part.ipt");

    /// <summary>Seed a P4B-era (no checkout field) manifest with one Verified entry.</summary>
    private void SeedP4bManifest()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".arch"));
        var json = """
        {"schema":"arch-plm.workspace-manifest.v1","serverOrigin":"https://plm.example.com",
         "organizationId":"org1","organizationCode":"ACME","rootCadDocumentId":"cad_root",
         "rootDocumentNumber":"ASM-1","updatedAtUtc":"2026-09-05T00:00:00Z",
         "entries":[
           {"relativePath":"part.ipt","cadDocumentId":"cad_part","documentNumber":"PRT-1","fileName":"part.ipt",
            "cadType":"IPT","fileVersionId":"fv_1","versionNumber":3,"checksum":"abc123","fileSize":1000,
            "isRoot":false,"dependsOn":[],"state":"Verified","retrievedAtUtc":"2026-09-05T00:00:00Z"}]}
        """;
        File.WriteAllText(Path.Combine(_root, WorkspaceManifest.RelativeManifestPath), json);
    }

    private static WorkspaceCheckoutBinding Binding() => new()
    {
        CheckoutId = "co_1",
        BaseFileVersionId = "fv_1",
        BaseVersionNumber = 3,
        BaseChecksum = "abc123",
        BaseFileSize = 1000,
        CheckedOutAtUtc = Now,
    };

    [Fact]
    public void A_P4B_era_manifest_loads_and_round_trips_with_no_checkout_field()
    {
        SeedP4bManifest();
        var mf = WorkspaceManifest.LoadOrEmpty(_root);
        var entry = Assert.Single(mf.Entries);
        Assert.Null(entry.Checkout);

        // saving it back must not emit a "checkout" key
        mf.MarkUnverified(PartPath);
        var text = File.ReadAllText(Path.Combine(_root, WorkspaceManifest.RelativeManifestPath));
        Assert.DoesNotContain("\"checkout\"", text);
    }

    [Fact]
    public void MarkCheckedOut_then_reload_carries_the_full_base_binding()
    {
        SeedP4bManifest();
        var mf = WorkspaceManifest.LoadOrEmpty(_root);

        Assert.True(mf.MarkCheckedOut(PartPath, Binding()));

        var reloaded = WorkspaceManifest.LoadOrEmpty(_root);
        var co = Assert.Single(reloaded.Entries).Checkout;
        Assert.NotNull(co);
        Assert.Equal("co_1", co!.CheckoutId);
        Assert.Equal("fv_1", co.BaseFileVersionId);
        Assert.Equal(3, co.BaseVersionNumber);
        Assert.Equal("abc123", co.BaseChecksum);
        Assert.Equal(1000, co.BaseFileSize);
    }

    [Fact]
    public void ClearCheckout_removes_the_marker_but_keeps_identity()
    {
        SeedP4bManifest();
        var mf = WorkspaceManifest.LoadOrEmpty(_root);
        mf.MarkCheckedOut(PartPath, Binding());

        Assert.True(WorkspaceManifest.LoadOrEmpty(_root).ClearCheckout(PartPath));

        var entry = Assert.Single(WorkspaceManifest.LoadOrEmpty(_root).Entries);
        Assert.Null(entry.Checkout);
        Assert.Equal("cad_part", entry.CadDocumentId);
        Assert.Equal("fv_1", entry.FileVersionId);
    }

    [Fact]
    public void RebindToNewVersion_points_at_the_new_version_Verified_and_clears_the_checkout()
    {
        SeedP4bManifest();
        var mf = WorkspaceManifest.LoadOrEmpty(_root);
        mf.MarkCheckedOut(PartPath, Binding());

        Assert.True(WorkspaceManifest.LoadOrEmpty(_root)
            .RebindToNewVersion(PartPath, "fv_2", 4, "def456", 2048, Now));

        var entry = Assert.Single(WorkspaceManifest.LoadOrEmpty(_root).Entries);
        Assert.Equal("fv_2", entry.FileVersionId);
        Assert.Equal(4, entry.VersionNumber);
        Assert.Equal("def456", entry.Checksum);
        Assert.Equal(2048, entry.FileSize);
        Assert.Equal(WorkspaceManifestEntryState.Verified, entry.State);
        Assert.Null(entry.Checkout);
    }

    [Fact]
    public void MarkUnverified_downgrades_state_and_optionally_clears_the_checkout()
    {
        SeedP4bManifest();
        var mf = WorkspaceManifest.LoadOrEmpty(_root);
        mf.MarkCheckedOut(PartPath, Binding());

        Assert.True(WorkspaceManifest.LoadOrEmpty(_root).MarkUnverified(PartPath, clearCheckout: false));
        var kept = Assert.Single(WorkspaceManifest.LoadOrEmpty(_root).Entries);
        Assert.Equal(WorkspaceManifestEntryState.Unverified, kept.State);
        Assert.NotNull(kept.Checkout);

        Assert.True(WorkspaceManifest.LoadOrEmpty(_root).MarkUnverified(PartPath, clearCheckout: true));
        Assert.Null(Assert.Single(WorkspaceManifest.LoadOrEmpty(_root).Entries).Checkout);
    }

    [Fact]
    public void Helpers_never_create_an_entry_and_never_match_by_filename()
    {
        SeedP4bManifest();
        var mf = WorkspaceManifest.LoadOrEmpty(_root);

        // same filename, different directory -> no match, nothing written
        Assert.False(mf.MarkCheckedOut(Path.Combine(_root, "sub", "part.ipt"), Binding()));
        Assert.False(mf.ClearCheckout(Path.Combine(Path.GetTempPath(), "elsewhere", "part.ipt")));
        Assert.Null(Assert.Single(WorkspaceManifest.LoadOrEmpty(_root).Entries).Checkout);
    }

    [Fact]
    public void A_corrupt_manifest_is_still_treated_as_no_bindings()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".arch"));
        File.WriteAllText(Path.Combine(_root, WorkspaceManifest.RelativeManifestPath), "}{ not json");
        var mf = WorkspaceManifest.LoadOrEmpty(_root);
        Assert.Empty(mf.Entries);
        Assert.False(mf.MarkCheckedOut(PartPath, Binding()));
    }

    [Fact]
    public void A_persistence_failure_is_observable_the_helper_throws()
    {
        SeedP4bManifest();
        var mf = WorkspaceManifest.LoadOrEmpty(_root);

        // hold the manifest open so the atomic replace cannot happen
        using var hold = new FileStream(
            Path.Combine(_root, WorkspaceManifest.RelativeManifestPath),
            FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Throws<WorkspaceManifestPersistException>(() => mf.MarkCheckedOut(PartPath, Binding()));
        Assert.Throws<WorkspaceManifestPersistException>(() => mf.MarkUnverified(PartPath));
        Assert.Throws<WorkspaceManifestPersistException>(
            () => mf.RebindToNewVersion(PartPath, "fv_2", 2, "x", 1, Now));
    }

    [Fact]
    public void A_no_match_still_returns_false_without_touching_the_disk()
    {
        SeedP4bManifest();
        var mf = WorkspaceManifest.LoadOrEmpty(_root);
        using var hold = new FileStream(
            Path.Combine(_root, WorkspaceManifest.RelativeManifestPath),
            FileMode.Open, FileAccess.Read, FileShare.Read);

        // no entry matches -> false, and NO save is attempted (no throw)
        Assert.False(mf.ClearCheckout(Path.Combine(_root, "sub", "other.ipt")));
    }

    [Fact]
    public void The_checkout_binding_serializes_with_lowerCamel_keys()
    {
        SeedP4bManifest();
        WorkspaceManifest.LoadOrEmpty(_root).MarkCheckedOut(PartPath, Binding());
        var text = File.ReadAllText(Path.Combine(_root, WorkspaceManifest.RelativeManifestPath));
        using var doc = JsonDocument.Parse(text);
        var checkout = doc.RootElement.GetProperty("entries")[0].GetProperty("checkout");
        Assert.Equal("co_1", checkout.GetProperty("checkoutId").GetString());
        Assert.Equal("fv_1", checkout.GetProperty("baseFileVersionId").GetString());
        Assert.Equal(1000, checkout.GetProperty("baseFileSize").GetInt64());
    }
}
