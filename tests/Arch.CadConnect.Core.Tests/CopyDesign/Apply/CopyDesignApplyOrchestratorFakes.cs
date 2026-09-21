using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

internal sealed class FakeReservationClient : ICopyDesignReservationClient
{
    public List<CopyDesignApplyRequest> Calls { get; } = new();
    public Func<CopyDesignApplyRequest, CopyDesignReservationResponse>? Responder { get; set; }

    /// <summary>Always throws this on EVERY call, unconditionally - used by
    ///  existing tests that need a single guaranteed failure.</summary>
    public Exception? ThrowOnCall { get; set; }

    /// <summary>HIGH 2 fix support: throw <see cref="TransientException"/>
    ///  for the FIRST N calls, then succeed - simulates a transient/
    ///  uncertain reservation failure that a SAME-key, SAME-request retry
    ///  resolves. Independent of <see cref="ThrowOnCall"/> (which always
    ///  wins if set, for existing tests' unconditional-failure scenarios).</summary>
    public int ThrowForFirstNCalls { get; set; }
    public Exception TransientException { get; set; } = new IOException("simulated transient network failure");
    private int _callCount;

    public Task<CopyDesignReservationResponse> ApplyAsync(CopyDesignApplyRequest request, CancellationToken ct)
    {
        Calls.Add(request);
        _callCount++;
        if (ThrowOnCall is not null)
        {
            throw ThrowOnCall;
        }
        if (_callCount <= ThrowForFirstNCalls)
        {
            throw TransientException;
        }
        return Task.FromResult((Responder ?? HappyResponse)(request));
    }

    public static CopyDesignReservationResponse HappyResponse(CopyDesignApplyRequest request)
    {
        var entries = request.WireEntries.Select(e => e.Action == CopyDesignApplyEntryAction.Copy
            ? new CopyDesignReservationResponseEntry(
                CopyDesignApplyEntryAction.Copy, e.Copy!.SourceCadDocumentId, e.Copy.SourceFileVersionId,
                e.Copy.SourceCadDocumentId + "-new", e.Copy.NewDocumentNumber, e.Copy.NewFileName,
                e.Copy.DocumentType == CadDocumentType.Iam ? "IAM" : "IPT")
            : new CopyDesignReservationResponseEntry(
                CopyDesignApplyEntryAction.Reuse, null, null, e.Reuse!.CadDocumentId, "10073-STD", "std.ipt", "IPT"))
            .ToArray();
        return new CopyDesignReservationResponse("contract", "op-1", "now", entries);
    }
}

internal sealed class FakePhysicalCopier : ICopyDesignPhysicalCopier
{
    public List<string> Calls { get; } = new();
    public HashSet<string> FailFor { get; } = new();

    /// <summary>CODEX ROUND 3, MEDIUM fix support: report this as
    ///  <see cref="CopyDesignPhysicalCopyResult.UnremovedTempPath"/> for a
    ///  failing cadDocumentId - simulates a failed physical copy that ALSO
    ///  left an operation-owned temp artifact behind.</summary>
    public Dictionary<string, string> UnremovedTempPathFor { get; } = new();

    public Task<CopyDesignPhysicalCopyResult> CopyAsync(CopyDesignNode node, CancellationToken ct)
    {
        Calls.Add(node.CadDocumentId!);
        if (!FailFor.Contains(node.CadDocumentId!))
        {
            return Task.FromResult(new CopyDesignPhysicalCopyResult(true, null));
        }
        UnremovedTempPathFor.TryGetValue(node.CadDocumentId!, out var unremovedTempPath);
        return Task.FromResult(new CopyDesignPhysicalCopyResult(false, "simulated copy failure", unremovedTempPath));
    }
}

internal sealed class FakeRewirer : ICopyDesignReferenceRewirer
{
    public List<string> Calls { get; } = new();
    public HashSet<string> FailFor { get; } = new();

    public Task<CopyDesignRewireResult> RewireAsync(CopyDesignNode copiedIamNode, IReadOnlyList<CopyDesignReferenceTarget> targets, CancellationToken ct)
    {
        Calls.Add(copiedIamNode.CadDocumentId!);
        return Task.FromResult(FailFor.Contains(copiedIamNode.CadDocumentId!)
            ? new CopyDesignRewireResult(false, "simulated rewire failure")
            : new CopyDesignRewireResult(true, null));
    }
}

internal sealed class FakeVerifier : ICopyDesignVerifier
{
    public List<string> Calls { get; } = new();
    public HashSet<string> FailFor { get; } = new();

    /// <summary>Set to make GatherFactsAsync THROW for a given cadDocumentId,
    ///  instead of returning a fail-shaped facts result - used to prove the
    ///  orchestrator's HIGH 5 fix (verification exception -&gt; cleanup, never
    ///  an uncontrolled failure).</summary>
    public HashSet<string> ThrowFor { get; } = new();
    public Exception ExceptionToThrow { get; set; } = new InvalidOperationException("simulated verification exception");

    /// <summary>CODEX ROUND 3, HIGH fix support: report
    ///  <c>OccurrenceEnumerationSucceeded: false</c> for a given
    ///  cadDocumentId - simulates an IAM whose occurrence enumeration itself
    ///  failed (as opposed to <see cref="FailFor"/>, which simulates other
    ///  verification failures).</summary>
    public HashSet<string> EnumerationFailureFor { get; } = new();

    public Task<CopyDesignNodeVerificationFacts> GatherFactsAsync(
        CopyDesignNode node, IReadOnlyList<CopyDesignReferenceTarget> referenceTargets, string sourceSha256BeforeOperation, CancellationToken ct)
    {
        Calls.Add(node.CadDocumentId!);
        if (ThrowFor.Contains(node.CadDocumentId!))
        {
            throw ExceptionToThrow;
        }

        var refs = referenceTargets.Select(t => new CopyDesignReferenceResolutionFact(
            t.ChildCadDocumentId, t.ExpectedTargetAbsolutePath, t.OriginalChildAbsolutePath, t.ExpectedTargetAbsolutePath, t.ChildIsCopy)).ToArray();
        var actual = referenceTargets.Select(t => new CopyDesignActualComponentOccurrence(t.ExpectedTargetAbsolutePath)).ToArray();

        var fail = FailFor.Contains(node.CadDocumentId!);
        var enumerationSucceeded = !EnumerationFailureFor.Contains(node.CadDocumentId!);
        return Task.FromResult(new CopyDesignNodeVerificationFacts(
            node.CadDocumentId!, node.SourceAbsolutePath, node.ProposedDestinationAbsolutePath ?? "",
            DestinationExists: !fail, DocumentTypeMatches: true, OpenableThroughInventor: true,
            ResultingSha256: fail ? null : "result-hash", ResultingFileSize: fail ? null : 100L,
            SourceSha256BeforeOperation: sourceSha256BeforeOperation,
            SourceSha256AfterOperation: sourceSha256BeforeOperation, ReferenceResolutions: refs,
            ActualComponentOccurrences: actual, OccurrenceEnumerationSucceeded: enumerationSucceeded));
    }
}

internal sealed class FakeMaterializer : ICopyDesignMaterializer
{
    public List<string> Calls { get; } = new();
    public List<CopyDesignMaterializeRequest> Requests { get; } = new();
    public Func<string, CopyDesignMaterializationResult>? Responder { get; set; }

    public Task<CopyDesignMaterializationResult> MaterializeFirstFileVersionAsync(
        CopyDesignMaterializeRequest request, CancellationToken ct)
    {
        Calls.Add(request.ResultingCadDocumentId);
        Requests.Add(request);
        var result = Responder?.Invoke(request.ResultingCadDocumentId)
            ?? new CopyDesignMaterializationResult(request.ResultingCadDocumentId, CopyDesignMaterializationOutcome.Materialized, "fv-1", 1, request.ExpectedSha256, request.ExpectedFileSize, "ok");
        return Task.FromResult(result);
    }
}

internal sealed class FakeHasher : ICopyDesignFileHasher
{
    public string ComputeSha256(string absolutePath) => "hash:" + absolutePath.ToLowerInvariant();
}

/// <summary>A tiny orchestrator test harness with sensible defaults and
///  override hooks, so each test configures only what it cares about.</summary>
internal sealed class OrchestratorHarness
{
    public FakeReservationClient Reservation { get; } = new();
    public FakePhysicalCopier Copier { get; } = new();
    public FakeRewirer Rewirer { get; } = new();
    public FakeVerifier Verifier { get; } = new();
    public FakeMaterializer Materializer { get; } = new();
    public FakeHasher Hasher { get; } = new();
    public CopyDesignApplyOperationGuard Guard { get; } = new();
    public HashSet<string> ExistingSources { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExistingDestinations { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Deleted { get; } = new();
    public Func<string, bool>? SourceExistsOverride { get; set; }
    public Func<string, bool>? DestinationExistsOverride { get; set; }

    /// <summary>HIGH 2 fix support: defaults to <c>null</c> (no classifier),
    ///  which means the retry loop NEVER activates - identical to
    ///  pre-Round-CODEX behavior - unless a test explicitly opts in.</summary>
    public Func<Exception, bool>? IsTransientReservationFailure { get; set; }
    public int MaxReservationAttempts { get; set; } = 3;

    public CopyDesignApplyOrchestrator Build() => new(
        Guard, Reservation, Copier, Rewirer, Verifier, Materializer, Hasher,
        SourceExistsOverride ?? (path => ExistingSources.Contains(path)),
        DestinationExistsOverride ?? (path => ExistingDestinations.Contains(path)),
        Deleted.Add,
        IsTransientReservationFailure,
        MaxReservationAttempts);
}
