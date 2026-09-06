# Arch Engineering CAD Connect (Autodesk Inventor add-in)

**Milestone P4A — foundation.** This establishes the C# solution, the Inventor
add-in shell, the "ARCH ENGINEERING" ribbon, the typed API boundary to the
Arch PLM server, and a safe desktop sign-in. The actual PDM operations
(Get Latest / Checkout / Check In / Undo / Status / Where Used) are **declared
but deliberately not implemented** — their buttons are visible and disabled,
and the client never fakes a successful PDM result. Those land in P4B/P4C.

---

## Architecture

```
Arch.CadConnect.sln
├── src/
│   ├── Arch.CadConnect.Core      (net8.0)  pure domain model – NO Inventor, NO HTTP, NO DB
│   ├── Arch.CadConnect.Api       (net8.0)  HttpClient talking ONLY to the Arch server HTTP API
│   └── Arch.CadConnect.Inventor  (net8.0-windows)  the ONLY project referencing Autodesk Inventor
└── tests/
    ├── Arch.CadConnect.Core.Tests (xunit)  runs with no Inventor, no server
    └── Arch.CadConnect.Api.Tests  (xunit)  runs with a fake HttpMessageHandler
```

Dependencies are strictly one-directional: `Inventor → Api → Core`. Nothing
depends on `Inventor`.

| Project | Responsibility | Must never contain |
|---|---|---|
| **Core** | `CadDocumentContext`, `CadDocumentStatus`, `ConnectionState(+Machine)`, `IArchSession` / `DesktopSession`, `ArchServerUri` (URL/HTTPS policy), `ArchCommand` + `RibbonCommandPolicy`, `ActiveDocumentTracker` | Inventor/COM types, `HttpClient`, Prisma/DB, server-storage paths |
| **Api** | `IArchApi` / `ArchApiClient`, DTOs, `ArchApiException` (safe errors), `DpapiSessionStore`, `ArchConnectionManager` | Inventor/COM types, DB access, raw server error text reaching the UI |
| **Inventor** | `ArchAddInServer` (`ApplicationAddInServer`), `RibbonFactory`, `InventorDocumentObserver`, `SignInDialog`, `ArchAddInController` | Any COM object crossing into Core/Api; blocking the UI thread on network I/O |

**Key rule:** a live `Inventor.Document` (a COM object) is read in exactly one
place — `Documents/DocumentContextFactory` — and immediately mapped to the
COM-free `CadDocumentContext`. COM objects never leave the Inventor project.

### Server API boundary

The add-in talks to the Arch server **only** over HTTP, and in P4A only to
three endpoints added for desktop clients:

| Endpoint | Purpose |
|---|---|
| `POST /api/desktop/auth/login` | email+password → opaque bearer token (`arch_dt_…`) + identity |
| `GET  /api/desktop/session` | validate the token, return identity + server clock ("Server Status") |
| `POST /api/desktop/auth/logout` | revoke the token server-side |

The existing PDM route handlers (`/api/cad-documents/…`, etc.) are **unchanged**
in P4A. A shared `resolveApiActor(request)` helper on the server already
accepts `Authorization: Bearer` alongside the browser cookie; wiring it into
the PDM routes so the add-in can call them is a mechanical P4B step.

---

## Inventor 2025 / 2026 compatibility

| Concern | Decision |
|---|---|
| **.NET target** | `net8.0-windows`. Inventor 2025 (release **29**) and Inventor 2026 (release **30**) both host .NET 8 add-ins — confirmed on this machine: `…\Inventor 2025\Bin\ClrAddinLoader.runtimeconfig.json` pins `net8.0` / `Microsoft.NETCore.App 8.0.0`. |
| **Interop reference** | `Autodesk.Inventor.Interop.dll` referenced with **`EmbedInteropTypes=true`**, so the COM type info is baked into our DLL and one build loads in both 2025 and 2026. We use only the stable core API (`Application`, `Document`, `UserInterfaceManager.Ribbons`, `CommandManager.ControlDefinitions`, `ApplicationEvents`) present in R29 and R30. |
| **Interop path** | `build/Inventor.props` probes, in order: `$(ArchInventorInteropDll)` → `build/Inventor.local.props` (git-ignored) → `%CommonProgramFiles%\Autodesk Shared\Inventor Interoperability 2026\Bin\…` → `…2025\…` → `%ProgramFiles%\Autodesk\Inventor 20xx\Bin\…`. **No absolute install path is committed.** If nothing resolves, the Inventor project fails the build with a clear message; Core/Api/tests still build. |
| **AddIn manifest** | `Manifest/Arch.CadConnect.addin.template` with an assembly-path token. `scripts/install-addin.ps1` renders it with the absolute path of the built DLL and drops it in the **per-user** `%APPDATA%\Autodesk\Inventor <version>\Addins\` folder (no admin). |
| **COM activation** | `EnableComHosting=true` → `Arch.CadConnect.Inventor.comhost.dll`. The install script registers it **per-user** under `HKCU\Software\Classes\CLSID\{93C45016-…}` (no `regsvr32`, no admin). Inventor reads the `.addin`, finds our `ClassId`, and `CoCreateInstance`s the comhost, which starts the .NET 8 runtime in Inventor's native process. |
| **Debugging / loading for both versions** | Run `install-addin.ps1 -InventorVersions 2025,2026`. Attach the debugger to `Inventor.exe` after start-up (`Debug ▸ Attach to Process`). To iterate: edit → `install-addin.ps1` (re-publishes + re-points the manifest) → restart Inventor. Use `Tools ▸ Add-Ins` in Inventor to toggle load state without uninstalling. |

Inventor **2026 is not installed on the current build machine**, so the 2026
path is validated by design + the version-independent interop + `EmbedInteropTypes`,
not by an in-app run. See *Known limitations*.

---

## Build

```powershell
cd inventor
dotnet restore          # offline from the machine NuGet cache
dotnet build            # Core + Api + tests build anywhere; Inventor needs the SDK present
dotnet test             # 78 tests, no Inventor / no server required
```

`Arch.CadConnect.Inventor` only builds where `Autodesk.Inventor.Interop.dll` is
resolvable (Inventor 2025/2026 installed, or `ArchInventorInteropDll` set).

---

## Local Arch server configuration

The desktop endpoints live in the existing Next.js app (`web/`). Run it:

```bash
cd web
npm run dev            # http://localhost:3000
```

Sign-in dialog default: **Server** = `http://localhost:3000`.

**Transport policy (no exceptions).** Plain HTTP is accepted **only** for a
loopback host — exactly `localhost`, `127.0.0.1`, or `::1`. Every other host,
including a private LAN IP (`192.168.x.x`, `10.x.x.x`) or a machine name,
**requires HTTPS**, in development and production alike. There is no
environment variable, config flag, or UI checkbox that can loosen this. The
client (`ArchServerUri`) and the server (`assertRequestTransportAllowed`)
enforce the identical rule, and the client never sends a credential or bearer
token before it passes. A TLS-terminating reverse proxy in front of the
server is respected via `x-forwarded-proto` / `x-forwarded-host`. To reach a
non-loopback dev server, put it behind HTTPS (e.g. `mkcert`, or a dev proxy).

Session TTL is 12h by default; override with `DESKTOP_SESSION_TTL_HOURS`
(1–720) on the server.

---

## Security notes

Follows the reference CLI's established lessons:

- **No client-trusted tenant.** `organizationId` / `userId` / `role` are bound
  into the server `DesktopSession` row from verified credentials and re-derived
  on every request. The client displays the org name but never sends it back.
- **No password persisted.** It exists only in the masked textbox and the one
  login request body.
- **Token at rest is DPAPI-encrypted** (`DataProtectionScope.CurrentUser`),
  under `%LOCALAPPDATA%\ArchEngineering\CadConnect\session.bin`. Another
  Windows user, or the same user off-box, cannot read it. Only its SHA-256
  hash is stored **server-side**.
- **Token in transit**: only ever attached to a request whose origin exactly
  matches the signed-in server origin (`ArchServerUri.MatchesOrigin`), and
  redirects are refused, never followed.
- **Expiry + real logout.** The token has a server-enforced `expiresAt`;
  *Sign Out* sets `revokedAt` server-side (idempotent) and deletes the local
  file.
- **Safe errors.** `ArchApiException.Message` is one of a small set of fixed
  strings — never a raw server body, stack trace, URL with query, or token.
  5xx responses stay generic. Nothing in Api/Core logs at all.
- **Whole-exchange timeout.** The client timeout covers connect + headers +
  response body; a body that starts then stalls is a `Timeout`
  (server-unavailable), never a hang, and never a raw
  `OperationCanceledException` reaching a caller. A sign-in timeout leaves the
  UI at "server unavailable" (not stuck at "connecting") and persists no
  session; *Sign Out* clears the local session even if the revoke call times
  out.
- **HTTPS required except for a loopback host** (see Transport policy above),
  client- and server-side, with no override.
- **Bounded login body.** The desktop login route rejects a request body over
  16 KB with a controlled 413 before parsing (checked against
  `Content-Length`, then enforced while streaming).
- **UI is not the security boundary.** `RibbonCommandPolicy` only disables
  buttons that could not succeed; the server re-authorizes every operation.

### Production auth debt (Alpha shortcuts to replace later)

- Bearer tokens are **long-lived opaque strings**, not short-lived
  access+refresh tokens. No token rotation.
- No OAuth/OIDC, no SSO, no device-code flow, no MFA. Just email+password →
  token.
- DPAPI protects against other local users and off-box copies, **not** against
  malware already running as the signed-in user. A hardware/OS credential
  vault (Windows Hello / DPAPI-NG / a broker) is the production target.
- `resolveApiActor` is wired into the desktop routes only; PDM routes still
  require the cookie session until P4B.
- No server-side "list / revoke my sessions" UI yet (the table and `revokedAt`
  support it).

---

## P4A limitations

- **No PDM operations.** Get Latest / Checkout / Check In / Undo / Status /
  Version / Revision / Where Used buttons are present but **disabled**, with a
  tooltip saying they are not in this milestone. `ArchApiClient` throws
  `ArchApiException(NotImplemented)` for each — it never fabricates success.
- **Document status is always `UNKNOWN` / `UNMANAGED`.** The server does not
  expose per-document status to the desktop client yet; the client reports the
  truth rather than inventing state.
- **No PLM identity on documents.** `CadDocumentContext.PlmIdentity` is always
  null — there is no operation yet that would learn it. File names are never
  treated as identity.
- **Inventor 2026 not run.** Design-validated only (see compatibility section).
- **Smoke test is partial** (see report §12): comhost COM activation is
  verified headlessly; the in-Inventor ribbon/flow check is a manual step.

---

## Next milestones

| Milestone | Scope |
|---|---|
| **P4B** | Wire `resolveApiActor` into the PDM routes; implement **Get Latest** end-to-end into an Inventor workspace; real document→PLM identity resolution; real `CadDocumentStatus` (CURRENT / OUT_OF_DATE / RELEASED). |
| **P4C** | **Checkout / Check In / Undo Checkout** from the ribbon, with the server's exclusive-lock + version-creation semantics; `CHECKED_OUT_BY_ME/OTHER`, `LOCAL_CONFLICT`. |
| **P4D** | **Where Used**, release information, assembly-aware multi-document operations. |
| later | Token rotation / short-lived sessions; session management UI; packaging/installer; Inventor 2027+. |
