# P5B-B — PLM Version Intelligence

P5B-B layers **authoritative Arch PLM version status** on top of the P5B-A
local reference health, per managed reference:

| Status | Meaning |
|---|---|
| **CURRENT** | The reference is locally managed with an exact stable `cadDocumentId` and a pinned local `fileVersionId`, and the authenticated authoritative Arch PLM server reports **that same** `fileVersionId` as the latest for that `cadDocumentId`. |
| **STALE** | Same exact identity, but the server reports a **different** latest `fileVersionId`. |
| **UNKNOWN VERSION** | Version status cannot be established safely or authoritatively (unresolved / unmanaged reference, missing stable identity, server unavailable, auth unavailable/rejected, unknown CAD document, malformed response, …). **Fail closed — CURRENT is never guessed.** |

CURRENT / STALE are decided **only** from
`cadDocumentId` + pinned local `fileVersionId` + the authenticated authoritative
server result. Never from a filename, display name, path, file timestamp, local
modification time, version number alone, or the local workspace manifest alone.

## Health integration

* STALE contributes **at least WARNING** to the combined overall health.
* UNKNOWN VERSION on a managed reference contributes **at least UNKNOWN** — it
  never improves health.
* Existing P5A / P5B-A ERROR / WARNING / PARTIAL outcomes are preserved
  (`ERROR > WARNING > UNKNOWN > HEALTHY`); a PARTIAL underlying scan is still
  floored at UNKNOWN.
* The P5B-A report (`ReferenceVersionReport.Local`) is carried through
  **unmodified**.

## Server contract (IMPLEMENTED)

CAD Connect never touches PostgreSQL and adds no schema/migration. It relies on
one small **read-only, authenticated** endpoint in the Arch PLM web repository,
which **is implemented and manually accepted**. Against an older Arch PLM server
that predates it, `HttpLatestVersionProbe` still degrades safely: a 404 /
missing route yields `LookupUnavailable`, while a contract mismatch or otherwise
malformed authoritative response yields `MalformedResponse` — both fail closed,
and every managed reference is classified **UNKNOWN VERSION**.

```
GET /api/desktop/cad-documents/latest-versions
    ?cadDocumentId=<id>&cadDocumentId=<id>...

Auth : Authorization: Bearer <desktop session token>
       (same resolveApiActor path as the other /api/desktop/* endpoints;
        tenant/org derived server-side, never from the client)
Scope: READ-ONLY. Identity only — no file bytes, no mutation, no checkout.

200:
{
  "contract": "arch-plm.desktop-latest-versions.v1",
  "results": [
    { "cadDocumentId": "<id>", "recognized": true,
      "latestFileVersionId": "<fvId>", "latestVersionNumber": 7 },
    { "cadDocumentId": "<id>", "recognized": false }
  ]
}

- latestFileVersionId = authoritative id of the newest CadFileVersion for that
  CadDocument in the caller's tenant.
- recognized:false (or an id omitted from "results") => that id is UNKNOWN VERSION.
- 401/403 => auth failed. 404 / missing route => lookup unavailable. Contract
  mismatch or a malformed authoritative response => malformed response. 5xx /
  timeout / redirect => server unavailable. Every case fails closed to
  UNKNOWN VERSION.
```

The client side (`ILatestVersionProbe` / `HttpLatestVersionProbe`,
`ILatestVersionOracle` / `LatestVersionLookup`, `ReferenceVersionClassifier`,
`ReferenceVersionReport`) is implemented and unit-tested against this exact
contract.
