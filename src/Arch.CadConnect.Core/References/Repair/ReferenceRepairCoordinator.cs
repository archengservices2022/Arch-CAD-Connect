namespace Arch.CadConnect.Core.References;

/// <summary>The terminal state of one P5C repair attempt.</summary>
public enum ReferenceRepairOutcome
{
    /// <summary>The plan was not <see cref="ReferenceRepairEligibility.Eligible"/> -
    ///  the engineer was never asked to confirm and nothing was touched.</summary>
    NotEligible,

    /// <summary>The engineer declined at the confirmation prompt - zero CAD
    ///  changes.</summary>
    CancelledByUser,

    /// <summary>The engineer confirmed, but an authorization step AFTER the
    ///  confirmation and BEFORE any COM mutation did NOT pass, or could not be
    ///  completed: the post-confirmation re-check (checkout still Mine + exact
    ///  identity, referencing document still writable, target still re-resolves,
    ///  target bytes/size still match the SERVER-authoritative FileVersion
    ///  metadata), OR the final protected-target lease + final authoritative
    ///  checkout read immediately before ReplaceReference. ZERO COM mutation was
    ///  performed - the stale confirmation was refused.</summary>
    DriftAborted,

    /// <summary>The Inventor replacement call itself failed - the reference was
    ///  not changed (or was changed and reported an error; either way the
    ///  document is not saved).</summary>
    ReplaceFailed,

    /// <summary>The replacement call returned, but a FRESH scan does not confirm
    ///  the reference now points at the intended target. Reported as a failure,
    ///  never hidden.</summary>
    VerificationFailed,

    /// <summary>The replacement was made AND a fresh scan confirms the reference
    ///  now resolves to the intended authoritative target. The document is
    ///  modified in memory and it is the engineer's job to Save.</summary>
    Repaired,
}

public sealed record ReferenceRepairResult(
    ReferenceRepairOutcome Outcome,
    ReferenceRepairPlan Plan,
    ReferenceReplaceResult? Replace,
    ReferenceRepairVerification? Verification,
    string Message)
{
    public bool Succeeded => Outcome == ReferenceRepairOutcome.Repaired;
}

/// <summary>The explicit engineer confirmation gate, invoked IMMEDIATELY before
///  any Inventor mutation. A false return makes zero CAD changes.</summary>
public interface IReferenceRepairConfirmation
{
    bool Confirm(ReferenceRepairPlan plan);
}

/// <summary>
/// The COM-free spine of P5C: given an eligible plan, an explicit confirmation
/// gate, a post-confirmation authorization re-check, a single-reference
/// replacer, and a way to re-scan, it runs
///
///   check eligibility -&gt; explicit confirmation
///   -&gt; POST-CONFIRMATION authorization re-check: captures an IMMUTABLE
///      authorization snapshot (parent cadDocumentId + checkout id + base
///      fileVersionId) and the pre-mutation count of edges already at the
///      target (any drift aborts, ZERO COM)
///   -&gt; PREPARE the mutation: all expensive discovery (loaded doc, descriptor
///      enumeration, exactly-one selection) done NOW, bound to that one
///      descriptor
///   -&gt; acquire a PROTECTED READ lease on the exact target + prove its bytes
///      match the SERVER-authoritative size/SHA-256 + a FINAL authoritative
///      checkout read that must match the IMMUTABLE snapshot EXACTLY - any
///      failure aborts, ZERO COM
///   -&gt; EXECUTE the already-prepared replacement (minimal COM: validity check
///      + one ReplaceReference) WHILE the lease is held -&gt; release the lease
///      (a lease-disposal failure after this point is never ordinary success)
///   -&gt; rescan -&gt; strict verify
///     (the SELECTED edge TRANSITIONED: target-edge count rose by exactly one
///      + relationship + verified identity + old path gone
///      + post-mutation target size/SHA-256 vs the SERVER-authoritative metadata)
///
/// and never claims success without a verifying fresh scan proving the selected
/// edge transitioned AND a post-mutation target-integrity match against
/// SERVER-authoritative metadata (never the mutable local manifest). It performs
/// NO save, NO check-in, NO checkout, NO Get Latest - and it only ever asks the
/// replacer to touch the one selected reference.
/// </summary>
public static class ReferenceRepairCoordinator
{
    public static ReferenceRepairResult Execute(
        ReferenceRepairPlan plan,
        IReferenceRepairConfirmation confirmation,
        IReferenceRepairPreflight preflight,
        IReferenceReplacer replacer,
        Func<CadReferenceScan> rescan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(replacer);
        ArgumentNullException.ThrowIfNull(rescan);

        if (!plan.CanProceed)
        {
            return new ReferenceRepairResult(ReferenceRepairOutcome.NotEligible, plan, null, null,
                plan.EligibilityLabel + " - " + plan.EligibilityDetail);
        }

        if (string.IsNullOrWhiteSpace(plan.CurrentReferencePath) || string.IsNullOrWhiteSpace(plan.ProposedTargetPath))
        {
            return new ReferenceRepairResult(ReferenceRepairOutcome.NotEligible, plan, null, null,
                "The plan is missing the current or target path - refusing to proceed.");
        }

        // EXPLICIT confirmation, immediately before mutation. A decline is a
        // hard stop with zero CAD changes.
        if (!confirmation.Confirm(plan))
        {
            return new ReferenceRepairResult(ReferenceRepairOutcome.CancelledByUser, plan, null, null,
                "Cancelled. No CAD reference was changed.");
        }

        // POST-CONFIRMATION AUTHORIZATION. The confirmation dialog could have
        // been open indefinitely; re-check EVERYTHING against fresh authoritative
        // state before touching Inventor. Any mismatch / missing information /
        // timeout / failure aborts with ZERO COM mutation.
        RepairPreflightVerdict verdict;
        try
        {
            verdict = preflight.RevalidateBeforeMutation(plan);
        }
        catch (Exception ex)
        {
            return new ReferenceRepairResult(ReferenceRepairOutcome.DriftAborted, plan, null, null,
                $"The post-confirmation authorization re-check threw {ex.GetType().Name}. "
                + "NO CAD reference was changed.");
        }

        if (verdict is null || !verdict.Authorized)
        {
            return new ReferenceRepairResult(ReferenceRepairOutcome.DriftAborted, plan, null, null,
                "Post-confirmation authorization failed: " + (verdict?.Detail ?? "no verdict was produced")
                + " NO CAD reference was changed.");
        }

        if (verdict.TrustedTargetSize < 0
            || !FileVersionIntegrity.IsRepresentableFileSize(verdict.TrustedTargetSize)
            || !FileVersionIntegrity.IsCanonicalSha256(verdict.TrustedTargetSha256))
        {
            return new ReferenceRepairResult(ReferenceRepairOutcome.DriftAborted, plan, null, null,
                "The post-confirmation re-check did not establish canonical SERVER-authoritative target size / "
                + "SHA-256 metadata. NO CAD reference was changed.");
        }

        if (verdict.Snapshot is not { IsComplete: true })
        {
            return new ReferenceRepairResult(ReferenceRepairOutcome.DriftAborted, plan, null, null,
                "The post-confirmation re-check did not establish a complete immutable authorization snapshot "
                + "(parent cadDocumentId + checkout id + base fileVersionId). NO CAD reference was changed.");
        }

        if (verdict.PreMutationTargetEdgeCount < 0)
        {
            return new ReferenceRepairResult(ReferenceRepairOutcome.DriftAborted, plan, null, null,
                "The post-confirmation re-check did not capture the pre-mutation evidence needed to prove the "
                + "selected reference transitions. NO CAD reference was changed.");
        }

        if (verdict.PreMutationReferenceFingerprint is null)
        {
            return new ReferenceRepairResult(ReferenceRepairOutcome.DriftAborted, plan, null, null,
                "The post-confirmation re-check did not capture the pre-mutation whole-reference-set fingerprint "
                + "needed to prove no unrelated reference changes. NO CAD reference was changed.");
        }

        // ALL expensive discovery happens NOW, BEFORE the final checkout read:
        // resolve the loaded parent document, enumerate its descriptors, select
        // EXACTLY ONE by resolved path + verified stable identity. The returned
        // prepared mutation is bound to that one descriptor.
        IPreparedReferenceReplacement prepared;
        try
        {
            prepared = replacer.Prepare(new ReferenceReplaceCommand(
                plan.ReferencingDocumentPath,
                plan.CurrentReferencePath!,
                plan.ProposedTargetPath!,
                plan.CadDocumentId!,
                plan.CurrentPinnedFileVersionId!));
        }
        catch (Exception ex)
        {
            return new ReferenceRepairResult(ReferenceRepairOutcome.DriftAborted, plan, null, null,
                $"Preparing the reference replacement threw {ex.GetType().Name} (before any mutation). "
                + "NO CAD reference was changed.");
        }
        if (prepared is null || !prepared.Ready)
        {
            return new ReferenceRepairResult(ReferenceRepairOutcome.DriftAborted, plan, null, null,
                "The reference to replace could not be prepared for mutation (before any mutation): "
                + (prepared?.Detail ?? "no prepared replacement was produced") + " NO CAD reference was changed.");
        }

        // LAST authorization step, immediately before COM mutation: acquire a
        // PROTECTED READ lease on the exact verified target file (denies
        // other-process write / delete / replace / rename while still letting
        // Inventor read it), prove the bytes it protects match the
        // SERVER-authoritative size + SHA-256, and perform ONE MORE authoritative
        // checkout-status read that must match the IMMUTABLE snapshot EXACTLY.
        ProtectedRepairTargetLease lease;
        try
        {
            lease = preflight.AcquireProtectedTargetForMutation(plan, verdict)
                ?? ProtectedRepairTargetLease.Failed("no lease verdict was produced");
        }
        catch (Exception ex)
        {
            return new ReferenceRepairResult(ReferenceRepairOutcome.DriftAborted, plan, null, null,
                $"The pre-mutation protected-target lease / final checkout authorization threw {ex.GetType().Name}. "
                + "NO CAD reference was changed.");
        }

        if (!lease.Acquired)
        {
            // No mutation was invoked; safe to dispose the (failed / empty)
            // lease and report a zero-mutation abort.
            try { lease.Dispose(); } catch { /* failed lease holds nothing */ }
            return new ReferenceRepairResult(ReferenceRepairOutcome.DriftAborted, plan, null, null,
                "The final pre-mutation authorization failed (protected target lease / final checkout read): "
                + lease.Detail + " NO CAD reference was changed.");
        }

        // The lease IS held. From here a mutation MAY have occurred; a lease
        // disposal failure after this point must NEVER hide the mutation
        // boundary or report ordinary success.
        var mutationMayHaveOccurred = false;
        ReferenceReplaceResult replace;
        Exception? leaseDisposeError = null;
        try
        {
            try
            {
                // MINIMAL final step on the ALREADY-PREPARED descriptor: a quick
                // COM-validity re-check then the one ReplaceReference call. No
                // re-enumeration / search / manifest reload here.
                mutationMayHaveOccurred = true;
                replace = prepared.Execute();
            }
            catch (Exception ex)
            {
                replace = new ReferenceReplaceResult(false,
                    $"The prepared replacement threw {ex.GetType().Name}; Inventor may already be modified.",
                    MutationInvoked: true);
            }
        }
        finally
        {
            // Release the lease the instant ReplaceReference returned or threw.
            try { lease.Dispose(); }
            catch (Exception ex) { leaseDisposeError = ex; }
        }

        // FINDING 4: a lease-disposal failure AFTER the mutation boundary is
        // never an ordinary success or a harmless pre-mutation abort.
        if (leaseDisposeError is not null && mutationMayHaveOccurred)
        {
            ReferenceRepairVerification? afterDrop = null;
            try
            {
                afterDrop = ReferenceRepairVerifier.Verify(
                    plan, rescan(), verdict.PreMutationTargetEdgeCount, verdict.PreMutationReferenceFingerprint);
            }
            catch { /* verification could not be completed; still report uncertainty */ }

            return new ReferenceRepairResult(ReferenceRepairOutcome.VerificationFailed, plan, replace, afterDrop,
                "ReplaceReference was invoked and then releasing the protected target lease threw "
                + $"{leaseDisposeError.GetType().Name}: {leaseDisposeError.Message}. The Inventor document MAY already "
                + "be modified - a fresh scan was attempted but this is NOT reported as success; inspect the document "
                + "before saving.");
        }

        if (!replace.ApiSucceeded && !replace.MutationInvoked)
        {
            return new ReferenceRepairResult(ReferenceRepairOutcome.ReplaceFailed, plan, replace, null,
                "The reference replacement did not complete: " + replace.Detail
                + " The document was not saved.");
        }

        ReferenceRepairVerification verification;
        try
        {
            var freshScan = rescan();
            verification = ReferenceRepairVerifier.Verify(
                plan, freshScan, verdict.PreMutationTargetEdgeCount, verdict.PreMutationReferenceFingerprint);
        }
        catch (Exception ex)
        {
            return new ReferenceRepairResult(ReferenceRepairOutcome.VerificationFailed, plan, replace, null,
                "ReplaceReference was invoked, but post-mutation verification could not be completed "
                + $"({ex.GetType().Name}). The Inventor document MAY already be modified; inspect it before saving.");
        }

        if (!replace.ApiSucceeded)
        {
            return new ReferenceRepairResult(ReferenceRepairOutcome.VerificationFailed, plan, replace, verification,
                "ReplaceReference was invoked and then reported failure. The Inventor document MAY already be modified. "
                + "A fresh scan was attempted; inspect the document before saving. " + replace.Detail);
        }

        if (!verification.Verified)
        {
            return new ReferenceRepairResult(ReferenceRepairOutcome.VerificationFailed, plan, replace, verification,
                "The replacement call returned, but a fresh scan does NOT confirm the reference now points at the "
                + "intended target. Treating this as a FAILURE - review the document in Inventor. "
                + string.Join(" ", verification.Reasons));
        }

        // STRICT post-mutation target-integrity: the fresh scan matched, but the
        // TARGET FILE itself must still be byte-for-byte the SERVER-authoritative
        // immutable FileVersion (never the mutable local manifest).
        RepairTargetBinaryState post;
        try
        {
            post = preflight.ReadTargetBinary(plan.ProposedTargetPath!) ?? RepairTargetBinaryState.Unreadable;
        }
        catch
        {
            post = RepairTargetBinaryState.Unreadable;
        }

        if (!post.Ok || !post.Exists
            || post.Size != verdict.TrustedTargetSize
            || !HexEquals(post.Sha256, verdict.TrustedTargetSha256))
        {
            return new ReferenceRepairResult(ReferenceRepairOutcome.VerificationFailed, plan, replace, verification,
                "The fresh scan matched, but the TARGET FILE's current size / SHA-256 no longer match the "
                + "SERVER-authoritative FileVersion metadata (it changed, is missing, or could not be re-read). "
                + "Treating this as a FAILURE - review the document in Inventor before saving.");
        }

        return new ReferenceRepairResult(ReferenceRepairOutcome.Repaired, plan, replace, verification,
            "Reference repaired and verified by a fresh scan (and the target file's bytes re-checked against the "
            + "SERVER-authoritative FileVersion metadata). The document is now MODIFIED in memory - review it and Save when you are "
            + "ready. P5C does not save, check in, or check out for you.");
    }

    private static bool HexEquals(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
