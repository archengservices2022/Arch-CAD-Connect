namespace Arch.CadConnect.Core.References;

/// <summary>
/// Pure, COM-free wording for disclosing the ACTUAL mutation scope of a P5C
/// assembly-component repair in the "Repair Reference" confirmation dialog:
/// one managed FILE reference, but potentially N &gt; 1 live
/// <c>ComponentOccurrence</c> objects bound to it, all of which will be
/// replaced together via <c>ComponentOccurrence.Replace(ReplaceAll: true)</c>.
///
/// WHY THIS EXISTS (Codex round-3 finding): the confirmation previously said
/// "Only this one reference will change", which reads as "one occurrence" and
/// is misleading whenever the same file backs more than one placement in the
/// assembly. The live occurrence count itself can only be established with
/// COM (see <c>InventorReferenceReplacer.TryCountLiveMatchingOccurrencesForDisclosure</c>,
/// disclosure-only, never an authorization decision); this type owns only the
/// wording, so the wording can be unit tested without Inventor.
/// </summary>
public static class RepairOccurrenceDisclosureText
{
    /// <param name="occurrenceCount">The live count of assembly component
    ///  occurrences bound to the authorized old file reference, or
    ///  <c>null</c> when it could not be established (non-assembly reference,
    ///  document not loaded, or an indeterminate/unreadable occurrence was
    ///  encountered) - callers must never guess "1" in that case.</param>
    public static string ScopeSentence(int? occurrenceCount)
    {
        if (occurrenceCount is null)
        {
            return "Only this managed file reference will change. If it is used by more than one occurrence "
                + "in this assembly, ALL occurrences using it will be replaced together - no other file "
                + "reference is touched.";
        }
        if (occurrenceCount == 1)
        {
            return "Only this managed file reference will change. It is used by 1 occurrence in this "
                + "assembly; that occurrence will be replaced.";
        }
        if (occurrenceCount > 1)
        {
            var n = occurrenceCount.Value;
            return $"Only this managed file reference will change. It is used by {n} occurrences in this "
                + $"assembly; all {n} will be replaced.";
        }
        // Defensive only - a non-positive count should never reach here (zero
        // matching occurrences fails closed before confirmation is shown).
        return "Only this managed file reference will change.";
    }
}
