using System.Net;
using System.Text;

using Arch.CadConnect.Api.References;
using Arch.CadConnect.Core.References;
using Arch.CadConnect.Core.Session;

namespace Arch.CadConnect.Api.Tests.References;

/// <summary>
/// The P5B-B authoritative latest-version probe over a scripted
/// <see cref="FakeHttpHandler"/>. Every non-200 / malformed / unreachable case
/// must fail closed (never throw) so version classification degrades to
/// UNKNOWN VERSION.
/// </summary>
public sealed class HttpLatestVersionProbeTests
{
    private static readonly ArchServerUri Server = ArchServerUri.Parse("https://plm.example.com");

    private static IArchSession Session() => new DesktopSession(
        Server,
        new ArchIdentity("u1", "Test User", "t@orga.com", "VIEWER", "org1", "ORGA", "Org A"),
        "arch_dt_TESTTOKEN123456",
        DateTimeOffset.UtcNow.AddHours(12),
        DateTimeOffset.UtcNow);

    private static HttpLatestVersionProbe Probe(FakeHttpHandler handler) =>
        new(Server, new HttpClient(handler), TimeSpan.FromSeconds(10));

    private static readonly string[] Ids = { "cad_a", "cad_b" };

    [Fact]
    public async Task Maps_a_well_formed_contract_response_to_per_id_results()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK,
            """
            {"contract":"arch-plm.desktop-latest-versions.v1","results":[
              {"cadDocumentId":"cad_a","recognized":true,"latestFileVersionId":"fv_a9","latestVersionNumber":9},
              {"cadDocumentId":"cad_b","recognized":false}
            ]}
            """);

        var lookup = await Probe(handler).LookupAsync(Session(), Ids);

        var a = lookup.Get("cad_a");
        Assert.Equal(LatestVersionOutcome.Found, a.Outcome);
        Assert.Equal("fv_a9", a.Version!.LatestFileVersionId);

        Assert.Equal(LatestVersionOutcome.DocumentNotRecognized, lookup.Get("cad_b").Outcome);

        // bearer attached, ids in the query string
        Assert.Equal("Bearer arch_dt_TESTTOKEN123456", handler.LastAuthorizationHeader);
        var url = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("cadDocumentId=cad_a", url);
        Assert.Contains("cadDocumentId=cad_b", url);
        Assert.EndsWith("/api/desktop/cad-documents/latest-versions", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task An_id_the_server_omits_is_not_recognized()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK,
            """
            {"contract":"arch-plm.desktop-latest-versions.v1","results":[
              {"cadDocumentId":"cad_a","recognized":true,"latestFileVersionId":"fv_a"}]}
            """);

        var lookup = await Probe(handler).LookupAsync(Session(), Ids);

        Assert.Equal(LatestVersionOutcome.Found, lookup.Get("cad_a").Outcome);
        Assert.Equal(LatestVersionOutcome.DocumentNotRecognized, lookup.Get("cad_b").Outcome);
    }

    [Fact]
    public async Task A_404_route_means_the_server_does_not_support_lookup_yet()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.NotFound, """{"error":"no route"}""");
        var lookup = await Probe(handler).LookupAsync(Session(), Ids);
        Assert.Equal(LatestVersionOutcome.LookupUnavailable, lookup.Get("cad_a").Outcome);
    }

    [Fact]
    public async Task A_401_is_an_authentication_failure()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.Unauthorized, """{"error":"nope"}""");
        var lookup = await Probe(handler).LookupAsync(Session(), Ids);
        Assert.Equal(LatestVersionOutcome.AuthenticationFailed, lookup.Get("cad_a").Outcome);
    }

    [Fact]
    public async Task A_500_is_server_unavailable()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.InternalServerError, "");
        var lookup = await Probe(handler).LookupAsync(Session(), Ids);
        Assert.Equal(LatestVersionOutcome.ServerUnavailable, lookup.Get("cad_a").Outcome);
    }

    [Fact]
    public async Task A_transport_exception_is_server_unavailable_not_thrown()
    {
        var handler = FakeHttpHandler.Throw(new HttpRequestException("boom"));
        var lookup = await Probe(handler).LookupAsync(Session(), Ids);
        Assert.Equal(LatestVersionOutcome.ServerUnavailable, lookup.Get("cad_a").Outcome);
    }

    [Fact]
    public async Task A_redirect_is_refused_and_treated_as_server_unavailable()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.Found, "");
        var lookup = await Probe(handler).LookupAsync(Session(), Ids);
        Assert.Equal(LatestVersionOutcome.ServerUnavailable, lookup.Get("cad_a").Outcome);
    }

    [Fact]
    public async Task A_wrong_contract_is_a_malformed_response()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK,
            """{"contract":"arch-plm.desktop-latest-versions.v2","results":[]}""");
        var lookup = await Probe(handler).LookupAsync(Session(), Ids);
        Assert.Equal(LatestVersionOutcome.MalformedResponse, lookup.Get("cad_a").Outcome);
    }

    [Fact]
    public async Task Invalid_json_is_a_malformed_response()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, "not json");
        var lookup = await Probe(handler).LookupAsync(Session(), Ids);
        Assert.Equal(LatestVersionOutcome.MalformedResponse, lookup.Get("cad_a").Outcome);
    }

    [Fact]
    public async Task No_ids_is_not_attempted_and_makes_no_request()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, "{}");
        var lookup = await Probe(handler).LookupAsync(Session(), Array.Empty<string>());
        Assert.Equal(LatestVersionOutcome.NotAttempted, lookup.Get("cad_a").Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_recognized_entry_with_no_latest_id_is_malformed_for_that_id()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK,
            """
            {"contract":"arch-plm.desktop-latest-versions.v1","results":[
              {"cadDocumentId":"cad_a","recognized":true}]}
            """);
        var lookup = await Probe(handler).LookupAsync(Session(), Ids);
        Assert.Equal(LatestVersionOutcome.MalformedResponse, lookup.Get("cad_a").Outcome);
    }

    // ---- helpers for the Codex-fix-round tests ---------------------

    private const string ContractId = "arch-plm.desktop-latest-versions.v1";

    /// <summary>A single-line contract body with the given entries verbatim.</summary>
    private static string Body(params string[] entries) =>
        "{\"contract\":\"" + ContractId + "\",\"results\":[" + string.Join(",", entries) + "]}";

    private static string Entry(string id, bool? recognized = true, string? latest = null)
    {
        var parts = new List<string> { "\"cadDocumentId\":\"" + id + "\"" };
        if (recognized is bool b)
        {
            parts.Add("\"recognized\":" + (b ? "true" : "false"));
        }
        else
        {
            parts.Add("\"recognized\":null");
        }
        if (latest is not null)
        {
            parts.Add("\"latestFileVersionId\":\"" + latest + "\"");
        }
        return "{" + string.Join(",", parts) + "}";
    }

    /// <summary>Same as Entry but omits the "recognized" field entirely.</summary>
    private static string EntryNoRecognized(string id, string latest) =>
        "{\"cadDocumentId\":\"" + id + "\",\"latestFileVersionId\":\"" + latest + "\"}";

    private sealed class FakeSession(ArchServerUri server, string header) : IArchSession
    {
        public ArchServerUri Server { get; } = server;
        public ArchIdentity Identity { get; } =
            new("u1", "Test User", "t@orga.com", "VIEWER", "org1", "ORGA", "Org A");
        public DateTimeOffset ExpiresAtUtc => DateTimeOffset.UtcNow.AddHours(1);
        public bool IsExpired(DateTimeOffset nowUtc) => false;
        public string AuthorizationHeaderValue() => header;
    }

    // ---- HIGH 1: session-origin binding -----------------------------

    [Fact]
    public async Task A_session_bound_to_a_different_origin_sends_zero_requests_and_no_token()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body());

        var otherOrigin = new FakeSession(
            ArchServerUri.Parse("https://evil.example.net"), "Bearer arch_dt_STOLEN0000000000000000");

        var lookup = await Probe(handler).LookupAsync(otherOrigin, Ids);

        Assert.Equal(LatestVersionOutcome.LookupUnavailable, lookup.Get("cad_a").Outcome);
        Assert.Empty(handler.Requests);
        Assert.Null(handler.LastAuthorizationHeader);
    }

    [Fact]
    public async Task A_matching_origin_sends_the_request_with_the_bearer()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(Entry("cad_a", latest: "fv_a")));

        var lookup = await Probe(handler).LookupAsync(
            new FakeSession(Server, "Bearer arch_dt_OK00000000000000000000"), Ids);

        Assert.Equal(LatestVersionOutcome.Found, lookup.Get("cad_a").Outcome);
        Assert.Single(handler.Requests);
        Assert.Equal("Bearer arch_dt_OK00000000000000000000", handler.LastAuthorizationHeader);
    }

    // ---- HIGH 2: only 200 OK + strict body -------------------------

    [Theory]
    [InlineData(HttpStatusCode.Accepted)]        // 202
    [InlineData(HttpStatusCode.PartialContent)]  // 206
    [InlineData(HttpStatusCode.NoContent)]       // 204
    public async Task A_non_200_success_status_is_never_a_successful_lookup(HttpStatusCode status)
    {
        var handler = FakeHttpHandler.Always(status, Body(Entry("cad_a", latest: "fv_a")));

        var lookup = await Probe(handler).LookupAsync(Session(), Ids);

        Assert.NotEqual(LatestVersionOutcome.Found, lookup.Get("cad_a").Outcome);
        Assert.Equal(LatestVersionOutcome.MalformedResponse, lookup.Get("cad_a").Outcome);
    }

    [Fact]
    public async Task A_missing_recognized_field_is_malformed_never_found()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(EntryNoRecognized("cad_a", "fv_a")));

        Assert.Equal(LatestVersionOutcome.MalformedResponse,
            (await Probe(handler).LookupAsync(Session(), Ids)).Get("cad_a").Outcome);
    }

    [Fact]
    public async Task A_null_recognized_field_is_malformed_never_found()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(Entry("cad_a", recognized: null, latest: "fv_a")));

        Assert.Equal(LatestVersionOutcome.MalformedResponse,
            (await Probe(handler).LookupAsync(Session(), Ids)).Get("cad_a").Outcome);
    }

    [Fact]
    public async Task An_explicit_recognized_false_is_document_not_recognized()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(Entry("cad_a", recognized: false)));

        Assert.Equal(LatestVersionOutcome.DocumentNotRecognized,
            (await Probe(handler).LookupAsync(Session(), Ids)).Get("cad_a").Outcome);
    }

    [Fact]
    public async Task Duplicate_cadDocumentId_entries_are_ambiguous_and_fail_closed_never_last_wins()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(
            Entry("cad_a", latest: "fv_a1"),
            Entry("cad_a", latest: "fv_a2"),
            Entry("cad_b", latest: "fv_b")));

        var lookup = await Probe(handler).LookupAsync(Session(), Ids);

        var a = lookup.Get("cad_a");
        Assert.Equal(LatestVersionOutcome.MalformedResponse, a.Outcome);
        Assert.Null(a.Version);
        // an unambiguous sibling in the same response is unaffected
        Assert.Equal(LatestVersionOutcome.Found, lookup.Get("cad_b").Outcome);
    }

    [Fact]
    public async Task An_oversized_body_fails_closed_and_is_not_parsed_as_a_prefix()
    {
        // Valid contract JSON, then >256 KiB of filler. A prefix parse would
        // see a Found result; the probe must not do that.
        var huge = Body(Entry("cad_a", latest: "fv_a")) + new string('x', 300 * 1024);
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, huge);

        var lookup = await Probe(handler).LookupAsync(Session(), Ids);

        Assert.NotEqual(LatestVersionOutcome.Found, lookup.Get("cad_a").Outcome);
        Assert.Equal(LatestVersionOutcome.MalformedResponse, lookup.Get("cad_a").Outcome);
    }

    [Fact]
    public async Task An_oversized_declared_content_length_fails_closed()
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            resp.Content.Headers.ContentLength = 10L * 1024 * 1024;
            return resp;
        });

        Assert.Equal(LatestVersionOutcome.MalformedResponse,
            (await Probe(handler).LookupAsync(Session(), Ids)).Get("cad_a").Outcome);
    }

    [Fact]
    public async Task A_truncated_json_body_fails_closed()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK,
            "{\"contract\":\"" + ContractId + "\",\"results\":[{\"cadDocumentId\":\"cad_a\",\"recognized\":true");

        Assert.Equal(LatestVersionOutcome.MalformedResponse,
            (await Probe(handler).LookupAsync(Session(), Ids)).Get("cad_a").Outcome);
    }

    [Fact]
    public async Task Valid_json_with_trailing_garbage_fails_closed()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body() + " then some junk");

        Assert.Equal(LatestVersionOutcome.MalformedResponse,
            (await Probe(handler).LookupAsync(Session(), Ids)).Get("cad_a").Outcome);
    }

    // ---- MEDIUM 1: the lookup boundary fails closed, never throws ---

    [Fact]
    public async Task A_malformed_authorization_header_value_does_not_escape_as_an_exception()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, "{}");
        var badHeader = new FakeSession(Server, "Bearer bad\r\nInjected-Header: x");

        var lookup = await Probe(handler).LookupAsync(badHeader, Ids);

        Assert.Equal(LatestVersionOutcome.MalformedResponse, lookup.Get("cad_a").Outcome);
        Assert.Empty(handler.Requests); // never sent
    }

    [Fact]
    public async Task A_bad_timeout_setup_does_not_escape_as_an_exception()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, "{}");
        var probe = new HttpLatestVersionProbe(Server, new HttpClient(handler), TimeSpan.FromSeconds(-5));

        var lookup = await probe.LookupAsync(Session(), Ids);

        Assert.Equal(LatestVersionOutcome.MalformedResponse, lookup.Get("cad_a").Outcome);
    }

    [Fact]
    public async Task A_null_session_or_null_ids_still_throws_argument_null()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, "{}");
        await Assert.ThrowsAsync<ArgumentNullException>(() => Probe(handler).LookupAsync(null!, Ids));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Probe(handler).LookupAsync(Session(), null!));
    }

    // ---- Round 2 HIGH 1: opaque stable identifiers ----------------

    [Fact]
    public async Task An_exact_opaque_latest_fileVersionId_is_returned_verbatim()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(Entry("cad_a", latest: "fv_a")));

        var a = (await Probe(handler).LookupAsync(Session(), Ids)).Get("cad_a");

        Assert.Equal(LatestVersionOutcome.Found, a.Outcome);
        Assert.Equal("fv_a", a.Version!.LatestFileVersionId); // not " fv_a " and not trimmed from anything
        Assert.Equal("cad_a", a.Version.CadDocumentId);
    }

    [Fact]
    public async Task A_whitespace_padded_authoritative_latest_fileVersionId_is_malformed_never_found()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, Body(Entry("cad_a", latest: " fv_a ")));

        var a = (await Probe(handler).LookupAsync(Session(), Ids)).Get("cad_a");

        Assert.NotEqual(LatestVersionOutcome.Found, a.Outcome);
        Assert.Equal(LatestVersionOutcome.MalformedResponse, a.Outcome);
        Assert.Null(a.Version);
    }

    [Fact]
    public async Task A_whitespace_padded_authoritative_cadDocumentId_is_dropped_never_normalised_onto_the_exact_id()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK,
            "{\"contract\":\"" + ContractId + "\",\"results\":["
            + "{\"cadDocumentId\":\" cad_a \",\"recognized\":true,\"latestFileVersionId\":\"fv_a\"}]}");

        var lookup = await Probe(handler).LookupAsync(Session(), Ids);

        // the client asked about "cad_a"; a padded server id never becomes it -
        // it is dropped, so "cad_a" resolves to the fail-closed fallback.
        Assert.NotEqual(LatestVersionOutcome.Found, lookup.Get("cad_a").Outcome);
        Assert.Equal(LatestVersionOutcome.DocumentNotRecognized, lookup.Get("cad_a").Outcome);
    }

    [Fact]
    public async Task A_padded_cadDocumentId_next_to_an_exact_valid_sibling_never_poisons_the_exact_result()
    {
        // Round 3 regression: a response with BOTH "cad_a" (valid, Found) AND
        // " cad_a " (padded). The padded id must NOT be trimmed onto "cad_a"
        // and must NOT poison / overwrite the exact valid result.
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK,
            "{\"contract\":\"" + ContractId + "\",\"results\":["
            + "{\"cadDocumentId\":\"cad_a\",\"recognized\":true,\"latestFileVersionId\":\"fv_a_exact\"},"
            + "{\"cadDocumentId\":\" cad_a \",\"recognized\":true,\"latestFileVersionId\":\"fv_a_padded\"}]}");

        var a = (await Probe(handler).LookupAsync(Session(), Ids)).Get("cad_a");

        Assert.Equal(LatestVersionOutcome.Found, a.Outcome);
        Assert.Equal("fv_a_exact", a.Version!.LatestFileVersionId);
        Assert.Equal("cad_a", a.Version.CadDocumentId);
    }

    [Fact]
    public async Task A_padded_cadDocumentId_before_an_exact_valid_sibling_still_never_poisons_it()
    {
        // Same as above with the padded entry FIRST, proving order-independence.
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK,
            "{\"contract\":\"" + ContractId + "\",\"results\":["
            + "{\"cadDocumentId\":\" cad_a \",\"recognized\":true,\"latestFileVersionId\":\"fv_a_padded\"},"
            + "{\"cadDocumentId\":\"cad_a\",\"recognized\":true,\"latestFileVersionId\":\"fv_a_exact\"}]}");

        var a = (await Probe(handler).LookupAsync(Session(), Ids)).Get("cad_a");

        Assert.Equal(LatestVersionOutcome.Found, a.Outcome);
        Assert.Equal("fv_a_exact", a.Version!.LatestFileVersionId);
    }

    [Fact]
    public async Task A_whitespace_only_authoritative_cadDocumentId_is_dropped_and_falls_to_the_fallback()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK,
            "{\"contract\":\"" + ContractId + "\",\"results\":["
            + "{\"cadDocumentId\":\"   \",\"recognized\":true,\"latestFileVersionId\":\"fv_a\"}]}");

        var lookup = await Probe(handler).LookupAsync(Session(), Ids);

        Assert.NotEqual(LatestVersionOutcome.Found, lookup.Get("cad_a").Outcome);
        Assert.Equal(LatestVersionOutcome.DocumentNotRecognized, lookup.Get("cad_a").Outcome);
    }

    // ---- Round 2 HIGH 2: strict UTF-8 decoding --------------------

    [Fact]
    public async Task Invalid_utf8_bytes_in_an_otherwise_valid_response_fail_closed_never_found()
    {
        // An ASCII JSON body with a single invalid UTF-8 byte (0xFF) spliced in.
        var head = Encoding.ASCII.GetBytes(
            "{\"contract\":\"" + ContractId + "\",\"results\":["
            + "{\"cadDocumentId\":\"cad_a\",\"recognized\":true,\"latestFileVersionId\":\"fv_");
        var tail = Encoding.ASCII.GetBytes("a\"}]}");
        var bytes = head.Concat(new byte[] { 0xFF }).Concat(tail).ToArray();

        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        });

        var lookup = await Probe(handler).LookupAsync(Session(), Ids);

        Assert.NotEqual(LatestVersionOutcome.Found, lookup.Get("cad_a").Outcome);
        Assert.Equal(LatestVersionOutcome.MalformedResponse, lookup.Get("cad_a").Outcome);
    }

    // ---- Round 2 MEDIUM: 401/403 decided from status, not body ----

    private sealed class UnreadableContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new IOException("body is not readable");
        protected override Task<Stream> CreateContentReadStreamAsync()
            => throw new IOException("body is not readable");
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task A_401_with_an_oversized_body_is_still_authentication_failed()
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(new string('x', 2 * 1024 * 1024), Encoding.UTF8, "application/json"),
            };
            resp.Content.Headers.ContentLength = 20L * 1024 * 1024;
            return resp;
        });

        Assert.Equal(LatestVersionOutcome.AuthenticationFailed,
            (await Probe(handler).LookupAsync(Session(), Ids)).Get("cad_a").Outcome);
    }

    [Fact]
    public async Task A_403_with_a_malformed_body_is_still_authentication_failed()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.Forbidden, "}{ this is not json at all");

        Assert.Equal(LatestVersionOutcome.AuthenticationFailed,
            (await Probe(handler).LookupAsync(Session(), Ids)).Get("cad_a").Outcome);
    }

    [Fact]
    public async Task A_401_with_an_invalid_utf8_body_is_still_authentication_failed()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new ByteArrayContent(new byte[] { 0xFF, 0xFE, 0x00, 0x80 }),
        });

        Assert.Equal(LatestVersionOutcome.AuthenticationFailed,
            (await Probe(handler).LookupAsync(Session(), Ids)).Get("cad_a").Outcome);
    }

    [Fact]
    public async Task A_403_body_is_never_consumed_to_determine_the_status()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new UnreadableContent(),
        });

        // If the probe tried to read the body first, this would surface as
        // ServerUnavailable (IOException). Status-first keeps it AuthenticationFailed.
        Assert.Equal(LatestVersionOutcome.AuthenticationFailed,
            (await Probe(handler).LookupAsync(Session(), Ids)).Get("cad_a").Outcome);
    }
}
