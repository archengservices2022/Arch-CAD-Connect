namespace Arch.CadConnect.Core.Workspace;

/// <summary>
/// Per-entry outcome of a Get Latest materialization. Mirrors the P3B-2B
/// materializer (<c>web/app/lib/workspace-materialize-core.ts</c>).
/// </summary>
public enum MaterializationStatus
{
    /// <summary>Bytes were downloaded, size + SHA-256 verified, and the file
    ///  was atomically placed (created new - never overwriting).</summary>
    Downloaded,

    /// <summary>An existing local file already matched the pinned checksum +
    ///  size. No download, no write.</summary>
    AlreadyCurrent,

    /// <summary>A non-destructive stop for THIS entry: an existing local file
    ///  differs (local-conflict), a path was unsafe, a plan collision, or the
    ///  entry carried no materializable version. Nothing was written.</summary>
    Blocked,

    /// <summary>A download / hash / size / write failure. Any partial artifact
    ///  was removed; the final path holds no valid-looking file.</summary>
    Failed,
}

public sealed record MaterializationEntryResult
{
    /// <summary>Authoritative identity.</summary>
    public required string CadDocumentId { get; init; }

    /// <summary>Authoritative identity of the pinned version; null when the
    ///  entry carried no materializable version at all.</summary>
    public required string? FileVersionId { get; init; }

    /// <summary>The (re-validated) relative filename this entry targeted, if
    ///  one could be resolved; null when blocked before any path resolved.</summary>
    public required string? RelativePath { get; init; }

    public required MaterializationStatus Status { get; init; }

    /// <summary>Human-readable detail for Blocked / Failed. Never contains a
    ///  server filesystem path.</summary>
    public string? Reason { get; init; }

    /// <summary>Bytes written for a fresh download; 0 for AlreadyCurrent.</summary>
    public long BytesWritten { get; init; }

    /// <summary>The verified, matching checksum for Downloaded / AlreadyCurrent.</summary>
    public string? Checksum { get; init; }

    public bool IsRoot { get; init; }
    public string? DocumentNumber { get; init; }
}

public sealed record MaterializationSummary(int Downloaded, int AlreadyCurrent, int Blocked, int Failed);

/// <summary>
/// The complete result of one Get Latest run. A per-entry problem never
/// aborts the others; every entry has a result.
/// </summary>
public sealed record MaterializationReport
{
    public required string WorkspaceRoot { get; init; }
    public required string RootCadDocumentId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset FinishedAtUtc { get; init; }
    public required int RequestedEntries { get; init; }

    /// <summary>Deterministic - same order as the plan's entries.</summary>
    public required IReadOnlyList<MaterializationEntryResult> Results { get; init; }

    public MaterializationSummary Summary => new(
        Downloaded: Results.Count(r => r.Status == MaterializationStatus.Downloaded),
        AlreadyCurrent: Results.Count(r => r.Status == MaterializationStatus.AlreadyCurrent),
        Blocked: Results.Count(r => r.Status == MaterializationStatus.Blocked),
        Failed: Results.Count(r => r.Status == MaterializationStatus.Failed));

    /// <summary>True when nothing failed and nothing was blocked - the local
    ///  workspace is a complete, verified copy of the pinned package.</summary>
    public bool ReachedValidState =>
        Results.All(r => r.Status is MaterializationStatus.Downloaded or MaterializationStatus.AlreadyCurrent);

    /// <summary>A one-line summary for the ribbon result dialog.</summary>
    public string ToSummaryLine()
    {
        var s = Summary;
        return $"{s.Downloaded} downloaded, {s.AlreadyCurrent} already current, "
             + $"{s.Blocked} blocked, {s.Failed} failed.";
    }
}
