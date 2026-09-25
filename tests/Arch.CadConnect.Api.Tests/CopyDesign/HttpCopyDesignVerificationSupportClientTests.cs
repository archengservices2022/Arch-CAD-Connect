using System.Net;

using Arch.CadConnect.Api.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;
using Arch.CadConnect.Core.Session;

namespace Arch.CadConnect.Api.Tests.CopyDesign;

/// <summary>
/// P6E-C: <see cref="HttpCopyDesignVerificationSupportClient"/> - the HTTP/API
/// wiring for the P6E-A verification-support endpoint, mapped into the P6E-B
/// Core verification-engine types. Covers every fail-closed rule the P6E-C
/// task enumerates: contract/operationId/entry validation (mirroring
/// <see cref="HttpDurableCopyDesignOperationStatusClientTests"/> plus this
/// endpoint's own ordinal-contiguity requirement), source-integrity evidence
/// state mapping, and component-edge topology scoping.
/// </summary>
public sealed class HttpCopyDesignVerificationSupportClientTests
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

    private static HttpCopyDesignVerificationSupportClient Client(FakeHttpHandler handler) =>
        new(Server, new HttpClient(handler), TimeSpan.FromSeconds(10));

    private static string Body(string operationEntries, string sourceIntegrity = "", string componentEdges = "", string? operationId = null)
    {
        var opId = operationId ?? OperationId;
        return $$"""
        {"contract":"arch-plm.copy-design-verification-support.v1","copyDesignOperationId":"{{opId}}",
         "operationStatus":{"contract":"arch-plm.copy-design-operation-status.v1","copyDesignOperationId":"{{opId}}","idempotencyKey":"key-1","performedAt":"2026-09-23T00:00:00.000Z","entries":[{{operationEntries}}]},
         "sourceFileVersionIntegrity":[{{sourceIntegrity}}],
         "componentEdges":[{{componentEdges}}]}
        """;
    }

    private const string CopyEntryA =
        """{"ordinal":0,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-src-a","sourceFileVersionId":"fv-src-a","resultingCadDocumentId":"cad-new-a","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT"}""";

    private const string ReuseEntryB =
        """{"ordinal":1,"action":"REUSE","state":"REUSED","resultingCadDocumentId":"cad-reused-b","originalDocumentNumber":"2002","originalFileName":"2002.ipt","originalDocumentType":"IPT"}""";

    private const string ReuseOnlyEntry =
        """{"ordinal":0,"action":"REUSE","state":"REUSED","resultingCadDocumentId":"cad-reused-b","originalDocumentNumber":"2002","originalFileName":"2002.ipt","originalDocumentType":"IPT"}""";

    private static string AvailableEvidenceA =>
        $$"""{"ordinal":0,"sourceCadDocumentId":"cad-src-a","sourceFileVersionId":"fv-src-a","state":"AVAILABLE","sha256":"{{CanonSha}}","fileSize":1024}""";

    private const string UnavailableEvidenceA =
        """{"ordinal":0,"sourceCadDocumentId":"cad-src-a","sourceFileVersionId":"fv-src-a","state":"UNAVAILABLE"}""";

    private const string InvalidEvidenceA =
        """{"ordinal":0,"sourceCadDocumentId":"cad-src-a","sourceFileVersionId":"fv-src-a","state":"INVALID","reason":"non-canonical checksum"}""";

    // ---- 1. valid full COPY response parses -------------------------------

    [Fact]
    public async Task Scenario01_A_valid_full_COPY_response_parses()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA, AvailableEvidenceA));

        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);

        Assert.True(result.Success);
        Assert.Equal(OperationId, result.OperationId);
        Assert.Equal("key-1", result.IdempotencyKey);
        var entry = Assert.Single(result.Entries!);
        Assert.Equal("COPY", entry.Action);
        var evidence = Assert.Single(result.SourceIntegrity!);
        Assert.Equal(CopyDesignSourceIntegrityState.Available, evidence.State);
        Assert.Empty(result.ComponentEdges!);
    }

    // ---- 2. valid nested response parses -----------------------------------

    [Fact]
    public async Task Scenario02_A_valid_nested_response_parses_the_embedded_operationStatus_verbatim()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA, AvailableEvidenceA));

        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);

        Assert.True(result.Success);
        var entry = Assert.Single(result.Entries!);
        Assert.Equal(0, entry.Ordinal);
        Assert.Equal("cad-src-a", entry.SourceCadDocumentId);
        Assert.Equal("fv-src-a", entry.SourceFileVersionId);
        Assert.Equal("cad-new-a", entry.ResultingCadDocumentId);
        Assert.Equal("1001", entry.OriginalDocumentNumber);
    }

    // ---- 3. valid mixed COPY + REUSE response parses -----------------------

    [Fact]
    public async Task Scenario03_A_valid_mixed_COPY_and_REUSE_response_parses()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK,
            Body(CopyEntryA + "," + ReuseEntryB, AvailableEvidenceA));

        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);

        Assert.True(result.Success);
        Assert.Equal(2, result.Entries!.Count);
        Assert.Single(result.Entries, e => e.Action == "COPY");
        Assert.Single(result.Entries, e => e.Action == "REUSE");
        Assert.Single(result.SourceIntegrity!);
    }

    // ---- 4/5/6. per-state evidence mapping ---------------------------------

    [Fact]
    public async Task Scenario04_AVAILABLE_evidence_maps_correctly()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA, AvailableEvidenceA));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);

        var evidence = Assert.Single(result.SourceIntegrity!);
        Assert.Equal(CopyDesignSourceIntegrityState.Available, evidence.State);
        Assert.Equal(CanonSha, evidence.Sha256);
        Assert.Equal(1024L, evidence.FileSize);
        Assert.Null(evidence.Reason);
    }

    [Fact]
    public async Task Scenario05_UNAVAILABLE_evidence_maps_correctly()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA, UnavailableEvidenceA));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);

        var evidence = Assert.Single(result.SourceIntegrity!);
        Assert.Equal(CopyDesignSourceIntegrityState.Unavailable, evidence.State);
        Assert.Null(evidence.Sha256);
        Assert.Null(evidence.FileSize);
    }

    [Fact]
    public async Task Scenario06_INVALID_evidence_maps_correctly()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA, InvalidEvidenceA));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);

        var evidence = Assert.Single(result.SourceIntegrity!);
        Assert.Equal(CopyDesignSourceIntegrityState.Invalid, evidence.State);
        Assert.Equal("non-canonical checksum", evidence.Reason);
        Assert.Null(evidence.Sha256);
        Assert.Null(evidence.FileSize);
    }

    // ---- 7. REUSE with no evidence accepted --------------------------------

    [Fact]
    public async Task Scenario07_REUSE_with_no_evidence_record_at_all_is_accepted()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(ReuseOnlyEntry));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);

        Assert.True(result.Success);
        Assert.Empty(result.SourceIntegrity!);
    }

    // ---- 8. missing COPY evidence rejected ---------------------------------

    [Fact]
    public async Task Scenario08_A_COPY_entry_with_no_evidence_record_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);

        Assert.Equal(CopyDesignVerificationSupportOutcome.MalformedResponse, result.Outcome);
    }

    // ---- 9. duplicate COPY evidence rejected -------------------------------

    [Fact]
    public async Task Scenario09_Duplicate_evidence_for_the_same_COPY_ordinal_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK,
            Body(CopyEntryA, AvailableEvidenceA + "," + UnavailableEvidenceA));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);

        Assert.Equal(CopyDesignVerificationSupportOutcome.MalformedResponse, result.Outcome);
    }

    // ---- 10. evidence references wrong source id rejected ------------------

    [Fact]
    public async Task Scenario10_Evidence_naming_a_DIFFERENT_source_id_than_its_own_entry_is_MalformedResponse()
    {
        var wrongSourceEvidence =
            $$"""{"ordinal":0,"sourceCadDocumentId":"cad-WRONG","sourceFileVersionId":"fv-src-a","state":"UNAVAILABLE"}""";
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA, wrongSourceEvidence));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);

        Assert.Equal(CopyDesignVerificationSupportOutcome.MalformedResponse, result.Outcome);
    }

    // ---- 11. wrong contract rejected ---------------------------------------

    [Fact]
    public async Task Scenario11_A_contract_mismatch_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK,
            """{"contract":"some.other.contract","copyDesignOperationId":"op-1","operationStatus":{"contract":"arch-plm.copy-design-operation-status.v1","copyDesignOperationId":"op-1","idempotencyKey":"key-1","entries":[]},"sourceFileVersionIntegrity":[],"componentEdges":[]}""");
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);

        Assert.Equal(CopyDesignVerificationSupportOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task Scenario11b_A_mismatched_nested_operationStatus_contract_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK,
            """{"contract":"arch-plm.copy-design-verification-support.v1","copyDesignOperationId":"op-1","operationStatus":{"contract":"some.other.contract","copyDesignOperationId":"op-1","idempotencyKey":"key-1","entries":[]},"sourceFileVersionIntegrity":[],"componentEdges":[]}""");
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);

        Assert.Equal(CopyDesignVerificationSupportOutcome.MalformedResponse, result.Outcome);
    }

    // ---- 12. wrong operation id rejected ------------------------------------

    [Fact]
    public async Task Scenario12_A_response_naming_a_DIFFERENT_operationId_than_requested_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA, AvailableEvidenceA, operationId: "op-DIFFERENT"));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);

        Assert.Equal(CopyDesignVerificationSupportOutcome.MalformedResponse, result.Outcome);
    }

    // ---- 13. duplicate / gapped ordinals rejected ---------------------------

    [Fact]
    public async Task Scenario13a_Duplicate_ordinals_across_entries_is_MalformedResponse()
    {
        var dupOrdinal =
            """{"ordinal":0,"action":"REUSE","state":"REUSED","resultingCadDocumentId":"cad-reused-c","originalDocumentNumber":"3003","originalFileName":"3003.ipt","originalDocumentType":"IPT"}""";
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA + "," + dupOrdinal, AvailableEvidenceA));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);

        Assert.Equal(CopyDesignVerificationSupportOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task Scenario13b_A_gapped_ordinal_sequence_is_MalformedResponse()
    {
        var ordinalTwo =
            """{"ordinal":2,"action":"REUSE","state":"REUSED","resultingCadDocumentId":"cad-reused-c","originalDocumentNumber":"3003","originalFileName":"3003.ipt","originalDocumentType":"IPT"}""";
        // Ordinals 0 and 2 present, 1 missing.
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA + "," + ordinalTwo, AvailableEvidenceA));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);

        Assert.Equal(CopyDesignVerificationSupportOutcome.MalformedResponse, result.Outcome);
    }

    // ---- 14. malformed operation entry rejected -----------------------------

    [Fact]
    public async Task Scenario14_An_unrecognized_action_is_MalformedResponse()
    {
        var badEntry =
            """{"ordinal":0,"action":"DELETE","state":"PENDING","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT"}""";
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(badEntry));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);

        Assert.Equal(CopyDesignVerificationSupportOutcome.MalformedResponse, result.Outcome);
    }

    // ---- 15. malformed component edge rejected ------------------------------

    [Fact]
    public async Task Scenario15_A_component_edge_missing_a_required_field_is_MalformedResponse()
    {
        var badEdge = """{"parentCadDocumentId":"cad-src-a"}""";
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA + "," + ReuseEntryB, AvailableEvidenceA, badEdge));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);

        Assert.Equal(CopyDesignVerificationSupportOutcome.MalformedResponse, result.Outcome);
    }

    // ---- 16. edge outside topology set rejected ------------------------------

    [Fact]
    public async Task Scenario16_An_edge_naming_an_endpoint_outside_the_topology_identity_set_is_MalformedResponse()
    {
        var outsideEdge = """{"parentCadDocumentId":"cad-src-a","childCadDocumentId":"cad-OUTSIDE"}""";
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA + "," + ReuseEntryB, AvailableEvidenceA, outsideEdge));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);

        Assert.Equal(CopyDesignVerificationSupportOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task Scenario16b_An_edge_WITHIN_the_topology_identity_set_is_accepted()
    {
        var validEdge = """{"parentCadDocumentId":"cad-src-a","childCadDocumentId":"cad-reused-b"}""";
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA + "," + ReuseEntryB, AvailableEvidenceA, validEdge));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);

        Assert.True(result.Success);
        var edge = Assert.Single(result.ComponentEdges!);
        Assert.Equal("cad-src-a", edge.ParentCadDocumentId);
        Assert.Equal("cad-reused-b", edge.ChildCadDocumentId);
    }

    // ---- 17. duplicate / conflicting edge behavior deterministic ------------

    [Fact]
    public async Task Scenario17_A_literal_duplicate_component_edge_is_deterministically_rejected()
    {
        var edge = """{"parentCadDocumentId":"cad-src-a","childCadDocumentId":"cad-reused-b"}""";
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA + "," + ReuseEntryB, AvailableEvidenceA, edge + "," + edge));
        var result1 = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);
        var result2 = await Client(FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA + "," + ReuseEntryB, AvailableEvidenceA, edge + "," + edge)))
            .GetVerificationSupportAsync(Session(), OperationId, default);

        Assert.Equal(CopyDesignVerificationSupportOutcome.MalformedResponse, result1.Outcome);
        Assert.Equal(result1.Outcome, result2.Outcome);
    }

    // ---- 18/19/20. transport-level fail closed -------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Scenario18_A_401_or_403_is_AuthenticationFailed(HttpStatusCode status)
    {
        var handler = FakeHttpHandler.Always(status, """{"error":"nope"}""");
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignVerificationSupportOutcome.AuthenticationFailed, result.Outcome);
    }

    [Fact]
    public async Task Scenario19_A_404_is_NotFound()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.NotFound, """{"error":"no such operation"}""");
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignVerificationSupportOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Scenario20_A_5xx_is_ServerUnavailable()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.InternalServerError, "oops");
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignVerificationSupportOutcome.ServerUnavailable, result.Outcome);
    }

    // ---- 21. malformed JSON fails closed --------------------------------------

    [Fact]
    public async Task Scenario21_Non_JSON_body_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, "not json");
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignVerificationSupportOutcome.MalformedResponse, result.Outcome);
    }

    // ---- 22. cancellation clean -------------------------------------------

    [Fact]
    public async Task Scenario22_An_already_cancelled_token_resolves_cleanly_without_throwing()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA, AvailableEvidenceA));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, cts.Token);

        Assert.Equal(CopyDesignVerificationSupportOutcome.ServerUnavailable, result.Outcome);
    }

    // ---- 23. no mutation HTTP verbs are used -------------------------------

    [Fact]
    public async Task Scenario23_The_request_uses_GET_only_never_a_mutating_verb()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA, AvailableEvidenceA));
        await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
    }

    // ---- 24. exact request path verified on wire ---------------------------

    [Fact]
    public async Task Scenario24_The_exact_request_path_and_auth_header_are_verified_on_the_wire()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA, AvailableEvidenceA));
        await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("/api/desktop/copy-design/operations/op-1/verification-support", sent.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer arch_dt_TESTTOKEN123456", handler.LastAuthorizationHeader);
    }

    // ---- extra: AVAILABLE without canonical evidence is rejected -----------

    [Fact]
    public async Task AVAILABLE_evidence_missing_sha256_is_MalformedResponse()
    {
        var badEvidence = """{"ordinal":0,"sourceCadDocumentId":"cad-src-a","sourceFileVersionId":"fv-src-a","state":"AVAILABLE","fileSize":1024}""";
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA, badEvidence));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignVerificationSupportOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task AVAILABLE_evidence_missing_fileSize_is_MalformedResponse()
    {
        var badEvidence = $$"""{"ordinal":0,"sourceCadDocumentId":"cad-src-a","sourceFileVersionId":"fv-src-a","state":"AVAILABLE","sha256":"{{CanonSha}}"}""";
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA, badEvidence));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignVerificationSupportOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task UNAVAILABLE_evidence_carrying_sha256_is_MalformedResponse()
    {
        var badEvidence = $$"""{"ordinal":0,"sourceCadDocumentId":"cad-src-a","sourceFileVersionId":"fv-src-a","state":"UNAVAILABLE","sha256":"{{CanonSha}}"}""";
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA, badEvidence));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignVerificationSupportOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task UNAVAILABLE_evidence_carrying_fileSize_is_MalformedResponse()
    {
        var badEvidence = """{"ordinal":0,"sourceCadDocumentId":"cad-src-a","sourceFileVersionId":"fv-src-a","state":"UNAVAILABLE","fileSize":1024}""";
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA, badEvidence));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignVerificationSupportOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task INVALID_evidence_carrying_sha256_is_MalformedResponse()
    {
        var badEvidence = $$"""{"ordinal":0,"sourceCadDocumentId":"cad-src-a","sourceFileVersionId":"fv-src-a","state":"INVALID","reason":"bad","sha256":"{{CanonSha}}"}""";
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA, badEvidence));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignVerificationSupportOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task INVALID_evidence_carrying_fileSize_is_MalformedResponse()
    {
        var badEvidence = """{"ordinal":0,"sourceCadDocumentId":"cad-src-a","sourceFileVersionId":"fv-src-a","state":"INVALID","reason":"bad","fileSize":1024}""";
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA, badEvidence));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignVerificationSupportOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task INVALID_evidence_with_a_blank_reason_is_MalformedResponse()
    {
        var badEvidence = """{"ordinal":0,"sourceCadDocumentId":"cad-src-a","sourceFileVersionId":"fv-src-a","state":"INVALID","reason":""}""";
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(CopyEntryA, badEvidence));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignVerificationSupportOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task Source_evidence_present_for_a_REUSE_ordinal_is_MalformedResponse()
    {
        var reuseEvidence =
            """{"ordinal":1,"sourceCadDocumentId":"cad-src-a","sourceFileVersionId":"fv-src-a","state":"UNAVAILABLE"}""";
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK,
            Body(CopyEntryA + "," + ReuseEntryB, AvailableEvidenceA + "," + reuseEvidence));
        var result = await Client(handler).GetVerificationSupportAsync(Session(), OperationId, default);
        Assert.Equal(CopyDesignVerificationSupportOutcome.MalformedResponse, result.Outcome);
    }
}
