namespace Arch.CadConnect.Core;

/// <summary>
/// The AUTHORITATIVE, stable Arch server identity of a CAD document. This is
/// what pins a local file to a PLM record - never the file name or path.
///
/// P4A never populates this (there is no client PDM operation yet); it exists
/// so <see cref="CadDocumentContext"/> and later milestones have a place to
/// carry it once Get Latest / Checkout return it.
/// </summary>
public sealed record PlmIdentity(
    /// <summary>Server CadDocument id (a cuid). Opaque; never parsed.</summary>
    string CadDocumentId,
    /// <summary>Human document number, e.g. "10073-MFD-AS210". Display only.</summary>
    string? DocumentNumber = null,
    /// <summary>Pinned FileVersion id the local copy corresponds to, if known.</summary>
    string? FileVersionId = null,
    int? VersionNumber = null);
