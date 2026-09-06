using Arch.CadConnect.Core;
using Arch.CadConnect.Core.Documents;

using InventorApi = Inventor;

namespace Arch.CadConnect.Inventor.Documents;

/// <summary>
/// Bridges Inventor's document lifecycle COM events to a COM-free
/// <see cref="ActiveDocumentTracker"/>. Inventor raises these events on the UI
/// thread, so the mapped <see cref="CadDocumentContext"/> is pushed straight
/// into the tracker (whose <c>Changed</c> event the ribbon controller handles,
/// also on the UI thread).
///
/// Holds the delegate instances in fields so the GC does not collect them
/// while Inventor still holds the COM sink, and unhooks everything in
/// <see cref="Dispose"/>.
/// </summary>
internal sealed class InventorDocumentObserver : IDisposable
{
    private readonly InventorApi.Application _application;
    private readonly ActiveDocumentTracker _tracker;
    private readonly InventorApi.ApplicationEvents _appEvents;

    private readonly InventorApi.ApplicationEventsSink_OnActivateDocumentEventHandler _onActivate;
    private readonly InventorApi.ApplicationEventsSink_OnDeactivateDocumentEventHandler _onDeactivate;
    private readonly InventorApi.ApplicationEventsSink_OnCloseDocumentEventHandler _onClose;
    private readonly InventorApi.ApplicationEventsSink_OnSaveDocumentEventHandler _onSave;

    private bool _disposed;

    public InventorDocumentObserver(InventorApi.Application application, ActiveDocumentTracker tracker)
    {
        _application = application;
        _tracker = tracker;
        _appEvents = application.ApplicationEvents;

        _onActivate = OnActivateDocument;
        _onDeactivate = OnDeactivateDocument;
        _onClose = OnCloseDocument;
        _onSave = OnSaveDocument;

        _appEvents.OnActivateDocument += _onActivate;
        _appEvents.OnDeactivateDocument += _onDeactivate;
        _appEvents.OnCloseDocument += _onClose;
        _appEvents.OnSaveDocument += _onSave;

        // Seed with whatever is already open.
        RefreshFromActiveDocument();
    }

    /// <summary>Re-read the active document now (used on start-up and after Save).</summary>
    public void RefreshFromActiveDocument() =>
        _tracker.Set(DocumentContextFactory.FromActive(_application));

    private void OnActivateDocument(
        InventorApi._Document documentObject,
        InventorApi.EventTimingEnum beforeOrAfter,
        InventorApi.NameValueMap context,
        out InventorApi.HandlingCodeEnum handlingCode)
    {
        handlingCode = InventorApi.HandlingCodeEnum.kEventNotHandled;
        if (beforeOrAfter == InventorApi.EventTimingEnum.kAfter)
        {
            _tracker.Set(DocumentContextFactory.From(documentObject as InventorApi.Document));
        }
    }

    private void OnDeactivateDocument(
        InventorApi._Document documentObject,
        InventorApi.EventTimingEnum beforeOrAfter,
        InventorApi.NameValueMap context,
        out InventorApi.HandlingCodeEnum handlingCode)
    {
        handlingCode = InventorApi.HandlingCodeEnum.kEventNotHandled;
        // On deactivate we do not yet know what (if anything) becomes active;
        // OnActivateDocument for the next document will follow. If the last
        // document is closing, OnCloseDocument handles the "no document" case.
    }

    private void OnCloseDocument(
        InventorApi._Document documentObject,
        string fullDocumentName,
        InventorApi.EventTimingEnum beforeOrAfter,
        InventorApi.NameValueMap context,
        out InventorApi.HandlingCodeEnum handlingCode)
    {
        handlingCode = InventorApi.HandlingCodeEnum.kEventNotHandled;
        if (beforeOrAfter == InventorApi.EventTimingEnum.kAfter)
        {
            // After a close, re-derive from whatever is left (may be None).
            RefreshFromActiveDocument();
        }
    }

    private void OnSaveDocument(
        InventorApi._Document documentObject,
        InventorApi.EventTimingEnum beforeOrAfter,
        InventorApi.NameValueMap context,
        out InventorApi.HandlingCodeEnum handlingCode)
    {
        handlingCode = InventorApi.HandlingCodeEnum.kEventNotHandled;
        if (beforeOrAfter == InventorApi.EventTimingEnum.kAfter)
        {
            // A first save gives an unsaved document its path/identity; a later
            // save clears the dirty flag. Either way, refresh.
            _tracker.Set(DocumentContextFactory.From(documentObject as InventorApi.Document));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            _appEvents.OnActivateDocument -= _onActivate;
            _appEvents.OnDeactivateDocument -= _onDeactivate;
            _appEvents.OnCloseDocument -= _onClose;
            _appEvents.OnSaveDocument -= _onSave;
        }
        catch
        {
            // Inventor may already be tearing down; nothing useful to do.
        }

        _tracker.Clear();
    }
}
