# RslCompanionUploader — Claude Code scope

## What this app is

A signed-in Windows (WinForms, .NET 10) companion for **rslcompanion.com** ("RSL Companion
Account Data Extractor"). It authenticates against the site's Firebase project
(`raid-account-manager`), lists the accounts linked to the signed-in user, and sends
Raid: Shadow Legends account data to the RSL Companion API.

The app's job is to read the live Raid process memory via the **private extraction engine** (git
submodule at `extraction/`, repo `RslCompanionExtraction`) and POST what it finds. Both payloads are
self-identifying (each carries the in-game `accountId`); the server routes by that id. Extraction
runs off the UI thread; engine console output is redirected into the activity log.

There are **two exports, split by cost** — see [docs/clan-export-schema.md](docs/clan-export-schema.md)
for the full rationale:

| Action | Payload | Endpoint | Cost |
| --- | --- | --- | --- |
| **Update user data** | consolidated profile (resources, champions, guardians, the whole artifact vault, `clanId`) | `/api/sync/consolidated/raw` | ~5 s first run of a game session, ~1 s after |
| **Export clan** | clan record + roster with member names | `/api/sync/clan/raw` | 18–31 s cold, ~7 s warm |

The split exists because nothing clan-related except the *id* hangs off the game's account object:
the clan record and the player-name cache are only findable by scanning the whole process. Folding
that into the routine export made it seven times slower. **Do not move clan name/roster work back
onto the consolidated path.**

**Artifacts are a deliberate exception to that reasoning.** The vault is also found by scanning
(nothing under the account object reaches it), but it stays on the consolidated path: it is the
account's own data, it costs ~3 s rather than ~25, and the cost is a one-off per game session — the
instance address is cached for the session and the class RVA ships in the offset catalog. Gear and
accessories are kept apart in the *payload* (`artifacts[]` / `accessories[]`) rather than in separate
uploads, so one call still snapshots the account while a consumer can load either alone.

## UI: native shell + WebView2 page

The **entire UI is one full-window WebView2 page** ([Forms/AppShell.cs](Forms/AppShell.cs)), styled to
match rslcompanion.com. [Forms/MainForm.cs](Forms/MainForm.cs) is a thin native shell: title bar + a
File/Help `MenuStrip`, hosting `AppShell` docked fill. `MainForm` stays the backend — it runs the
status poll, extraction and API calls, and **pushes a single view-state** into the shell (signed-in
flag, user, connection status, update/uncovered-build banners, accounts, busy + which action is
busy, frontend URL). The page posts back seven actions: `export`, `exportClan`, `signIn`, `signOut`,
`refresh`, `reportBuild`, `openUrl`. Check for updates, recalibrate and about stay native menu items
calling straight into `MainForm` — no bridge needed.

The app opens its main window **before authenticating** (like Postman): [Program.cs](Program.cs) tries
to restore a session but never blocks on it. When there is no session the window opens signed-out —
the top bar shows a **Sign In** button and the body a sign-in prompt; the game-status pill still works.
Clicking Sign In runs the browser handoff via the [BrowserSignInForm](Forms/BrowserSignInForm.cs) splash
and, on success, `MainForm.EnterSignedInAsync` loads accounts and enables export. Sign out drops back to
the signed-out state in place (no process restart).

The page is a top bar (brand + connection pill + Sign In button / identity), optional banners, the
accounts grid, an "Open RSL Helper" bar (opens `MainForm.HelperUrl()` via `openUrl`), and a
collapsible activity console.

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
the two game-reading actions.** Their target is never chosen by the user: both read the live process,
so the only account they could ever act on is the one being played. A new (unimported) account's tile
offers "Add this game account" only; a matched existing one offers "Update user data" **and** "Export
clan". When no game is reachable, no tile carries buttons at all. Clan export is withheld until the
account is imported so the clan payload is always filed against an account the server knows.

Because the clan export runs for up to a minute, `SetBusy` takes a *kind* (`"export"` / `"clan"`) and
the page shows progress on the button that is running — an elapsed-seconds count and an indeterminate
bar, plus the engine's own phase lines in the activity log. A shared spinner on a 30-second operation
is indistinguishable from a hang; don't regress it to one.

Because the whole UI is WebView2, the runtime (preinstalled on Win11) is now load-bearing; if it's
missing, `AppShell` shows a plain fallback label instead of the page.

**An uncovered game build resolves itself; it no longer asks the user to file a report.** When Raid
updates ahead of a release, the app asks RSL Companion for a published memory map
(`Endpoints.BuildCertification`) and installs it into the user's own
`calibrated-offsets.json`, and only falls back to the ~35–50 s local calibration scan when the server
has none. The lookup is **opt-in** — a TaskDialog with a "Check automatically from now on"
verification box, one offer per build per session, the tick persisted to
`settings.json`. The `reportBuild` bridge action kept its name but now drives that flow (the old
GitHub-issue prompt is gone), and the banner clears once *any* local map exists, certified or
self-calibrated — `CoveredByShippedCatalog` alone would keep nagging a user who already fixed it.

File-based JSON import (`resources` / `champions`) used to live here but was moved to the
rslcompanion.com metadata tooling — do not reintroduce it in this app.

## Public repo / private engine split

This repo is **public**; the extraction engine is **private** and optional at build time:

- With the submodule present (`git submodule update --init`, needs access to the private repo),
  the build defines `EXTRACTION` and the "Update user data" / "Export clan" buttons work.
- Without it, the project still builds and runs — extraction code paths are `#if EXTRACTION`
  and the button is hidden. With no engine there is nothing left to do but sign in and view the
  (empty) accounts pane.
- Engine internals, data files (`offsets_cache.json`, `exports/champion_index.json`,
  `resource-allowlist.json`), limitations, and vendoring rules are documented **in the
  private repo's CLAUDE.md** — do not re-document them here.

## Browser launch (protocol handler)

The app registers `rslcompanion-extractor://` under HKCU on every startup ([ProtocolHandler.cs](ProtocolHandler.cs));
the installer also registers it at install time. rslcompanion.com launches
`rslcompanion-extractor://sync?rt=<firebase refresh token>`; the app exchanges the refresh token
for a session ([Program.cs](Program.cs) `TrySignInFromLaunchUri`) and skips the login screen.

## Export payload contracts — keep them in sync

Two doc pairs, one per endpoint, each prose + JSON Schema 2020-12:

- [docs/export-schema.md](docs/export-schema.md) / [.json](docs/export-schema.json) — what
  `POST /api/sync/consolidated/raw` receives. Read by RaidTools' `ConsolidatedJsonSyncAdapter`.
- [docs/clan-export-schema.md](docs/clan-export-schema.md) / [.json](docs/clan-export-schema.json) —
  what `POST /api/sync/clan/raw` receives. They join on `clanId` ↔ `clan.id`.

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

They live in this repo precisely because it is public, so consumers can reference them without access
to the private engine. The clan pair also carries a **privacy note**: it is the only payload
describing people other than the uploading user (clanmates' ids and display names).

**Any change to an emitted JSON must update that pair's two files in the same commit as the code
change** — a new/renamed/retyped field, a new resource id, or even a changed resource *name*. Bump
`schemaVersion` in the JSON Schema plus the "Schema version" line in the `.md`, add a Changelog row,
and state the consumer impact in the commit message and release tag. The contract is only useful if
it is never behind the code.

Note both payloads have two fields the engine's own `ConsolidatedProfile` / `ClanProfile` models do
not: `uploaderVersion` and `gameVersion` are stamped on at serialize time by
`MainForm.SerializeWithProvenance`, so engine-level dumps legitimately lack them (the schemas mark
them optional for that reason).

## Config

`appsettings.json` (next to the exe):

| Key | Purpose | Default |
| --- | --- | --- |
| `ApiBaseUrl` | RSL Companion API origin | `https://api.rslcompanion.com` |
| `Endpoints.SyncConsolidated` | Parser sync path for "Update user data" | `/api/sync/consolidated/raw` |
| `Endpoints.SyncClan` | Clan sync path for "Export clan" | `/api/sync/clan/raw` |
| `Endpoints.BuildCertification` | Memory-map lookup for an uncovered game build | `/api/extractor/offsets` |

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
