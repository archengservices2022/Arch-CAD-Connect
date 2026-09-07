namespace Arch.CadConnect.Core.References;

/// <summary>
/// Classifies a reference edge from the parent and child document TYPES only.
/// Never inspects the filename, the document number, or any path text - a
/// coincidental "DWG" or "ASM" in a name means nothing here.
/// </summary>
public static class CadRelationshipClassifier
{
    public static CadRelationshipKind Classify(CadDocumentType parent, CadDocumentType child)
    {
        var childIsModel = child is CadDocumentType.Ipt or CadDocumentType.Iam;

        return parent switch
        {
            CadDocumentType.Iam when childIsModel => CadRelationshipKind.Component,
            CadDocumentType.Idw or CadDocumentType.Dwg when childIsModel => CadRelationshipKind.DrawingModel,
            _ => CadRelationshipKind.Other,
        };
    }
}
