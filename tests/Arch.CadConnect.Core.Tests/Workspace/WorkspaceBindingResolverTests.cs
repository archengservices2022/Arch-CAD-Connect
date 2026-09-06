using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.Tests.Workspace;

public class WorkspaceBindingResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arch-cc-bind-" + Guid.NewGuid().ToString("N"));

    public WorkspaceBindingResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private void WriteManifest(params (string cad, string rel, string fv, MaterializationStatus status)[] entries)
    {
        var plan = new WorkspacePlan
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
                    VersionNumber = 2,
                    Checksum = "sha-" + e.fv,
                    FileSize = 10,
                    OriginalFileName = e.rel,
                    ContentPath = $"/api/file-versions/{e.fv}/content",
                },
            }).ToArray(),
        };

        var report = new MaterializationReport
        {
            WorkspaceRoot = _root,
            RootCadDocumentId = "cad_root",
            StartedAtUtc = Now,
            FinishedAtUtc = Now,
            RequestedEntries = entries.Length,
            Results = entries.Select(e => new MaterializationEntryResult
            {
                CadDocumentId = e.cad,
                FileVersionId = e.fv,
                RelativePath = e.rel,
                Status = e.status,
                Checksum = e.status is MaterializationStatus.Downloaded or MaterializationStatus.AlreadyCurrent ? "sha-" + e.fv : null,
                DocumentNumber = e.cad.ToUpperInvariant(),
            }).ToArray(),
        };

        var mf = WorkspaceManifest.LoadOrEmpty(_root);
        mf.ApplyRun(report, plan, new WorkspaceManifestRunContext("https://plm.example.com", "org1", "ACME", "cad_root", "ASM-1"), Now);
        mf.SaveAtomic();
    }

    [Fact]
    public void Resolves_the_stable_identity_for_an_exact_root_plus_relativePath_match()
    {
        WriteManifest(("cad_root", "asm.iam", "fv_1", MaterializationStatus.Downloaded));

        var binding = WorkspaceBindingResolver.Resolve(Path.Combine(_root, "asm.iam"), new[] { _root });

        Assert.NotNull(binding);
        Assert.Equal("cad_root", binding!.Identity.CadDocumentId);
        Assert.Equal("fv_1", binding.Identity.FileVersionId);
        Assert.Equal(WorkspaceManifestEntryState.Verified, binding.State);
    }

    [Fact]
    public void A_file_with_the_same_name_in_another_directory_is_not_bound()
    {
        WriteManifest(("cad_b", "part.ipt", "fv_2", MaterializationStatus.Downloaded));

        var elsewhere = Path.Combine(Path.GetTempPath(), "other-" + Guid.NewGuid().ToString("N"), "part.ipt");
        Assert.Null(WorkspaceBindingResolver.Resolve(elsewhere, new[] { _root }));
    }

    [Fact]
    public void An_unverified_entry_still_binds_identity_but_reports_its_state()
    {
        // First a verified run, then a blocked run downgrades it to Unverified.
        WriteManifest(("cad_b", "part.ipt", "fv_2", MaterializationStatus.Downloaded));
        WriteManifest(("cad_b", "part.ipt", "fv_2", MaterializationStatus.Blocked));

        var binding = WorkspaceBindingResolver.Resolve(Path.Combine(_root, "part.ipt"), new[] { _root });

        Assert.NotNull(binding);
        Assert.Equal("cad_b", binding!.Identity.CadDocumentId);
        Assert.Equal(WorkspaceManifestEntryState.Unverified, binding.State);
    }

    [Fact]
    public void No_manifest_under_the_root_means_no_binding()
    {
        Assert.Null(WorkspaceBindingResolver.Resolve(Path.Combine(_root, "asm.iam"), new[] { _root }));
    }

    [Fact]
    public void A_corrupt_manifest_is_treated_as_no_binding_never_throws()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".arch"));
        File.WriteAllText(Path.Combine(_root, WorkspaceManifest.RelativeManifestPath), "}{ not json");

        Assert.Null(WorkspaceBindingResolver.Resolve(Path.Combine(_root, "asm.iam"), new[] { _root }));
    }

    [Fact]
    public void Null_or_relative_roots_are_skipped()
    {
        WriteManifest(("cad_root", "asm.iam", "fv_1", MaterializationStatus.Downloaded));

        var binding = WorkspaceBindingResolver.Resolve(
            Path.Combine(_root, "asm.iam"),
            new string?[] { null, "", "  ", @"relative\path", _root });

        Assert.NotNull(binding);
        Assert.Equal("cad_root", binding!.Identity.CadDocumentId);
    }

    [Fact]
    public void A_blank_file_path_returns_no_binding()
    {
        Assert.Null(WorkspaceBindingResolver.Resolve(null, new[] { _root }));
        Assert.Null(WorkspaceBindingResolver.Resolve("   ", new[] { _root }));
    }
}
