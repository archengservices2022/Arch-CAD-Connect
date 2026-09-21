namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// P6C: the request shape sent to the EXISTING P6B server contract
/// (<c>POST /api/desktop/copy-design/apply</c>). Mirrors the server's
/// <c>CopyDesignApplyRequest</c> (see web/app/lib/copy-design-lineage-core.ts)
/// exactly - this client never invents its own shape.
/// </summary>
public enum CopyDesignApplyEntryAction
{
    Copy,
    Reuse,
}

public sealed record CopyDesignApplyCopyEntry(
    string SourceCadDocumentId,
    string SourceFileVersionId,
    string NewDocumentNumber,
    string NewFileName,
    CadDocumentType DocumentType,
    string? Description);

public sealed record CopyDesignApplyReuseEntry(string CadDocumentId);

/// <summary>ONE request entry, paired with EITHER <see cref="Copy"/> or
///  <see cref="Reuse"/> according to <see cref="Action"/> - never both, never
///  neither (enforced by <see cref="CopyDesignApplyRequestMapper"/>, the only
///  producer).</summary>
public sealed record CopyDesignApplyEntry(
    CopyDesignApplyEntryAction Action,
    CopyDesignApplyCopyEntry? Copy,
    CopyDesignApplyReuseEntry? Reuse);

/// <summary>ONE mapped entry, still carrying the exact plan node it came from
///  - the sole correlation the rest of P6C uses between "what we asked P6B
///  for" and "what P6B gave back" and "what we physically do next". Never
///  correlated by file name or path.</summary>
public sealed record CopyDesignApplyMappedEntry(CopyDesignNode Node, CopyDesignApplyEntry Entry);

public sealed record CopyDesignApplyRequest(
    string IdempotencyKey,
    string? Label,
    IReadOnlyList<CopyDesignApplyMappedEntry> Entries)
{
    /// <summary>The exact wire-shape entries, in the SAME order, with no plan
    ///  node attached - what actually goes over HTTP.</summary>
    public IReadOnlyList<CopyDesignApplyEntry> WireEntries => Entries.Select(e => e.Entry).ToArray();
}

/// <summary>
/// The result of <see cref="CopyDesignApplyRequestMapper.Map"/>: either a
/// ready-to-send <see cref="Request"/>, or a fail-closed
/// <see cref="FailureReason"/> - never a partially-built request.
/// </summary>
public sealed record CopyDesignApplyMappingResult(bool Success, string? FailureReason, CopyDesignApplyRequest? Request)
{
    public static CopyDesignApplyMappingResult Fail(string reason) => new(false, reason, null);
    public static CopyDesignApplyMappingResult Ok(CopyDesignApplyRequest request) => new(true, null, request);
}
