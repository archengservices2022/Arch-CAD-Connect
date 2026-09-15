# P5C — Controlled Inventor Reference Repair

P5C lets an engineer repair **ONE** eligible stale managed CAD reference of the
active document through an explicit **preview → confirm** workflow. Diagnosis is
automatic; **modification is never automatic**.

```
Scan (P5A)
  -> Diagnose local health (P5B-A) + authoritative version status (P5B-B)
  -> Select ONE eligible STALE managed reference
  -> Resolve the repair target:
       STABLE LOCAL PATH  <- verified workspace manifest (root + relativePath)
       CANONICAL INTEGRITY <- authenticated Arch server FileVersion metadata
                              (fileSize + 64-hex SHA-256); NEVER the manifest
  -> Preview
  -> Explicit engineer confirmation
  -> POST-CONFIRMATION authoritative re-check:
       fresh scan
       -> fresh authoritative checkout read
       -> fresh authoritative latest-version + server integrity metadata
       -> fail-closed affirmative writability
       -> re-resolve target + re-hash its bytes vs SERVER size/SHA-256
       -> CAPTURE an IMMUTABLE authorization snapshot
            (parent cadDocumentId + checkout id + base fileVersionId - stable IDs)
       -> CAPTURE the pre-mutation count of edges already at the target
       -- any drift / missing info / timeout / failure aborts, ZERO mutation
  -> PREPARE the mutation NOW (before the final checkout read): resolve the
       loaded parent doc, enumerate descriptors, select EXACTLY ONE by resolved
       path + verified stable identity - bound to that one descriptor
  -> Acquire a PROTECTED READ lease on the exact target file
       (denies other-process write / delete / replace / rename;
        still lets Inventor read it)
  -> Hash/size the bytes the LEASE protects vs SERVER size/SHA-256
  -> FINAL authoritative checkout read  (LAST authorization step):
       reload the manifest ONLY as a consistency check; the fresh manifest AND
       the server checkout must match the IMMUTABLE snapshot EXACTLY - a reloaded
       manifest never redefines the expected identity; any drift aborts, ZERO COM
  -> EXECUTE the already-prepared replacement (minimal: COM-validity re-check on
       the SAME descriptor + one ReplaceReference)  -- lease HELD throughout
  -> Release the lease  (a disposal failure here is never plain success)
  -> Rescan
  -> Verify from the fresh scan: the count of edges at the target with the full
       stable identity rose by EXACTLY ONE vs the pre-mutation count (the
       SELECTED edge transitioned - a pre-existing target edge can never stand
       in), same parent + relationship kind + verified identity, old path gone
  -> Re-hash the target one more time vs the SERVER-authoritative metadata
```

## Eligibility (intentionally narrow)

| Reference | P5C verdict |
|---|---|
| **STALE + MANAGED**, exact stable identity, **canonical server integrity metadata**, a local copy whose current bytes match the **server** size + SHA-256, **writable** referencing document | **ELIGIBLE** |
| CURRENT | NOT REPAIRABLE |
| UNKNOWN VERSION | NOT REPAIRABLE (fail closed) |
| unmanaged / unresolved (no provable stable identity) | NOT REPAIRABLE |
| server did not return canonical `fileSize` + 64-hex `checksum` for the target | NOT REPAIRABLE (fail closed) |
| authoritative target binary not available locally as a verified managed copy | NOT REPAIRABLE (run Get Latest — P5C never does it for you) |
| target candidate identity mismatch (`cadDocumentId` / `fileVersionId`) | REJECTED |
| local manifest `fileSize` / `checksum` disagree with the server | REJECTED (run Get Latest — P5C never reconciles the manifest) |
| target file's current bytes do not match the server size + SHA-256 | REJECTED |
| target candidate identified by filename / path / timestamp only | REJECTED |
| ambiguous target (2+ managed copies claim the exact identity) | REJECTED |
| referencing document read-only / not checked out by me | REJECTED (P5C never checks out) |
| referencing document has no verified checkout binding to snapshot (no authoritative checkout) | DRIFT-ABORTED at the mutation boundary — P5C requires an authoritative checkout of the referencing document |

## Identity rule

The repair target is **never** chosen by filename, display name, path
similarity, timestamp, or version number alone. Two distinct authorities are
used and must **not** be confused:

* **Stable local mapping — the verified workspace manifest.** The local file is
  identified by an exact `root + relativePath` manifest binding whose
  `cadDocumentId` **and** `fileVersionId` both match the authoritative target
  and whose state is `Verified`. This — and workspace membership / local managed
  identity — is *all* the manifest is trusted for.
* **Canonical binary integrity — the authenticated Arch server.** The
  authoritative latest FileVersion's **byte size** and **SHA-256** come from the
  P5B-B `GET /api/desktop/cad-documents/latest-versions` result
  (`recognized: true` now also returns `fileSize` + a 64-lowercase-hex
  `checksum`, from the server's selected authoritative FileVersion record). The
  target file's **current on-disk bytes** are hashed and must match **these
  server values** exactly. The mutable local `.arch/workspace.json`
  `fileSize` / `checksum` are **not** the integrity authority.

If the local manifest's own recorded `fileSize` / `checksum` **disagree** with
the server, the repair **fails closed** — P5C never silently reconciles the
manifest; the engineer must run Get Latest.

The local target must match **all** of: same `cadDocumentId`, authoritative
target `fileVersionId`, server-authoritative `fileSize`, and server-authoritative
SHA-256. If the exact target cannot be established that way, or is not available
locally, repair does **not** proceed. The persisted `Verified` flag alone is
never sufficient, and neither is a server response that omitted canonical
integrity metadata.

The current reference must likewise have a verified manifest binding. An
`Unverified` binding remains visible to P5A/P5B diagnostics, but cannot
authorize P5C repair.

P5C requires an **authoritative checkout** of the referencing document. The
post-confirmation re-check captures an **immutable snapshot** of that checkout's
stable identity — parent `cadDocumentId`, checkout id, base fileVersionId — from
the verified local binding, after confirming it agrees exactly with the
authenticated server checkout-status response. That snapshot is the authority
for the rest of the flow; a manifest reloaded at the mutation boundary is only
a consistency check against it. A server `mine` response is only accepted when
it is **complete**: it must carry a non-blank checkout id **and** a non-blank
base FileVersion id, and both must match the verified local checkout binding
exactly (ordinal). A missing, blank, or mismatched checkout id or base
FileVersion id makes the authoritative checkout **invalid / unavailable** — a
partial `mine` is
never sufficient. This shape is enforced both at the HTTP boundary
(`CheckoutHttpClient.GetStatusAsync` fails the response) and again in the pure
`ReferenceRepairWritability.AuthoritativeCheckoutMatches` decision.

Writability is decided by a **fail-closed probe** (`LocalWritabilityProbe`) with
an explicit result — `Writable` / `ReadOnly` / `Missing` / `Indeterminate`. P5C
authorizes only on an affirmative `Writable`; a read-only attribute, an
access-denied or attribute-probe failure, a missing file, or any inability to
open the file for write all reject. Authorization is never derived by negating a
helper whose own failure also returns "not controlled". The probe changes no
file content and no timestamp, clears no read-only attribute, and never checks
anything out.

## What P5C never does

No automatic Save, Checkout, Check In, Undo Checkout, FileVersion creation,
engineering-revision creation, release, or broad Get Latest. After an explicitly
confirmed replacement the Inventor document is **dirty in memory** — that is
expected, and Save stays entirely with the engineer.

## Post-confirmation authorization (the mutation boundary)

The explicit engineer confirmation is **not** itself authorization to mutate
stale state — the engineer may leave the confirmation dialog open indefinitely.
**After** the confirmation returns and **immediately before** the single Inventor
`ReplaceReference` call, `ReferenceRepairCoordinator` runs
`IReferenceRepairPreflight.RevalidateBeforeMutation`, which re-establishes every
fact the preview relied on, from scratch:

* a **fresh COM scan** of the referencing document;
* the current reference is **still present exactly once** with a verified
  manifest identity matching the confirmed plan (same parent, relationship kind,
  `cadDocumentId`, current pinned `fileVersionId`);
* a **fresh, authenticated, time-bounded** checkout-status read — the server must
  still report a **complete** `mine` whose checkout id and base FileVersion id
  match the verified local binding exactly;
* a **fresh, authenticated, time-bounded** latest-version read — the reference
  must still be **STALE** with the **same** authoritative target `fileVersionId`;
* a **fail-closed writability probe** — the referencing document must still be
  affirmatively `Writable`;
* the authoritative target's **stable local path** is re-resolved from the
  verified workspace manifest and its current bytes **re-hashed** — the resolved
  path, `cadDocumentId`, `fileVersionId`, on-disk size, and SHA-256 must still
  match, and the size + SHA-256 are checked against the **server-authoritative**
  FileVersion metadata (a manifest that disagrees with the server aborts);
* the repair is **re-planned** and must be byte-for-byte identical in preview
  identity to the confirmed plan;
* an **immutable authorization snapshot** — parent `cadDocumentId`, expected
  checkout id, expected base fileVersionId (stable Arch IDs only, no filename or
  path) — is **captured** from the verified checkout binding. This snapshot,
  not any later manifest reload, defines the identity the mutation is
  authorized for;
* the **pre-mutation count** of edges of the referencing document that already
  resolve to the authoritative target with the full stable identity is
  captured, so verification can later prove the *selected* edge is the one that
  transitioned.

The **trusted** target size and SHA-256 the re-check hands forward are the
**server-authoritative** values (never the manifest).

Any mismatch, missing information, timeout, read failure, authentication
failure, server failure, or drift — or an exception anywhere in the re-check —
aborts as **`DriftAborted`** with **ZERO COM mutation**.

## Preparing the mutation (before the final checkout read)

All non-trivial discovery — resolving the loaded parent document, enumerating
its reference descriptors, selecting **exactly one** by resolved path + verified
stable Arch identity, validating the relationship — happens **now**, in
`IReferenceReplacer.Prepare`, and returns an `IPreparedReferenceReplacement`
bound to that **one** descriptor COM pointer. On a large assembly this is the
expensive part; doing it here keeps the authorization race that follows as
small as possible. A `Prepare` that cannot select a single valid descriptor
aborts **before** the final checkout read, with zero mutation.

## Protected target lease + final checkout + minimal execute

1. **`ProtectedFileLease.Acquire`** opens the *exact* verified target path
   `FileAccess.Read` with `FileShare.Read` — another **reader** (Inventor
   itself) may still open it, but any other process that tries to open it for
   **write**, or to **delete / replace / rename** it, gets a sharing violation
   for as long as the lease is held. The lease never writes, never changes
   attributes, never clears read-only protection, and never bypasses Vault. If
   it cannot be acquired, the repair **fails closed** with zero mutation.
2. The bytes reachable **through that held handle** are hashed and must match
   the **server-authoritative** size + SHA-256 — so the protected bytes are
   provably the authoritative FileVersion.
3. The **final authoritative checkout read** — the **last** authorization
   operation before COM. The manifest is reloaded **only as a fresh consistency
   check**: `RepairAuthorizationSnapshot.MatchesFinalState` requires the fresh
   manifest binding **and** the fresh authoritative server checkout to match the
   **immutable snapshot** exactly (parent `cadDocumentId` + checkout id + base
   fileVersionId, still a complete `Mine`). The checkout read is made for the
   snapshot's `cadDocumentId`, never a freshly loaded one. A manifest reloaded
   here can never redefine the expected checkout id / base fileVersionId. Any
   request/auth failure, timeout, malformed/incomplete response, state change,
   or **parent-identity / checkout-id / base-fileVersionId drift** (even if the
   manifest and server drift together to another internally-consistent
   checkout) **aborts before COM**, with zero mutation.
4. **Execute** is then the minimal step: a quick COM-validity re-check that the
   **same** already-selected descriptor is still valid and still resolves to the
   path it was selected for (it can never re-enumerate, reload a manifest, or
   select a different descriptor), followed by the one `ReplaceReference` call.

The coordinator holds the lease **continuously across the `Execute` call**
(`try { … } finally { lease.Dispose(); }`, and the mutation-may-have-occurred
flag is set *before* `Execute`) and releases it the instant it returns or
throws. If disposing the lease **throws after the mutation boundary**, the
result is **never ordinary success and never a harmless `DriftAborted`**: a
fresh verification scan is attempted, the disposal error is preserved in the
message, and the outcome is `VerificationFailed` stating the document may
already be modified. If **both** `Execute` and `Dispose` throw, the result still
carries mutation-uncertain semantics.

There is **no atomic server-side authorization lease** — the final checkout
read is the chosen P5C scope. It narrows, but does not eliminate, the window
between authorization and mutation; anything it cannot see is caught only by the
post-mutation verification.

## Verification

After the `ReplaceReference` call, P5C runs a **fresh P5A scan** and
`ReferenceRepairVerifier` proves the **explicitly selected** reference
transitioned to the target — not merely that "one matching target edge exists
somewhere":

* the count of edges of the referencing document that resolve to the intended
  target path with the **same relationship kind**, a present **`IsVerified`
  manifest identity**, the exact `cadDocumentId`, and the exact
  authoritative-target `fileVersionId` rose by **exactly one** versus the
  **pre-mutation count** captured during the re-check. So the concrete
  faulty case — selected stale edge *A* disappears while a pre-existing edge
  *B* was already at the target — fails: the count did not rise;
* the **old (selected) path** no longer resolves for any edge of the document.

A count that did not rise by exactly one (selected edge missing, an
impersonating pre-existing edge, an ambiguous multi-edge transition), a wrong
relationship kind, a wrong parent, an unverified binding, an identity mismatch,
or an **uncaptured pre-mutation count** is a **verification failure**.

Then P5C **re-hashes the target file one final time** and requires its current
size and SHA-256 to still match the **server-authoritative** FileVersion
metadata captured earlier (never the local manifest). A byte or size change to
the target between authorization and this point is a **verification failure**,
not a success.

Once `ReplaceReference` has been invoked, a guarded verification scan is always
attempted even if Inventor reports or throws an error. If verification cannot
complete, the result explicitly warns that the document may already be
modified.

## Architecture

COM-free planning / target resolution / verification / coordination live in
`Arch.CadConnect.Core/References/Repair/`:

| Type | Role |
|---|---|
| `IRepairTargetLocator` / `WorkspaceManifestRepairTargetLocator` | STABLE LOCAL PATH from a verified manifest binding; verify the file's current bytes against the **server-authoritative** size + SHA-256; fail closed if the manifest disagrees with the server |
| `FileVersionIntegrity` | shared canonical-SHA-256 (`^[0-9a-f]{64}$`) + safe-integer-size predicates — the same semantics the web repo enforces |
| `ReferenceRepairPlanner` → `ReferenceRepairPlan` | pure eligibility + preview; carries the **server-authoritative** target `fileSize` + SHA-256 |
| `ReferenceRepairWritability` | pure "is the referencing document already writable?" + complete-`Mine` checkout match |
| `IReferenceReplacer.Prepare` → `IPreparedReferenceReplacement.Execute` | two-phase COM replace: ALL discovery in `Prepare` (bound to one descriptor); `Execute` is the minimal COM-validity re-check + one `ReplaceReference` |
| `ReferenceReplaceTargetingResolver` | COM-free exactly-one-descriptor selection by resolved path + verified stable identity |
| `IReferenceRepairConfirmation` | the explicit confirmation gate |
| `RepairAuthorizationSnapshot` | the IMMUTABLE authorization identity (parent cadDocumentId + checkout id + base fileVersionId); `MatchesFinalState` compares a fresh manifest + fresh server checkout to it exactly |
| `IReferenceRepairPreflight` | the POST-confirmation re-check (captures the snapshot + pre-mutation target-edge count), the protected-target lease + snapshot-checked final checkout read, and a post-mutation target re-hash |
| `ProtectedFileLease` | pure-.NET protected READ handle (`FileShare.Read`) that denies other-process write / delete / replace / rename; hashes the bytes it protects |
| `ReferenceRepairVerifier` | pure post-repair verification: proves the target-edge count rose by exactly one (`CountResolvedTargetEdges` used before + after) |
| `ReferenceRepairCoordinator` | eligibility → confirm → re-authorize → **prepare** → acquire lease + final checkout → **execute** (lease held) → release (disposal failure ≠ success) → rescan → prove transition → re-hash vs server metadata |
| `ReferenceRepairTextReport` | deterministic preview text (no secrets) |

`Arch.CadConnect.Core/Files/LocalWritabilityProbe.cs` is the shared fail-closed
writability probe (`Writable` / `ReadOnly` / `Missing` / `Indeterminate`).

The only Inventor/COM code is `InventorReferenceReplacer` / `PreparedInventorReplacement`
(descriptor discovery in `Prepare`; a minimal COM-validity check + one
`ReplaceReference` call in `Execute` — no manifest reload, no re-enumeration),
`InventorRepairPreflight` (the COM + bounded-HTTP + I/O implementation of
`IReferenceRepairPreflight`, including capturing the authorization snapshot from
the verified checkout binding, acquiring the `ProtectedFileLease`, and the
snapshot-checked final checkout read for the snapshot's `cadDocumentId`), plus
the `RepairReferenceDialog` / controller wiring. **No server repository change and no
database/migration change in this repo** — P5C consumes the already-shipped
P5B-B / P5C-B server endpoint (`4a02b1dc`, "Add authoritative FileVersion
integrity metadata") and the existing managed workspace / Get Latest
architecture.

There is **no dedicated server "repair authorization" endpoint and no atomic
server authorization lease**. The post-confirmation re-check and the final
checkout read compose the existing authenticated checkout-status and
latest-versions reads. `InventorRepairPreflight` performs those reads
synchronously with a 15 s combined `CancellationToken` budget at the mutation
boundary, which can block the calling thread for up to that budget; any timeout
aborts with zero mutation.
