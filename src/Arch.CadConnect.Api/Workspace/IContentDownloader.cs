namespace Arch.CadConnect.Api.Workspace;

/// <summary>
/// Opens a byte stream for the EXACT <c>contentPath</c> a workspace-plan
/// entry's pinned version carries. Implementations decide transport /
/// authentication; a caller never constructs, re-derives or re-requests any
/// other URL. C# counterpart of the P3B-2B <c>ContentDownloader</c> port
/// point (<c>web/app/lib/workspace-materialize-core.ts</c>).
/// </summary>
public interface IContentDownloader
{
    /// <summary>
    /// Open a read stream for <paramref name="contentPath"/> (which must be
    /// EXACTLY <c>/api/file-versions/&lt;id&gt;/content</c>). The returned
    /// stream is caller-owned; disposing it releases the HTTP response and
    /// the transfer timeout. Throws before returning on: an unexpected
    /// contentPath shape, an origin mismatch, ANY redirect, a non-2xx
    /// response, or a connect/header timeout.
    /// </summary>
    Task<Stream> OpenContentStreamAsync(string contentPath, CancellationToken ct = default);
}
