using Arch.CadConnect.Core;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.Tests.Workspace;

public class WorkspaceManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arch-cc-mf-" + Guid.NewGuid().ToString("N"));

    public WorkspaceManifestTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static WorkspaceManifestRunContext Context() =>
        new("https://plm.example.com", "org1", "ACME", "cad_root", "ASM-1");

    private static WorkspacePlan PlanWith(params (string cad, string rel, string fv, string sha, long size)[] entries) => new()
    {
        Contract = new WorkspacePlanContractRef { Id = WorkspacePlanContract.Id, Version = WorkspacePlanContract.Version },
        Root = new WorkspacePlanRoot { CadDocumentId = "cad_root" },
        Safe = true,
        Entries = entries.Select(e => new WorkspacePlanEntry
        {
            CadDocumentId = e.cad,
            DocumentNumber = e.cad.ToUpperInvariant(),
            FileName = e.rel,
            CadType = "IPT",
            IsRoot = e.cad == "cad_root",
            Placement = new WorkspacePlanPlacement { GeneratedRelativePath = e.rel },
            CanMaterialize = true,
            Version = new WorkspacePlanVersion
            {
                FileVersionId = e.fv,
                VersionNumber = 3,
                Checksum = e.sha,
                FileSize = e.size,
                OriginalFileName = e.rel,
                ContentPath = $"/api/file-versions/{e.fv}/content",
            },
        }).ToArray(),
    };

    private static MaterializationEntryResult Result(
        string cad, string rel, string fv, MaterializationStatus status, string? sha = null) => new()
    {
        CadDocumentId = cad,
        FileVersionId = fv,
        RelativePath = rel,
        Status = status,
        Checksum = sha,
        DocumentNumber = cad.ToUpperInvariant(),
    };

    private static MaterializationReport ReportWith(params MaterializationEntryResult[] results) => new()
    {
        WorkspaceRoot = "x",
        RootCadDocumentId = "cad_root",
        StartedAtUtc = Now,
        FinishedAtUtc = Now,
        RequestedEntries = results.Length,
        Results = results,
    };

    [Fact]
    public void LoadOrEmpty_returns_empty_when_the_manifest_is_missing()
    {
        var mf = WorkspaceManifest.LoadOrEmpty(_root);
        Assert.Empty(mf.Entries);
    }

    [Fact]
    public void ApplyRun_then_SaveAtomic_round_trips_verified_entries()
    {
        var plan = PlanWith(("cad_root", "asm.iam", "fv_1", "aaa", 10), ("cad_b", "part.ipt", "fv_2", "bbb", 20));
        var report = ReportWith(
            Result("cad_root", "asm.iam", "fv_1", MaterializationStatus.Downloaded, "aaa"),
            Result("cad_b", "part.ipt", "fv_2", MaterializationStatus.AlreadyCurrent, "bbb"));

        var mf = WorkspaceManifest.LoadOrEmpty(_root);
        mf.ApplyRun(report, plan, Context(), Now);
        mf.SaveAtomic();

        Assert.True(File.Exists(mf.ManifestFilePath));
        var reloaded = WorkspaceManifest.LoadOrEmpty(_root);
        Assert.Equal(2, reloaded.Entries.Count);

        var root = reloaded.Entries.Single(e => e.RelativePath == "asm.iam");
        Assert.Equal("cad_root", root.CadDocumentId);
        Assert.Equal("fv_1", root.FileVersionId);
        Assert.Equal("aaa", root.Checksum);
        Assert.True(root.IsRoot);
        Assert.Equal(WorkspaceManifestEntryState.Verified, root.State);
    }

    [Fact]
    public void ApplyRun_a_blocked_entry_that_was_previously_bound_is_downgraded_not_kept_as_current()
    {
        var plan = PlanWith(("cad_b", "part.ipt", "fv_2", "bbb", 20));

        // first run: verified
        var mf = WorkspaceManifest.LoadOrEmpty(_root);
        mf.ApplyRun(ReportWith(Result("cad_b", "part.ipt", "fv_2", MaterializationStatus.Downloaded, "bbb")), plan, Context(), Now);
        mf.SaveAtomic();

        // second run: the user edited part.ipt -> local-conflict -> blocked
        var mf2 = WorkspaceManifest.LoadOrEmpty(_root);
        mf2.ApplyRun(ReportWith(Result("cad_b", "part.ipt", "fv_2", MaterializationStatus.Blocked)), plan, Context(), Now);
        mf2.SaveAtomic();

        var reloaded = WorkspaceManifest.LoadOrEmpty(_root);
        var entry = Assert.Single(reloaded.Entries);
        Assert.Equal("cad_b", entry.CadDocumentId);                         // identity kept
        Assert.Equal(WorkspaceManifestEntryState.Unverified, entry.State);  // no currency claim
    }

    [Fact]
    public void ApplyRun_a_blocked_entry_with_no_prior_binding_writes_nothing()
    {
        var plan = PlanWith(("cad_c", "c.ipt", "fv_3", "ccc", 30));
        var mf = WorkspaceManifest.LoadOrEmpty(_root);
        mf.ApplyRun(ReportWith(Result("cad_c", "c.ipt", "fv_3", MaterializationStatus.Failed)), plan, Context(), Now);
        Assert.Empty(mf.Entries);
    }

    [Fact]
    public void LoadOrEmpty_treats_a_corrupt_manifest_as_no_bindings_never_throws()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".arch"));
        File.WriteAllText(Path.Combine(_root, WorkspaceManifest.RelativeManifestPath), "{ not json ]");
        var mf = WorkspaceManifest.LoadOrEmpty(_root);
        Assert.Empty(mf.Entries);
    }

    [Fact]
    public void FindByAbsolutePath_matches_root_plus_relativePath_only_never_by_filename()
    {
        var plan = PlanWith(("cad_b", "part.ipt", "fv_2", "bbb", 20));
        var mf = WorkspaceManifest.LoadOrEmpty(_root);
        mf.ApplyRun(ReportWith(Result("cad_b", "part.ipt", "fv_2", MaterializationStatus.Downloaded, "bbb")), plan, Context(), Now);

        var match = mf.FindByAbsolutePath(Path.Combine(_root, "part.ipt"));
        Assert.NotNull(match);
        Assert.Equal("cad_b", match!.CadDocumentId);

        // same filename, DIFFERENT directory -> not a match (no filename inference)
        var otherDir = Path.Combine(Path.GetTempPath(), "somewhere-else", "part.ipt");
        Assert.Null(mf.FindByAbsolutePath(otherDir));

        var identity = match.ToPlmIdentity();
        Assert.Equal("cad_b", identity.CadDocumentId);
        Assert.Equal("fv_2", identity.FileVersionId);
    }
}
