using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Arch.CadConnect.Core.Session;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Api.Workspace;

/// <summary>
/// Runs the full Get Latest protocol once, end to end - the C# equivalent of
/// <c>web/cli/orchestrate-workspace-get.ts</c> plus the P4B identity binding:
///
///   resolve (documentNumber -> stable cadDocumentId)
///     -> fetch + validate the workspace plan (refuse an unsafe plan wholesale)
///     -> materialize (staged, size + SHA-256 verified, never overwriting)
///     -> update the workspace manifest ATOMICALLY, only for what was verified
///     -> return a MaterializationReport
///
/// The manifest (`.arch\workspace.json`) is touched ONLY after materialization
/// completes; if the plan was refused or materialization threw, the manifest
/// is left exactly as it was (PM control 1).
/// </summary>
public sealed class GetLatestOrchestrator
{
    private readonly ArchServerUri _server;
    private readonly Func<IArchSession, WorkspacePlanClient> _planClientFactory;
    private readonly Func<IArchSession, IContentDownloader> _downloaderFactory;
    private readonly Func<IArchSession, CadDocumentLookup, CancellationToken, Task<ResolvedCadDocument>> _resolve;
    private readonly WorkspaceMaterializer _materializer;
    private readonly Func<DateTimeOffset> _clock;

    public GetLatestOrchestrator(
        ArchServerUri server,
        Func<IArchSession, CadDocumentLookup, CancellationToken, Task<ResolvedCadDocument>> resolve,
        Func<IArchSession, WorkspacePlanClient>? planClientFactory = null,
        Func<IArchSession, IContentDownloader>? downloaderFactory = null,
        WorkspaceMaterializer? materializer = null,
        Func<DateTimeOffset>? clock = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _planClientFactory = planClientFactory ?? (s => WorkspacePlanClient.Create(_server, s));
        _downloaderFactory = downloaderFactory ?? (s => HttpContentDownloader.Create(_server, s));
        _materializer = materializer ?? new WorkspaceMaterializer();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<MaterializationReport> RunAsync(
        IArchSession session,
        GetLatestRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        SafeWorkspacePath.RequireAbsoluteRoot(request.WorkspaceRoot);

        // 1. resolve the explicit selection to an authoritative id.
        var resolved = await _resolve(session, request.Root, ct).ConfigureAwait(false);

        // 2. fetch + validate the version-pinned plan (throws on unsafe).
        var planClient = _planClientFactory(session);
        var plan = await planClient.FetchAsync(resolved.CadDocumentId, ct).ConfigureAwait(false);

        // 3. materialize (per-entry verified downloads; never overwrites).
        var downloader = _downloaderFactory(session);
        var report = await _materializer
            .MaterializeAsync(plan, request.WorkspaceRoot, downloader, ct, _clock)
            .ConfigureAwait(false);

        // 4. update the manifest ATOMICALLY, AFTER a completed run. Only
        //    verified (Downloaded / AlreadyCurrent) entries claim currency.
        try
        {
            var manifest = WorkspaceManifest.LoadOrEmpty(request.WorkspaceRoot);
            manifest.ApplyRun(
                report,
                plan,
                new WorkspaceManifestRunContext(
                    ServerOrigin: _server.OriginString,
                    OrganizationId: session.Identity.OrganizationId,
                    OrganizationCode: session.Identity.OrganizationCode,
                    RootCadDocumentId: resolved.CadDocumentId,
                    RootDocumentNumber: resolved.DocumentNumber),
                _clock());
            manifest.SaveAtomic();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or WorkspaceManifestPersistException)
        {
            // Get Latest is non-destructive: the on-disk files are valid and
            // the manifest is rebuilt on the next successful run. (P4C's
            // state-changing operations do NOT swallow this - see
            // CheckoutOrchestrator.)
        }

        return report;
    }

    /// <summary>The default resolve implementation over
    ///  <c>GET /api/desktop/cad-documents/resolve</c>. Used by
    ///  <c>ArchApiClient</c>; injectable for tests.</summary>
    public static async Task<ResolvedCadDocument> ResolveViaHttpAsync(
        ArchServerUri server,
        IArchSession session,
        HttpClient http,
        CadDocumentLookup lookup,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var url = server.ResolvePath(
            $"/api/desktop/cad-documents/resolve?{lookup.QueryParameter}={Uri.EscapeDataString(lookup.Value)}");
        if (!server.MatchesOrigin(url))
        {
            throw new ArchApiException(ArchApiFailureKind.BadRequest, "Refusing to resolve against a different server.");
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(session.AuthorizationHeaderValue());
            request.Headers.Accept.ParseAdd("application/json");
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ArchApiException(ArchApiFailureKind.Timeout, "The Arch PLM server did not respond in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new ArchApiException(ArchApiFailureKind.Network,
                "Could not reach the Arch PLM server. Check the address and your network.",
                diagnostic: ex.GetType().Name, inner: ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                throw new ArchApiException(ArchApiFailureKind.Server, "Arch PLM returned an unexpected response.", (int)response.StatusCode, "redirect");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new ArchApiException(ArchApiFailureKind.NotFound,
                    $"No CAD document matched \"{lookup.Value}\" in your organization.", 404);
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new ArchApiException(ArchApiFailureKind.Unauthorized, "Your session is not valid. Sign in again.", 401);
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new ArchApiException(ArchApiFailureKind.Server, "Arch PLM returned an unexpected response.", (int)response.StatusCode);
            }

            ResolveDto? dto;
            try { dto = JsonSerializer.Deserialize<ResolveDto>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            catch (JsonException) { dto = null; }

            // Require the exact server contract AND every identity/display field
            // before this result is trusted. A missing / unsupported contract,
            // or an empty cadDocumentId / documentNumber / fileName / cadType,
            // fails here - BEFORE RunAsync fetches a plan, downloads a byte or
            // touches the manifest. Identity is cadDocumentId only; the other
            // fields are validated-present but never used as identity.
            if (dto is null
                || !DesktopCadDocumentContract.IsSupported(dto.Contract)
                || string.IsNullOrWhiteSpace(dto.CadDocumentId)
                || string.IsNullOrWhiteSpace(dto.DocumentNumber)
                || string.IsNullOrWhiteSpace(dto.FileName)
                || string.IsNullOrWhiteSpace(dto.CadType))
            {
                throw new ArchApiException(
                    ArchApiFailureKind.Server,
                    "Arch PLM returned an unexpected response.",
                    diagnostic: "resolve response failed contract check");
            }

            return new ResolvedCadDocument(dto.CadDocumentId, dto.DocumentNumber, dto.FileName, dto.CadType);
        }
    }

    private sealed record ResolveDto(
        string Contract = "",
        string CadDocumentId = "",
        string DocumentNumber = "",
        string FileName = "",
        string CadType = "");
}
