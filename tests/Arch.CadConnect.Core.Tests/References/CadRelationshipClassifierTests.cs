using Arch.CadConnect.Core;
using Arch.CadConnect.Core.References;

namespace Arch.CadConnect.Core.Tests.References;

public class CadRelationshipClassifierTests
{
    [Theory]
    [InlineData(CadDocumentType.Iam, CadDocumentType.Ipt)] // assembly -> part
    [InlineData(CadDocumentType.Iam, CadDocumentType.Iam)] // assembly -> sub-assembly
    public void An_assembly_referencing_a_model_is_a_component(CadDocumentType parent, CadDocumentType child)
    {
        Assert.Equal(CadRelationshipKind.Component, CadRelationshipClassifier.Classify(parent, child));
    }

    [Theory]
    [InlineData(CadDocumentType.Idw, CadDocumentType.Ipt)]
    [InlineData(CadDocumentType.Idw, CadDocumentType.Iam)]
    [InlineData(CadDocumentType.Dwg, CadDocumentType.Ipt)]
    [InlineData(CadDocumentType.Dwg, CadDocumentType.Iam)]
    public void A_drawing_referencing_a_model_is_a_drawing_model(CadDocumentType parent, CadDocumentType child)
    {
        Assert.Equal(CadRelationshipKind.DrawingModel, CadRelationshipClassifier.Classify(parent, child));
    }

    [Theory]
    [InlineData(CadDocumentType.Ipt, CadDocumentType.Ipt)]   // part -> part (derived) - not safely classifiable
    [InlineData(CadDocumentType.Iam, CadDocumentType.Idw)]   // assembly -> drawing - not a component
    [InlineData(CadDocumentType.Iam, CadDocumentType.Unknown)] // unknown child
    [InlineData(CadDocumentType.Unknown, CadDocumentType.Ipt)] // unknown parent
    [InlineData(CadDocumentType.Idw, CadDocumentType.Idw)]    // drawing -> drawing
    public void Anything_else_is_other(CadDocumentType parent, CadDocumentType child)
    {
        Assert.Equal(CadRelationshipKind.Other, CadRelationshipClassifier.Classify(parent, child));
    }
}
