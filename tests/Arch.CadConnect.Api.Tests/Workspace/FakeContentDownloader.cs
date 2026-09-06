using System.Security.Cryptography;

using Arch.CadConnect.Api.Workspace;

namespace Arch.CadConnect.Api.Tests.Workspace;

/// <summary>A scriptable <see cref="IContentDownloader"/> for materializer tests.</summary>
public sealed class FakeContentDownloader : IContentDownloader
{
    private readonly Dictionary<string, Func<Stream>> _byPath = new(StringComparer.Ordinal);

    public List<string> Requested { get; } = new();

    public FakeContentDownloader Serve(string fileVersionId, byte[] bytes)
    {
        _byPath[$"/api/file-versions/{fileVersionId}/content"] = () => new MemoryStream(bytes, writable: false);
        return this;
    }

    public FakeContentDownloader ServeStream(string fileVersionId, Func<Stream> factory)
    {
        _byPath[$"/api/file-versions/{fileVersionId}/content"] = factory;
        return this;
    }

    public FakeContentDownloader Fail(string fileVersionId, Exception ex)
    {
        _byPath[$"/api/file-versions/{fileVersionId}/content"] = () => throw ex;
        return this;
    }

    public Task<Stream> OpenContentStreamAsync(string contentPath, CancellationToken ct = default)
    {
        Requested.Add(contentPath);
        if (!_byPath.TryGetValue(contentPath, out var factory))
        {
            throw new ContentDownloadHttpException(404);
        }
        return Task.FromResult(factory());
    }

    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
