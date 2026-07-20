# RSL Companion Uploader

A small Windows (WinForms, .NET 10) desktop app that signs in against the same **Firebase
Authentication** backing rslcompanion.com, lists the accounts linked to the signed-in user, and
exports the live Raid: Shadow Legends account to RSL Companion.

## What it does

0. **Browser launch (protocol handler)** — on every startup the app registers the
   `rslcompanion-extractor://` URI scheme under HKCU (no admin needed). rslcompanion.com's
   "Launch Account Data Extractor" button opens
   `rslcompanion-extractor://sync?rt=<firebase refresh token>`; the app exchanges the refresh
   token for a session and skips the login screen entirely.
1. **Sign in** — reuses RaidTools' auth (Firebase project `raid-account-manager`):
   - **Email / password** — native form, via the Firebase REST API (`accounts:signInWithPassword`).
   - **Google / Microsoft / Discord** — a "Sign in with browser" button opens the live
     rslcompanion.com login in an embedded WebView2. All three providers work exactly as on the
     website; the app then reads the resulting Firebase ID token + refresh token out of the page's
     IndexedDB. No extra OAuth client IDs/secrets are needed.
2. **View your accounts** — the UI (a single full-window WebView2 page styled like the site) lists
   accounts from `GET /api/accounts` (Bearer = Firebase ID token) as read-only status tiles: Raid
   closed shows a red "open Raid" prompt, an unimported running account shows a "new account
   detected" tile, and the profile matching the running game turns green.
3. **Export account** *(builds with the private engine only)* — reads the **live Raid: Shadow
   Legends process** in memory via the private extraction engine (submodule at `extraction/`),
   builds a consolidated profile (resources + champions), and POSTs it to
   `/api/sync/consolidated/raw`. Requires the game to be running.

File-based JSON import (resources/champions) previously lived here; it has moved to the
rslcompanion.com metadata tooling.

The Firebase ID token is auto-refreshed (via the refresh token) before it expires on every API call.

## Public repo, private engine

This repository is public; the game-data extraction engine is a **private git submodule** at
`extraction/` (repo `RslCompanionExtraction`). Anyone can build and run this project without it —
the "Export account" button is compiled out (`EXTRACTION` define absent), leaving auth and the
accounts pane. With access to the private repo:

```
git submodule update --init
dotnet build RslCompanionUploader.csproj   # now builds with EXTRACTION enabled
```

## Configuration

`appsettings.json` (copied next to the exe). Change these once the real endpoints exist:

| Key | Purpose | Default |
| --- | --- | --- |
| `ApiBaseUrl` | RaidTools API origin | `https://api.rslcompanion.com` |
| `FrontendUrl` | Site loaded for browser sign-in | `https://rslcompanion.com` |
| `Firebase.ApiKey` / `Firebase.ProjectId` | Firebase web config | `raid-account-manager` |
| `Endpoints.SyncConsolidated` | Export-account sync path | `/api/sync/consolidated/raw` |

## Build & run

```
dotnet build RslCompanionUploader.csproj
dotnet run --project RslCompanionUploader.csproj
```

Requires the WebView2 runtime (preinstalled on Windows 11) for the social-login button only.

## Installer

`installer/setup.iss` (Inno Setup 6) produces a per-user setup.exe — no admin prompt; it installs
to `%LocalAppData%\Programs` and registers the `rslcompanion-extractor://` protocol at install
time so the website launch button works before the app's first run.

```
dotnet publish RslCompanionUploader.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -o publish\win-x64
ISCC.exe installer\setup.iss
```

Output: `installer/dist/RslCompanionAccountDataExtractor-Setup.exe` (self-contained — users do
not need the .NET runtime).

## Releases (GitHub Actions)

`.github/workflows/release.yml` builds everything on a tag push:

```
git tag v1.0.0
git push origin v1.0.0
```

The workflow publishes the app, compiles the installer (version taken from the tag), computes a
SHA-256 checksum, and attaches `RslCompanionAccountDataExtractor-Setup-<version>.exe` +
`.sha256` to a GitHub Release with the hash in the release notes. It can also be run manually
from the Actions tab with a version input.

The stable frontend download URL
(`https://get.rslcompanion.com/RslCompanionAccountDataExtractor-Setup.exe`) should redirect to
the latest release asset. A code-signing step is stubbed in the workflow — enable it once a
signing identity (e.g. Azure Trusted Signing) exists, so the published checksum matches the
signed binary.
