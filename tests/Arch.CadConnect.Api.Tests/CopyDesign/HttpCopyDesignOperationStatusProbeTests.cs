using System.Net;

using Arch.CadConnect.Api.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;
using Arch.CadConnect.Core.Session;

namespace Arch.CadConnect.Api.Tests.CopyDesign;

/// <summary>
/// P6D ROUND 3, HIGH fix (items D/E), ROUND 4 (items D/E): the authoritative
/// per-operation Copy Design status probe over a scripted
/// <see cref="FakeHttpHandler"/>. Every non-200 / malformed / internally-
/// inconsistent / unreachable / expectation-mismatched case must fail closed
/// (never throw, never Found) so the orchestrator's RESUME classification
/// refuses to trust an unverifiable OR untrustworthy response.
/// </summary>
public sealed class HttpCopyDesignOperationStatusProbeTests
{
    private static readonly ArchServerUri Server = ArchServerUri.Parse("https://plm.example.com");
    private const string OperationId = "op-1";
    private const string IdempotencyKey = "key-1";
    private static readonly string CanonSha = new string('a', 64);

    private static IArchSession Session() => new DesktopSession(
        Server,
        new ArchIdentity("u1", "Test User", "t@orga.com", "ENGINEER", "org1", "ORGA", "Org A"),
        "arch_dt_TESTTOKEN123456",
        DateTimeOffset.UtcNow.AddHours(12),
        DateTimeOffset.UtcNow);

    private static HttpCopyDesignOperationStatusProbe Probe(FakeHttpHandler handler) =>
        new(Server, new HttpClient(handler), TimeSpan.FromSeconds(10));

    private static string Body(string entries) =>
        $$"""
        {"contract":"arch-plm.copy-design-operation-status.v1","copyDesignOperationId":"{{OperationId}}",
         "idempotencyKey":"{{IdempotencyKey}}","performedAt":"2026-09-22T00:00:00.000Z","entries":[{{entries}}]}
        """;

    /// <summary>The expectation matching the SINGLE well-formed COPY entry
    ///  most tests script in their response body - "cad-src" -> "cad-new",
    ///  "1001"/"1001.ipt"/"IPT", no description.</summary>
    private static CopyDesignOperationStatusExpectation OneCopyExpectation(string? description = null) =>
        new(OperationId, IdempotencyKey, new[]
        {
            new CopyDesignOperationStatusExpectedEntry(
                0, IsCopy: true, "cad-src", "cad-new", "1001", "1001.ipt", "IPT", description),
        });

    private static CopyDesignOperationStatusExpectation OneReuseExpectation() =>
        new(OperationId, IdempotencyKey, new[]
        {
            new CopyDesignOperationStatusExpectedEntry(
                0, IsCopy: false, null, "cad-reused", "1001", "bolt.ipt", "IPT", null),
        });

    private static CopyDesignOperationStatusExpectation EmptyExpectation() =>
        new(OperationId, IdempotencyKey, Array.Empty<CopyDesignOperationStatusExpectedEntry>());

    [Fact]
    public async Task Maps_a_well_formed_PENDING_entry()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT"}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());

        Assert.True(result.Success);
        Assert.Equal(OperationId, result.CopyDesignOperationId);
        var entry = Assert.Single(result.Entries!);
        Assert.True(entry.IsCopy);
        Assert.Equal(CopyDesignOperationEntryState.Pending, entry.State);
        Assert.Equal("cad-src", entry.SourceCadDocumentId);

        Assert.Equal("Bearer arch_dt_TESTTOKEN123456", handler.LastAuthorizationHeader);
        Assert.EndsWith("/api/desktop/copy-design/operations/op-1/status", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Maps_a_well_formed_MATERIALIZED_entry()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            $$"""{"ordinal":0,"action":"COPY","state":"MATERIALIZED","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT","fileVersionId":"fv-1","versionNumber":1,"sha256":"{{CanonSha}}","fileSize":1024}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());

        Assert.True(result.Success);
        var entry = Assert.Single(result.Entries!);
        Assert.Equal(CopyDesignOperationEntryState.Materialized, entry.State);
        Assert.Equal("fv-1", entry.FileVersionId);
        Assert.Equal(1, entry.VersionNumber);
        Assert.Equal(CanonSha, entry.Sha256);
        Assert.Equal(1024L, entry.FileSize);
    }

    [Fact]
    public async Task Maps_a_well_formed_INVALID_entry()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"COPY","state":"INVALID","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT"}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());

        Assert.True(result.Success);
        Assert.Equal(CopyDesignOperationEntryState.Invalid, Assert.Single(result.Entries!).State);
    }

    [Fact]
    public async Task Maps_a_well_formed_REUSED_entry()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"REUSE","state":"REUSED","resultingCadDocumentId":"cad-reused","originalDocumentNumber":"1001","originalFileName":"bolt.ipt","originalDocumentType":"IPT"}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneReuseExpectation());

        Assert.True(result.Success);
        var entry = Assert.Single(result.Entries!);
        Assert.False(entry.IsCopy);
        Assert.Equal(CopyDesignOperationEntryState.Reused, entry.State);
        Assert.Null(entry.SourceCadDocumentId);
    }

    [Fact]
    public async Task A_REUSE_entry_with_a_server_side_description_is_accepted_the_client_has_no_expectation_for_it()
    {
        // The client never submitted a description for a REUSE entry (only
        // a cadDocumentId) - it cannot possibly have an independent
        // expectation for the EXISTING document's current description, so
        // the probe must accept whatever the server reports here.
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"REUSE","state":"REUSED","resultingCadDocumentId":"cad-reused","originalDocumentNumber":"1001","originalFileName":"bolt.ipt","originalDocumentType":"IPT","originalDescription":"Standard M6 bolt"}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneReuseExpectation());

        Assert.True(result.Success);
        Assert.Equal("Standard M6 bolt", Assert.Single(result.Entries!).OriginalDescription);
    }

    [Fact]
    public async Task A_404_is_NotFound()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.NotFound, """{"error":"no such operation"}""");
        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.False(result.Success);
        Assert.Equal(CopyDesignOperationStatusOutcome.NotFound, result.Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_401_or_403_is_AuthenticationFailed(HttpStatusCode status)
    {
        var handler = FakeHttpHandler.Always(status, """{"error":"nope"}""");
        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.AuthenticationFailed, result.Outcome);
    }

    [Fact]
    public async Task A_5xx_is_ServerUnavailable()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.InternalServerError, "oops");
        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.ServerUnavailable, result.Outcome);
    }

    [Fact]
    public async Task A_redirect_is_ServerUnavailable()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.Found, "");
        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.ServerUnavailable, result.Outcome);
    }

    [Fact]
    public async Task Non_JSON_body_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, "not json");
        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_contract_mismatch_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK,
            """{"contract":"some.other.contract","copyDesignOperationId":"op-1","idempotencyKey":"key-1","entries":[]}""");
        var result = await Probe(handler).LookupAsync(Session(), EmptyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_response_naming_a_DIFFERENT_operationId_than_requested_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK,
            """{"contract":"arch-plm.copy-design-operation-status.v1","copyDesignOperationId":"op-DIFFERENT","idempotencyKey":"key-1","entries":[]}""");
        var result = await Probe(handler).LookupAsync(Session(), EmptyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    // P6D ROUND 4, item D/E adversarial coverage -----------------------------

    [Fact]
    public async Task A_response_naming_a_DIFFERENT_idempotencyKey_than_expected_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT"}"""));

        // The response's own idempotencyKey ("key-1", baked into Body())
        // does not match what THIS expectation demands.
        var expectation = new CopyDesignOperationStatusExpectation(
            OperationId, "key-DIFFERENT", OneCopyExpectation().Entries);

        var result = await Probe(handler).LookupAsync(Session(), expectation);
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task An_entry_count_that_does_not_match_the_expectation_is_MalformedResponse_extra_entry()
    {
        // The response has ONE entry; the expectation (built from a
        // ZERO-entry reservation) has none - an "extra" entry the caller
        // never reserved at all.
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT"}"""));

        var result = await Probe(handler).LookupAsync(Session(), EmptyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_missing_REUSE_entry_the_expectation_requires_is_MalformedResponse()
    {
        // The expectation requires ONE REUSE entry; the response has ZERO.
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(""));
        var result = await Probe(handler).LookupAsync(Session(), OneReuseExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_REUSE_entry_reporting_the_WRONG_resultingCadDocumentId_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"REUSE","state":"REUSED","resultingCadDocumentId":"cad-WRONG","originalDocumentNumber":"1001","originalFileName":"bolt.ipt","originalDocumentType":"IPT"}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneReuseExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_REUSE_entry_reporting_a_non_REUSED_state_is_MalformedResponse()
    {
        // The wire shape itself already forbids a REUSE action carrying a
        // COPY-only state, but this proves it end to end against the
        // expectation too - a "PENDING" REUSE entry is nonsense either way.
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"REUSE","state":"PENDING","resultingCadDocumentId":"cad-reused","originalDocumentNumber":"1001","originalFileName":"bolt.ipt","originalDocumentType":"IPT"}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneReuseExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_response_entry_naming_an_ordinal_the_caller_never_reserved_is_MalformedResponse()
    {
        // Ordinal 1 does not exist in a one-entry (ordinal 0) expectation.
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":1,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT"}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_duplicate_ordinal_across_two_entries_fails_the_WHOLE_response_closed()
    {
        var expectation = new CopyDesignOperationStatusExpectation(OperationId, IdempotencyKey, new[]
        {
            new CopyDesignOperationStatusExpectedEntry(0, true, "cad-src", "cad-new", "1001", "1001.ipt", "IPT", null),
            new CopyDesignOperationStatusExpectedEntry(1, true, "cad-src-2", "cad-new-2", "1002", "1002.ipt", "IPT", null),
        });
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT"},{"ordinal":0,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-src-2","resultingCadDocumentId":"cad-new-2","originalDocumentNumber":"1002","originalFileName":"1002.ipt","originalDocumentType":"IPT"}"""));

        var result = await Probe(handler).LookupAsync(Session(), expectation);
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    // P6D ROUND 5, item A: response ORDER must exactly match the
    // expectation's (immutable operation/request) order - a shuffled
    // response naming every correct ordinal exactly once, with every OTHER
    // field otherwise correct, is STILL MalformedResponse.

    private static CopyDesignOperationStatusExpectation ThreeEntryExpectation() =>
        new(OperationId, IdempotencyKey, new[]
        {
            new CopyDesignOperationStatusExpectedEntry(0, true, "cad-src-0", "cad-new-0", "1000", "1000.ipt", "IPT", null),
            new CopyDesignOperationStatusExpectedEntry(1, true, "cad-src-1", "cad-new-1", "1001", "1001.ipt", "IPT", null),
            new CopyDesignOperationStatusExpectedEntry(2, true, "cad-src-2", "cad-new-2", "1002", "1002.ipt", "IPT", null),
        });

    private static string ThreeEntryRow(int ordinal) =>
        $$"""{"ordinal":{{ordinal}},"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-src-{{ordinal}}","resultingCadDocumentId":"cad-new-{{ordinal}}","originalDocumentNumber":"100{{ordinal}}","originalFileName":"100{{ordinal}}.ipt","originalDocumentType":"IPT"}""";

    [Fact]
    public async Task Response_entries_in_the_CORRECT_request_order_are_accepted()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            $"{ThreeEntryRow(0)},{ThreeEntryRow(1)},{ThreeEntryRow(2)}"));

        var result = await Probe(handler).LookupAsync(Session(), ThreeEntryExpectation());

        Assert.True(result.Success);
        Assert.Equal(3, result.Entries!.Count);
    }

    [Fact]
    public async Task Response_entries_in_REVERSED_order_are_MalformedResponse_even_though_every_ordinal_is_present_exactly_once()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            $"{ThreeEntryRow(2)},{ThreeEntryRow(1)},{ThreeEntryRow(0)}"));

        var result = await Probe(handler).LookupAsync(Session(), ThreeEntryExpectation());

        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task Response_entries_with_ONE_MIDDLE_entry_moved_are_MalformedResponse()
    {
        // ordinal 1 moved to the end: 0, 2, 1 - a subtler reordering than a
        // full reversal, still an exact set of {0,1,2} but the WRONG order.
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            $"{ThreeEntryRow(0)},{ThreeEntryRow(2)},{ThreeEntryRow(1)}"));

        var result = await Probe(handler).LookupAsync(Session(), ThreeEntryExpectation());

        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_wrong_immutable_originalFileName_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"WRONG-NAME.ipt","originalDocumentType":"IPT"}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_wrong_immutable_originalDocumentNumber_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"9999-WRONG","originalFileName":"1001.ipt","originalDocumentType":"IPT"}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_wrong_originalDocumentType_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IAM"}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_wrong_COPY_originalDescription_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT","originalDescription":"the WRONG description"}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation("the expected description"));
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_wrong_source_identity_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-SOME-OTHER-SOURCE","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT"}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_PENDING_entry_carrying_materialization_metadata_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            $$"""{"ordinal":0,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT","fileVersionId":"fv-should-not-be-here","versionNumber":1,"sha256":"{{CanonSha}}","fileSize":1024}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task An_INVALID_entry_carrying_materialization_metadata_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            $$"""{"ordinal":0,"action":"COPY","state":"INVALID","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT","fileVersionId":"fv-should-not-be-here","versionNumber":1,"sha256":"{{CanonSha}}","fileSize":1024}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_REUSED_entry_carrying_materialization_metadata_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            $$"""{"ordinal":0,"action":"REUSE","state":"REUSED","resultingCadDocumentId":"cad-reused","originalDocumentNumber":"1001","originalFileName":"bolt.ipt","originalDocumentType":"IPT","versionNumber":1,"sha256":"{{CanonSha}}","fileSize":1024}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneReuseExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_duplicate_resultingCadDocumentId_fails_the_WHOLE_response_closed()
    {
        var expectation = new CopyDesignOperationStatusExpectation(OperationId, IdempotencyKey, new[]
        {
            new CopyDesignOperationStatusExpectedEntry(0, true, "cad-src", "cad-new", "1001", "1001.ipt", "IPT", null),
            new CopyDesignOperationStatusExpectedEntry(1, true, "cad-src-2", "cad-new", "1002", "1002.ipt", "IPT", null),
        });
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT"},{"ordinal":1,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-src-2","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1002","originalFileName":"1002.ipt","originalDocumentType":"IPT"}"""));

        var result = await Probe(handler).LookupAsync(Session(), expectation);
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_MATERIALIZED_entry_missing_canonical_integrity_metadata_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"COPY","state":"MATERIALIZED","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT","fileVersionId":"fv-1","versionNumber":1,"sha256":null,"fileSize":1024}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_MATERIALIZED_entry_with_versionNumber_other_than_1_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            $$"""{"ordinal":0,"action":"COPY","state":"MATERIALIZED","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT","fileVersionId":"fv-1","versionNumber":2,"sha256":"{{CanonSha}}","fileSize":1024}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_MATERIALIZED_entry_with_a_malformed_hash_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"COPY","state":"MATERIALIZED","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT","fileVersionId":"fv-1","versionNumber":1,"sha256":"NOT-CANONICAL-HEX","fileSize":1024}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_MATERIALIZED_entry_with_a_negative_fileSize_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            $$"""{"ordinal":0,"action":"COPY","state":"MATERIALIZED","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT","fileVersionId":"fv-1","versionNumber":1,"sha256":"{{CanonSha}}","fileSize":-5}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    // P6D ROUND 5, item B: fileSize must be STRICTLY POSITIVE for MATERIALIZED.

    [Fact]
    public async Task A_MATERIALIZED_entry_with_fileSize_ZERO_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            $$"""{"ordinal":0,"action":"COPY","state":"MATERIALIZED","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT","fileVersionId":"fv-1","versionNumber":1,"sha256":"{{CanonSha}}","fileSize":0}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_MATERIALIZED_entry_with_a_positive_fileSize_is_accepted()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            $$"""{"ordinal":0,"action":"COPY","state":"MATERIALIZED","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT","fileVersionId":"fv-1","versionNumber":1,"sha256":"{{CanonSha}}","fileSize":1}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.True(result.Success);
        Assert.Equal(1L, Assert.Single(result.Entries!).FileSize);
    }

    [Fact]
    public async Task An_unknown_action_value_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"MOVE","state":"PENDING","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT"}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task An_unknown_state_value_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"COPY","state":"SOMETHING-ELSE","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"cad-new","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT"}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_REUSE_entry_carrying_a_sourceCadDocumentId_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"REUSE","state":"REUSED","sourceCadDocumentId":"cad-should-not-be-here","resultingCadDocumentId":"cad-reused","originalDocumentNumber":"1001","originalFileName":"bolt.ipt","originalDocumentType":"IPT"}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneReuseExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_blank_resultingCadDocumentId_is_MalformedResponse()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            """{"ordinal":0,"action":"COPY","state":"PENDING","sourceCadDocumentId":"cad-src","resultingCadDocumentId":"","originalDocumentNumber":"1001","originalFileName":"1001.ipt","originalDocumentType":"IPT"}"""));

        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
    }

    [Fact]
    public async Task A_thrown_exception_from_the_transport_is_ServerUnavailable_never_propagated()
    {
        var handler = FakeHttpHandler.Throw(new HttpRequestException("connection refused"));
        var result = await Probe(handler).LookupAsync(Session(), OneCopyExpectation());
        Assert.Equal(CopyDesignOperationStatusOutcome.ServerUnavailable, result.Outcome);
    }

    [Fact]
    public async Task A_blank_operationId_is_MalformedResponse_and_never_sends_a_request()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(""));
        var expectation = new CopyDesignOperationStatusExpectation("   ", IdempotencyKey, Array.Empty<CopyDesignOperationStatusExpectedEntry>());
        var result = await Probe(handler).LookupAsync(Session(), expectation);
        Assert.Equal(CopyDesignOperationStatusOutcome.MalformedResponse, result.Outcome);
        Assert.Empty(handler.Requests);
    }
}
