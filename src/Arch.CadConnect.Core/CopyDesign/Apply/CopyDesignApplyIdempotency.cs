namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// P6C: a client-generated OPAQUE idempotency key for ONE logical "Apply Copy
/// Design" attempt.
///
/// LIFECYCLE: generated EXACTLY ONCE when the user explicitly confirms an
/// apply attempt (see <see cref="CopyDesignApplyAttempt"/>). The SAME key is
/// reused for every HTTP retry of that SAME logical attempt (network
/// hiccups, a transient server error, this process's own bounded retry) - a
/// fresh key is NEVER minted merely because a retry occurred. A fresh key is
/// only ever minted for a genuinely NEW confirmed attempt (the user re-opens
/// the confirmation dialog and confirms again, e.g. after choosing a new
/// preview or after a prior attempt was abandoned).
///
/// Never derived from a filename, path, document number, or any other
/// mutable/guessable value - see the P6B server contract's own idempotency
/// design, which this deliberately mirrors.
/// </summary>
public static class CopyDesignIdempotencyKeyGenerator
{
    public static string NewKey() => "cadconnect-p6c-" + Guid.NewGuid().ToString("N");
}

/// <summary>
/// P6C: the mutable "one confirmed apply attempt in progress" holder. Owns
/// the idempotency key for exactly one attempt and exposes it for as many
/// retries of that SAME attempt as needed; <see cref="StartNewAttempt"/> is
/// the ONLY way to mint a new key, and it is explicit that doing so begins a
/// SEPARATE logical operation.
/// </summary>
public sealed class CopyDesignApplyAttempt
{
    private CopyDesignApplyAttempt(string idempotencyKey)
    {
        IdempotencyKey = idempotencyKey;
    }

    public string IdempotencyKey { get; }

    /// <summary>Begin a brand-new logical attempt with a freshly minted key.
    ///  Call this ONLY when the user explicitly (re-)confirms an apply.</summary>
    public static CopyDesignApplyAttempt StartNewAttempt() => new(CopyDesignIdempotencyKeyGenerator.NewKey());

    /// <summary>Reconstructs an in-flight attempt around an ALREADY-minted
    ///  key - used only to retry the SAME logical attempt (e.g. after a
    ///  transient network failure), never to start a new one.</summary>
    public static CopyDesignApplyAttempt Resume(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("An idempotency key is required to resume an attempt.", nameof(idempotencyKey));
        }
        return new CopyDesignApplyAttempt(idempotencyKey);
    }
}
