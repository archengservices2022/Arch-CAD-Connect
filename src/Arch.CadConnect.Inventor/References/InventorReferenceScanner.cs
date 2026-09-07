using Arch.CadConnect.Core.References;

using InventorApi = Inventor;

namespace Arch.CadConnect.Inventor.References;

/// <summary>
/// The Inventor-bound P5A scanner: composes the pure
/// <see cref="CadReferenceScanner"/> over an
/// <see cref="InventorDocumentReferenceSource"/>. All COM reads happen on the
/// caller's thread (the Inventor UI thread) - the scan does no network or
/// background work.
/// </summary>
internal sealed class InventorReferenceScanner : ICadReferenceScanner
{
    private readonly CadReferenceScanner _inner;

    public InventorReferenceScanner(
        InventorApi.Application application,
        IEnumerable<string?> workspaceRoots,
        Func<DateTimeOffset>? clock = null)
        => _inner = new CadReferenceScanner(
            new InventorDocumentReferenceSource(application),
            workspaceRoots,
            clock);

    public CadReferenceScan Scan(string rootAbsolutePath) => _inner.Scan(rootAbsolutePath);
}
