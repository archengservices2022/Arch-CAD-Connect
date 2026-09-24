using Arch.CadConnect.Core.CopyDesign.Apply;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

/// <summary>P6D ROUND 2, CRITICAL fix: the PURE decision logic behind
///  "never let Inventor Save/SaveAs a drawing while a referenced model is
///  Dirty" - fully unit-testable without any COM/live Inventor session,
///  since the Inventor adapters only ever gather facts and hand them to
///  this class.</summary>
public class CopyDesignDrawingDependentSafetyGuardTests
{
    [Fact]
    public void No_dependents_at_all_is_safe()
    {
        var result = CopyDesignDrawingDependentSafetyGuard.Evaluate(Array.Empty<CopyDesignDependentDocumentState>());

        Assert.True(result.Safe);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void Clean_referenced_models_are_safe_the_drawing_operation_proceeds()
    {
        var dependents = new[]
        {
            new CopyDesignDependentDocumentState(@"C:\models\part-a.ipt", IsDirty: false),
            new CopyDesignDependentDocumentState(@"C:\models\part-b.ipt", IsDirty: false),
        };

        var result = CopyDesignDrawingDependentSafetyGuard.Evaluate(dependents);

        Assert.True(result.Safe);
    }

    // required test: clean drawing + dirty REUSE model => fail before save
    [Fact]
    public void A_dirty_REUSE_model_dependent_fails_closed()
    {
        var dependents = new[]
        {
            new CopyDesignDependentDocumentState(@"C:\models\std-bolt.ipt", IsDirty: true), // REUSE target, open with unsaved edits
        };

        var result = CopyDesignDrawingDependentSafetyGuard.Evaluate(dependents);

        Assert.False(result.Safe);
        Assert.Contains("std-bolt.ipt", result.FailureReason);
        Assert.Contains("Dirty", result.FailureReason);
    }

    // required test: clean drawing + dirty COPY source model => fail before save
    [Fact]
    public void A_dirty_COPY_source_model_dependent_fails_closed()
    {
        var dependents = new[]
        {
            new CopyDesignDependentDocumentState(@"C:\models\part-a.ipt", IsDirty: true), // COPY source, still open/dirty
        };

        var result = CopyDesignDrawingDependentSafetyGuard.Evaluate(dependents);

        Assert.False(result.Safe);
        Assert.Contains("part-a.ipt", result.FailureReason);
    }

    [Fact]
    public void One_dirty_dependent_among_many_clean_ones_still_fails_closed()
    {
        var dependents = new[]
        {
            new CopyDesignDependentDocumentState(@"C:\models\clean-a.ipt", IsDirty: false),
            new CopyDesignDependentDocumentState(@"C:\models\dirty-b.ipt", IsDirty: true),
            new CopyDesignDependentDocumentState(@"C:\models\clean-c.ipt", IsDirty: false),
        };

        var result = CopyDesignDrawingDependentSafetyGuard.Evaluate(dependents);

        Assert.False(result.Safe);
        Assert.Contains("dirty-b.ipt", result.FailureReason);
        Assert.DoesNotContain("clean-a.ipt", result.FailureReason);
    }

    [Fact]
    public void Every_dirty_dependent_is_named_in_the_failure_reason_not_just_the_first()
    {
        var dependents = new[]
        {
            new CopyDesignDependentDocumentState(@"C:\models\z-dirty.ipt", IsDirty: true),
            new CopyDesignDependentDocumentState(@"C:\models\a-dirty.iam", IsDirty: true),
        };

        var result = CopyDesignDrawingDependentSafetyGuard.Evaluate(dependents);

        Assert.False(result.Safe);
        Assert.Contains("z-dirty.ipt", result.FailureReason);
        Assert.Contains("a-dirty.iam", result.FailureReason);
    }

    [Fact]
    public void Evaluate_throws_on_null_never_silently_treats_null_as_empty()
    {
        Assert.Throws<ArgumentNullException>(() => CopyDesignDrawingDependentSafetyGuard.Evaluate(null!));
    }
}
