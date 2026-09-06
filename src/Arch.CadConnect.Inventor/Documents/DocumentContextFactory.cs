using System.Runtime.InteropServices;

using Arch.CadConnect.Core;
using Arch.CadConnect.Core.Workspace;

using InventorApi = Inventor;

namespace Arch.CadConnect.Inventor.Documents;

/// <summary>
/// The ONE place a live <c>Inventor.Document</c> (a COM object) is read and
/// turned into a COM-free <see cref="CadDocumentContext"/>. Nothing here
/// returns, stores, or exposes a COM type. Every COM property read is wrapped
/// because Inventor can throw (RPC_E_*, document mid-close, etc.).
///
/// P4B: if the document's absolute path matches an entry in a verified
/// workspace manifest (<c>.arch\workspace.json</c>) under one of the known
/// workspace roots, the STABLE Arch identity from that manifest is attached as
/// <see cref="CadDocumentContext.PlmIdentity"/>. The identity is NEVER inferred
/// from the file name - only an exact <c>root + relativePath</c> match counts
/// (see <see cref="WorkspaceBindingResolver"/>).
/// </summary>
internal static class DocumentContextFactory
{
    public static CadDocumentContext From(
        InventorApi.Document? document,
        IEnumerable<string?>? workspaceRoots = null)
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

        var context = CadDocumentContext.Create(fullPath, displayName, isDirty);
        return WithManifestBinding(context, workspaceRoots);
    }

    /// <summary>The active document of an application, mapped safely.</summary>
    public static CadDocumentContext FromActive(
        InventorApi.Application? application,
        IEnumerable<string?>? workspaceRoots = null)
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

        return From(active, workspaceRoots);
    }

    /// <summary>
    /// Attach the stable PLM identity IF the document's exact absolute path is
    /// bound in a workspace manifest. A file that is not in any known managed
    /// workspace stays unmanaged (<see cref="CadDocumentContext.PlmIdentity"/>
    /// == null) - truthfully.
    /// </summary>
    private static CadDocumentContext WithManifestBinding(
        CadDocumentContext context,
        IEnumerable<string?>? workspaceRoots)
    {
        if (workspaceRoots is null || context.FullPath is null)
        {
            return context;
        }

        WorkspaceBindingResolver.Binding? binding;
        try
        {
            binding = WorkspaceBindingResolver.Resolve(context.FullPath, workspaceRoots);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            binding = null;
        }

        return binding is null ? context : context with { PlmIdentity = binding.Identity };
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
