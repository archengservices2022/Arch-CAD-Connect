using System.Runtime.InteropServices;

using Arch.CadConnect.Core.Ribbon;

using InventorApi = Inventor;

namespace Arch.CadConnect.Inventor.Ribbon;

/// <summary>
/// Builds the "ARCH ENGINEERING" ribbon tab (CONNECTION / PDM / INFORMATION
/// panels) on every Inventor ribbon, keeps the created
/// <c>ButtonDefinition</c>s, forwards clicks as <see cref="CommandInvoked"/>,
/// and applies an enable/disable map from
/// <see cref="RibbonCommandPolicy"/>.
///
/// Command layout comes entirely from <see cref="ArchCommands"/> in Core, so
/// the ribbon and the (testable) enablement policy can never drift.
/// </summary>
internal sealed class RibbonFactory : IDisposable
{
    private readonly InventorApi.Application _application;

    private readonly Dictionary<ArchCommand, InventorApi.ButtonDefinition> _buttons = new();
    private readonly Dictionary<ArchCommand, InventorApi.ButtonDefinitionSink_OnExecuteEventHandler> _handlers = new();
    private readonly List<InventorApi.RibbonTab> _tabs = new();

    private bool _disposed;

    public RibbonFactory(InventorApi.Application application) => _application = application;

    /// <summary>Raised on the UI thread when a ribbon button is clicked.</summary>
    public event Action<ArchCommand>? CommandInvoked;

    public void Build()
    {
        var controlDefs = _application.CommandManager.ControlDefinitions;

        foreach (var command in ArchCommands.All)
        {
            var button = GetOrCreateButton(controlDefs, command);
            _buttons[command] = button;

            var handler = MakeHandler(command);
            _handlers[command] = handler;
            button.OnExecute += handler;
        }

        var ribbons = _application.UserInterfaceManager.Ribbons;
        foreach (InventorApi.Ribbon ribbon in ribbons)
        {
            BuildTabOn(ribbon);
        }
    }

    private InventorApi.ButtonDefinition GetOrCreateButton(
        InventorApi.ControlDefinitions controlDefs,
        ArchCommand command)
    {
        var internalName = command.InternalName();
        try
        {
            // Re-activation after a soft unload can leave the definition around.
            return (InventorApi.ButtonDefinition)controlDefs[internalName];
        }
        catch (ArgumentException)
        {
            // not defined yet - fall through and create it
        }
        catch (COMException)
        {
            // not defined yet
        }

        var button = controlDefs.AddButtonDefinition(
            command.DisplayName(),
            internalName,
            InventorApi.CommandTypesEnum.kNonShapeEditCmdType,
            "{" + ArchAddInInfo.AddInClsid + "}",
            command.ToolTip(),
            command.DisplayName(),
            null,
            null,
            InventorApi.ButtonDisplayEnum.kDisplayTextInLearningMode);

        return button;
    }

    private InventorApi.ButtonDefinitionSink_OnExecuteEventHandler MakeHandler(ArchCommand command) =>
        _ => CommandInvoked?.Invoke(command);

    private void BuildTabOn(InventorApi.Ribbon ribbon)
    {
        var tabInternal = $"{ArchCommands.InternalIdPrefix}.Tab";
        InventorApi.RibbonTab? tab = TryGetTab(ribbon, tabInternal);
        tab ??= ribbon.RibbonTabs.Add(
            ArchCommands.RibbonTabName,
            tabInternal,
            "{" + ArchAddInInfo.AddInClsid + "}");
        _tabs.Add(tab);

        foreach (var panelName in ArchCommands.PanelOrder)
        {
            var panelInternal = $"{ArchCommands.InternalIdPrefix}.Panel.{panelName}";
            InventorApi.RibbonPanel? panel = TryGetPanel(tab, panelInternal);
            panel ??= tab.RibbonPanels.Add(
                panelName,
                panelInternal,
                "{" + ArchAddInInfo.AddInClsid + "}");

            foreach (var command in ArchCommands.All.Where(c => c.Panel() == panelName))
            {
                try
                {
                    panel.CommandControls.AddButton(_buttons[command], DisplayAsLarge(command));
                }
                catch (COMException)
                {
                    // Button already on the panel from a prior activation.
                }
            }
        }
    }

    private static bool DisplayAsLarge(ArchCommand command) => command is
        ArchCommand.SignIn or ArchCommand.GetLatest or ArchCommand.Checkout or ArchCommand.Status;

    public void ApplyEnablement(IReadOnlyDictionary<ArchCommand, bool> enablement)
    {
        foreach (var (command, enabled) in enablement)
        {
            if (_buttons.TryGetValue(command, out var button))
            {
                try
                {
                    button.Enabled = enabled;
                }
                catch (COMException)
                {
                    // Inventor UI mid-teardown; ignore.
                }
            }
        }
    }

    private static InventorApi.RibbonTab? TryGetTab(InventorApi.Ribbon ribbon, string internalName)
    {
        try { return ribbon.RibbonTabs[internalName]; }
        catch (ArgumentException) { return null; }
        catch (COMException) { return null; }
    }

    private static InventorApi.RibbonPanel? TryGetPanel(InventorApi.RibbonTab tab, string internalName)
    {
        try { return tab.RibbonPanels[internalName]; }
        catch (ArgumentException) { return null; }
        catch (COMException) { return null; }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        foreach (var (command, button) in _buttons)
        {
            try
            {
                if (_handlers.TryGetValue(command, out var handler))
                {
                    button.OnExecute -= handler;
                }
            }
            catch
            {
                // Inventor shutting down.
            }
        }

        // Leave the tab in place across a soft reload; remove it fully so a
        // clean unload does not leave an empty "ARCH ENGINEERING" tab behind.
        foreach (var tab in _tabs)
        {
            try { tab.Delete(); }
            catch { /* already gone */ }
        }

        _buttons.Clear();
        _handlers.Clear();
        _tabs.Clear();
    }
}
