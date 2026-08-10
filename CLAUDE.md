# RslCompanionUploader — Claude Code scope

## What this app is

A signed-in Windows (WinForms, .NET 10) companion for **rslcompanion.com** ("RSL Companion
Account Data Extractor"). It authenticates against the site's Firebase project
(`raid-account-manager`), lists the accounts linked to the signed-in user, and sends
Raid: Shadow Legends account data to the RSL Companion API.

The app's job is to read the live Raid process memory via the **private extraction engine** (git
submodule at `extraction/`, repo `RslCompanionExtraction`) and POST what it finds. The payload is
self-identifying (it carries the in-game `accountId`); the server routes by that id. Extraction
runs off the UI thread; engine console output is redirected into the activity log.

There is **one export**:

| Action | Payload | Endpoint | Cost |
| --- | --- | --- | --- |
| **Update user data** | consolidated profile (resources, champions, guardians, the whole artifact vault, relics, gemstones, `clanId` + `clanName`) | `/api/sync/consolidated/raw` | ~5 s first run of a game session, ~1 s after |

**There used to be a second one — "Export clan" → `/api/sync/clan/raw` — and it is gone. Do not
bring it back.** It posted the clan record plus a roster of every clanmate's id and display name,
read out of the exporting player's client. RSL Companion stopped ingesting rosters: those people
never installed this app and never agreed to anything, so clan membership is now built from each
member importing their *own* account, and the server endpoint no longer exists. The engine still has
`ExtractClanAsync` — nothing calls it.

**The clan's id *and name* both ride on the consolidated payload, and both are free** (schema 14).
The id is two pointers off `UserGameData`; the name is a pointer walk from `AppModel` — see
`ClanExtractor.TryReadClanName` in the engine. **The roster is reachable by that same walk and is
still not emitted.** That is the whole point: the reason was never cost, and now that cost is
measurably zero, the consent reason stands on its own. Don't let a future "but it's cheap now"
reopen it.

**Artifacts are the one thing that pays a scan on this path, deliberately.** The vault is not
reachable from the account object either, but it stays on the consolidated path: it is the account's
own data, it costs ~3 s rather than ~25, and the cost is a one-off per game session — the instance
address is cached for the session and the class RVA ships in the offset catalog. Gear and
accessories are kept apart in the *payload* (`artifacts[]` / `accessories[]`) rather than in separate
uploads, so one call still snapshots the account while a consumer can load either alone.

## UI: native shell + WebView2 page

The **entire UI is one full-window WebView2 page** ([Forms/AppShell.cs](Forms/AppShell.cs)), styled to
match rslcompanion.com. [Forms/MainForm.cs](Forms/MainForm.cs) is a thin native shell: title bar + a
File/Help `MenuStrip`, hosting `AppShell` docked fill. `MainForm` stays the backend — it runs the
status poll, extraction and API calls, and **pushes a single view-state** into the shell (signed-in
flag, user, connection status, update banner, accounts, busy + which action is busy, frontend URL).
The page posts back five actions: `export`, `signIn`, `signOut`, `refresh`, `openUrl`. Check for
updates, recalibrate and about stay native menu items calling straight into `MainForm` — no bridge
needed. There is no uncovered-build bridge action or banner: covering an uncovered build is triggered
automatically from `MainForm`, not from anything the page posts back.

The app opens its main window **before authenticating** (like Postman). [Program.cs](Program.cs) now
does *no* authentication at all — it hands `MainForm` the launch code (if any) and starts the message
loop. `MainForm.RestoreSessionAsync` runs on Load: redeem the launch code, else restore the saved
session. **That work moved off the startup path deliberately** — both legs make a network call, and
the Windows Hello option prompts the user, so doing it before the first paint gives them a hang with
nothing on screen to explain it. The window opens signed-out and fills in when the session arrives;
the game-status pill works throughout. Clicking Sign In opens [SignInPanel](Forms/SignInPanel.cs) and,
on success, `MainForm.EnterSignedInAsync` loads accounts and enables export. Sign out drops back to
the signed-out state in place (no process restart).

The page is a top bar (brand + connection pill + Sign In button / identity), an optional update
banner, the accounts grid, an "Open RSL Helper" bar (opens `MainForm.HelperUrl()` via `openUrl`), and
a collapsible activity console.

`HelperUrl()` is `AppConfig.FrontendUrl` plus **`?account=<in-game id>`** whenever the running game is
on an account the profile has already imported, so the site opens on the account being played rather
than on whatever that browser last selected. The id needs no translation: `GET /api/accounts` reports
the in-game id as both `id` and `userId`. The site consumes it in `ActiveAccountService` (RaidTools
frontend), which reads the param at *module load* — the auth guards redirect without preserving query
params, so anything later is too late — writes it to its `raidtools.activeAccountId` storage, and
strips it from the address bar. Clearing the stored sync method alongside it is deliberate: it makes
the navbar re-pin the preferred (Extractor) snapshot for that account. "Raid not running" is stated once, by the top-bar pill — the accounts
grid never repeats it as a tile. A running-but-unimported account shows a "new account detected"
tile, and the profile matching the running game turns green (all others keep a black border).

**Tiles are status and cannot be selected — except the one tile the running game is on, which carries
the game-reading action.** Its target is never chosen by the user: it reads the live process, so the
only account it could ever act on is the one being played. A new (unimported) account's tile offers
"Add this game account"; a matched existing one offers "Update user data". Both post the same
`export` action — the server create-or-updates by the in-game id in the payload. When no game is
reachable, no tile carries buttons at all.

`SetBusy` takes a *kind* (`"export"`, or null for background work like an account reload) so the page
can show progress on the button that is running rather than only greying everything out, with the
engine's own phase lines in the activity log alongside.

Because the whole UI is WebView2, the runtime (preinstalled on Win11) is load-bearing; if it's
missing, `AppShell` shows a plain fallback label instead of the page. Sign-in does **not** depend on
it — that flow is the user's own browser plus a native panel, so it still works on a machine where
the runtime is absent.

**An uncovered game build resolves itself, automatically, with no button to click.** When Raid updates
ahead of a release, `MainForm.UpdateReportPrompt` fires the moment the status poll notices a build
`CoveredByShippedCatalog` doesn't know: it asks RSL Companion for a published memory map
(`Endpoints.BuildCertification`) and installs it into the user's own `calibrated-offsets.json`, and
only falls back to the ~35–50 s local calibration scan when the server has none. The server lookup is
still **opt-in** — a TaskDialog with a "Check automatically from now on" verification box, one offer
per build per session, the tick persisted to `settings.json` — but nothing here waits for the user to
notice or click a banner first. Both the certify check and the calibration own a
once-per-build-per-session guard, so re-running this on every poll tick is safe. It stops re-triggering
once *any* local map exists, certified or self-calibrated — `CoveredByShippedCatalog` alone would keep
firing for a user who already fixed it.

**A calibration result is only trusted if it looks like a real account.** `ExtractionService.CalibrateAsync`
extracts without throwing even when the offsets are wrong — a bad scan can read zeroed or garbage
fields just as easily as it can crash. Since `KnownOffsets.Export` writes into a catalog that
`TryResolve` treats as ground truth forever afterwards (a known hash is never recalibrated), the result
is validated (a parseable positive account id, a non-empty name) before it is allowed to touch that
file. A result that fails validation returns `Success: false` and nothing is written, leaving whatever
was already in the catalog — a prior good calibration, or nothing — untouched.

**Both `TryCertifyBuildAsync` and `TrySelfCalibrateAsync` hold `SetBusy(true)` for their duration**,
same as export — the accounts grid's action buttons disable while either is in flight, since a click
landing mid-scan or mid-apply would race the process attach the scan already owns.

**`ExportAccountAsync` validates before uploading, not just on the calibration path.** The same
"parseable id, non-empty name" bar used for calibration is applied to every extracted profile right
before it's sent to RSL Companion — a bad read can slip through fine on a `Update user data` click
even when calibration itself reported success earlier in the session. A failing check logs and returns
without calling `UploadConsolidatedAsync`.

File-based JSON import (`resources` / `champions`) used to live here but was moved to the
rslcompanion.com metadata tooling — do not reintroduce it in this app.

## Public repo / private engine split

This repo is **public**; the extraction engine is **private** and optional at build time:

- With the submodule present (`git submodule update --init`, needs access to the private repo),
  the build defines `EXTRACTION` and the "Update user data" button works.
- Without it, the project still builds and runs — extraction code paths are `#if EXTRACTION`
  and the button is hidden. With no engine there is nothing left to do but sign in and view the
  (empty) accounts pane.
- Engine internals, data files (`offsets_cache.json`, `exports/champion_index.json`,
  `resource-allowlist.json`), limitations, and vendoring rules are documented **in the
  private repo's CLAUDE.md** — do not re-document them here.

## Sign-in: the real browser, always

Clicking Sign In shows [SignInPanel](Forms/SignInPanel.cs) — an in-window "finish this in your
browser" state — and opens the user's **real default browser** at `/connect-extractor`. That page
mints a one-time handoff code and launches `rslcompanion-extractor://sync?code=…`; Windows routes it
to this app, `SingleInstance` forwards it to the waiting panel, and
[Auth/ExtractorHandoff.cs](Auth/ExtractorHandoff.cs) redeems it for a Firebase **custom token**.

**Hosting that page in an embedded WebView2 was built, tested and abandoned. Do not rebuild it.** It
looks obviously better — one window, no browser bounce, and the handoff code never touches a command
line — and it does not work, for reasons no amount of layout work fixes:

- **Google and Microsoft will not complete a consent flow in an embedded browser.** They screen the
  surface, and `signInWithPopup` opens a chromeless second WebView2 with no address bar. The user
  reaches a real provider page that then goes nowhere.
- **The popup has no opener to answer.** Suppress the popup window and `window.open()` returns null;
  host it and the flow still has to postMessage back. Navigating the main view to the popup's URL —
  the obvious fix — destroys the handshake outright and presents as the app hanging with no error.
- **`signInWithRedirect` in-window works around the popup but not the surface check**, and it drags a
  `connect` redirect-intent through the site's auth service purely for the app's benefit.
- **Stale script fails silently.** The WebView2 profile outlives releases, so a cached bundle quietly
  runs an older sign-in flow against a site that has moved on.

The real browser already has the user's session, password manager and 2FA, and is the surface the
providers actually support. The site keeps its `?embed=1` mode from that attempt (unused, harmless —
it only activates on the parameter, which this app no longer sends).

**The app still gets its own session, and that is the point.** It is not a copy of the browser's.
`/connect-extractor` hands over a code, not credentials, and `signInWithCustomToken` mints tokens that
belong to this install — so a token on this disk is never worth a browser session, and the two are
independently revocable. **Do not "simplify" this into reading the browser's session** (Chrome
app-bound-encrypts its store, and it is an infostealer pattern) or into an in-app password form (it
would lock out every Google and Microsoft user and put a password back into a desktop process).

**`SignInPanel` earns its place with the checkbox, not the status.** The browser signs the *user* in,
but only the app can be asked whether the *app* should stay signed in, because only the app writes to
this disk — so the question is asked while the browser works and read when the code comes back.

The app registers `rslcompanion-extractor://` under HKCU on every startup
([ProtocolHandler.cs](ProtocolHandler.cs)); the installer also registers it at install time. A code
arriving on the launch URI at startup (site-initiated: dashboard → "Sync New Account") is redeemed by
`MainForm.RestoreSessionAsync`.

**It used to be `?rt=<firebase refresh token>`, and that is a protocol break, not a refactor.** Windows
hands a protocol URI to its handler as *process arguments* — readable by any local process and
routinely logged by EDR agents, Sysmon and crash reporters — so a refresh token there was a
credential that mints ID tokens indefinitely, sitting on a command line. The code is worth one
sign-in for ~60 s, is single-use, and only the SHA-256 of it is stored server-side. `rt` is
deliberately **not** still accepted: the site no longer sends it, and reading it would keep the old
credential path alive on the one surface it was removed from. Server contract:
`docs/extractor-handoff.md` in the RaidTools repo. **An uploader older than this cannot sign in from
the website at all** — the launch URI carries a parameter it does not read.

## Staying signed in

Once signed in, the app never needs the website again: it holds a Firebase **refresh token**, and
Firebase refresh tokens do not expire on their own. [Auth/SessionManager.cs](Auth/SessionManager.cs)
owns restore/persist/forget; [Auth/CredentialStore.cs](Auth/CredentialStore.cs) owns the file at
`%LOCALAPPDATA%\RslCompanionUploader\creds.dat`.

**Persistence is opt-in, and the opt-in is total.** [Auth/SessionProtection.cs](Auth/SessionProtection.cs)
is the single stored value (`sessionProtection` in `settings.json`), chosen on the sign-in window and
changeable later from Help ▸ Session security without signing out:

| Level | What is stored | What it stops |
| --- | --- | --- |
| `None` (default) | **nothing** — no token, no email, no display name | n/a; session ends with the process |
| `WindowsAccount` | refresh token, DPAPI CurrentUser | another Windows user, disk theft |
| `WindowsHello` | the above, additionally AES-GCM under a TPM-held key | **code running as the signed-in user** |

At `None` the file does not exist. Identity fields used to be saved regardless "for convenience" —
they are identity, the choice is the only mandate for keeping any of it, and a file that exists only
when consent was given is a file whose presence answers "did I agree to this?".

**`sessionProtectionChosen` is separate from the level, and the difference matters.** A launch from
the website signs the app in with no sign-in screen, so there is no checkbox to read — and treating
the `None` default as an answer would mean silently never remembering anyone who arrives that way.
`MainForm.AskProtectionIfUnansweredAsync` asks **once ever** on that path, after the UI is up. The
sign-in panel and Help ▸ Session security also set the flag; nothing asks twice.

Hello does not hand out key material, it signs, so the blob carries a random challenge and the AES key
is SHA-256 of the Hello signature over it ([Auth/HelloProtector.cs](Auth/HelloProtector.cs)). This
relies on Hello signing with **RSASSA-PKCS1-v1_5, which is deterministic**. Which factor is offered —
face, fingerprint, PIN — is Windows' decision, not ours. If the credential is lost (Hello reset, TPM
cleared) the saved session is simply unreadable and the user signs in again; every caller treats a
Hello failure as "no saved session" rather than an error to surface. **If Hello was chosen and then
declined, nothing is written and the preference resets** — silently storing it unprotected would be
the opposite of the request.

The DPAPI entropy constant is a public constant in a public repo and therefore **adds no secrecy**. It
is retained only so files written by earlier versions stay readable; the protection comes from DPAPI's
CurrentUser scope and, optionally, Hello. Don't churn it thinking it does something.

**A failed refresh only clears the saved session when Firebase actually rejected it**
(`TOKEN_EXPIRED`, `USER_DISABLED`, `INVALID_REFRESH_TOKEN`, …). Anything else — no network yet, DNS
not up, a 503 — leaves the file alone. It used to clear on *any* exception, so launching before the
network was ready silently discarded a good session and sent the user back to the website. Guessing
wrong in this direction costs one sign-in; in the other it throws away a valid session because Wi-Fi
was slow.

**Sign-out asks which kind it is**, because they are different actions. Plain sign-out forgets this
PC. "Sign out everywhere" also calls `POST /api/auth/logout` (which already existed — it blacklists
the presented ID token and calls Admin `RevokeRefreshTokens`) and clears the shared WebView2 profile.
**Firebase revokes per user, not per device**, so "everywhere" genuinely signs the browser out too;
that is stated on the button rather than in a follow-up confirmation, because it is the surprising
part and afterwards is too late to read it.

## Export payload contract — keep the pair in sync

One doc pair, prose + JSON Schema 2020-12:

- [docs/export-schema.md](docs/export-schema.md) / [.json](docs/export-schema.json) — what
  `POST /api/sync/consolidated/raw` receives. Read by RaidTools' `ConsolidatedJsonSyncAdapter`.

`docs/clan-export-schema.md` / `.json` are **deleted** (schema 13), with the clan export they
described. Old links to them in the changelog rows are left unlinked on purpose.

Alongside them, two **static game metadata** files — not payloads, since every account sees the same
tables, but they ship here because the payload's ids are opaque without them:
[docs/role-names.json](docs/role-names.json) for `heroes[].roleId`, and
[docs/artifact-enums.json](docs/artifact-enums.json) for the artifact `kindId` (slot), `statKindId`,
`rankId`, `rarityId` and `setKindId`. Same rule applies: if one of those enums gains a member, that
file and the schema pair change together. Each table in the artifact file states how it was
corroborated, and the weaker ones say so — two of them replaced tables that were wrong for years.

A third pair runs the **other way** — it is what the uploader *receives*:
[docs/build-certification-schema.md](docs/build-certification-schema.md) / [.json](docs/build-certification-schema.json)
is the response to `GET /api/extractor/offsets/{gameAssemblyHash}`, the memory map for a game build
this release predates. Same maintenance rule.

[docs/raidtools-schema10-migration.md](docs/raidtools-schema10-migration.md) is the consumer-side
migration guide for the schema 9 + 10 artifact changes, written to be handed straight to an agent
working on RaidTools. It is a **summary of the contract, not part of it** — if it ever disagrees with
the schema pair, the schema pair wins and the guide is what needs fixing.

[docs/raidtools-skill-attribution.md](docs/raidtools-skill-attribution.md) is a second note of that
kind: how a consumer attributes an owned copy's `skills[].typeId` to a form **at that copy's own
ascension**. Same standing — a summary, never the contract. It exists because ascension does not only
add skills, it also replaces them (374 of 1,355 champions hold a skill below max ascension that is
gone by max), so the champion catalog's `champions{}.forms[]` — deliberately the max-ascension kit —
attributes 97.4% of owned skills and structurally cannot do better. The catalog's `types[]` carries
every variant and closes it.

**The bundled `exports/champion_index.json` is the SLIM shape: `champions{}` without `types[]`.**
`types[]` is 3.98 MB of the 4.47 MB catalog and nothing here reads it (`LoadChampionCatalog` touches
`champions{}` only), so the installer ships without it and the full catalog stays in
RslCompanionMetadata for consumers that need per-ascension kits. Both come out of one
`ChampionIndexExporter` run so they cannot drift, and slim is a strict subset — dropping the full
file in after a game patch still works, the reverse loses data.

They live in this repo precisely because it is public, so consumers can reference them without access
to the private engine. **Nothing the uploader sends now describes anyone but the signed-in user** —
that became true when the clan roster export was withdrawn, and it is a property worth keeping.

**Any change to an emitted JSON must update that pair's two files in the same commit as the code
change** — a new/renamed/retyped field, a new resource id, or even a changed resource *name*. Bump
`schemaVersion` in the JSON Schema plus the "Schema version" line in the `.md`, add a Changelog row,
and state the consumer impact in the commit message and release tag. The contract is only useful if
it is never behind the code.

Note the payload has two fields the engine's own `ConsolidatedProfile` model does not:
`uploaderVersion` and `gameVersion` are stamped on at serialize time by
`MainForm.SerializeWithProvenance`, so engine-level dumps legitimately lack them (the schema marks
them optional for that reason).

## Config

`appsettings.json` (next to the exe):

| Key | Purpose | Default |
| --- | --- | --- |
| `ApiBaseUrl` | RSL Companion API origin | `https://api.rslcompanion.com` |
| `Endpoints.SyncConsolidated` | Parser sync path for "Update user data" | `/api/sync/consolidated/raw` |
| `Endpoints.BuildCertification` | Memory-map lookup for an uncovered game build | `/api/extractor/offsets` |
| `Endpoints.HandoffExchange` | Redeems the launch URI's one-time code for a Firebase custom token | `/api/extractor/handoff/exchange` |
| `Endpoints.Logout` | Revokes the session server-side, for "sign out everywhere" only | `/api/auth/logout` |

User preferences the app writes back live in `%LOCALAPPDATA%\RslCompanion\settings.json`
([UserSettings.cs](UserSettings.cs)) — *not* in `appsettings.json`, which is install-time config next
to the exe and part of the installer's signed file set.

## Build & release

```
dotnet build RslCompanionUploader.csproj
```

Installer: `installer/setup.iss` (Inno Setup 6). Releases: push a `v*` tag —
`.github/workflows/release.yml` builds (with submodule), compiles the installer, and attaches
it + SHA-256 checksum to a GitHub Release. CI needs the `EXTRACTION_REPO_TOKEN` secret (PAT
with read access to the private extraction repo) to fetch the submodule.
