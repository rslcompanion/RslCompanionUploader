# RslCompanionUploader — Claude Code scope

## What this app is

A signed-in Windows (WinForms, .NET 10) companion for **rslcompanion.com** ("RSL Companion
Account Data Extractor"). It authenticates against the site's Firebase project
(`raid-account-manager`), lists the accounts linked to the signed-in user, and sends
Raid: Shadow Legends account data to the RSL Companion API.

Two ways data gets in:

1. **File upload** — user picks a JSON file; the app POSTs the `resources` / `champions` slice
   to the per-account import endpoints. See [Api/RslCompanionApiClient.cs](Api/RslCompanionApiClient.cs).
2. **Sync from game** — reads the live Raid process memory via the **private extraction engine**
   (git submodule at `extraction/`, repo `RslCompanionExtraction`), builds a consolidated
   profile, and POSTs it to `POST {ApiBaseUrl}/api/sync/consolidated/raw`. The profile is
   self-identifying (carries the in-game `accountId`); the server routes it by that id.
   Extraction runs off the UI thread; engine console output is redirected into the activity log.

## Public repo / private engine split

This repo is **public**; the extraction engine is **private** and optional at build time:

- With the submodule present (`git submodule update --init`, needs access to the private repo),
  the build defines `EXTRACTION` and the "Sync from game" button works.
- Without it, the project still builds and runs — extraction code paths are `#if EXTRACTION`
  and the button is hidden. File upload always works.
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
| `Endpoints.SyncConsolidated` | Parser sync path for "Sync from game" | `/api/sync/consolidated/raw` |
| `Endpoints.UploadResources` / `UploadChampions` | Per-account file-upload paths | `/api/profile-import/...` |

## Build & release

```
dotnet build RslCompanionUploader.csproj
```

Installer: `installer/setup.iss` (Inno Setup 6). Releases: push a `v*` tag —
`.github/workflows/release.yml` builds (with submodule), compiles the installer, and attaches
it + SHA-256 checksum to a GitHub Release. CI needs the `EXTRACTION_REPO_TOKEN` secret (PAT
with read access to the private extraction repo) to fetch the submodule.
