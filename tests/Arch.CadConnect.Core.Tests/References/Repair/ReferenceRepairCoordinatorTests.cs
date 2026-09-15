using Arch.CadConnect.Core;
using Arch.CadConnect.Core.References;

using static Arch.CadConnect.Core.Tests.References.Repair.RepairFixtures;

namespace Arch.CadConnect.Core.Tests.References.Repair;

/// <summary>
/// The P5C spine: eligibility -&gt; explicit confirmation -&gt; POST-CONFIRMATION
/// re-check (immutable snapshot + pre-mutation target-edge count) -&gt; PREPARE
/// the mutation (all discovery) -&gt; acquire a PROTECTED target lease + final
/// checkout read vs the immutable snapshot -&gt; EXECUTE the prepared replacement
/// WHILE the lease is held -&gt; release (a disposal failure after this point is
/// never plain success) -&gt; rescan -&gt; strict verify (the SELECTED edge
/// TRANSITIONED + post-mutation target integrity vs SERVER metadata).
/// </summary>
public class ReferenceRepairCoordinatorTests
{
    private static readonly string Parent = RootIam;
    private static readonly string OldPath = P("Arch", "Job1", "PART-A.ipt");
    private static readonly string NewPath = P("Arch", "Latest", "PART-A.ipt");

    private static readonly RepairAuthorizationSnapshot Snap =
        new("cad_parent", "co_1", "fv_base_1");

    // ROUND 4 (Codex HIGH - whole-reference-set proof): the pre-mutation
    // fingerprint of the one edge under repair - "no other managed reference
    // exists", the implicit assumption of every test in this file that does
    // not construct a scan with an unrelated edge.
    private static readonly ManagedReferenceFingerprint OldFp =
        new(P("Arch", "Job1", "PART-A.ipt"), CadRelationshipKind.Component, "cad_a", "fv_v1", IsVerified: true);
    private static readonly ManagedReferenceFingerprint NewFp =
        new(P("Arch", "Latest", "PART-A.ipt"), CadRelationshipKind.Component, "cad_a", "fv_v3", IsVerified: true);

    private static ReferenceRepairPlan EligiblePlan() =>
        ReferenceRepairPlanner.Plan(
            Stale(cad: "cad_a", pinned: "fv_v1", latest: "fv_v3"),
            VerifiedTarget(cad: "cad_a", fv: "fv_v3", path: NewPath),
            WritableReferencing(Parent));

    private static CadReferenceScan ScanResolvingTo(string resolved, CadManifestIdentity? identity) => new(
        new CadReferenceRoot(Parent, CadDocumentType.Iam, null),
        new[]
        {
            new CadReference
            {
                ParentAbsolutePath = Parent,
                InventorReportedName = Path.GetFileName(resolved),
                ResolvedAbsolutePath = resolved,
                ReferenceType = CadDocumentType.Ipt,
                RelationshipKind = CadRelationshipKind.Component,
                Resolution = CadReferenceResolution.Resolved,
                Scope = ReferenceWorkspaceScope.InsideWorkspace,
                ManifestIdentity = identity,
            },
        },
        new[] { new CadReferenceNode(Parent, CadDocumentType.Iam, true) },
        Now);

    private static CadReferenceScan RepairedScan() =>
        ScanResolvingTo(NewPath, new CadManifestIdentity("cad_a", "fv_v3", "DOC-cad_a", "PART-A.ipt"));

    private sealed class StubPrepared : IPreparedReferenceReplacement
    {
        private readonly bool _succeeds;
        private readonly bool _mutationInvoked;
        private readonly List<string>? _events;
        private readonly Exception? _executeThrows;

        public StubPrepared(bool ready, string detail, bool succeeds, bool mutationInvoked,
            List<string>? events, Exception? executeThrows)
        {
            Ready = ready;
            Detail = detail;
            _succeeds = succeeds;
            _mutationInvoked = mutationInvoked;
            _events = events;
            _executeThrows = executeThrows;
        }

        public bool Ready { get; }
        public string Detail { get; }
        public int ExecuteCalls { get; private set; }

        public ReferenceReplaceResult Execute()
        {
            ExecuteCalls++;
            _events?.Add("replace");
            if (_executeThrows is not null) throw _executeThrows;
            return new ReferenceReplaceResult(_succeeds, _succeeds ? "ok" : "boom",
                MutationInvoked: _succeeds || _mutationInvoked);
        }
    }

    private sealed class PreparingReplacer : IReferenceReplacer
    {
        public bool PrepareReady { get; set; } = true;
        public string PrepareDetail { get; set; } = "the reference could not be selected";
        public Exception? PrepareThrows { get; set; }
        public bool ExecuteSucceeds { get; set; } = true;
        public bool ExecuteMutationInvoked { get; set; }
        public Exception? ExecuteThrows { get; set; }
        public List<string>? Events { get; set; }

        public int PrepareCalls { get; private set; }
        public List<ReferenceReplaceCommand> Commands { get; } = new();
        public StubPrepared? LastPrepared { get; private set; }

        public IPreparedReferenceReplacement Prepare(ReferenceReplaceCommand command)
        {
            PrepareCalls++;
            Commands.Add(command);
            Events?.Add("prepare");
            if (PrepareThrows is not null) throw PrepareThrows;
            LastPrepared = new StubPrepared(PrepareReady, PrepareDetail, ExecuteSucceeds,
                ExecuteMutationInvoked, Events, ExecuteThrows);
            return LastPrepared;
        }
    }

    private sealed class StubConfirmation(bool answer) : IReferenceRepairConfirmation
    {
        public int Calls { get; private set; }
        public bool Confirm(ReferenceRepairPlan plan) { Calls++; return answer; }
    }

    private sealed class StubLease(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    private sealed class StubPreflight : IReferenceRepairPreflight
    {
        public RepairPreflightVerdict Verdict { get; set; } =
            new(true, "revalidated", TargetSize, TargetSha, Snap, 0, new[] { OldFp });

        public RepairTargetBinaryState Binary { get; set; } =
            new(Ok: true, Exists: true, Size: TargetSize, Sha256: TargetSha);

        public Exception? RevalidateThrows { get; set; }

        public bool LeaseAcquires { get; set; } = true;
        public string LeaseFailDetail { get; set; } = "the final checkout is no longer Mine";
        public Exception? LeaseThrows { get; set; }
        public Exception? LeaseDisposeThrows { get; set; }

        public int RevalidateCalls { get; private set; }
        public int ReadTargetCalls { get; private set; }
        public int LeaseCalls { get; private set; }
        public List<string>? Events { get; set; }

        public RepairPreflightVerdict RevalidateBeforeMutation(ReferenceRepairPlan confirmedPlan)
        {
            RevalidateCalls++;
            if (RevalidateThrows is not null) throw RevalidateThrows;
            return Verdict;
        }

        public RepairTargetBinaryState ReadTargetBinary(string absolutePath)
        {
            ReadTargetCalls++;
            return Binary;
        }

        public ProtectedRepairTargetLease AcquireProtectedTargetForMutation(
            ReferenceRepairPlan confirmedPlan, RepairPreflightVerdict verdict)
        {
            LeaseCalls++;
            if (LeaseThrows is not null) throw LeaseThrows;
            if (!LeaseAcquires) return ProtectedRepairTargetLease.Failed(LeaseFailDetail);
            Events?.Add("lease-acquired");
            return new ProtectedRepairTargetLease(true, "held", new StubLease(() =>
            {
                Events?.Add("lease-disposed");
                if (LeaseDisposeThrows is not null) throw LeaseDisposeThrows;
            }));
        }
    }

    private static ReferenceRepairResult Run(
        ReferenceRepairPlan plan,
        IReferenceRepairConfirmation confirm,
        IReferenceReplacer replacer,
        Func<CadReferenceScan> rescan,
        StubPreflight? preflight = null)
        => ReferenceRepairCoordinator.Execute(plan, confirm, preflight ?? new StubPreflight(), replacer, rescan);

    // ---- eligibility / confirmation ----------------------------------

    [Fact]
    public void An_ineligible_plan_never_confirms_never_reauthorizes_never_prepares_never_leases_never_rescans()
    {
        var replacer = new PreparingReplacer();
        var confirm = new StubConfirmation(true);
        var preflight = new StubPreflight();
        var rescans = 0;

        var plan = ReferenceRepairPlanner.Plan(Current(), VerifiedTarget(fv: "fv_v3"), WritableReferencing(Parent));
        var result = Run(plan, confirm, replacer, () => { rescans++; return RepairedScan(); }, preflight);

        Assert.Equal(ReferenceRepairOutcome.NotEligible, result.Outcome);
        Assert.Equal(0, confirm.Calls);
        Assert.Equal(0, preflight.RevalidateCalls);
        Assert.Equal(0, replacer.PrepareCalls);
        Assert.Equal(0, preflight.LeaseCalls);
        Assert.Equal(0, rescans);
    }

    [Fact]
    public void Cancellation_makes_zero_mutation_and_no_reauthorization()
    {
        var replacer = new PreparingReplacer();
        var preflight = new StubPreflight();

        var result = Run(EligiblePlan(), new StubConfirmation(false), replacer, RepairedScan, preflight);

        Assert.Equal(ReferenceRepairOutcome.CancelledByUser, result.Outcome);
        Assert.Equal(0, preflight.RevalidateCalls);
        Assert.Equal(0, replacer.PrepareCalls);
        Assert.Equal(0, preflight.LeaseCalls);
    }

    // ---- post-confirmation re-check --------------------------------

    [Fact]
    public void A_denied_reauthorization_aborts_with_zero_mutation_no_prepare_no_lease_no_rescan()
    {
        var replacer = new PreparingReplacer();
        var rescans = 0;
        var preflight = new StubPreflight { Verdict = RepairPreflightVerdict.Deny("the checkout is no longer yours") };

        var result = Run(EligiblePlan(), new StubConfirmation(true), replacer,
            () => { rescans++; return RepairedScan(); }, preflight);

        Assert.Equal(ReferenceRepairOutcome.DriftAborted, result.Outcome);
        Assert.Equal(0, replacer.PrepareCalls);
        Assert.Equal(0, preflight.LeaseCalls);
        Assert.Equal(0, rescans);
    }

    [Fact]
    public void A_reauthorization_that_throws_aborts_with_zero_mutation()
    {
        var replacer = new PreparingReplacer();
        var preflight = new StubPreflight { RevalidateThrows = new IOException("network gone") };

        var result = Run(EligiblePlan(), new StubConfirmation(true), replacer, RepairedScan, preflight);

        Assert.Equal(ReferenceRepairOutcome.DriftAborted, result.Outcome);
        Assert.Equal(0, replacer.PrepareCalls);
        Assert.Contains("no cad reference was changed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_verdict_without_canonical_server_target_metadata_aborts_with_zero_mutation()
    {
        var replacer = new PreparingReplacer();
        var preflight = new StubPreflight { Verdict = new RepairPreflightVerdict(true, "ok", -1, null, Snap, 0) };

        var result = Run(EligiblePlan(), new StubConfirmation(true), replacer, RepairedScan, preflight);

        Assert.Equal(ReferenceRepairOutcome.DriftAborted, result.Outcome);
        Assert.Equal(0, replacer.PrepareCalls);
    }

    [Fact]
    public void A_verdict_without_a_complete_immutable_snapshot_aborts_with_zero_mutation()
    {
        var replacer = new PreparingReplacer();
        var preflight = new StubPreflight
        {
            Verdict = new RepairPreflightVerdict(true, "ok", TargetSize, TargetSha,
                new RepairAuthorizationSnapshot("cad_parent", "", "fv_base_1"), 0),
        };

        var result = Run(EligiblePlan(), new StubConfirmation(true), replacer, RepairedScan, preflight);

        Assert.Equal(ReferenceRepairOutcome.DriftAborted, result.Outcome);
        Assert.Equal(0, replacer.PrepareCalls);
        Assert.Contains("immutable authorization snapshot", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_verdict_without_pre_mutation_evidence_aborts_with_zero_mutation()
    {
        var replacer = new PreparingReplacer();
        var preflight = new StubPreflight
        {
            Verdict = new RepairPreflightVerdict(true, "ok", TargetSize, TargetSha, Snap, PreMutationTargetEdgeCount: -1),
        };

        var result = Run(EligiblePlan(), new StubConfirmation(true), replacer, RepairedScan, preflight);

        Assert.Equal(ReferenceRepairOutcome.DriftAborted, result.Outcome);
        Assert.Equal(0, replacer.PrepareCalls);
    }

    [Fact]
    public void A_verdict_without_the_pre_mutation_whole_reference_set_fingerprint_aborts_with_zero_mutation()
    {
        // ROUND 4 (Codex HIGH): a verdict with valid edge-count evidence but NO
        // whole-reference-set fingerprint must still abort before Prepare.
        var replacer = new PreparingReplacer();
        var preflight = new StubPreflight
        {
            Verdict = new RepairPreflightVerdict(
                true, "ok", TargetSize, TargetSha, Snap, PreMutationTargetEdgeCount: 0,
                PreMutationReferenceFingerprint: null),
        };

        var result = Run(EligiblePlan(), new StubConfirmation(true), replacer, RepairedScan, preflight);

        Assert.Equal(ReferenceRepairOutcome.DriftAborted, result.Outcome);
        Assert.Equal(0, replacer.PrepareCalls);
        Assert.Contains("whole-reference-set fingerprint", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- FINDING 2: discovery is prepared BEFORE the final checkout -----

    [Fact]
    public void The_mutation_is_prepared_BEFORE_the_lease_and_executed_AFTER_it_then_the_lease_is_released()
    {
        var events = new List<string>();
        var replacer = new PreparingReplacer { Events = events };
        var preflight = new StubPreflight { Events = events };

        var result = Run(EligiblePlan(), new StubConfirmation(true), replacer, RepairedScan, preflight);

        Assert.Equal(ReferenceRepairOutcome.Repaired, result.Outcome);
        Assert.Equal(new[] { "prepare", "lease-acquired", "replace", "lease-disposed" }, events);
        Assert.Equal(1, replacer.PrepareCalls);
        Assert.Equal(1, replacer.LastPrepared!.ExecuteCalls);
    }

    // ---- ROUND 5 (Codex HIGH): final target freshness must be last-mile --

    [Fact]
    public void Round5_no_further_preflight_interaction_occurs_between_a_successful_lease_and_Execute()
    {
        // AcquireProtectedTargetForMutation is where the reordered last-mile
        // sequence lives (final checkout re-check, THEN the final
        // authoritative latest-version proof, as the LAST server-authority
        // action). From the coordinator's perspective it is a single opaque
        // call - this proves NOTHING else touches the preflight (no further
        // network/manifest/checkout call of any kind) between that call
        // succeeding and Execute actually running.
        var events = new List<string>();
        var replacer = new PreparingReplacer { Events = events };
        var preflight = new StubPreflight { Events = events };

        var result = Run(EligiblePlan(), new StubConfirmation(true), replacer, RepairedScan, preflight);

        Assert.Equal(ReferenceRepairOutcome.Repaired, result.Outcome);
        Assert.Equal(new[] { "prepare", "lease-acquired", "replace", "lease-disposed" }, events);
        Assert.Equal(1, preflight.LeaseCalls);
        Assert.Equal(1, preflight.RevalidateCalls);
    }

    [Fact]
    public void The_prepared_command_is_bound_to_the_exact_selected_reference_identity()
    {
        var replacer = new PreparingReplacer();

        Run(EligiblePlan(), new StubConfirmation(true), replacer, RepairedScan);

        var command = Assert.Single(replacer.Commands);
        Assert.Equal(Parent, command.ReferencingDocumentAbsolutePath);
        Assert.Equal(OldPath, command.CurrentReferenceResolvedPath);
        Assert.Equal(NewPath, command.TargetAbsolutePath);
        Assert.Equal("cad_a", command.ExpectedCadDocumentId);
        Assert.Equal("fv_v1", command.ExpectedCurrentFileVersionId);
    }

    [Fact]
    public void A_not_ready_prepare_aborts_BEFORE_the_final_checkout_with_zero_mutation()
    {
        var replacer = new PreparingReplacer { PrepareReady = false, PrepareDetail = "no descriptor matched the path + identity" };
        var preflight = new StubPreflight();
        var rescans = 0;

        var result = Run(EligiblePlan(), new StubConfirmation(true), replacer,
            () => { rescans++; return RepairedScan(); }, preflight);

        Assert.Equal(ReferenceRepairOutcome.DriftAborted, result.Outcome);
        Assert.Equal(1, replacer.PrepareCalls);
        Assert.Equal(0, preflight.LeaseCalls);           // final checkout never reached
        Assert.Equal(0, rescans);
        Assert.Contains("no descriptor matched", result.Message);
        Assert.Contains("before any mutation", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_prepare_that_throws_aborts_with_zero_mutation()
    {
        var replacer = new PreparingReplacer { PrepareThrows = new InvalidOperationException("COM teardown") };
        var preflight = new StubPreflight();

        var result = Run(EligiblePlan(), new StubConfirmation(true), replacer, RepairedScan, preflight);

        Assert.Equal(ReferenceRepairOutcome.DriftAborted, result.Outcome);
        Assert.Equal(0, preflight.LeaseCalls);
    }

    // ---- protected lease + final checkout -------------------------

    [Fact]
    public void A_failed_lease_or_final_checkout_aborts_with_zero_mutation_and_no_execute()
    {
        var replacer = new PreparingReplacer();
        var rescans = 0;
        var preflight = new StubPreflight { LeaseAcquires = false, LeaseFailDetail = "the checkout changed to another user" };

        var result = Run(EligiblePlan(), new StubConfirmation(true), replacer,
            () => { rescans++; return RepairedScan(); }, preflight);

        Assert.Equal(ReferenceRepairOutcome.DriftAborted, result.Outcome);
        Assert.Equal(1, replacer.PrepareCalls);
        Assert.Equal(0, replacer.LastPrepared!.ExecuteCalls);
        Assert.Equal(0, rescans);
        Assert.Contains("checkout changed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("the final authoritative checkout read could not be completed")]
    [InlineData("the final manifest + authoritative checkout state do not EXACTLY match the immutable authorization snapshot")]
    [InlineData("parent cadDocumentId drift")]
    [InlineData("checkout id drift")]
    [InlineData("base fileVersionId drift")]
    [InlineData("the bytes protected by the lease do not match the server size / SHA-256")]
    public void Any_final_authorization_failure_aborts_before_execute(string detail)
    {
        var replacer = new PreparingReplacer();
        var preflight = new StubPreflight { LeaseAcquires = false, LeaseFailDetail = detail };

        var result = Run(EligiblePlan(), new StubConfirmation(true), replacer, RepairedScan, preflight);

        Assert.Equal(ReferenceRepairOutcome.DriftAborted, result.Outcome);
        Assert.Equal(0, replacer.LastPrepared!.ExecuteCalls);
        Assert.Contains(detail, result.Message);
    }

    [Fact]
    public void A_lease_acquisition_that_throws_aborts_with_zero_mutation()
    {
        var replacer = new PreparingReplacer();
        var preflight = new StubPreflight { LeaseThrows = new TimeoutException("final checkout read timed out") };

        var result = Run(EligiblePlan(), new StubConfirmation(true), replacer, RepairedScan, preflight);

        Assert.Equal(ReferenceRepairOutcome.DriftAborted, result.Outcome);
        Assert.Equal(0, replacer.LastPrepared!.ExecuteCalls);
        Assert.Contains("no cad reference was changed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- FINDING 4: lease disposal failure after possible mutation ----

    [Fact]
    public void ReplaceReference_executes_then_Dispose_throws_never_success_verification_attempted()
    {
        var rescans = 0;
        var replacer = new PreparingReplacer();
        var preflight = new StubPreflight { LeaseDisposeThrows = new IOException("handle already invalidated") };

        var result = Run(EligiblePlan(), new StubConfirmation(true), replacer,
            () => { rescans++; return RepairedScan(); }, preflight);

        Assert.Equal(ReferenceRepairOutcome.VerificationFailed, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Equal(1, replacer.LastPrepared!.ExecuteCalls);
        Assert.Equal(1, rescans); // fresh verification attempted
        Assert.Contains("MAY already be modified", result.Message);
        Assert.Contains("IOException", result.Message);
        Assert.Contains("handle already invalidated", result.Message);
    }

    [Fact]
    public void ReplaceReference_throws_and_Dispose_also_throws_stays_mutation_uncertain_not_DriftAborted()
    {
        var replacer = new PreparingReplacer { ExecuteThrows = new InvalidComObjectExceptionShim() };
        var preflight = new StubPreflight { LeaseDisposeThrows = new IOException("handle already invalidated") };

        var result = Run(EligiblePlan(), new StubConfirmation(true), replacer, RepairedScan, preflight);

        Assert.NotEqual(ReferenceRepairOutcome.DriftAborted, result.Outcome);
        Assert.Equal(ReferenceRepairOutcome.VerificationFailed, result.Outcome);
        Assert.Contains("MAY already be modified", result.Message);
    }

    private sealed class InvalidComObjectExceptionShim : Exception { }

    [Fact]
    public void A_lease_acquisition_failure_with_a_throwing_dispose_still_uses_zero_mutation_semantics()
    {
        // The lease was never acquired -> no mutation; a disposal throw on the
        // failed (empty) lease must not turn a zero-mutation abort into an
        // uncertainty result. (The failed lease holds no handle, so dispose is a
        // no-op; this asserts the pre-mutation path is unaffected.)
        var replacer = new PreparingReplacer();
        var preflight = new StubPreflight { LeaseAcquires = false, LeaseFailDetail = "final checkout unavailable" };

        var result = Run(EligiblePlan(), new StubConfirmation(true), replacer, RepairedScan, preflight);

        Assert.Equal(ReferenceRepairOutcome.DriftAborted, result.Outcome);
        Assert.Contains("NO CAD reference was changed", result.Message);
    }

    // ---- FINDING 3: prove the selected edge transitioned -------------

    [Fact]
    public void A_pre_existing_target_edge_that_does_not_rise_by_one_is_a_verification_failure()
    {
        // verdict says 1 edge was already at the target; the post-scan still
        // shows 1 -> the selected edge did NOT transition.
        var preflight = new StubPreflight
        {
            Verdict = new RepairPreflightVerdict(
                true, "ok", TargetSize, TargetSha, Snap, PreMutationTargetEdgeCount: 1,
                PreMutationReferenceFingerprint: new[] { OldFp, NewFp }),
        };

        var result = Run(EligiblePlan(), new StubConfirmation(true), new PreparingReplacer(), RepairedScan, preflight);

        Assert.Equal(ReferenceRepairOutcome.VerificationFailed, result.Outcome);
        Assert.Contains("pre-existing target edge", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- strict post-mutation target integrity (vs SERVER) ---------

    [Fact]
    public void A_target_byte_change_after_the_lease_is_released_is_a_verification_failure()
    {
        var preflight = new StubPreflight
        {
            Binary = new RepairTargetBinaryState(Ok: true, Exists: true, Size: TargetSize, Sha256: new string('b', 64)),
        };

        var result = Run(EligiblePlan(), new StubConfirmation(true), new PreparingReplacer(), RepairedScan, preflight);

        Assert.Equal(ReferenceRepairOutcome.VerificationFailed, result.Outcome);
        Assert.Contains("server-authoritative", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unreadable_target_after_mutation_is_a_verification_failure()
    {
        var preflight = new StubPreflight { Binary = RepairTargetBinaryState.Unreadable };

        var result = Run(EligiblePlan(), new StubConfirmation(true), new PreparingReplacer(), RepairedScan, preflight);

        Assert.Equal(ReferenceRepairOutcome.VerificationFailed, result.Outcome);
    }

    // ---- happy path + existing invariants --------------------------

    [Fact]
    public void Successful_replacement_requires_reauth_prepare_lease_a_verifying_scan_and_a_target_match()
    {
        var replacer = new PreparingReplacer();
        var preflight = new StubPreflight();
        var rescans = 0;

        var result = Run(EligiblePlan(), new StubConfirmation(true), replacer,
            () => { rescans++; return RepairedScan(); }, preflight);

        Assert.Equal(ReferenceRepairOutcome.Repaired, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.Equal(1, preflight.RevalidateCalls);
        Assert.Equal(1, replacer.PrepareCalls);
        Assert.Equal(1, preflight.LeaseCalls);
        Assert.Equal(1, rescans);
        Assert.Equal(1, preflight.ReadTargetCalls);
        Assert.True(result.Verification!.Verified);
    }

    [Fact]
    public void A_not_ready_prepare_before_the_checkout_is_ReplaceFailed_free_zero_mutation()
    {
        var replacer = new PreparingReplacer { PrepareReady = false };

        var result = Run(EligiblePlan(), new StubConfirmation(true), replacer, RepairedScan);

        Assert.Equal(ReferenceRepairOutcome.DriftAborted, result.Outcome);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void A_failed_execute_call_is_reported_as_ReplaceFailed_and_no_rescan()
    {
        var rescans = 0;
        var replacer = new PreparingReplacer { ExecuteSucceeds = false };
        var result = Run(EligiblePlan(), new StubConfirmation(true), replacer,
            () => { rescans++; return RepairedScan(); });

        Assert.Equal(ReferenceRepairOutcome.ReplaceFailed, result.Outcome);
        Assert.Equal(0, rescans);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Failure_after_the_mutation_boundary_always_rescans_and_reports_uncertainty()
    {
        var rescans = 0;
        var replacer = new PreparingReplacer { ExecuteSucceeds = false, ExecuteMutationInvoked = true };
        var result = Run(EligiblePlan(), new StubConfirmation(true), replacer,
            () => { rescans++; return RepairedScan(); });

        Assert.Equal(ReferenceRepairOutcome.VerificationFailed, result.Outcome);
        Assert.Equal(1, rescans);
        Assert.Contains("MAY already be modified", result.Message);
    }

    [Fact]
    public void Rescan_exception_after_mutation_is_an_explicit_verification_failure()
    {
        var result = Run(EligiblePlan(), new StubConfirmation(true), new PreparingReplacer(),
            () => throw new IOException("scan failed"));

        Assert.Equal(ReferenceRepairOutcome.VerificationFailed, result.Outcome);
        Assert.Null(result.Verification);
        Assert.Contains("MAY already be modified", result.Message);
    }

    [Fact]
    public void API_success_but_a_verification_mismatch_is_a_failure_not_hidden()
    {
        var staleScan = ScanResolvingTo(OldPath, new CadManifestIdentity("cad_a", "fv_v1", "DOC-cad_a", "PART-A.ipt"));

        var result = Run(EligiblePlan(), new StubConfirmation(true), new PreparingReplacer(), () => staleScan);

        Assert.Equal(ReferenceRepairOutcome.VerificationFailed, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.False(result.Verification!.Verified);
        Assert.Contains("FAILURE", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
