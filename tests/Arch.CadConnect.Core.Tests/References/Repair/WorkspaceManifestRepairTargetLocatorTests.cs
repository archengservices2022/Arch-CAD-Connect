using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

using Arch.CadConnect.Core.References;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.Tests.References.Repair;

/// <summary>
/// The P5C target locator resolves the STABLE LOCAL PATH from a verified
/// workspace-manifest binding (exact cadDocumentId AND fileVersionId) - never by
/// filename - and then verifies the file's CURRENT bytes against the
/// SERVER-authoritative size + SHA-256. The mutable manifest is NOT the
/// integrity authority: a manifest that disagrees with the server fails closed.
/// </summary>
public class WorkspaceManifestRepairTargetLocatorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly string _ws = Path.Combine(Path.GetTempPath(), "arch-cc-p5c-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] ValidBytes = Encoding.UTF8.GetBytes("bytes");
    private static readonly long ValidSize = ValidBytes.LongLength;
    private static readonly string ValidSha = Convert.ToHexString(SHA256.HashData(ValidBytes)).ToLowerInvariant();

    public WorkspaceManifestRepairTargetLocatorTests() => Directory.CreateDirectory(_ws);

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { /* best effort */ }
    }

    private WorkspaceManifestRepairTargetLocator Locator(params string[] roots) =>
        new(roots.Length == 0 ? new[] { _ws } : roots);

    // The default happy call: server metadata == the file's real bytes.
    private RepairTargetResolution LocateValid(string cad = "cad_a", string fv = "fv_v3", string? ws = null)
        => new WorkspaceManifestRepairTargetLocator(new[] { ws ?? _ws }).Locate(cad, fv, ValidSize, ValidSha);

    [Fact]
    public void Resolves_the_verified_local_copy_of_the_exact_target_fileVersion()
    {
        WriteManifest(_ws, ("PART-A.ipt", "cad_a", "fv_v3", "Verified"));
        File.WriteAllBytes(Path.Combine(_ws, "PART-A.ipt"), ValidBytes);

        var resolution = LocateValid();

        Assert.Equal(RepairTargetOutcome.Resolved, resolution.Outcome);
        Assert.NotNull(resolution.Candidate);
        Assert.Equal(RepairTargetIdentitySource.WorkspaceManifestVerified, resolution.Candidate!.IdentitySource);
        Assert.Equal(Path.GetFullPath(Path.Combine(_ws, "PART-A.ipt")), resolution.Candidate.AbsolutePath);
        Assert.True(resolution.Candidate.ExistsOnDisk);
        Assert.True(resolution.Candidate.BinaryIntegrityVerified);
        Assert.True(resolution.Candidate.ManifestAgreesWithServer);
        Assert.Equal(ValidSize, resolution.Candidate.ServerFileSize);
        Assert.Equal(ValidSha, resolution.Candidate.ServerSha256);
        Assert.Equal("cad_a", resolution.Candidate.CadDocumentId);
        Assert.Equal("fv_v3", resolution.Candidate.FileVersionId);
    }

    [Fact]
    public void Altered_target_bytes_fail_closed_against_the_server_sha()
    {
        WriteManifest(_ws, ("PART-A.ipt", "cad_a", "fv_v3", "Verified"));
        File.WriteAllText(Path.Combine(_ws, "PART-A.ipt"), "other"); // same length, different bytes

        Assert.Equal(RepairTargetOutcome.TargetBinaryVerificationFailed, LocateValid().Outcome);
    }

    [Fact]
    public void A_file_whose_size_differs_from_the_server_fails_closed()
    {
        WriteManifest(_ws, ("PART-A.ipt", "cad_a", "fv_v3", "Verified"));
        File.WriteAllText(Path.Combine(_ws, "PART-A.ipt"), "a much longer file than the server says");

        Assert.Equal(RepairTargetOutcome.TargetBinaryVerificationFailed, LocateValid().Outcome);
    }

    [Fact]
    public void A_manifest_whose_recorded_size_disagrees_with_the_server_is_TargetManifestServerMismatch()
    {
        WriteManifestWithIntegrity(_ws, fileSize: 999, checksum: ValidSha,
            ("PART-A.ipt", "cad_a", "fv_v3", "Verified"));
        File.WriteAllBytes(Path.Combine(_ws, "PART-A.ipt"), ValidBytes);

        // server says the real size; the manifest recorded 999 -> fail closed,
        // never reconcile.
        Assert.Equal(RepairTargetOutcome.TargetManifestServerMismatch, LocateValid().Outcome);
    }

    [Fact]
    public void A_manifest_whose_recorded_checksum_disagrees_with_the_server_is_TargetManifestServerMismatch()
    {
        WriteManifestWithIntegrity(_ws, fileSize: ValidSize, checksum: new string('9', 64),
            ("PART-A.ipt", "cad_a", "fv_v3", "Verified"));
        File.WriteAllBytes(Path.Combine(_ws, "PART-A.ipt"), ValidBytes);

        Assert.Equal(RepairTargetOutcome.TargetManifestServerMismatch, LocateValid().Outcome);
    }

    [Fact]
    public void Altered_bytes_with_a_manifest_updated_to_match_them_STILL_fail_against_the_server()
    {
        // The attacker changed the target bytes AND rewrote the local manifest's
        // size/checksum to match the altered bytes, keeping the same
        // cadDocumentId + fileVersionId. The server-authoritative values are
        // still the original ones -> repair MUST fail closed.
        var alteredBytes = Encoding.UTF8.GetBytes("tampered payload");
        var alteredSha = Convert.ToHexString(SHA256.HashData(alteredBytes)).ToLowerInvariant();

        WriteManifestWithIntegrity(_ws, fileSize: alteredBytes.LongLength, checksum: alteredSha,
            ("PART-A.ipt", "cad_a", "fv_v3", "Verified"));
        File.WriteAllBytes(Path.Combine(_ws, "PART-A.ipt"), alteredBytes);

        // Locate with the ORIGINAL (server-authoritative) size + SHA.
        var resolution = new WorkspaceManifestRepairTargetLocator(new[] { _ws })
            .Locate("cad_a", "fv_v3", ValidSize, ValidSha);

        Assert.NotEqual(RepairTargetOutcome.Resolved, resolution.Outcome);
        Assert.Equal(RepairTargetOutcome.TargetManifestServerMismatch, resolution.Outcome);
    }

    [Fact]
    public void No_canonical_server_integrity_metadata_is_NoServerIntegrityMetadata()
    {
        WriteManifest(_ws, ("PART-A.ipt", "cad_a", "fv_v3", "Verified"));
        File.WriteAllBytes(Path.Combine(_ws, "PART-A.ipt"), ValidBytes);

        Assert.Equal(RepairTargetOutcome.NoServerIntegrityMetadata,
            Locator().Locate("cad_a", "fv_v3", -1, "").Outcome);
        Assert.Equal(RepairTargetOutcome.NoServerIntegrityMetadata,
            Locator().Locate("cad_a", "fv_v3", ValidSize, "NOTHEX").Outcome);
        Assert.Equal(RepairTargetOutcome.NoServerIntegrityMetadata,
            Locator().Locate("cad_a", "fv_v3", ValidSize, ValidSha.ToUpperInvariant()).Outcome);
    }

    [Fact]
    public void Binary_verification_read_failure_fails_closed()
    {
        WriteManifest(_ws, ("PART-A.ipt", "cad_a", "fv_v3", "Verified"));
        File.WriteAllBytes(Path.Combine(_ws, "PART-A.ipt"), ValidBytes);
        var locator = new WorkspaceManifestRepairTargetLocator(new[] { _ws },
            binaryMatches: (_, _, _) => throw new IOException("unreadable"));

        Assert.Equal(RepairTargetOutcome.TargetBinaryVerificationFailed,
            locator.Locate("cad_a", "fv_v3", ValidSize, ValidSha).Outcome);
    }

    [Fact]
    public void The_binary_is_verified_against_the_SERVER_values_not_the_manifest()
    {
        WriteManifest(_ws, ("PART-A.ipt", "cad_a", "fv_v3", "Verified"));
        File.WriteAllBytes(Path.Combine(_ws, "PART-A.ipt"), ValidBytes);
        long sawSize = -1;
        string sawSha = "";
        var locator = new WorkspaceManifestRepairTargetLocator(new[] { _ws },
            binaryMatches: (_, size, sha) => { sawSize = size; sawSha = sha; return true; });

        locator.Locate("cad_a", "fv_v3", ValidSize, ValidSha);

        Assert.Equal(ValidSize, sawSize);
        Assert.Equal(ValidSha, sawSha);
    }

    [Fact]
    public void A_managed_copy_that_is_not_at_the_authoritative_latest_is_TargetVersionNotLocal()
    {
        WriteManifest(_ws, ("PART-A.ipt", "cad_a", "fv_v1", "Verified"));
        File.WriteAllBytes(Path.Combine(_ws, "PART-A.ipt"), ValidBytes);

        var resolution = LocateValid();

        Assert.Equal(RepairTargetOutcome.TargetVersionNotLocal, resolution.Outcome);
        Assert.Null(resolution.Candidate);
    }

    [Fact]
    public void A_matching_fileVersion_that_was_never_verified_is_TargetCopyUnverified()
    {
        WriteManifest(_ws, ("PART-A.ipt", "cad_a", "fv_v3", "Unverified"));
        File.WriteAllBytes(Path.Combine(_ws, "PART-A.ipt"), ValidBytes);

        Assert.Equal(RepairTargetOutcome.TargetCopyUnverified, LocateValid().Outcome);
    }

    [Fact]
    public void A_verified_entry_whose_file_is_gone_is_TargetFileMissing()
    {
        WriteManifest(_ws, ("PART-A.ipt", "cad_a", "fv_v3", "Verified"));
        // no file on disk

        Assert.Equal(RepairTargetOutcome.TargetFileMissing, LocateValid().Outcome);
    }

    [Fact]
    public void No_managed_workspace_binding_for_the_cad_document_is_NoManagedCopyFound()
    {
        WriteManifest(_ws, ("OTHER.ipt", "cad_other", "fv_o1", "Verified"));

        Assert.Equal(RepairTargetOutcome.NoManagedCopyFound, LocateValid().Outcome);
    }

    [Fact]
    public void Two_workspaces_binding_the_exact_target_to_different_paths_is_Ambiguous()
    {
        var ws2 = _ws + "-b";
        Directory.CreateDirectory(ws2);
        try
        {
            WriteManifest(_ws, ("PART-A.ipt", "cad_a", "fv_v3", "Verified"));
            WriteManifest(ws2, ("PART-A.ipt", "cad_a", "fv_v3", "Verified"));
            File.WriteAllBytes(Path.Combine(_ws, "PART-A.ipt"), ValidBytes);
            File.WriteAllBytes(Path.Combine(ws2, "PART-A.ipt"), ValidBytes);

            Assert.Equal(RepairTargetOutcome.AmbiguousTargets,
                Locator(_ws, ws2).Locate("cad_a", "fv_v3", ValidSize, ValidSha).Outcome);
        }
        finally
        {
            try { Directory.Delete(ws2, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void A_same_named_file_with_a_different_cadDocumentId_is_never_a_target()
    {
        WriteManifest(_ws, ("PART-A.ipt", "cad_SOMETHING_ELSE", "fv_v3", "Verified"));
        File.WriteAllBytes(Path.Combine(_ws, "PART-A.ipt"), ValidBytes);

        Assert.Equal(RepairTargetOutcome.NoManagedCopyFound, LocateValid().Outcome);
    }

    [Theory]
    [InlineData("", "fv_v3")]
    [InlineData("cad_a", "")]
    [InlineData(" cad_a ", "fv_v3")]
    [InlineData("cad_a", " fv_v3 ")]
    public void A_malformed_target_identity_is_NotAttempted(string cad, string fv)
    {
        WriteManifest(_ws, ("PART-A.ipt", "cad_a", "fv_v3", "Verified"));
        Assert.Equal(RepairTargetOutcome.NotAttempted, Locator().Locate(cad, fv, ValidSize, ValidSha).Outcome);
    }

    private static void WriteManifest(string dir, params (string rel, string cad, string fv, string state)[] entries)
        => WriteManifestWithIntegrity(dir, ValidSize, ValidSha, entries);

    private static void WriteManifestWithIntegrity(
        string dir, long fileSize, string checksum,
        params (string rel, string cad, string fv, string state)[] entries)
    {
        var doc = new
        {
            schema = WorkspaceManifest.SchemaId,
            serverOrigin = "https://plm.example.com",
            organizationId = "org1",
            organizationCode = "ACME",
            rootCadDocumentId = "cad_root",
            rootDocumentNumber = "ASM-1",
            updatedAtUtc = Now,
            entries = entries.Select(e => new
            {
                relativePath = e.rel,
                cadDocumentId = e.cad,
                documentNumber = "DOC-" + e.cad,
                fileName = e.rel,
                cadType = e.rel.EndsWith(".iam") ? "IAM" : "IPT",
                fileVersionId = e.fv,
                versionNumber = 3,
                checksum,
                fileSize,
                isRoot = false,
                dependsOn = Array.Empty<string>(),
                state = e.state,
                retrievedAtUtc = Now,
            }).ToArray(),
        };

        var path = Path.Combine(dir, WorkspaceManifest.RelativeManifestPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
