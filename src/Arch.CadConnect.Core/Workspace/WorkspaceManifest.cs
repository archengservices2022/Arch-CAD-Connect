using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arch.CadConnect.Core.Workspace;

/// <summary>
/// The managed-cache metadata a Get Latest writes to
/// <c>&lt;workspaceRoot&gt;\.arch\workspace.json</c>. It binds a local file at
/// <c>root + relativePath</c> to a STABLE Arch identity (cadDocumentId /
/// fileVersionId) because the client RECORDED it from a server response -
/// never because of the file's name.
///
/// AUTHORITY: this file is a cache, never authoritative. The server is. A
/// missing / corrupt / unreadable manifest is treated as "no bindings" -
/// it is never surfaced as an error and is rebuilt on the next successful
/// Get Latest.
///
/// TRUTHFULNESS (PM control 1): an entry is only marked
/// <see cref="WorkspaceManifestEntryState.Verified"/> when the local file's
/// bytes were successfully size + SHA-256 checked THIS run
/// (Downloaded / AlreadyCurrent). If a run cannot verify a path that a
/// previous run had bound (a local-conflict, or a failed download), its entry
/// is downgraded to <see cref="WorkspaceManifestEntryState.Unverified"/> -
/// the identity binding is kept (useful for later checkout), but the manifest
/// never claims the file is a current verified copy when it is not. A
/// partial/failed run only ever calls <see cref="ApplyRun"/> +
/// <see cref="SaveAtomic"/> for what it actually verified; if materialization
/// threw, the manifest is not touched at all.
/// </summary>
public sealed class WorkspaceManifest
{
    public const string SchemaId = "arch-plm.workspace-manifest.v1";
    public const string RelativeManifestPath = @".arch\workspace.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _root;
    private WorkspaceManifestDocument _doc;

    private WorkspaceManifest(string root, WorkspaceManifestDocument doc)
    {
        _root = root;
        _doc = doc;
    }

    public string ManifestFilePath => Path.Combine(_root, RelativeManifestPath);

    public IReadOnlyList<WorkspaceManifestEntry> Entries => _doc.Entries;

    /// <summary>
    /// Load the manifest under <paramref name="workspaceRoot"/>, or an empty
    /// one if it is missing / corrupt / the wrong schema. Never throws for a
    /// bad file.
    /// </summary>
    public static WorkspaceManifest LoadOrEmpty(string workspaceRoot)
    {
        SafeWorkspacePath.RequireAbsoluteRoot(workspaceRoot);
        var root = Path.GetFullPath(workspaceRoot);
        var path = Path.Combine(root, RelativeManifestPath);

        try
        {
            if (File.Exists(path))
            {
                var doc = JsonSerializer.Deserialize<WorkspaceManifestDocument>(File.ReadAllText(path), Json);
                if (doc is not null && doc.Schema == SchemaId && doc.Entries is not null)
                {
                    return new WorkspaceManifest(root, doc);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // fall through - treat as no bindings
        }

        return new WorkspaceManifest(root, WorkspaceManifestDocument.Empty(SchemaId));
    }

    /// <summary>
    /// Merge one completed Get Latest run into the in-memory manifest.
    /// - Verified entries (Downloaded / AlreadyCurrent): identity + verified
    ///   checksum + retrieval time recorded.
    /// - Blocked / Failed for a path already bound: downgraded to Unverified
    ///   (identity kept, no currency claim).
    /// - Blocked / Failed for a path NOT bound: no entry written.
    /// - Entries for paths outside this run's plan: preserved untouched.
    /// Call <see cref="SaveAtomic"/> afterwards to persist.
    /// </summary>
    public void ApplyRun(
        MaterializationReport report,
        WorkspacePlan plan,
        WorkspaceManifestRunContext context,
        DateTimeOffset nowUtc)
    {
        var byPath = _doc.Entries.ToDictionary(e => Key(e.RelativePath), StringComparer.OrdinalIgnoreCase);
        var planByDoc = plan.Entries.ToDictionary(e => e.CadDocumentId);

        foreach (var result in report.Results)
        {
            if (result.RelativePath is null)
            {
                continue; // never resolved a path - nothing to bind
            }
            var key = Key(result.RelativePath);
            planByDoc.TryGetValue(result.CadDocumentId, out var planEntry);

            switch (result.Status)
            {
                case MaterializationStatus.Downloaded:
                case MaterializationStatus.AlreadyCurrent:
                    byPath[key] = new WorkspaceManifestEntry
                    {
                        RelativePath = result.RelativePath,
                        CadDocumentId = result.CadDocumentId,
                        DocumentNumber = result.DocumentNumber ?? planEntry?.DocumentNumber ?? "",
                        FileName = planEntry?.FileName ?? "",
                        CadType = planEntry?.CadType ?? "",
                        FileVersionId = result.FileVersionId ?? planEntry?.Version?.FileVersionId ?? "",
                        VersionNumber = planEntry?.Version?.VersionNumber ?? 0,
                        Checksum = result.Checksum ?? planEntry?.Version?.Checksum ?? "",
                        FileSize = planEntry?.Version?.FileSize ?? 0,
                        IsRoot = planEntry?.IsRoot ?? result.IsRoot,
                        DependsOn = planEntry?.DependsOn.Select(d => d.CadDocumentId).ToArray() ?? [],
                        State = WorkspaceManifestEntryState.Verified,
                        RetrievedAtUtc = nowUtc,
                    };
                    break;

                case MaterializationStatus.Blocked:
                case MaterializationStatus.Failed:
                    if (byPath.TryGetValue(key, out var prior))
                    {
                        byPath[key] = prior with
                        {
                            State = WorkspaceManifestEntryState.Unverified,
                        };
                    }
                    break;
            }
        }

        _doc = _doc with
        {
            Schema = SchemaId,
            ServerOrigin = context.ServerOrigin,
            OrganizationId = context.OrganizationId,
            OrganizationCode = context.OrganizationCode,
            RootCadDocumentId = context.RootCadDocumentId,
            RootDocumentNumber = context.RootDocumentNumber,
            UpdatedAtUtc = nowUtc,
            Entries = byPath.Values
                .OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };
    }

    /// <summary>
    /// Write the manifest atomically (temp file + replace). The on-disk files
    /// stay valid whether or not this succeeds - the temp file is cleaned up
    /// on failure and the previous manifest is left in place - but a failure
    /// is now OBSERVABLE: it throws <see cref="WorkspaceManifestPersistException"/>
    /// so a state-changing P4C operation can report that its local record was
    /// not durably saved (the server remains authoritative; recovery is a
    /// Get Latest).
    /// </summary>
    /// <exception cref="WorkspaceManifestPersistException">the temp write or
    ///  the atomic replace failed (permissions, IO, the file held open, …).</exception>
    public void SaveAtomic()
    {
        var path = ManifestFilePath;
        var dir = Path.GetDirectoryName(path)!;

        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(_doc, Json);
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw new WorkspaceManifestPersistException(
                $"Could not save the workspace manifest ({ex.GetType().Name}).", ex);
        }
    }

    /// <summary>The manifest entry, if any, for an ABSOLUTE local file path
    ///  under this workspace root. Case-insensitive. Never infers - only an
    ///  exact <c>root + relativePath</c> match counts.</summary>
    public WorkspaceManifestEntry? FindByAbsolutePath(string absoluteFilePath)
    {
        if (string.IsNullOrWhiteSpace(absoluteFilePath) || !Path.IsPathFullyQualified(absoluteFilePath))
        {
            return null;
        }
        var full = Path.GetFullPath(absoluteFilePath);
        foreach (var entry in _doc.Entries)
        {
            string candidate;
            try
            {
                candidate = SafeWorkspacePath.ResolveWithinRoot(_root, entry.RelativePath);
            }
            catch (Exception ex) when (ex is UnsafeWorkspacePathException or WorkspaceRootException)
            {
                continue;
            }
            if (string.Equals(candidate, full, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }
        return null;
    }

    // ---- P4C: checkout state (all mutate the matching entry ONLY, then
    //      persist atomically; never create an entry - identity must come
    //      from a prior Get Latest). Matched by exact absolute path, never by
    //      filename. Return VALUE: true = an entry matched and was saved;
    //      false = no entry matched (nothing written). THROWS
    //      WorkspaceManifestPersistException when an entry matched but the
    //      durable save failed - callers of a state-changing operation MUST
    //      treat that as "local record not reconciled". -----------------

    /// <summary>Record that THIS user holds the server checkout of the file at
    ///  <paramref name="absoluteFilePath"/>. Snapshots the exact base
    ///  FileVersion identity + checksum + size so Undo has an authoritative
    ///  target even if the entry's own version fields later drift.</summary>
    public bool MarkCheckedOut(string absoluteFilePath, WorkspaceCheckoutBinding binding)
        => UpdateEntry(absoluteFilePath, e => e with { Checkout = binding });

    /// <summary>Clear the local checkout marker (after a successful check-in or
    ///  undo). Identity + version fields are untouched.</summary>
    public bool ClearCheckout(string absoluteFilePath)
        => UpdateEntry(absoluteFilePath, e => e with { Checkout = null });

    /// <summary>After a verified check-in: point the entry at the NEW immutable
    ///  FileVersion, mark it Verified, and clear the checkout marker.</summary>
    public bool RebindToNewVersion(
        string absoluteFilePath,
        string fileVersionId,
        int versionNumber,
        string checksum,
        long fileSize,
        DateTimeOffset nowUtc,
        WorkspaceManifestEntryState state = WorkspaceManifestEntryState.Verified)
        => UpdateEntry(absoluteFilePath, e => e with
        {
            FileVersionId = fileVersionId,
            VersionNumber = versionNumber,
            Checksum = checksum,
            FileSize = fileSize,
            State = state,
            RetrievedAtUtc = nowUtc,
            Checkout = null,
        });

    /// <summary>Mark the entry Unverified: identity is kept, but the local
    ///  file's current bytes are NOT a confirmed copy of the pinned version.
    ///  Used when a materialize / restore could not be completed.</summary>
    public bool MarkUnverified(string absoluteFilePath, bool clearCheckout = false)
        => UpdateEntry(absoluteFilePath, e => e with
        {
            State = WorkspaceManifestEntryState.Unverified,
            Checkout = clearCheckout ? null : e.Checkout,
        });

    private bool UpdateEntry(string absoluteFilePath, Func<WorkspaceManifestEntry, WorkspaceManifestEntry> mutate)
    {
        var target = FindByAbsolutePath(absoluteFilePath);
        if (target is null)
        {
            return false;
        }
        var key = Key(target.RelativePath);
        _doc = _doc with
        {
            Entries = _doc.Entries
                .Select(e => string.Equals(Key(e.RelativePath), key, StringComparison.OrdinalIgnoreCase) ? mutate(e) : e)
                .ToArray(),
        };
        SaveAtomic();
        return true;
    }

    private static string Key(string relativePath) => relativePath.Trim();
}

/// <summary>
/// The workspace manifest could not be written durably (temp write, atomic
/// replace, permissions, IO, the file held open, …). The on-disk files remain
/// valid and the previous manifest is untouched; the SERVER stays
/// authoritative and recovery is a Get Latest. Callers of a state-changing
/// P4C operation must surface this as "local record not reconciled" - never
/// as a false success.
/// </summary>
public sealed class WorkspaceManifestPersistException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Local record that THIS client holds the server checkout of one managed
/// file. It is a cache of server-authoritative state (the server
/// <c>CadCheckout</c> row is the truth); it exists so the ribbon and Undo can
/// work without a round-trip and so Undo knows the EXACT base FileVersion to
/// restore. Never used as identity - the entry's <c>cadDocumentId</c> is.
/// </summary>
public sealed record WorkspaceCheckoutBinding
{
    [JsonPropertyName("checkoutId")] public string CheckoutId { get; init; } = "";
    [JsonPropertyName("baseFileVersionId")] public string BaseFileVersionId { get; init; } = "";
    [JsonPropertyName("baseVersionNumber")] public int BaseVersionNumber { get; init; }
    [JsonPropertyName("baseChecksum")] public string BaseChecksum { get; init; } = "";
    [JsonPropertyName("baseFileSize")] public long BaseFileSize { get; init; }
    [JsonPropertyName("checkedOutAtUtc")] public DateTimeOffset CheckedOutAtUtc { get; init; }
}

public sealed record WorkspaceManifestRunContext(
    string ServerOrigin,
    string OrganizationId,
    string OrganizationCode,
    string RootCadDocumentId,
    string RootDocumentNumber);

public enum WorkspaceManifestEntryState
{
    /// <summary>Bytes verified (size + SHA-256) this run.</summary>
    Verified,

    /// <summary>Identity known, but the local file's current bytes were NOT
    ///  verified against the pinned version this run.</summary>
    Unverified,
}

public sealed record WorkspaceManifestDocument
{
    [JsonPropertyName("schema")] public string Schema { get; init; } = "";
    [JsonPropertyName("serverOrigin")] public string ServerOrigin { get; init; } = "";
    [JsonPropertyName("organizationId")] public string OrganizationId { get; init; } = "";
    [JsonPropertyName("organizationCode")] public string OrganizationCode { get; init; } = "";
    [JsonPropertyName("rootCadDocumentId")] public string RootCadDocumentId { get; init; } = "";
    [JsonPropertyName("rootDocumentNumber")] public string RootDocumentNumber { get; init; } = "";
    [JsonPropertyName("updatedAtUtc")] public DateTimeOffset UpdatedAtUtc { get; init; }
    [JsonPropertyName("entries")] public IReadOnlyList<WorkspaceManifestEntry> Entries { get; init; } = [];

    public static WorkspaceManifestDocument Empty(string schema) => new() { Schema = schema, Entries = [] };
}

public sealed record WorkspaceManifestEntry
{
    [JsonPropertyName("relativePath")] public string RelativePath { get; init; } = "";
    [JsonPropertyName("cadDocumentId")] public string CadDocumentId { get; init; } = "";
    [JsonPropertyName("documentNumber")] public string DocumentNumber { get; init; } = "";
    [JsonPropertyName("fileName")] public string FileName { get; init; } = "";
    [JsonPropertyName("cadType")] public string CadType { get; init; } = "";
    [JsonPropertyName("fileVersionId")] public string FileVersionId { get; init; } = "";
    [JsonPropertyName("versionNumber")] public int VersionNumber { get; init; }
    [JsonPropertyName("checksum")] public string Checksum { get; init; } = "";
    [JsonPropertyName("fileSize")] public long FileSize { get; init; }
    [JsonPropertyName("isRoot")] public bool IsRoot { get; init; }
    [JsonPropertyName("dependsOn")] public IReadOnlyList<string> DependsOn { get; init; } = [];

    [JsonPropertyName("state")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WorkspaceManifestEntryState State { get; init; } = WorkspaceManifestEntryState.Verified;

    [JsonPropertyName("retrievedAtUtc")] public DateTimeOffset RetrievedAtUtc { get; init; }

    /// <summary>P4C: present only while THIS client holds the server checkout
    ///  of this file. Absent in every P4B-era manifest and in the normal
    ///  "controlled" state - JSON omits it when null.</summary>
    [JsonPropertyName("checkout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkspaceCheckoutBinding? Checkout { get; init; }

    /// <summary>Project to the COM-free identity type used by
    ///  <c>CadDocumentContext</c>.</summary>
    public PlmIdentity ToPlmIdentity() => new(
        CadDocumentId,
        string.IsNullOrEmpty(DocumentNumber) ? null : DocumentNumber,
        string.IsNullOrEmpty(FileVersionId) ? null : FileVersionId,
        VersionNumber == 0 ? null : VersionNumber);
}
