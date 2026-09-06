using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Arch.CadConnect.Core.Session;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Api.Workspace;

/// <summary>
/// Fetches and fully validates the Managed Workspace Plan from the existing
/// authenticated endpoint (<c>GET /api/cad-documents/:id/workspace-plan</c>).
/// A port of <c>web/cli/workspace-plan-client.ts</c>:
///
///   - origin-bound bearer request, ALL redirects refused, non-2xx rejected;
///   - single timeout across connect + headers + JSON body (P4A pattern);
///   - contract shape + id + supported-major-version checks (the same
///     <see cref="WorkspacePlanContract"/> the materializer enforces);
///   - refuses an unsafe/incomplete plan (<c>safe != true</c>) - the whole
///     Get Latest is refused, nothing is downloaded.
/// </summary>
public sealed class WorkspacePlanClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ArchServerUri _server;
    private readonly IArchSession _session;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public WorkspacePlanClient(
        ArchServerUri server,
        IArchSession session,
        HttpClient http,
        TimeSpan? timeout = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public static WorkspacePlanClient Create(ArchServerUri server, IArchSession session, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        return new WorkspacePlanClient(server, session, new HttpClient(handler, disposeHandler: true), timeout);
    }

    /// <summary>
    /// Fetch and validate the plan for <paramref name="cadDocumentId"/>.
    /// </summary>
    /// <exception cref="ArchApiException">transport / non-2xx failure.</exception>
    /// <exception cref="UnsupportedWorkspaceContractException">unknown contract id or unsupported version.</exception>
    /// <exception cref="UnsafeWorkspacePlanException">the plan is not <c>safe</c>.</exception>
    public async Task<WorkspacePlan> FetchAsync(string cadDocumentId, CancellationToken ct = default)
    {
        var url = _server.ResolvePath(
            $"/api/cad-documents/{Uri.EscapeDataString(cadDocumentId)}/workspace-plan");
        if (!_server.MatchesOrigin(url))
        {
            throw new ArchApiException(ArchApiFailureKind.BadRequest, "Refusing to request a plan from a different server.");
        }

        var (status, text) = await SendAndReadAsync(url, ct).ConfigureAwait(false);

        if ((int)status is >= 300 and < 400)
        {
            throw new ArchApiException(ArchApiFailureKind.Server,
                "Arch PLM returned an unexpected response. Please try again.", (int)status, "unexpected redirect");
        }
        if (!IsSuccess(status))
        {
            throw MapFailure((int)status, text);
        }

        WorkspacePlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize<WorkspacePlan>(text, Json);
        }
        catch (JsonException)
        {
            throw new ArchApiException(ArchApiFailureKind.Server,
                "Arch PLM returned an unexpected response. Please try again.", diagnostic: "invalid plan json");
        }

        if (!WorkspacePlanContract.HasValidShape(plan))
        {
            throw new ArchApiException(ArchApiFailureKind.Server,
                "Arch PLM returned an unexpected response. Please try again.", diagnostic: "plan failed shape check");
        }

        if (plan!.Contract.Id != WorkspacePlanContract.Id)
        {
            throw new UnsupportedWorkspaceContractException(
                $"Unsupported workspace contract id: \"{plan.Contract.Id}\".");
        }
        if (!WorkspacePlanContract.IsSupportedVersion(plan.Contract.Version))
        {
            throw new UnsupportedWorkspaceContractException(
                $"Unsupported workspace contract version: \"{plan.Contract.Version}\".");
        }
        if (!plan.Safe)
        {
            throw new UnsafeWorkspacePlanException(plan.Problems);
        }

        return plan;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAndReadAsync(Uri url, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(_session.AuthorizationHeaderValue());
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);

            var bytes = await ReadCappedAsync(response.Content, linked.Token).ConfigureAwait(false);
            return (response.StatusCode, Encoding.UTF8.GetString(bytes));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ArchApiException(ArchApiFailureKind.Timeout, "The Arch PLM server did not respond in time.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new ArchApiException(ArchApiFailureKind.Network, "The request was cancelled.");
        }
        catch (HttpRequestException ex)
        {
            throw new ArchApiException(ArchApiFailureKind.Network,
                "Could not reach the Arch PLM server. Check the address and your network.",
                diagnostic: ex.GetType().Name, inner: ex);
        }
        catch (IOException ex)
        {
            throw new ArchApiException(ArchApiFailureKind.Network,
                "The connection to the Arch PLM server was lost.", diagnostic: ex.GetType().Name, inner: ex);
        }
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        const int max = 8 * 1024 * 1024; // a plan is small; cap defensively
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > max) { buffer.Write(chunk, 0, max - (int)buffer.Length); break; }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static bool IsSuccess(HttpStatusCode s) => (int)s is >= 200 and < 300;

    private static ArchApiException MapFailure(int code, string body)
    {
        var serverError = TryExtractError(body);
        return code switch
        {
            400 => new ArchApiException(ArchApiFailureKind.BadRequest, serverError ?? "The request was rejected.", code),
            401 => new ArchApiException(ArchApiFailureKind.Unauthorized, serverError ?? "Your session is not valid. Sign in again.", code),
            403 => new ArchApiException(ArchApiFailureKind.Unauthorized, "You do not have permission for that.", code),
            404 => new ArchApiException(ArchApiFailureKind.NotFound, serverError ?? "That CAD document was not found in your organization.", code),
            409 or 422 => new ArchApiException(ArchApiFailureKind.Conflict, serverError ?? "The operation could not be completed.", code),
            _ => new ArchApiException(ArchApiFailureKind.Server, "Arch PLM returned an unexpected response. Please try again.", code, $"http {code}"),
        };
    }

    private static string? TryExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
            {
                var t = e.GetString()?.Trim();
                return string.IsNullOrEmpty(t) ? null : (t.Length > 300 ? t[..300] : t);
            }
        }
        catch (JsonException) { /* ignore */ }
        return null;
    }
}

public sealed class UnsupportedWorkspaceContractException(string message) : Exception(message);

/// <summary>The plan is not <c>safe</c> - a required document has no stored
///  binary, a dependency dangles, or two documents collide on one path. The
///  whole Get Latest is refused. The <see cref="Problems"/> list is safe to
///  show the user.</summary>
public sealed class UnsafeWorkspacePlanException(IReadOnlyList<WorkspacePlanProblem> problems)
    : Exception("The Get Latest package is incomplete or unsafe and was not materialized.")
{
    public IReadOnlyList<WorkspacePlanProblem> Problems { get; } = problems;
}
