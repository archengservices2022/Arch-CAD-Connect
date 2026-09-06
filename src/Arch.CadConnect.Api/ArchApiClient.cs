using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Arch.CadConnect.Api.Dtos;
using Arch.CadConnect.Api.Workspace;
using Arch.CadConnect.Core.Session;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Api;

public sealed class ArchApiClientOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Non-authoritative label stored with the server session so a user can
    /// recognize it later ("Inventor 2025 on WS-01"). Server sanitizes it.
    /// </summary>
    public string? ClientLabel { get; init; }
}

/// <summary>
/// <see cref="IArchApi"/> over <see cref="HttpClient"/>. Talks ONLY to the
/// Arch PLM HTTP API. Never logs; maps every failure to a safe
/// <see cref="ArchApiException"/>.
///
/// The client <see cref="ArchApiClientOptions.Timeout"/> covers the WHOLE
/// exchange - connect, headers AND the response body. A body that starts but
/// then stalls is a <see cref="ArchApiFailureKind.Timeout"/>
/// (<c>IsServerUnavailable</c>), never a hang and never a raw
/// <see cref="OperationCanceledException"/> leaking to a caller.
/// </summary>
public sealed class ArchApiClient : IArchApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string DesktopAuthContract = "arch-plm.desktop-auth.v1";

    /// <summary>Defensive cap on how much of a response body we buffer/consider.</summary>
    private const int MaxResponseBodyBytes = 64 * 1024;

    private readonly HttpClient _http;
    private readonly ArchApiClientOptions _options;

    public ArchApiClient(ArchServerUri server, HttpClient http, ArchApiClientOptions? options = null)
    {
        Server = server ?? throw new ArgumentNullException(nameof(server));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? new ArchApiClientOptions();
    }

    /// <summary>Build a client with a hardened default handler (no redirects, no cookies).</summary>
    public static ArchApiClient Create(ArchServerUri server, ArchApiClientOptions? options = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        var http = new HttpClient(handler, disposeHandler: true);
        return new ArchApiClient(server, http, options);
    }

    public ArchServerUri Server { get; }

    // ---- Authentication -------------------------------------------------

    public async Task<IArchSession> SignInAsync(
        string email,
        string password,
        string? clientLabel,
        CancellationToken ct = default)
    {
        var body = new LoginRequestDto(email, password, clientLabel ?? _options.ClientLabel);
        using var request = new HttpRequestMessage(HttpMethod.Post, Server.ResolvePath("/api/desktop/auth/login"))
        {
            Content = JsonContent.Create(body, options: Json),
        };

        var (status, text) = await SendAndReadAsync(request, session: null, ct).ConfigureAwait(false);
        EnsureSuccess(status, text);

        var dto = Deserialize<LoginResponseDto>(text);
        if (!string.Equals(dto.Contract, DesktopAuthContract, StringComparison.Ordinal)
            || !string.Equals(dto.TokenType, "Bearer", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(dto.Token))
        {
            throw new ArchApiException(ArchApiFailureKind.Server, GenericServerMessage, diagnostic: "login response failed contract check");
        }

        return new DesktopSession(Server, ToIdentity(dto.Identity), dto.Token, dto.ExpiresAt, DateTimeOffset.UtcNow);
    }

    public async Task SignOutAsync(IArchSession session, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Server.ResolvePath("/api/desktop/auth/logout"));
        try
        {
            // Logout is best-effort and idempotent server-side. NEITHER a
            // mapped ArchApiException (network/timeout/5xx/4xx) NOR a raw
            // cancellation may stop the caller from clearing its local
            // session - so swallow both here.
            await SendAndReadAsync(request, session, ct).ConfigureAwait(false);
        }
        catch (ArchApiException)
        {
            // handled by the caller clearing local state
        }
        catch (OperationCanceledException)
        {
            // SendAndReadAsync maps these already; this is belt-and-suspenders
            // for a cancellation raised before the request is even sent.
        }
    }

    public async Task<SessionResponseDto> GetSessionAsync(IArchSession session, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Server.ResolvePath("/api/desktop/session"));

        var (status, text) = await SendAndReadAsync(request, session, ct).ConfigureAwait(false);
        EnsureSuccess(status, text);

        var dto = Deserialize<SessionResponseDto>(text);
        if (!string.Equals(dto.Contract, DesktopAuthContract, StringComparison.Ordinal))
        {
            throw new ArchApiException(ArchApiFailureKind.Server, GenericServerMessage, diagnostic: "session response failed contract check");
        }
        return dto;
    }

    // ---- Get Latest (P4B) --------------------------------------------

    public Task<ResolvedCadDocument> ResolveCadDocumentAsync(
        IArchSession session, CadDocumentLookup lookup, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(lookup);
        return GetLatestOrchestrator.ResolveViaHttpAsync(
            Server, session, _http, lookup, _options.Timeout, ct);
    }

    public Task<MaterializationReport> GetLatestAsync(
        IArchSession session, GetLatestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        // The orchestrator wires resolve + plan fetch + verified downloads +
        // materialize + manifest. Its plan client / downloader reuse THIS
        // client's hardened HttpClient (no redirects, no cookies).
        var orchestrator = new GetLatestOrchestrator(
            Server,
            resolve: (s, lu, c) => ResolveCadDocumentAsync(s, lu, c),
            planClientFactory: s => new WorkspacePlanClient(Server, s, _http, _options.Timeout),
            downloaderFactory: s => new HttpContentDownloader(Server, s, _http));
        return orchestrator.RunAsync(session, request, ct);
    }

    // ---- PDM write operations: declared, not implemented yet (P4C) -------

    public Task<PlmDocumentStatusDto> GetDocumentStatusAsync(IArchSession s, string id, CancellationToken ct = default)
        => throw ArchApiException.NotImplemented("Status");

    public Task CheckoutAsync(IArchSession s, string id, CancellationToken ct = default)
        => throw ArchApiException.NotImplemented("Checkout");

    public Task CheckInAsync(IArchSession s, string id, string localFilePath, CancellationToken ct = default)
        => throw ArchApiException.NotImplemented("Check In");

    public Task UndoCheckoutAsync(IArchSession s, string id, CancellationToken ct = default)
        => throw ArchApiException.NotImplemented("Undo Checkout");

    public Task<WhereUsedDto> GetWhereUsedAsync(IArchSession s, string id, CancellationToken ct = default)
        => throw ArchApiException.NotImplemented("Where Used");

    public Task<ReleaseInfoDto> GetReleaseInfoAsync(IArchSession s, string itemRevisionId, CancellationToken ct = default)
        => throw ArchApiException.NotImplemented("Release information");

    // ---- Transport -----------------------------------------------------

    private const string GenericServerMessage =
        "Arch PLM returned an unexpected response. Please try again or contact your administrator.";

    /// <summary>
    /// Send the request and read the FULL response body, all under a single
    /// timeout that spans connect + headers + body. Returns the status and the
    /// (capped) body text. Any failure - including a body that stalls after
    /// the headers arrive - is thrown as a mapped <see cref="ArchApiException"/>.
    /// </summary>
    private async Task<(HttpStatusCode Status, string Body)> SendAndReadAsync(
        HttpRequestMessage request,
        IArchSession? session,
        CancellationToken ct)
    {
        // A token is only ever attached to a request whose origin is EXACTLY
        // this client's server origin (defence against a redirected / rewritten
        // request URI carrying the bearer token elsewhere).
        if (session is not null)
        {
            if (request.RequestUri is null || !Server.MatchesOrigin(request.RequestUri))
            {
                throw new ArchApiException(
                    ArchApiFailureKind.BadRequest,
                    "Refusing to send an authenticated request to a different server.");
            }

            request.Headers.Authorization = AuthenticationHeaderValue.Parse(session.AuthorizationHeaderValue());
        }

        request.Headers.Accept.ParseAdd("application/json");

        using var timeoutCts = new CancellationTokenSource(_options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);

            // Never follow a redirect for an (possibly authenticated) request -
            // it could carry the bearer token to another host.
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                throw new ArchApiException(
                    ArchApiFailureKind.Server, GenericServerMessage, (int)response.StatusCode, "unexpected redirect");
            }

            // Read the body under the SAME linked token: a body that stalls
            // after headers is a timeout, not an unbounded wait.
            var bytes = await ReadCappedAsync(response.Content, linked.Token).ConfigureAwait(false);
            return (response.StatusCode, Encoding.UTF8.GetString(bytes));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Fired at ANY stage - connect, headers, or a stalled body.
            throw new ArchApiException(ArchApiFailureKind.Timeout, "The Arch PLM server did not respond in time.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new ArchApiException(ArchApiFailureKind.Network, "The request was cancelled.");
        }
        catch (OperationCanceledException)
        {
            // No token we own is cancelled - treat an unexplained cancellation
            // as a transient server problem rather than letting a raw OCE out.
            throw new ArchApiException(ArchApiFailureKind.Timeout, "The Arch PLM server did not respond in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new ArchApiException(
                ArchApiFailureKind.Network,
                "Could not reach the Arch PLM server. Check the address and your network.",
                diagnostic: ex.GetType().Name,
                inner: ex);
        }
        catch (IOException ex)
        {
            // The connection dropped while the body was being read.
            throw new ArchApiException(
                ArchApiFailureKind.Network,
                "The connection to the Arch PLM server was lost.",
                diagnostic: ex.GetType().Name,
                inner: ex);
        }
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBodyBytes)
            {
                buffer.Write(chunk, 0, MaxResponseBodyBytes - (int)buffer.Length);
                break;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>Throw a mapped exception for a non-2xx status; the server's short
    ///  {"error":"..."} string is used only for user-safe cases.</summary>
    private static void EnsureSuccess(HttpStatusCode status, string body)
    {
        var code = (int)status;
        if (code is >= 200 and < 300)
        {
            return;
        }

        var serverError = TryExtractError(body);
        throw code switch
        {
            400 => new ArchApiException(ArchApiFailureKind.BadRequest, serverError ?? "The request was rejected.", code),
            401 => new ArchApiException(ArchApiFailureKind.Unauthorized, serverError ?? "Your session is not valid. Sign in again.", code),
            403 => new ArchApiException(ArchApiFailureKind.Unauthorized, "You do not have permission for that.", code),
            404 => new ArchApiException(ArchApiFailureKind.NotFound, serverError ?? "Not found.", code),
            409 or 422 => new ArchApiException(ArchApiFailureKind.Conflict, serverError ?? "The operation could not be completed.", code),
            413 => new ArchApiException(ArchApiFailureKind.BadRequest, "The request was too large.", code),
            415 => new ArchApiException(ArchApiFailureKind.BadRequest, "The request format was not accepted.", code),
            _ => new ArchApiException(ArchApiFailureKind.Server, GenericServerMessage, code, $"http {code}"),
        };
    }

    private static T Deserialize<T>(string body)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(body, Json);
            if (value is null)
            {
                throw new ArchApiException(ArchApiFailureKind.Server, GenericServerMessage, diagnostic: "empty body");
            }
            return value;
        }
        catch (JsonException)
        {
            throw new ArchApiException(ArchApiFailureKind.Server, GenericServerMessage, diagnostic: "invalid json body");
        }
    }

    private static string? TryExtractError(string body)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<ApiErrorDto>(body, Json);
            var text = dto?.Error?.Trim();
            return string.IsNullOrEmpty(text) ? null : (text.Length > 300 ? text[..300] : text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Core.Session.ArchIdentity ToIdentity(IdentityDto dto) => new(
        dto.UserId,
        dto.Name,
        dto.Email,
        dto.Role,
        dto.Organization.Id,
        dto.Organization.Code,
        dto.Organization.Name);
}
