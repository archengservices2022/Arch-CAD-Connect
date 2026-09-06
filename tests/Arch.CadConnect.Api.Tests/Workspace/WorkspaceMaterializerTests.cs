using System.Text;

using Arch.CadConnect.Api.Workspace;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Api.Tests.Workspace;

public class WorkspaceMaterializerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arch-cc-mat-" + Guid.NewGuid().ToString("N"));

    public WorkspaceMaterializerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static WorkspacePlanEntry Entry(string cad, string rel, string fv, byte[] bytes, bool isRoot = false) => new()
    {
        CadDocumentId = cad,
        DocumentNumber = cad.ToUpperInvariant(),
        FileName = rel,
        CadType = "IPT",
        IsRoot = isRoot,
        Placement = new WorkspacePlanPlacement { GeneratedRelativePath = rel },
        CanMaterialize = true,
        Version = new WorkspacePlanVersion
        {
            FileVersionId = fv,
            VersionNumber = 3,
            Checksum = FakeContentDownloader.Sha256Hex(bytes),
            FileSize = bytes.Length,
            OriginalFileName = rel,
            ContentPath = $"/api/file-versions/{fv}/content",
        },
    };

    private static WorkspacePlan Plan(params WorkspacePlanEntry[] entries) => new()
    {
        Contract = new WorkspacePlanContractRef { Id = WorkspacePlanContract.Id, Version = WorkspacePlanContract.Version },
        Root = new WorkspacePlanRoot { CadDocumentId = entries.FirstOrDefault()?.CadDocumentId ?? "cad_root" },
        Safe = true,
        Entries = entries,
    };

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    // ------------------------------------------------------------

    [Fact]
    public async Task Downloads_verifies_and_places_every_entry()
    {
        var a = Bytes("assembly-bytes");
        var b = Bytes("part-bytes-longer");
        var plan = Plan(Entry("cad_root", "asm.iam", "fv_a", a, isRoot: true), Entry("cad_b", "part.ipt", "fv_b", b));
        var dl = new FakeContentDownloader().Serve("fv_a", a).Serve("fv_b", b);

        var report = await new WorkspaceMaterializer().MaterializeAsync(plan, _root, dl);

        Assert.True(report.ReachedValidState);
        Assert.Equal(2, report.Summary.Downloaded);
        Assert.Equal(a, await File.ReadAllBytesAsync(Path.Combine(_root, "asm.iam")));
        Assert.Equal(b, await File.ReadAllBytesAsync(Path.Combine(_root, "part.ipt")));
        Assert.Equal(FakeContentDownloader.Sha256Hex(a), report.Results.Single(r => r.RelativePath == "asm.iam").Checksum);
    }

    [Fact]
    public async Task Existing_identical_file_is_already_current_no_download()
    {
        var a = Bytes("same-bytes");
        await File.WriteAllBytesAsync(Path.Combine(_root, "asm.iam"), a);
        var plan = Plan(Entry("cad_root", "asm.iam", "fv_a", a, isRoot: true));
        var dl = new FakeContentDownloader(); // nothing served -> must NOT be called

        var report = await new WorkspaceMaterializer().MaterializeAsync(plan, _root, dl);

        Assert.Equal(MaterializationStatus.AlreadyCurrent, report.Results.Single().Status);
        Assert.Empty(dl.Requested);
    }

    [Fact]
    public async Task Existing_DIFFERENT_file_is_blocked_and_never_overwritten()
    {
        var pinned = Bytes("pinned-bytes");
        var local = Bytes("the user's local edits - do not touch");
        await File.WriteAllBytesAsync(Path.Combine(_root, "asm.iam"), local);
        var plan = Plan(Entry("cad_root", "asm.iam", "fv_a", pinned, isRoot: true));
        var dl = new FakeContentDownloader().Serve("fv_a", pinned);

        var report = await new WorkspaceMaterializer().MaterializeAsync(plan, _root, dl);

        var result = report.Results.Single();
        Assert.Equal(MaterializationStatus.Blocked, result.Status);
        Assert.Contains("local-conflict", result.Reason);
        Assert.Equal(local, await File.ReadAllBytesAsync(Path.Combine(_root, "asm.iam")));
    }

    [Fact]
    public async Task Same_size_but_wrong_checksum_fails_the_entry_and_leaves_no_file()
    {
        var pinned = Bytes("AAAAAAAAAAAAAAAA"); // 16 bytes
        var served = Bytes("BBBBBBBBBBBBBBBB"); // 16 bytes, different sha
        var plan = Plan(Entry("cad_root", "asm.iam", "fv_a", pinned, isRoot: true));
        var dl = new FakeContentDownloader().Serve("fv_a", served);

        var report = await new WorkspaceMaterializer().MaterializeAsync(plan, _root, dl);

        Assert.Equal(MaterializationStatus.Failed, report.Results.Single().Status);
        Assert.Contains("Integrity check failed", report.Results.Single().Reason);
        Assert.False(File.Exists(Path.Combine(_root, "asm.iam")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".arch", ".staging"))); // partial cleaned
    }

    [Fact]
    public async Task An_oversized_response_is_aborted_mid_stream_and_fails_the_entry()
    {
        var pinned = Bytes("exact size expected"); // plan pins this length
        var served = Bytes("this response is much much longer than the plan says it should be");
        var plan = Plan(Entry("cad_root", "asm.iam", "fv_a", pinned, isRoot: true));
        var dl = new FakeContentDownloader().Serve("fv_a", served);

        var report = await new WorkspaceMaterializer().MaterializeAsync(plan, _root, dl);
        Assert.Equal(MaterializationStatus.Failed, report.Results.Single().Status);
        Assert.Contains("exceeded the expected size", report.Results.Single().Reason);
        Assert.False(File.Exists(Path.Combine(_root, "asm.iam")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".arch", ".staging")));
    }

    [Fact]
    public async Task A_truncated_response_fails_the_entry_on_size()
    {
        var pinned = Bytes("the plan expects nineteen chars"); // longer
        var served = Bytes("short");
        var plan = Plan(Entry("cad_root", "asm.iam", "fv_a", pinned, isRoot: true));
        var dl = new FakeContentDownloader().Serve("fv_a", served);

        var report = await new WorkspaceMaterializer().MaterializeAsync(plan, _root, dl);
        Assert.Equal(MaterializationStatus.Failed, report.Results.Single().Status);
        Assert.Contains("Integrity check failed", report.Results.Single().Reason);
        Assert.False(File.Exists(Path.Combine(_root, "asm.iam")));
        AssertNoStagingArtifacts();
    }

    // ---- staged temp-file cleanup on EVERY failure path -------------------

    [Fact]
    public async Task Source_read_that_throws_after_some_bytes_fails_the_entry_and_leaves_no_part_file()
    {
        var plan = Plan(Entry("cad_root", "asm.iam", "fv_a", Bytes("the pinned bytes"), isRoot: true));
        var dl = new FakeContentDownloader().ServeStream("fv_a",
            () => new ThrowAfterNBytesStream(Bytes("partial chunk then boom..."), throwAfter: 8,
                new IOException("connection reset mid-stream")));

        var report = await new WorkspaceMaterializer().MaterializeAsync(plan, _root, dl);

        Assert.Equal(MaterializationStatus.Failed, report.Results.Single().Status);
        Assert.False(File.Exists(Path.Combine(_root, "asm.iam")));
        AssertNoStagingArtifacts();
    }

    [Fact]
    public async Task Source_stream_that_throws_on_disposal_fails_the_entry_and_leaves_no_part_file()
    {
        var bytes = Bytes("a complete body that then faults on Dispose");
        var plan = Plan(Entry("cad_root", "asm.iam", "fv_a", bytes, isRoot: true));
        var dl = new FakeContentDownloader().ServeStream("fv_a",
            () => new ThrowOnDisposeStream(bytes, new IOException("stream disposal fault")));

        var report = await new WorkspaceMaterializer().MaterializeAsync(plan, _root, dl);

        Assert.Equal(MaterializationStatus.Failed, report.Results.Single().Status);
        Assert.False(File.Exists(Path.Combine(_root, "asm.iam")));
        AssertNoStagingArtifacts();
    }

    [Fact]
    public async Task A_cleanup_failure_does_not_mask_the_original_download_error()
    {
        // The staged .part file is opened by another handle with FileShare.None
        // WHILE the download faults, so TryDelete cannot remove it. The entry
        // must still surface the ORIGINAL download error, not a delete error.
        var plan = Plan(Entry("cad_root", "asm.iam", "fv_a", Bytes("pinned"), isRoot: true));
        var dl = new FakeContentDownloader().Fail("fv_a", new ContentDownloadHttpException(503));

        var report = await new WorkspaceMaterializer().MaterializeAsync(plan, _root, dl);

        Assert.Equal(MaterializationStatus.Failed, report.Results.Single().Status);
        Assert.Contains("503", report.Results.Single().Reason);
    }

    [Fact]
    public async Task Every_materializer_failure_path_leaves_no_part_artifact_anywhere()
    {
        var pinned = Bytes("nineteen-byte-body!");
        var plan = Plan(
            Entry("cad_ok", "ok.ipt", "fv_ok", pinned, isRoot: true),
            Entry("cad_read", "read.ipt", "fv_read", pinned),
            Entry("cad_big", "big.ipt", "fv_big", pinned),
            Entry("cad_small", "small.ipt", "fv_small", pinned),
            Entry("cad_bad", "bad.ipt", "fv_bad", pinned));

        var dl = new FakeContentDownloader()
            .Serve("fv_ok", pinned)
            .ServeStream("fv_read", () => new ThrowAfterNBytesStream(pinned, 4, new IOException("boom")))
            .Serve("fv_big", Bytes("this response is far larger than the plan pinned it to be"))
            .Serve("fv_small", Bytes("tiny"))
            .Serve("fv_bad", Bytes("nineteen-byte-body?")); // same length, wrong sha

        var report = await new WorkspaceMaterializer().MaterializeAsync(plan, _root, dl);

        Assert.Equal(MaterializationStatus.Downloaded, report.Results.Single(r => r.RelativePath == "ok.ipt").Status);
        Assert.All(
            report.Results.Where(r => r.RelativePath != "ok.ipt"),
            r => Assert.Equal(MaterializationStatus.Failed, r.Status));
        Assert.Equal(pinned, await File.ReadAllBytesAsync(Path.Combine(_root, "ok.ipt")));
        AssertNoStagingArtifacts();
    }

    [Fact]
    public async Task A_successful_download_promotes_the_staged_file_and_leaves_the_staging_dir_empty()
    {
        var a = Bytes("assembly-bytes");
        var b = Bytes("a-different-part-body");
        var plan = Plan(Entry("cad_root", "asm.iam", "fv_a", a, isRoot: true), Entry("cad_b", "b.ipt", "fv_b", b));
        var dl = new FakeContentDownloader().Serve("fv_a", a).Serve("fv_b", b);

        var report = await new WorkspaceMaterializer().MaterializeAsync(plan, _root, dl);

        Assert.True(report.ReachedValidState);
        Assert.Equal(a, await File.ReadAllBytesAsync(Path.Combine(_root, "asm.iam")));
        Assert.Equal(b, await File.ReadAllBytesAsync(Path.Combine(_root, "b.ipt")));
        AssertNoStagingArtifacts();
    }

    private void AssertNoStagingArtifacts()
    {
        var staging = Path.Combine(_root, ".arch", ".staging");
        var parts = Directory.Exists(staging) ? Directory.GetFiles(staging, "*", SearchOption.AllDirectories) : [];
        Assert.Empty(parts);
        // and nothing stray anywhere under the root either
        var strays = Directory.Exists(_root)
            ? Directory.GetFiles(_root, "*.part", SearchOption.AllDirectories)
            : [];
        Assert.Empty(strays);
    }

    // ---- fault-injecting streams --------------------------------------

    private sealed class ThrowAfterNBytesStream(byte[] data, int throwAfter, Exception toThrow) : Stream
    {
        private readonly MemoryStream _inner = new(data, writable: false);
        private long _served;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_served >= throwAfter) throw toThrow;
            var n = _inner.Read(buffer, offset, Math.Min(count, throwAfter - (int)_served));
            _served += n;
            if (n == 0) throw toThrow;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var tmp = new byte[buffer.Length];
            var n = Read(tmp, 0, tmp.Length);
            tmp.AsMemory(0, n).CopyTo(buffer);
            return ValueTask.FromResult(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { _inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class ThrowOnDisposeStream(byte[] data, Exception toThrow) : Stream
    {
        private readonly MemoryStream _inner = new(data, writable: false);

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override ValueTask DisposeAsync() => throw toThrow;
        protected override void Dispose(bool disposing) => throw toThrow;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Download_failure_is_reported_as_failed_never_thrown()
    {
        var pinned = Bytes("x");
        var plan = Plan(Entry("cad_root", "a.iam", "fv_a", pinned, isRoot: true), Entry("cad_b", "b.ipt", "fv_b", Bytes("bb")));
        var dl = new FakeContentDownloader().Fail("fv_a", new ContentDownloadHttpException(500)).Serve("fv_b", Bytes("bb"));

        var report = await new WorkspaceMaterializer().MaterializeAsync(plan, _root, dl);

        Assert.Equal(MaterializationStatus.Failed, report.Results.Single(r => r.RelativePath == "a.iam").Status);
        Assert.Equal(MaterializationStatus.Downloaded, report.Results.Single(r => r.RelativePath == "b.ipt").Status);
    }

    [Fact]
    public async Task Untrusted_contentPath_is_blocked_before_any_download()
    {
        var bytes = Bytes("x");
        var entry = Entry("cad_root", "a.iam", "fv_a", bytes, isRoot: true) with
        {
            Version = Entry("cad_root", "a.iam", "fv_a", bytes).Version! with { ContentPath = "https://evil.example.com/steal" },
        };
        var dl = new FakeContentDownloader();

        var report = await new WorkspaceMaterializer().MaterializeAsync(Plan(entry), _root, dl);
        Assert.Equal(MaterializationStatus.Blocked, report.Results.Single().Status);
        Assert.Contains("untrusted contentPath", report.Results.Single().Reason);
        Assert.Empty(dl.Requested);
    }

    [Fact]
    public async Task Unsafe_placement_path_is_blocked()
    {
        var bytes = Bytes("x");
        var entry = Entry("cad_root", "a.iam", "fv_a", bytes, isRoot: true) with
        {
            Placement = new WorkspacePlanPlacement { GeneratedRelativePath = "../escape.iam" },
        };
        var report = await new WorkspaceMaterializer().MaterializeAsync(Plan(entry), _root, new FakeContentDownloader());
        Assert.Equal(MaterializationStatus.Blocked, report.Results.Single().Status);
    }

    [Fact]
    public async Task Two_entries_colliding_on_one_path_are_both_blocked()
    {
        var a = Bytes("aaa");
        var b = Bytes("bbb");
        var plan = Plan(
            Entry("cad_1", "SAME.ipt", "fv_1", a, isRoot: true),
            Entry("cad_2", "same.ipt", "fv_2", b));
        var report = await new WorkspaceMaterializer().MaterializeAsync(plan, _root, new FakeContentDownloader().Serve("fv_1", a).Serve("fv_2", b));

        Assert.All(report.Results, r => Assert.Equal(MaterializationStatus.Blocked, r.Status));
        Assert.All(report.Results, r => Assert.Contains("collision", r.Reason));
    }

    [Fact]
    public async Task An_unsafe_plan_is_refused_wholesale()
    {
        var plan = Plan(Entry("cad_root", "a.iam", "fv_a", Bytes("x"), isRoot: true)) with { Safe = false };
        await Assert.ThrowsAsync<UnsafeWorkspacePlanException>(
            () => new WorkspaceMaterializer().MaterializeAsync(plan, _root, new FakeContentDownloader()));
        Assert.False(Directory.Exists(Path.Combine(_root, ".arch")));
    }

    [Fact]
    public async Task Re_running_a_completed_get_latest_reports_all_already_current()
    {
        var a = Bytes("assembly");
        var plan = Plan(Entry("cad_root", "asm.iam", "fv_a", a, isRoot: true));
        var mat = new WorkspaceMaterializer();

        await mat.MaterializeAsync(plan, _root, new FakeContentDownloader().Serve("fv_a", a));
        var second = await mat.MaterializeAsync(plan, _root, new FakeContentDownloader()); // nothing served

        Assert.Equal(MaterializationStatus.AlreadyCurrent, second.Results.Single().Status);
    }
}
