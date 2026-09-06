namespace Arch.CadConnect.Inventor;

/// <summary>
/// Stable identifiers for the add-in. The <see cref="AddInClsid"/> MUST match
/// the <c>ClassId</c> / <c>ClientId</c> in the generated <c>.addin</c>
/// manifest and the COM registration - it is how Inventor finds and activates
/// us. Never change it once shipped.
/// </summary>
public static class ArchAddInInfo
{
    /// <summary>COM class id of <see cref="ArchAddInServer"/>.</summary>
    public const string AddInClsid = "93C45016-BDCA-4734-BB1A-4A06C36E900C";

    public const string DisplayName = "Arch Engineering CAD Connect";
    public const string Description = "Arch Engineering PLM connectivity for Autodesk Inventor.";

    /// <summary>Ribbon tab + internal id prefix.</summary>
    public const string RibbonTabName = "ARCH ENGINEERING";
    public const string InternalIdPrefix = "Arch.CadConnect";

    /// <summary>Inventor release numbers this add-in is validated against.</summary>
    public const int MinInventorRelease = 29; // Inventor 2025
    public const int MaxValidatedInventorRelease = 30; // Inventor 2026
}
