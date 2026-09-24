using System.Linq;

using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.Tests.Workspace;

/// <summary>
/// P6D: a documentNumber alone is no longer unique (a model and its drawing
/// may share one engineering number), so a Get Latest bootstrap lookup by
/// number can optionally carry a documentType to disambiguate. These tests
/// pin down the ONE place that decides the resulting HTTP query parameters
/// (<see cref="CadDocumentLookup.BuildQueryParameters"/>) so documentNumber
/// and documentType can never be reversed, and that a blank/unsupported type
/// is rejected before any request is ever built.
/// </summary>
public class CadDocumentLookupTests
{
    [Fact]
    public void ByNumber_without_a_type_still_builds_the_untyped_query_unchanged()
    {
        var lookup = CadDocumentLookup.ByNumber("P6D-REAL-PART-A");

        Assert.Equal(new[] { ("documentNumber", "P6D-REAL-PART-A") }, lookup.BuildQueryParameters());
    }

    [Fact]
    public void ByNumber_with_IDW_sends_documentNumber_and_documentType_IDW()
    {
        var lookup = CadDocumentLookup.ByNumber(documentNumber: "P6D-REAL-ROOT", documentType: "IDW");

        Assert.Equal(
            new[] { ("documentNumber", "P6D-REAL-ROOT"), ("documentType", "IDW") },
            lookup.BuildQueryParameters());
    }

    [Fact]
    public void ByNumber_with_IAM_independently_sends_documentNumber_and_documentType_IAM()
    {
        var lookup = CadDocumentLookup.ByNumber(documentNumber: "P6D-REAL-ROOT", documentType: "IAM");

        Assert.Equal(
            new[] { ("documentNumber", "P6D-REAL-ROOT"), ("documentType", "IAM") },
            lookup.BuildQueryParameters());
    }

    [Fact]
    public void The_same_document_number_with_different_types_never_collapses_to_the_same_query()
    {
        var idw = CadDocumentLookup.ByNumber(documentNumber: "P6D-REAL-ROOT", documentType: "IDW");
        var iam = CadDocumentLookup.ByNumber(documentNumber: "P6D-REAL-ROOT", documentType: "IAM");

        Assert.NotEqual(idw.BuildQueryParameters(), iam.BuildQueryParameters());
    }

    [Fact]
    public void Values_cannot_be_reversed_the_number_never_lands_under_documentType_and_vice_versa()
    {
        var lookup = CadDocumentLookup.ByNumber(documentNumber: "P6D-REAL-ROOT", documentType: "IDW");
        var parameters = lookup.BuildQueryParameters();

        var byName = parameters.ToDictionary(p => p.Name, p => p.Value);
        Assert.Equal("P6D-REAL-ROOT", byName["documentNumber"]);
        Assert.Equal("IDW", byName["documentType"]);
    }

    [Fact]
    public void ById_never_carries_a_documentType_even_though_it_shares_the_record_type()
    {
        var lookup = CadDocumentLookup.ById("cad_123");

        Assert.Null(lookup.DocumentType);
        Assert.Equal(new[] { ("cadDocumentId", "cad_123") }, lookup.BuildQueryParameters());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_document_type_is_rejected_before_any_request_is_built(string blank)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => CadDocumentLookup.ByNumber(documentNumber: "P6D-REAL-ROOT", documentType: blank));
        Assert.Equal("documentType", ex.ParamName);
    }

    [Theory]
    [InlineData("PDF")]
    [InlineData("OTHER")]
    [InlineData("iam")]
    [InlineData("bogus")]
    public void An_unsupported_document_type_fails_safely_before_any_request_is_built(string unsupported)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => CadDocumentLookup.ByNumber(documentNumber: "P6D-REAL-ROOT", documentType: unsupported));
        Assert.Equal("documentType", ex.ParamName);
    }

    [Fact]
    public void SupportedDocumentTypes_is_exactly_the_managed_CAD_types_the_dialog_may_offer()
    {
        Assert.Equal(new[] { "IAM", "IPT", "IDW", "DWG" }, CadDocumentLookup.SupportedDocumentTypes);
    }
}
