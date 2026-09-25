using System.Runtime.CompilerServices;

namespace Arch.CadConnect.Core.Tests.Wiring;

/// <summary>
/// P6E-D: there is no WinForms test project, so
/// <c>Arch.CadConnect.Inventor</c> (net8.0-windows, COM/WinForms, referenced
/// by no test project) cannot be exercised directly. This is the
/// "maximize portable/source-level coverage" fallback the P6E-D task asks
/// for: it reads the ACTUAL controller/dialog source files as text (located
/// relative to THIS file via <see cref="CallerFilePathAttribute"/>, so it
/// works regardless of working directory) and proves, structurally, that the
/// new "Verify Copy Design" wiring:
///   - DOES reference the read-only P6E-C/P6E-B/P6D collaborators it must use
///     (verification-support client, drawing-association authority, the
///     P6E-B orchestrator/topology builder, the UNMODIFIED Inventor
///     verifiers);
///   - NEVER references any mutating Copy Design class (physical copier,
///     reference rewirers, materialization/apply/resume/recover clients, any
///     lifecycle/release API).
///
/// A change to `ArchAddInController.cs` that accidentally wires a mutating
/// collaborator into the Verify flow, or that stops calling a required
/// read-only collaborator, fails this test even though nothing here can
/// compile against the Inventor/COM assembly itself.
/// </summary>
public class CopyDesignVerifyWiringSourceTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        // .../inventor/tests/Arch.CadConnect.Core.Tests/Wiring/<this file>.cs
        // -> up three directories -> .../inventor (repo root).
        var dir = Path.GetDirectoryName(here)!;
        return Path.GetFullPath(Path.Combine(dir, "..", "..", ".."));
    }

    private static string ReadInventorSource(string relativePath)
    {
        var path = Path.Combine(RepoRoot(), "src", "Arch.CadConnect.Inventor", relativePath);
        Assert.True(File.Exists(path), $"Expected source file not found: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>Extracts one method's full body (the braces included), by
    ///  brace-depth counting from the first '{' after the first occurrence of
    ///  <paramref name="methodSignatureFragment"/> - robust to nested blocks,
    ///  lambdas, and string literals containing braces are not a concern here
    ///  since this codebase does not embed literal braces in string content
    ///  inside these methods.</summary>
    private static string ExtractMethodBody(string source, string methodSignatureFragment)
    {
        var start = source.IndexOf(methodSignatureFragment, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not locate '{methodSignatureFragment}' in source.");

        var braceStart = source.IndexOf('{', start);
        Assert.True(braceStart >= 0);

        var depth = 0;
        var i = braceStart;
        for (; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }
            }
        }
        Assert.True(depth == 0, $"Unbalanced braces while extracting '{methodSignatureFragment}'.");
        return source[braceStart..(i + 1)];
    }

    private static readonly string[] ForbiddenMutatingIdentifiers =
    {
        "InventorCopyDesignPhysicalCopier",
        "InventorCopyDesignReferenceRewirer",
        "InventorCopyDesignDrawingReferenceRewirer",
        "CopyDesignMaterializeHttpClient",
        "MaterializeFirstFileVersionAsync",
        "ApplyCopyDesignAsync",
        "CopyDesignApplyOrchestrator",
        "ExecuteCopyDesignAttempt",
        "ICopyDesignPhysicalCopier",
        "ICopyDesignReferenceRewirer",
        "ICopyDesignMaterializer",
        "CopyDesignApplyRequest",
        "CopyDesignMaterializeRequest",
        "CopyDesignReservationResponse",
        ".SaveAs(",
        ".SaveCopyAs(",
        ".Save2(",
        "ReplaceReference",
    };

    private static readonly string[] RequiredReadOnlyIdentifiers =
    {
        "GetCopyDesignVerificationSupportAsync",
        "GetDrawingAssociationsAsync",
        "CopyDesignVerificationTopologyBuilder",
        "CopyDesignVerificationOrchestrator",
        "InventorCopyDesignVerifier",
        "InventorCopyDesignDrawingVerifier",
        // P6E-B/D FINAL SEMANTIC ALIGNMENT (item 8): the topology pre-check's
        // own "cannot safely build" branch must construct its result through
        // the SAME shared Core factory the orchestrator's internal fallback
        // uses - never a separately invented UI-only INCOMPLETE
        // interpretation.
        "CopyDesignOperationVerificationResult.StructuralFailure",
    };

    /// <summary>Strips <c>//</c> and <c>/* */</c> comments (good enough for
    ///  this codebase's style - none of these methods embed a string literal
    ///  containing "//" or "/*") so an explanatory comment that legitimately
    ///  NAMES a forbidden class to say it is NOT used (e.g. "no Save/
    ///  ReplaceReference call anywhere") never trips the forbidden-identifier
    ///  guard - only actual code references do.</summary>
    private static string StripComments(string code)
    {
        var noBlockComments = System.Text.RegularExpressions.Regex.Replace(
            code, @"/\*.*?\*/", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
        var lines = noBlockComments.Split('\n').Select(line =>
        {
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            return idx >= 0 ? line[..idx] : line;
        });
        return string.Join('\n', lines);
    }

    private string VerifyFlowSource()
    {
        var source = ReadInventorSource("ArchAddInController.cs");
        var run = ExtractMethodBody(source, "private void RunCopyDesignVerify()");
        var continueStep = ExtractMethodBody(source, "private void ContinueCopyDesignVerify(");
        var finish = ExtractMethodBody(source, "private void FinishCopyDesignVerify(");
        return run + Environment.NewLine + continueStep + Environment.NewLine + finish;
    }

    private string VerifyFlowCodeOnly() => StripComments(VerifyFlowSource());

    [Fact]
    public void The_command_dispatch_switch_wires_CopyDesignVerify_to_RunCopyDesignVerify()
    {
        var source = ReadInventorSource("ArchAddInController.cs");
        Assert.Contains("case ArchCommand.CopyDesignVerify:", source);

        var dispatchIndex = source.IndexOf("case ArchCommand.CopyDesignVerify:", StringComparison.Ordinal);
        var nextBreakIndex = source.IndexOf("break;", dispatchIndex, StringComparison.Ordinal);
        var dispatchSlice = source[dispatchIndex..nextBreakIndex];
        Assert.Contains("RunCopyDesignVerify();", dispatchSlice);
    }

    [Theory]
    [MemberData(nameof(RequiredIdentifiersData))]
    public void Verify_flow_references_every_required_read_only_collaborator(string identifier)
    {
        Assert.Contains(identifier, VerifyFlowSource());
    }

    public static IEnumerable<object[]> RequiredIdentifiersData() =>
        RequiredReadOnlyIdentifiers.Select(id => new object[] { id });

    [Theory]
    [MemberData(nameof(ForbiddenIdentifiersData))]
    public void Verify_flow_never_references_a_mutating_Copy_Design_collaborator(string identifier)
    {
        Assert.DoesNotContain(identifier, VerifyFlowCodeOnly());
    }

    public static IEnumerable<object[]> ForbiddenIdentifiersData() =>
        ForbiddenMutatingIdentifiers.Select(id => new object[] { id });

    [Fact]
    public void No_lifecycle_or_release_style_API_name_appears_anywhere_in_the_Verify_flow()
    {
        var flow = VerifyFlowCodeOnly();
        Assert.DoesNotContain("Lifecycle", flow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ReleaseInfo", flow, StringComparison.Ordinal);
        Assert.DoesNotContain(".CheckInAsync(", flow, StringComparison.Ordinal);
        Assert.DoesNotContain(".CheckoutAsync(", flow, StringComparison.Ordinal);
    }

    [Fact]
    public void The_new_verify_dialog_delegates_validation_to_the_pure_Core_helper_and_never_names_a_mutating_class()
    {
        var dialogSource = ReadInventorSource(Path.Combine("Ui", "CopyDesignVerifyDialog.cs"));
        Assert.Contains("CopyDesignVerifyDialogInput.Validate", dialogSource);
        Assert.Contains("Directory.Exists", dialogSource);

        foreach (var identifier in ForbiddenMutatingIdentifiers)
        {
            Assert.DoesNotContain(identifier, dialogSource);
        }
    }

    [Fact]
    public void There_is_no_separately_invented_UI_only_topology_incomplete_renderer()
    {
        // P6E-B/D FINAL SEMANTIC ALIGNMENT: the prior RenderTopologyIncomplete
        // method was removed entirely - both paths now share Core's own
        // Render(StructuralFailure(...)) construction, so this method name
        // must not exist anywhere in the codebase any more.
        Assert.DoesNotContain("RenderTopologyIncomplete", VerifyFlowSource());
        var textReportSource = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Arch.CadConnect.Core", "CopyDesign", "Apply", "CopyDesignVerificationResultTextReport.cs"));
        Assert.DoesNotContain("RenderTopologyIncomplete", textReportSource);
    }

    [Fact]
    public void The_result_dialog_word_wraps_and_is_not_the_no_wrap_ScanResultDialog()
    {
        var resultDialogSource = ReadInventorSource(Path.Combine("Ui", "CopyDesignVerificationResultDialog.cs"));
        Assert.Contains("WordWrap = true", resultDialogSource);
    }
}
