using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Arch.CadConnect.Api.Workspace;
using Arch.CadConnect.Core;
using Arch.CadConnect.Core.CopyDesign.Apply;
using Arch.CadConnect.Core.Session;

namespace Arch.CadConnect.Api.CopyDesign;

/// <summary>
/// P6C: the ONLY client of the EXISTING P6B server contract
/// (<c>POST /api/desktop/copy-design/apply</c>), bearer-authenticated. Same
/// hardening as <see cref="CheckoutHttpClient"/> / <see cref="CheckInClient"/>:
/// origin-bound bearer, redirects refused, one timeout across the whole
/// exchange, raw server error text never surfaced past a mapped
/// <see cref="ArchApiException"/>. Never accesses PostgreSQL directly - this
/// is the sole seam.
/// </summary>
public sealed class CopyDesignApplyHttpClient : ICopyDesignReservationClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ArchServerUri _server;
    private readonly IArchSession _session;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public CopyDesignApplyHttpClient(ArchServerUri server, IArchSession session, HttpClient http, TimeSpan? timeout = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public static CopyDesignApplyHttpClient Create(ArchServerUri server, IArchSession session, TimeSpan? timeout = null)
        => new(server, session, HardenedHttp.NewClient(), timeout);

    public async Task<CopyDesignReservationResponse> ApplyAsync(CopyDesignApplyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wire = new WireRequestDto(
            request.IdempotencyKey,
            request.Label,
            request.WireEntries.Select(ToWireEntry).ToArray());
        var body = JsonSerializer.Serialize(wire, Json);

        var url = _server.ResolvePath("/api/desktop/copy-design/apply");
        if (!_server.MatchesOrigin(url))
        {
            throw new ArchApiException(ArchApiFailureKind.BadRequest,
                "Refusing to send an authenticated request to a different server.");
        }

        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        HttpStatusCode status;
        string text;
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Headers.Authorization = AuthenticationHeaderValue.Parse(_session.AuthorizationHeaderValue());
            httpRequest.Headers.Accept.ParseAdd("application/json");
            httpRequest.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _http
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                throw new ArchApiException(ArchApiFailureKind.Server,
                    "Arch PLM returned an unexpected response.", (int)response.StatusCode, "unexpected redirect");
            }

            status = response.StatusCode;
            text = await ReadCappedAsync(response.Content, linked.Token).ConfigureAwait(false);
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

        if (status != HttpStatusCode.OK)
        {
            throw CheckoutHttpClient.MapFailure((int)status, text);
        }

        WireResponseDto? dto;
        try { dto = JsonSerializer.Deserialize<WireResponseDto>(text, Json); }
        catch (JsonException) { dto = null; }

        if (dto is null || string.IsNullOrWhiteSpace(dto.CopyDesignOperationId) || dto.Entries is null)
        {
            throw new ArchApiException(ArchApiFailureKind.Server,
                "Arch PLM returned an unexpected response. Please try again.", diagnostic: "copy-design/apply response failed shape check");
        }

        var entries = new List<CopyDesignReservationResponseEntry>();
        foreach (var entry in dto.Entries)
        {
            var action = entry.Action switch
            {
                "COPY" => CopyDesignApplyEntryAction.Copy,
                "REUSE" => CopyDesignApplyEntryAction.Reuse,
                _ => (CopyDesignApplyEntryAction?)null,
            };
            if (action is null || string.IsNullOrWhiteSpace(entry.ResultingCadDocumentId)
                || string.IsNullOrWhiteSpace(entry.DocumentNumber) || string.IsNullOrWhiteSpace(entry.FileName)
                || string.IsNullOrWhiteSpace(entry.DocumentType))
            {
                throw new ArchApiException(ArchApiFailureKind.Server,
                    "Arch PLM returned an unexpected response. Please try again.", diagnostic: "copy-design/apply entry failed shape check");
            }
            entries.Add(new CopyDesignReservationResponseEntry(
                action.Value, entry.SourceCadDocumentId, entry.SourceFileVersionId,
                entry.ResultingCadDocumentId, entry.DocumentNumber, entry.FileName, entry.DocumentType));
        }

        return new CopyDesignReservationResponse(dto.Contract ?? "", dto.CopyDesignOperationId, dto.PerformedAt ?? "", entries);
    }

    private static WireEntryDto ToWireEntry(CopyDesignApplyEntry entry) => entry.Action == CopyDesignApplyEntryAction.Copy
        ? new WireEntryDto(
            "COPY", entry.Copy!.SourceCadDocumentId, entry.Copy.SourceFileVersionId,
            entry.Copy.NewDocumentNumber, entry.Copy.NewFileName, CopyDesignWireMapping.DocumentTypeWireName(entry.Copy.DocumentType),
            entry.Copy.Description, CadDocumentId: null)
        : new WireEntryDto("REUSE", null, null, null, null, null, null, entry.Reuse!.CadDocumentId);

    private static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        const int max = 256 * 1024;
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > max) { buffer.Write(chunk, 0, max - (int)buffer.Length); break; }
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed record WireRequestDto(
        [property: JsonPropertyName("idempotencyKey")] string IdempotencyKey,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("entries")] IReadOnlyList<WireEntryDto> Entries);

    private sealed record WireEntryDto(
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("sourceCadDocumentId")] string? SourceCadDocumentId,
        [property: JsonPropertyName("sourceFileVersionId")] string? SourceFileVersionId,
        [property: JsonPropertyName("newDocumentNumber")] string? NewDocumentNumber,
        [property: JsonPropertyName("newFileName")] string? NewFileName,
        [property: JsonPropertyName("documentType")] string? DocumentType,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("cadDocumentId")] string? CadDocumentId);

    private sealed record WireResponseDto(
        [property: JsonPropertyName("contract")] string? Contract,
        [property: JsonPropertyName("copyDesignOperationId")] string? CopyDesignOperationId,
        [property: JsonPropertyName("performedAt")] string? PerformedAt,
        [property: JsonPropertyName("entries")] IReadOnlyList<WireResponseEntryDto>? Entries);

    private sealed record WireResponseEntryDto(
        [property: JsonPropertyName("action")] string? Action,
        [property: JsonPropertyName("sourceCadDocumentId")] string? SourceCadDocumentId,
        [property: JsonPropertyName("sourceFileVersionId")] string? SourceFileVersionId,
        [property: JsonPropertyName("resultingCadDocumentId")] string? ResultingCadDocumentId,
        [property: JsonPropertyName("documentNumber")] string? DocumentNumber,
        [property: JsonPropertyName("fileName")] string? FileName,
        [property: JsonPropertyName("documentType")] string? DocumentType);
}
