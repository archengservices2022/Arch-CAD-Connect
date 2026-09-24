using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.Session;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Api.CopyDesign;

/// <summary>
/// P6D DRAWING ASSOCIATION AUTHORITY: the real, HTTP-backed authority
/// <c>DrawingAssociation.cs</c>'s class doc comment anticipated - Copy Design
/// Preview previously had no way to confirm a genuinely-existing server
/// <c>DRAWING_REFERENCE</c> relationship, because <c>ArchAddInController</c>
/// never supplied anything but the always-<c>NotAvailable</c>
/// <c>NoDrawingAssociationSource</c> stub. This class is the missing piece:
/// it fetches, in ONE batched request, the authoritative drawing set for
/// every candidate model id (see <see cref="CopyDesignDrawingAssociationCandidates"/>),
/// and the caller wraps the result in a <see cref="PrefetchedDrawingAssociationSource"/>
/// before calling <c>CopyDesignPlanner.Plan</c> (whose
/// <c>IDrawingAssociationSource</c> is synchronous - the network round trip
/// MUST happen before planning, never during it).
///
/// FAIL CLOSED: this method NEVER throws for a transport, auth, contract, or
/// local request-construction failure - every such case becomes
/// <see cref="DrawingAssociationOutcome.NotAvailable"/> for every id in the
/// batch (the planner already treats that exactly like "authority
/// unavailable" - see <c>CopyDesignPlanner.cs</c>). Only HTTP 200 OK with a
/// contract-stamped body can ever produce <see cref="DrawingAssociationOutcome.Found"/>
/// for an id - including a legitimate "Found, zero drawings".
///
/// ---------------------------------------------------------------------------
/// SERVER CONTRACT (P6D) - GET /api/desktop/cad-documents/drawing-associations
///     ?cadDocumentId=&lt;id&gt;&amp;cadDocumentId=&lt;id&gt;...
///
///   200 response body:
///   {
///     "contract": "arch-plm.desktop-drawing-associations.v1",
///     "results": [
///       { "cadDocumentId": "&lt;modelId&gt;", "recognized": true,
///         "drawings": [
///           { "cadDocumentId": "&lt;drawingId&gt;", "documentNumber": "...",
///             "fileName": "...", "cadType": "IDW", "currentFileVersionId": "&lt;fvId&gt;" }
///         ] },
///       { "cadDocumentId": "&lt;otherModelId&gt;", "recognized": false }
///     ]
///   }
///
///   - "recognized" MUST be an explicit boolean. Missing/null -> malformed,
///     that id fails closed to NotAvailable.
///   - "recognized": true REQUIRES a "drawings" array (possibly empty - a
///     legitimate, distinct "this model has no drawing" answer, mapped to
///     Found with zero Drawings, never NotAvailable).
///   - Each drawing entry requires a non-blank "cadDocumentId",
///     "documentNumber" and "fileName"; "cadType" is parsed via
///     <see cref="CopyDesignWireMapping.ParseDocumentTypeWireName"/> (an
///     unrecognized value becomes <c>CadDocumentType.Unknown</c>, which the
///     PLANNER's own evidence validation already rejects as malformed - this
///     client never filters it out itself, so that warning is never lost).
///     "currentFileVersionId" is nullable (the drawing exists but has never
///     had a FileVersion uploaded).
///   - A "cadDocumentId" (model OR drawing) with surrounding whitespace, or a
///     duplicate model id in "results", is a contract violation - dropped /
///     failed closed, never trimmed into validity, never "last wins".
///   - An id requested but absent from "results" -> NotAvailable (never a
///     currency guess).
///   - 401/403 -> authentication failed -> NotAvailable for every id. 404 on
///     the route -> an older server that predates this endpoint ->
///     NotAvailable for every id (degrades exactly like the P6A stub did).
///     A contract mismatch, non-200, or any other malformed/non-JSON body ->
///     NotAvailable for every id.
/// ---------------------------------------------------------------------------
/// </summary>
public interface IDrawingAssociationClient
{
    Task<IReadOnlyDictionary<string, DrawingAssociationResult>> FetchAsync(
        IArchSession session,
        IReadOnlyCollection<string> modelCadDocumentIds,
        WorkspaceManifest? manifest,
        CancellationToken ct = default);
}

public sealed class HttpDrawingAssociationClient : IDrawingAssociationClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>STRICT UTF-8: invalid bytes throw rather than being silently
    ///  repaired, so corrupt bytes can never reach the JSON parser as
    ///  trusted authoritative data.</summary>
    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private const int MaxResponseBodyBytes = 512 * 1024;
    private const int MaxIdsPerRequest = 200;

    private readonly ArchServerUri _server;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public HttpDrawingAssociationClient(ArchServerUri server, HttpClient http, TimeSpan? timeout = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public static HttpDrawingAssociationClient Create(ArchServerUri server, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        return new HttpDrawingAssociationClient(server, new HttpClient(handler, disposeHandler: true), timeout);
    }

    public async Task<IReadOnlyDictionary<string, DrawingAssociationResult>> FetchAsync(
        IArchSession session,
        IReadOnlyCollection<string> modelCadDocumentIds,
        WorkspaceManifest? manifest,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(modelCadDocumentIds);

        var ids = modelCadDocumentIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && !HasSurroundingWhitespace(id))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxIdsPerRequest)
            .ToArray();

        if (ids.Length == 0)
        {
            return new Dictionary<string, DrawingAssociationResult>(StringComparer.Ordinal);
        }

        // SESSION-ORIGIN BINDING (defence in depth): same exact-origin
        // discipline as every other desktop client in this codebase.
        if (!_server.MatchesOrigin(session.Server.Origin))
        {
            return AllNotAvailable(ids);
        }

        try
        {
            return await SendAndInterpretAsync(session, ids, manifest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return AllNotAvailable(ids);
        }
        catch (HttpRequestException)
        {
            return AllNotAvailable(ids);
        }
        catch (SocketException)
        {
            return AllNotAvailable(ids);
        }
        catch (IOException)
        {
            return AllNotAvailable(ids);
        }
        catch (Exception ex) when (
            ex is UriFormatException or FormatException or ArgumentException
               or InvalidOperationException or NotSupportedException
               or JsonException or InvalidDataException or OverflowException
               or ObjectDisposedException or DecoderFallbackException)
        {
            return AllNotAvailable(ids);
        }
    }

    private async Task<IReadOnlyDictionary<string, DrawingAssociationResult>> SendAndInterpretAsync(
        IArchSession session, string[] ids, WorkspaceManifest? manifest, CancellationToken ct)
    {
        var query = string.Join("&", ids.Select(id => "cadDocumentId=" + Uri.EscapeDataString(id)));
        var url = _server.ResolvePath("/api/desktop/cad-documents/drawing-associations?" + query);

        if (!_server.MatchesOrigin(url))
        {
            return AllNotAvailable(ids);
        }

        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(session.AuthorizationHeaderValue());
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            // Every non-200 (auth failure, route missing on an older server,
            // redirect, malformed-request 4xx, 5xx) fails EVERY requested id
            // closed - never a per-id guess based on status alone.
            return AllNotAvailable(ids);
        }

        var body = await ReadCappedAsync(response.Content, linked.Token).ConfigureAwait(false);
        return Interpret(ids, body, manifest);
    }

    private static IReadOnlyDictionary<string, DrawingAssociationResult> Interpret(
        string[] requestedIds, string body, WorkspaceManifest? manifest)
    {
        ResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ResponseDto>(body, Json);
        }
        catch (JsonException)
        {
            return AllNotAvailable(requestedIds);
        }

        if (dto is null || dto.Contract != ContractId || dto.Results is null)
        {
            return AllNotAvailable(requestedIds);
        }

        var byId = new Dictionary<string, DrawingAssociationResult>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);

        foreach (var r in dto.Results)
        {
            var id = r?.CadDocumentId;
            if (r is null || string.IsNullOrEmpty(id) || HasSurroundingWhitespace(id))
            {
                continue;
            }

            if (byId.ContainsKey(id) || ambiguous.Contains(id))
            {
                ambiguous.Add(id);
                byId.Remove(id);
                continue;
            }

            if (r.Recognized != true)
            {
                byId[id] = DrawingAssociationResult.NotAvailable;
                continue;
            }

            if (r.Drawings is null)
            {
                // "recognized": true MUST carry a drawings array (possibly
                // empty) - its absence is malformed, never treated as empty.
                byId[id] = DrawingAssociationResult.NotAvailable;
                continue;
            }

            var drawings = new List<AssociatedDrawing>(r.Drawings.Count);
            var entryMalformed = false;
            foreach (var d in r.Drawings)
            {
                if (d is null
                    || string.IsNullOrEmpty(d.CadDocumentId) || HasSurroundingWhitespace(d.CadDocumentId)
                    || string.IsNullOrEmpty(d.DocumentNumber)
                    || string.IsNullOrEmpty(d.FileName))
                {
                    entryMalformed = true;
                    break;
                }

                var local = manifest?.FindByCadDocumentId(d.CadDocumentId);
                drawings.Add(new AssociatedDrawing(
                    CadDocumentId: d.CadDocumentId,
                    CurrentFileVersionId: string.IsNullOrEmpty(d.CurrentFileVersionId) ? null : d.CurrentFileVersionId,
                    AbsolutePath: local?.AbsolutePath,
                    DocumentType: CopyDesignWireMapping.ParseDocumentTypeWireName(d.CadType),
                    IsVerified: true,
                    AssociationEvidence: "Arch server DRAWING_REFERENCE"));
            }

            // A malformed drawing entry inside an otherwise-recognized result
            // proves nothing usable about THIS model's drawing set - fail
            // this one id closed rather than silently drop just that entry.
            byId[id] = entryMalformed
                ? DrawingAssociationResult.NotAvailable
                : new DrawingAssociationResult(DrawingAssociationOutcome.Found, drawings);
        }

        foreach (var id in ambiguous)
        {
            byId[id] = DrawingAssociationResult.NotAvailable;
        }

        var result = new Dictionary<string, DrawingAssociationResult>(StringComparer.Ordinal);
        foreach (var id in requestedIds)
        {
            result[id] = byId.TryGetValue(id, out var found) ? found : DrawingAssociationResult.NotAvailable;
        }
        return result;
    }

    private static Dictionary<string, DrawingAssociationResult> AllNotAvailable(IEnumerable<string> ids)
    {
        var result = new Dictionary<string, DrawingAssociationResult>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            result[id] = DrawingAssociationResult.NotAvailable;
        }
        return result;
    }

    private static bool HasSurroundingWhitespace(string value) =>
        value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));

    private static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
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
                throw new InvalidDataException("The Arch PLM response body exceeds the size cap.");
            }
            buffer.Write(chunk, 0, read);
        }

        return StrictUtf8.GetString(buffer.ToArray());
    }

    private const string ContractId = "arch-plm.desktop-drawing-associations.v1";

    private sealed record ResponseDto(
        string Contract = "",
        IReadOnlyList<ResultEntryDto>? Results = null);

    private sealed record ResultEntryDto(
        string CadDocumentId = "",
        bool? Recognized = null,
        IReadOnlyList<DrawingEntryDto>? Drawings = null);

    private sealed record DrawingEntryDto(
        string CadDocumentId = "",
        string DocumentNumber = "",
        string FileName = "",
        string CadType = "",
        string? CurrentFileVersionId = null);
}
