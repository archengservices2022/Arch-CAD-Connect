using System.Net;

using Arch.CadConnect.Api.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;
using Arch.CadConnect.Core.Session;

namespace Arch.CadConnect.Api.Tests.CopyDesign;

/// <summary>
/// P6D PRODUCTION RECOVERY: <see cref="HttpDurableCopyDesignOperationStatusClient"/> -
/// the RAW, no-expectation-needed discovery lookup used ONLY to reconstruct a
/// durable resume attempt. Deliberately narrower than
/// <see cref="HttpCopyDesignOperationStatusProbeTests"/> (which this class
/// does NOT replace or modify) - covers the same per-entry structural
/// validation, MINUS anything that needs a caller expectation, PLUS the new
/// <c>sourceFileVersionId</c> field this discovery flow requires.
/// </summary>
public sealed class HttpDurableCopyDesignOperationStatusClientTests
{
    private static readonly ArchServerUri Server = ArchServerUri.Parse("https://plm.example.com");
    private const string OperationId = "op-1";
    private static readonly string CanonSha = new string('a', 64);

    private static IArchSession Session() => new DesktopSession(
        Server,
        new ArchIdentity("u1", "Test User", "t@orga.com", "ENGINEER", "org1", "ORGA", "Org A"),
        "arch_dt_TESTTOKEN123456",
        DateTimeOffset.UtcNow.AddHours(12),
        DateTimeOffset.UtcNow);

    private static HttpDurableCopyDesignOperationStatusClient Client(FakeHttpHandler handler) =>
        new(Server, new HttpClient(handler), TimeSpan.FromSeconds(10));

    private static string Body(string entries) =>
        $$"""
        {"contract":"arch-plm.copy-design-operation-status.v1","copyDesignOperationId":"{{OperationId}}",
         "idempotencyKey":"key-1","performedAt":"2026-09-23T00:00:00.000Z","entries":[{{entries}}]}
        """;

    [Fact]
    public async Task Maps_a_well_formed_PENDING_entry_including_sourceFileVersionId()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-src","sourceFileVersionId":"fv-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT"}"""));

        var result = await Client(handler).GetRawStatusAsync(Session(), OperationId, default);

        Assert.True(result.Success);
        Assert.Equal(OperationId, result.OperationId);
        Assert.Equal("key-1", result.IdempotencyKey);
        var entry = Assert.Single(result.Entries!);
        Assert.Equal("COPY", entry.Action);
        Assert.Equal("PENDING", entry.State);
        Assert.Equal("cad-src", entry.SourceCadDocumentId);
        Assert.Equal("fv-src", entry.SourceFileVersionId);

        Assert.Equal("Bearer arch_dt_TESTTOKEN123456", handler.LastAuthorizationHeader);
        Assert.EndsWith("/api/desktop/copy-design/operations/op-1/status", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Maps_a_well_formed_MATERIALIZED_entry()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            $$"""{"ordinal":0,"action":"COPY","state":"MATERIALIZED","sourceCadDocumentId":"cad-src","sourceFileVersionId":"fv-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT","fileVersionId":"fv-1","versionNumber":1,"sha256":"{{CanonSha}}","fileSize":1024}"""));

        var result = await Client(handler).GetRawStatusAsync(Session(), OperationId, default);

        Assert.True(result.Success);
        var entry = Assert.Single(result.Entries!);
        Assert.Equal("MATERIALIZED", entry.State);
        Assert.Equal("fv-src", entry.SourceFileVersionId);
        Assert.Equal("fv-1", entry.FileVersionId);
        Assert.Equal(1, entry.VersionNumber);
        Assert.Equal(CanonSha, entry.Sha256);
        Assert.Equal(1024L, entry.FileSize);
    }

    [Fact]
    public async Task Maps_a_well_formed_REUSED_entry_with_no_source_fields()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"REUSE","state":"REUSED","resultingCadDocumentId":"cad-reused","originalDocumentNumber":"1001","originalFileName":"bolt.ipt","originalDocumentType":"IPT"}"""));

        var result = await Client(handler).GetRawStatusAsync(Session(), OperationId, default);

        Assert.True(result.Success);
        var entry = Assert.Single(result.Entries!);
        Assert.Equal("REUSE", entry.Action);
        Assert.Null(entry.SourceCadDocumentId);
        Assert.Null(entry.SourceFileVersionId);
    }

    [Fact]
    public async Task A_COPY_entry_missing_sourceFileVersionId_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT"}"""));

        var result = await Client(handler).GetRawStatusAsync(Session(), OperationId, default);

        Assert.Equal(CopyDesignDurableResumeStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_404_is_NotFound()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.NotFound, """{"error":"no such operation"}""");
        var result = await Client(handler).GetRawStatusAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignDurableResumeStatusOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task A_wrong_operation_id_supplied_by_the_caller_still_maps_to_whatever_the_server_returns_for_it()
    {
        // The client always asks for the id it was given; the server (not
        // this client) is what proves existence. A caller-supplied WRONG id
        // simply gets whatever the real endpoint reports for that id - here,
        // simulated as 404, exactly like an unknown one.
        var handler = FakeHttpHandler.Always(HttpStatusCode.NotFound, """{"error":"no such operation"}""");
        var result = await Client(handler).GetRawStatusAsync(Session(), "op-WRONG-ID", default);
        Assert.Equal(CopyDesignDurableResumeStatusOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task A_foreign_tenant_operation_id_is_ALSO_NotFound_indistinguishable_from_unknown()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.NotFound, """{"error":"no such operation"}""");
        var result = await Client(handler).GetRawStatusAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignDurableResumeStatusOutcome.NotFound, result.Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_401_or_403_is_AuthenticationFailed(HttpStatusCode status)
    {
        var handler = FakeHttpHandler.Always(status, """{"error":"nope"}""");
        var result = await Client(handler).GetRawStatusAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignDurableResumeStatusOutcome.AuthenticationFailed, result.Outcome);
    }

    [Fact]
    public async Task A_5xx_is_ServerUnavailable()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.InternalServerError, "oops");
        var result = await Client(handler).GetRawStatusAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignDurableResumeStatusOutcome.ServerUnavailable, result.Outcome);
    }

    [Fact]
    public async Task A_redirect_is_ServerUnavailable()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.Found, "");
        var result = await Client(handler).GetRawStatusAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignDurableResumeStatusOutcome.ServerUnavailable, result.Outcome);
    }

    [Fact]
    public async Task Non_JSON_body_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, "not json");
        var result = await Client(handler).GetRawStatusAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignDurableResumeStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_contract_mismatch_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK,
            """{"contract":"some.other.contract","copyDesignOperationId":"op-1","idempotencyKey":"key-1","entries":[]}""");
        var result = await Client(handler).GetRawStatusAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignDurableResumeStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_response_naming_a_DIFFERENT_operationId_than_requested_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK,
            """{"contract":"arch-plm.copy-design-operation-status.v1","copyDesignOperationId":"op-DIFFERENT","idempotencyKey":"key-1","entries":[]}""");
        var result = await Client(handler).GetRawStatusAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignDurableResumeStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_duplicate_resultingCadDocumentId_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-a","sourceFileVersionId":"fv-a","resultingCadDocumentId":"cad-new","originalDocumentNumber":"A","originalFileName":"A.ipt","originalDocumentType":"IPT"},""" +
            """{"ordinal":1,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-b","sourceFileVersionId":"fv-b","resultingCadDocumentId":"cad-new","originalDocumentNumber":"B","originalFileName":"B.ipt","originalDocumentType":"IPT"}"""));

        var result = await Client(handler).GetRawStatusAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignDurableResumeStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_MATERIALIZED_entry_with_a_non_canonical_checksum_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"COPY","state":"MATERIALIZED","sourceCadDocumentId":"cad-src","sourceFileVersionId":"fv-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT","fileVersionId":"fv-1","versionNumber":1,"sha256":"NOTCANONICAL","fileSize":1024}"""));

        var result = await Client(handler).GetRawStatusAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignDurableResumeStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_MATERIALIZED_entry_with_zero_fileSize_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            $$"""{"ordinal":0,"action":"COPY","state":"MATERIALIZED","sourceCadDocumentId":"cad-src","sourceFileVersionId":"fv-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT","fileVersionId":"fv-1","versionNumber":1,"sha256":"{{CanonSha}}","fileSize":0}"""));

        var result = await Client(handler).GetRawStatusAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignDurableResumeStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_PENDING_entry_contradictorily_carrying_materialization_metadata_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            $$"""{"ordinal":0,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-src","sourceFileVersionId":"fv-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT","fileVersionId":"fv-1"}"""));

        var result = await Client(handler).GetRawStatusAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignDurableResumeStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task An_unrecognized_action_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"DELETE","state":"PENDING","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT"}"""));

        var result = await Client(handler).GetRawStatusAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignDurableResumeStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task An_unrecognized_state_for_a_COPY_action_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"COPY","state":"WEIRD","sourceCadDocumentId":"cad-src","sourceFileVersionId":"fv-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT"}"""));

        var result = await Client(handler).GetRawStatusAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignDurableResumeStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task The_exact_live_operation_shape_round_trips_correctly()
    {
        // Mirrors the LIVE operation this round exists to recover:
        // 3 MATERIALIZED models + 1 PENDING drawing.
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(string.Join(",", new[]
        {
            $$"""{"ordinal":0,"action":"COPY","state":"MATERIALIZED","sourceCadDocumentId":"cad-root-iam","sourceFileVersionId":"fv-root-iam-src","resultingCadDocumentId":"cad-root-iam-new","originalDocumentNumber":"P6DNEW-REAL-ROOT","originalFileName":"P6DNEW-REAL-ROOT.iam","originalDocumentType":"IAM","fileVersionId":"fv-root-iam-1","versionNumber":1,"sha256":"{{CanonSha}}","fileSize":74752}""",
            $$"""{"ordinal":1,"action":"COPY","state":"MATERIALIZED","sourceCadDocumentId":"cad-part-a","sourceFileVersionId":"fv-part-a-src","resultingCadDocumentId":"cad-part-a-new","originalDocumentNumber":"P6DNEW-REAL-PART-A","originalFileName":"P6DNEW-REAL-PART-A.ipt","originalDocumentType":"IPT","fileVersionId":"fv-part-a-1","versionNumber":1,"sha256":"{{CanonSha}}","fileSize":80896}""",
            $$"""{"ordinal":2,"action":"COPY","state":"MATERIALIZED","sourceCadDocumentId":"cad-part-b","sourceFileVersionId":"fv-part-b-src","resultingCadDocumentId":"cad-part-b-new","originalDocumentNumber":"P6DNEW-REAL-PART-B","originalFileName":"P6DNEW-REAL-PART-B.ipt","originalDocumentType":"IPT","fileVersionId":"fv-part-b-1","versionNumber":1,"sha256":"{{CanonSha}}","fileSize":80896}""",
            """{"ordinal":3,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-root-idw","sourceFileVersionId":"fv-root-idw-src","resultingCadDocumentId":"cad-root-idw-new","originalDocumentNumber":"P6DNEW-REAL-ROOT","originalFileName":"P6DNEW-REAL-ROOT.idw","originalDocumentType":"IDW"}""",
        })));

        var result = await Client(handler).GetRawStatusAsync(Session(), OperationId, default);

        Assert.True(result.Success);
        Assert.Equal(4, result.Entries!.Count);
        Assert.Equal(3, result.Entries.Count(e => e.State == "MATERIALIZED"));
        Assert.Equal(1, result.Entries.Count(e => e.State == "PENDING"));
        var idw = result.Entries.Single(e => e.OriginalFileName == "P6DNEW-REAL-ROOT.idw");
        Assert.Equal("cad-root-idw", idw.SourceCadDocumentId);
        Assert.Equal("fv-root-idw-src", idw.SourceFileVersionId);
    }
}
