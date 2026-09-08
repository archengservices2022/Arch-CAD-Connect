using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Arch.CadConnect.Core.References;
using Arch.CadConnect.Core.Session;

namespace Arch.CadConnect.Api.References;

file static class OpaqueId
{
    /// <summary>A stable identifier is opaque - leading / trailing whitespace is
    ///  a contract violation, NOT something to normalise away into a valid
    ///  identity.</summary>
    public static bool HasSurroundingWhitespace(string value) =>
        value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));
}

/// <summary>
/// <see cref="ILatestVersionProbe"/> over the authenticated Arch PLM HTTP API.
/// Reuses the established desktop-client transport rules: origin-bound bearer,
/// ALL redirects refused, a single timeout across connect + headers + body,
/// a defensively capped response, and no logging.
///
/// FAIL CLOSED: this method NEVER throws for a transport, auth, contract, or
/// local request-construction failure - every such case becomes a
/// (per-id or whole-lookup) <see cref="LatestVersionOutcome"/> so P5B-B version
/// classification degrades to UNKNOWN VERSION. Only HTTP 200 OK with a
/// contract-stamped body can produce <see cref="LatestVersionOutcome.Found"/>.
///
/// ---------------------------------------------------------------------------
/// SERVER CONTRACT (P5B-B) - IMPLEMENTED in the Arch PLM web repository as the
/// authenticated, tenant-scoped, read-only endpoint below (manually accepted).
/// An older Arch PLM server that predates it still degrades safely: a 404 /
/// missing route returns <see cref="LatestVersionOutcome.LookupUnavailable"/>,
/// while a contract mismatch or otherwise malformed authoritative response
/// returns <see cref="LatestVersionOutcome.MalformedResponse"/> - both fail
/// closed, and every managed reference is classified UNKNOWN VERSION.
///
///   GET /api/desktop/cad-documents/latest-versions
///       ?cadDocumentId=&lt;id&gt;&amp;cadDocumentId=&lt;id&gt;...
///
///   Auth : Authorization: Bearer &lt;desktop session token&gt; (same
///          resolveApiActor path as the other desktop endpoints; tenant is
///          derived server-side, never from the client).
///   Scope: READ-ONLY. Returns identity only - no file bytes, no mutation.
///
///   200 response body:
///   {
///     "contract": "arch-plm.desktop-latest-versions.v1",
///     "results": [
///       { "cadDocumentId": "&lt;id&gt;", "recognized": true,
///         "latestFileVersionId": "&lt;fvId&gt;", "latestVersionNumber": 7 },
///       { "cadDocumentId": "&lt;id&gt;", "recognized": false }
///     ]
///   }
///
///   - "recognized" MUST be an explicit boolean. Missing / null => the entry is
///     malformed and that id fails closed (never treated as Found).
///   - "recognized": true REQUIRES a non-blank "latestFileVersionId".
///   - "recognized": false (or an id absent from "results") means the tenant
///     has no such CadDocument -> that id is UNKNOWN VERSION.
///   - A "cadDocumentId" that appears more than once makes the authoritative
///     identity ambiguous -> that id fails closed (never "last wins").
///   - 401 / 403 -> authentication failed. 404 on the route -> lookup
///     unavailable. A contract mismatch, or any other malformed / non-JSON
///     body, -> malformed response. Any non-200 status (incl. 202 / 206) ->
///     never Found. 5xx / timeout / redirect -> server unavailable.
///
/// NO schema / migration change is required server-side: the newest
/// CadFileVersion per CadDocument is already stored. This is a thin read.
/// ---------------------------------------------------------------------------
/// </summary>
public sealed class HttpLatestVersionProbe : ILatestVersionProbe
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>STRICT UTF-8: invalid bytes throw <see cref="DecoderFallbackException"/>
    ///  rather than being silently repaired with U+FFFD, so corrupt bytes can
    ///  never reach the JSON parser as trusted authoritative data.</summary>
    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Defensive cap on how much of a response body we buffer. An
    ///  over-cap body FAILS - it is never truncated and parsed as a prefix.</summary>
    private const int MaxResponseBodyBytes = 256 * 1024;

    /// <summary>Defensive cap on how many ids we will ask about in one request.</summary>
    private const int MaxIdsPerRequest = 200;

    private readonly ArchServerUri _server;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public HttpLatestVersionProbe(ArchServerUri server, HttpClient http, TimeSpan? timeout = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public static HttpLatestVersionProbe Create(ArchServerUri server, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        return new HttpLatestVersionProbe(server, new HttpClient(handler, disposeHandler: true), timeout);
    }

    public async Task<LatestVersionLookup> LookupAsync(
        IArchSession session,
        IReadOnlyCollection<string> cadDocumentIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(cadDocumentIds);

        // SESSION-ORIGIN BINDING (defence in depth): the bearer token is bound
        // to the session's origin. If this probe is pointed at a different
        // server than the session authenticates, send NOTHING and attach NO
        // token - exact-origin semantics, same as everywhere else in the client.
        if (!_server.MatchesOrigin(session.Server.Origin))
        {
            return LatestVersionLookup.WholeFailure(LatestVersionOutcome.LookupUnavailable);
        }

        // cadDocumentId is an OPAQUE stable identifier - it is passed through
        // verbatim (never trimmed). Blank / whitespace-only / whitespace-padded
        // values are not valid stable ids and are dropped here; the classifier
        // fails those closed to UNKNOWN VERSION.
        var ids = cadDocumentIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && !OpaqueId.HasSurroundingWhitespace(id))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxIdsPerRequest)
            .ToArray();

        if (ids.Length == 0)
        {
            return LatestVersionLookup.WholeFailure(LatestVersionOutcome.NotAttempted);
        }

        try
        {
            return await SendAndInterpretAsync(session, ids, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Our timeout, or a caller cancellation - either way, fail closed.
            return LatestVersionLookup.WholeFailure(LatestVersionOutcome.ServerUnavailable);
        }
        catch (HttpRequestException)
        {
            return LatestVersionLookup.WholeFailure(LatestVersionOutcome.ServerUnavailable);
        }
        catch (SocketException)
        {
            return LatestVersionLookup.WholeFailure(LatestVersionOutcome.ServerUnavailable);
        }
        catch (IOException)
        {
            return LatestVersionLookup.WholeFailure(LatestVersionOutcome.ServerUnavailable);
        }
        catch (Exception ex) when (
            ex is UriFormatException or FormatException or ArgumentException
               or InvalidOperationException or NotSupportedException
               or JsonException or InvalidDataException or OverflowException
               or ObjectDisposedException or DecoderFallbackException)
        {
            // A local request-construction / auth-header / timeout-setup /
            // body-decoding / body-handling / parsing failure (invalid UTF-8
            // included). Never let it escape, and never let it become a
            // currency guess.
            return LatestVersionLookup.WholeFailure(LatestVersionOutcome.MalformedResponse);
        }
    }

    private async Task<LatestVersionLookup> SendAndInterpretAsync(
        IArchSession session, string[] ids, CancellationToken ct)
    {
        var query = string.Join("&", ids.Select(id => "cadDocumentId=" + Uri.EscapeDataString(id)));
        var url = _server.ResolvePath("/api/desktop/cad-documents/latest-versions?" + query);

        // The resolved URL must ALSO sit on this session's origin.
        if (!_server.MatchesOrigin(url))
        {
            return LatestVersionLookup.WholeFailure(LatestVersionOutcome.LookupUnavailable);
        }

        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(session.AuthorizationHeaderValue());
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);

        // Decide the body-INDEPENDENT outcomes from the AUTHORITATIVE HTTP
        // status ALONE, before a single body byte is read. A 401 / 403 with an
        // oversized, malformed, invalid-UTF-8, or stalled body is still an
        // authentication failure - and still invalidates the session upstream.
        var code = (int)response.StatusCode;
        if (code is 401 or 403)
        {
            return LatestVersionLookup.WholeFailure(LatestVersionOutcome.AuthenticationFailed);
        }
        if (code is >= 300 and < 400)
        {
            return LatestVersionLookup.WholeFailure(LatestVersionOutcome.ServerUnavailable);
        }
        if (code is 404 or 405 or 501)
        {
            return LatestVersionLookup.WholeFailure(LatestVersionOutcome.LookupUnavailable);
        }

        var body = await ReadCappedAsync(response.Content, linked.Token).ConfigureAwait(false);
        return Interpret(response.StatusCode, body);
    }

    private static LatestVersionLookup Interpret(HttpStatusCode status, string body)
    {
        var code = (int)status;

        if (code is >= 300 and < 400)
        {
            return LatestVersionLookup.WholeFailure(LatestVersionOutcome.ServerUnavailable);
        }
        if (code is 401 or 403)
        {
            return LatestVersionLookup.WholeFailure(LatestVersionOutcome.AuthenticationFailed);
        }
        if (code is 404 or 405 or 501)
        {
            // The route / method is not present on this (older) server.
            return LatestVersionLookup.WholeFailure(LatestVersionOutcome.LookupUnavailable);
        }
        if (code is 400 or 413 or 414 or 422)
        {
            return LatestVersionLookup.WholeFailure(LatestVersionOutcome.MalformedResponse);
        }
        if (code != 200)
        {
            // ONLY 200 OK is a successful authoritative lookup. Every other 2xx
            // (202 Accepted, 206 Partial Content, ...) is a contract violation
            // for this synchronous read and must NEVER become Found; anything
            // else is a server problem.
            return LatestVersionLookup.WholeFailure(
                code is >= 200 and < 300
                    ? LatestVersionOutcome.MalformedResponse
                    : LatestVersionOutcome.ServerUnavailable);
        }

        LatestVersionResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<LatestVersionResponseDto>(body, Json);
        }
        catch (JsonException)
        {
            return LatestVersionLookup.WholeFailure(LatestVersionOutcome.MalformedResponse);
        }

        if (dto is null || !LatestVersionContract.IsSupported(dto.Contract) || dto.Results is null)
        {
            return LatestVersionLookup.WholeFailure(LatestVersionOutcome.MalformedResponse);
        }

        var byId = new Dictionary<string, LatestVersionResult>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);

        foreach (var r in dto.Results)
        {
            var id = r?.CadDocumentId;
            if (r is null || id is null || id.Length == 0)
            {
                continue;
            }

            // OPAQUE identity: a cadDocumentId with surrounding whitespace (or
            // one that is whitespace-only) is a contract violation and cannot be
            // trusted as a stable key. It is DROPPED entirely - never trimmed,
            // never mapped onto another identifier, never used to poison or
            // overwrite an exact valid sibling. The caller's exact id then
            // resolves to the fail-closed fallback (DocumentNotRecognized ->
            // UNKNOWN VERSION).
            if (OpaqueId.HasSurroundingWhitespace(id) || string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (byId.ContainsKey(id) || ambiguous.Contains(id))
            {
                // Same cadDocumentId twice -> the authoritative identity is
                // ambiguous. Never "last wins".
                ambiguous.Add(id);
                byId.Remove(id);
                continue;
            }

            if (r.Recognized != true)
            {
                // recognized:false -> not recognized; missing / null -> the
                // entry is malformed and fails closed (never Found).
                byId[id] = LatestVersionResult.Failure(id,
                    r.Recognized == false
                        ? LatestVersionOutcome.DocumentNotRecognized
                        : LatestVersionOutcome.MalformedResponse);
                continue;
            }

            var fvId = r.LatestFileVersionId;
            if (fvId is null || fvId.Length == 0
                || string.IsNullOrWhiteSpace(fvId)
                || OpaqueId.HasSurroundingWhitespace(fvId))
            {
                // Blank OR whitespace-padded latestFileVersionId -> the
                // authoritative identity is not usable. Never trim it into a
                // valid identity.
                byId[id] = LatestVersionResult.Failure(id, LatestVersionOutcome.MalformedResponse);
                continue;
            }

            byId[id] = LatestVersionResult.Found(id, fvId);
        }

        foreach (var id in ambiguous)
        {
            byId[id] = LatestVersionResult.Failure(id, LatestVersionOutcome.MalformedResponse);
        }

        // An id we asked about that the server omitted from "results" is treated
        // as not recognized (fail closed, never a currency guess).
        return LatestVersionLookup.FromResults(byId.Values, LatestVersionOutcome.DocumentNotRecognized);
    }

    private static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        // A declared length over the cap fails immediately - never buffer it.
        if (content.Headers.ContentLength is > MaxResponseBodyBytes)
        {
            throw new InvalidDataException("The Arch PLM response body exceeds the size cap.");
        }

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBodyBytes)
            {
                // Do NOT truncate and parse the prefix - a crafted body could
                // carry valid JSON in the first 256 KiB and garbage after it.
                throw new InvalidDataException("The Arch PLM response body exceeds the size cap.");
            }
            buffer.Write(chunk, 0, read);
        }

        // STRICT decode: invalid UTF-8 throws DecoderFallbackException (caught
        // by the fail-closed boundary as MalformedResponse) - it is never
        // repaired with U+FFFD and never reaches the JSON parser.
        return StrictUtf8.GetString(buffer.ToArray());
    }

    private sealed record LatestVersionResponseDto(
        string Contract = "",
        IReadOnlyList<LatestVersionEntryDto>? Results = null);

    private sealed record LatestVersionEntryDto(
        string CadDocumentId = "",
        bool? Recognized = null,
        string? LatestFileVersionId = null,
        int? LatestVersionNumber = null);
}
