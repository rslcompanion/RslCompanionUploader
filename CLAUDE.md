# RslCompanionUploader — Claude Code scope

## What this app is

A signed-in Windows (WinForms, .NET 10) companion for **rslcompanion.com** ("RSL Companion
Account Data Extractor"). It authenticates against the site's Firebase project
(`raid-account-manager`), lists the accounts linked to the signed-in user, and sends
Raid: Shadow Legends account data to the RSL Companion API.

The app's single job is **Export account** — reading the live Raid process memory via the
**private extraction engine** (git submodule at `extraction/`, repo `RslCompanionExtraction`),
building a consolidated profile, and POSTing it to `POST {ApiBaseUrl}/api/sync/consolidated/raw`.
The profile is self-identifying (carries the in-game `accountId`); the server routes it by that id.
Extraction runs off the UI thread; engine console output is redirected into the activity log.

## UI: native shell + WebView2 page

The **entire UI is one full-window WebView2 page** ([Forms/AppShell.cs](Forms/AppShell.cs)), styled to
match rslcompanion.com. [Forms/MainForm.cs](Forms/MainForm.cs) is a thin native shell: title bar + a
File/Help `MenuStrip`, hosting `AppShell` docked fill. `MainForm` stays the backend — it runs the
status poll, extraction and API calls, and **pushes a single view-state** into the shell (user,
connection status, update/uncovered-build banners, accounts, busy). The page posts back only three
actions: `export`, `reportBuild`, `openUrl`. Everything else (refresh, sign out, check for updates,
recalibrate, about) is a native menu item calling straight into `MainForm` — no bridge needed.

The page is a top bar (brand + connection pill + identity), optional banners, the accounts grid, a
contextual export button, and a collapsible activity console. Tiles are status, not controls — the
user can't select them. Raid closed shows a red "open Raid" tile, a running-but-unimported account
shows a "new account detected" tile, and the profile matching the running game turns green (all others
keep a black border). The export button's target is chosen automatically — it is the account the
running game is on: "Add current game account…" for a new account, "Update account" for a matched
existing one, and no button when no game is reachable. Clicking it runs the export (which reads the
live game and routes by the in-game id).

Because the whole UI is WebView2, the runtime (preinstalled on Win11) is now load-bearing; if it's
missing, `AppShell` shows a plain fallback label instead of the page.

File-based JSON import (`resources` / `champions`) used to live here but was moved to the
rslcompanion.com metadata tooling — do not reintroduce it in this app.

## Public repo / private engine split

This repo is **public**; the extraction engine is **private** and optional at build time:

- With the submodule present (`git submodule update --init`, needs access to the private repo),
  the build defines `EXTRACTION` and the "Export account" button works.
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

## Config

`appsettings.json` (next to the exe):

| Key | Purpose | Default |
| --- | --- | --- |
| `ApiBaseUrl` | RSL Companion API origin | `https://api.rslcompanion.com` |
| `Endpoints.SyncConsolidated` | Parser sync path for "Export account" | `/api/sync/consolidated/raw` |

## Build & release

```
dotnet build RslCompanionUploader.csproj
```

Installer: `installer/setup.iss` (Inno Setup 6). Releases: push a `v*` tag —
`.github/workflows/release.yml` builds (with submodule), compiles the installer, and attaches
it + SHA-256 checksum to a GitHub Release. CI needs the `EXTRACTION_REPO_TOKEN` secret (PAT
with read access to the private extraction repo) to fetch the submodule.
