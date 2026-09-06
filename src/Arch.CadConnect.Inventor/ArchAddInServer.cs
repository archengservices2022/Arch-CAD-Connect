using System.Runtime.InteropServices;

using InventorApi = Inventor;

namespace Arch.CadConnect.Inventor;

/// <summary>
/// The Inventor add-in entry point. Inventor reads the <c>.addin</c> manifest,
/// finds this class by <see cref="ArchAddInInfo.AddInClsid"/>, COM-activates
/// it, and calls <see cref="Activate"/> / <see cref="Deactivate"/>.
///
/// Keeps almost nothing itself - it just constructs and tears down
/// <see cref="ArchAddInController"/>, which owns the ribbon, connection and
/// document observer. Any exception in <see cref="Activate"/> is swallowed
/// after being surfaced once, so a failed add-in never blocks Inventor from
/// starting.
/// </summary>
[Guid(ArchAddInInfo.AddInClsid)]
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[ProgId("Arch.CadConnect.Inventor.ArchAddInServer")]
public sealed class ArchAddInServer : InventorApi.ApplicationAddInServer
{
    private ArchAddInController? _controller;

    public void Activate(InventorApi.ApplicationAddInSite addInSiteObject, bool firstTime)
    {
        try
        {
            _controller = new ArchAddInController(addInSiteObject.Application);
        }
        catch (Exception ex)
        {
            _controller = null;
            TryReportActivationFailure(addInSiteObject, ex);
        }
    }

    public void Deactivate()
    {
        try
        {
            _controller?.Dispose();
        }
        finally
        {
            _controller = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    /// <summary>Obsolete Inventor API surface - unused; command wiring is via ButtonDefinition.OnExecute.</summary>
    public void ExecuteCommand(int commandID)
    {
    }

    /// <summary>No automation object is exposed in P4A.</summary>
    public object? Automation => null;

    private static void TryReportActivationFailure(InventorApi.ApplicationAddInSite site, Exception ex)
    {
        try
        {
            site.Application.StatusBarText =
                $"{ArchAddInInfo.DisplayName} failed to load: {ex.GetType().Name}";
        }
        catch
        {
            // If even that fails, there is nothing safe left to do.
        }
    }
}
