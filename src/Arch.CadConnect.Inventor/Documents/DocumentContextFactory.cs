using System.Runtime.InteropServices;

using Arch.CadConnect.Core;

using InventorApi = Inventor;

namespace Arch.CadConnect.Inventor.Documents;

/// <summary>
/// The ONE place a live <c>Inventor.Document</c> (a COM object) is read and
/// turned into a COM-free <see cref="CadDocumentContext"/>. Nothing here
/// returns, stores, or exposes a COM type. Every COM property read is wrapped
/// because Inventor can throw (RPC_E_*, document mid-close, etc.).
/// </summary>
internal static class DocumentContextFactory
{
    public static CadDocumentContext From(InventorApi.Document? document)
    {
        if (document is null)
        {
            return CadDocumentContext.None;
        }

        string? fullPath = TryGet(() => document.FullFileName);
        string? displayName = TryGet(() => document.DisplayName);
        bool isDirty = TryGet(() => (bool?)document.Dirty) ?? true;

        // A brand-new unsaved Inventor document reports an empty FullFileName.
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            fullPath = null;
        }

        return CadDocumentContext.Create(fullPath, displayName, isDirty);
    }

    /// <summary>The active document of an application, mapped safely.</summary>
    public static CadDocumentContext FromActive(InventorApi.Application? application)
    {
        if (application is null)
        {
            return CadDocumentContext.None;
        }

        InventorApi.Document? active = null;
        try
        {
            // ActiveDocument throws when zero documents are open.
            active = application.ActiveDocument;
        }
        catch (COMException)
        {
            return CadDocumentContext.None;
        }
        catch (InvalidComObjectException)
        {
            return CadDocumentContext.None;
        }

        return From(active);
    }

    private static T? TryGet<T>(Func<T?> read)
    {
        try
        {
            return read();
        }
        catch (COMException)
        {
            return default;
        }
        catch (InvalidComObjectException)
        {
            return default;
        }
        catch (InvalidCastException)
        {
            return default;
        }
    }
}
