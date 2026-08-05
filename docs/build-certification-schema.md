# Build certification response contract

**This file is the contract between the uploader and the RSL Companion API.** It describes exactly
what `GET {ApiBaseUrl}/api/extractor/offsets/{gameAssemblyHash}` must return.

- Machine-readable form: [`build-certification-schema.json`](build-certification-schema.json)
  (JSON Schema 2020-12).
- This repo is public, so the API side can implement against both files without access to the
  private extraction engine.
- **Schema version: 1** — bump `certificationVersion` below and add a Changelog row on every wire
  change.
- **This is the only contract in this folder that runs the other way.** The two export schemas
  describe what the uploader *sends*; this one describes what it *receives*.

> **Maintenance rule:** any change to the response — a new field, a renamed field, a changed type —
> must update this file **and** `build-certification-schema.json` **in the same commit** as the code
> change.

---

## What this is for

The uploader reads the running game's memory through a **memory map** keyed by
SHA-256(`GameAssembly.dll`) — the game build. Every offset in it is a field layout or link-time RVA,
identical for every player on that build, and nothing in it is per-account or per-machine.

A release ships maps for the builds that existed when it was cut. When Raid updates, users who patch
before the next uploader release have no map, and the app falls back to **deriving one locally: a
~35–50 second memory scan, once per game update, per user**.

This endpoint removes that. The map for a new build is derived once, published here, and every user
on that build downloads it in a second instead of scanning. It is the same data either way — the
scan's output is what gets published.

---

## Transport

| | |
|---|---|
| Method / path | `GET {ApiBaseUrl}/api/extractor/offsets/{gameAssemblyHash}` (path from `appsettings.json` → `Endpoints.BuildCertification`) |
| Default origin | `https://api.rslcompanion.com` |
| Query | `uploaderVersion` (required), `gameVersion` (optional display label, e.g. `11.70.0`) |
| Auth | `Authorization: Bearer <Firebase ID token>` |
| Content type | `application/json; charset=utf-8` |

`{gameAssemblyHash}` is the lowercase hex SHA-256 of the running game's `GameAssembly.dll`, 64
characters.

**The hash is the key, not the version string.** The game can ship different binaries under one
version label, and a map applied to the wrong binary produces silently wrong reads rather than a
clean failure — that is the whole reason the catalog is hash-keyed.

### Status codes

| Code | Meaning | Client behaviour |
|---|---|---|
| `200` | A map for this build is published. | Validate and install it. |
| `404` | No map for this build yet. | Falls back to a local calibration scan. **This is the normal answer for a game update we haven't mapped, not an error** — do not log it as one, and do not return 500 for it. |
| `401` / `403` | Session invalid. | Reported, then falls back to local calibration. |
| other | Lookup failed. | Reported, then falls back to local calibration. |

---

## Response body (200)

```json
{
  "certificationVersion": 1,
  "gameAssemblyHash": "9f2c…",
  "gameVersion": "11.70.0",
  "minUploaderVersion": "1.6.2",
  "publishedAt": "2026-08-05T11:02:19Z",
  "offsets": { "gameVersion": "11.70.0", "resolvedAt": "…", "…": 0 }
}
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `certificationVersion` | integer | yes | This contract's version. Bumped on every wire change. |
| `gameAssemblyHash` | string | yes | Echoes the requested hash. **The client re-checks it and rejects a mismatch** — see below. |
| `gameVersion` | string \| null | no | Display label only (`11.70.0`). Never used to select a map. |
| `minUploaderVersion` | string \| null | no | Oldest uploader that can use this map. See "Compatibility" below. |
| `publishedAt` | string (date-time) | no | Informational. |
| `offsets` | object | yes | The map itself — one entry of the engine's build catalog. |

### `offsets` is opaque to this contract

It is one value from the extraction engine's `builds` map, exactly as
`%LOCALAPPDATA%\RslCompanion\calibrated-offsets.json` and the shipped `known-offsets.json` store it.
**Its field set is defined by the private engine and changes as the engine learns new offsets**, so
this schema deliberately does not enumerate it — pinning it here would produce a second definition
free to drift from the one that matters. The API stores and serves it verbatim.

The natural source of a published entry is a user's own `calibrated-offsets.json`: take the entry
under the build's hash and publish it as `offsets`.

**Session-local addresses must not be published.** `userContext_DirectAddress`,
`cachedArtifacts_DirectAddress`, `resources_EntriesDirectAddress`, `items_DictDirectAddress`,
`heroType_KlassAddress`, `allianceNote_KlassAddress` and `userNote_KlassAddress` are live heap
pointers that die when the game restarts. The engine already strips them when it writes a catalog
file, so an entry copied from one is clean; anything else must be stripped before publishing. A stale
pointer does not read as dead — reused heap memory passes a naive validity check about half the time,
which is how a fabricated account id once got reported as connected.

### Compatibility (`minUploaderVersion`)

A map can exist and still not be usable: a newer engine can add offsets an older one has no code to
read, or change what a field means. `minUploaderVersion` is how the server says so.

The client compares it against its own version and, when it is behind, **installs nothing** and tells
the user to update the app. An unparseable version on either side is treated as compatible — refusing
to certify over a malformed string would strand the user on a scan for no gain.

Omit the field when any released uploader can use the map.

---

## What the client does with a 200

1. **Rejects a `gameAssemblyHash` that isn't the one it asked about.** A map for the wrong build
   fails silently rather than loudly, so this is checked rather than trusted.
2. Rejects the response when `minUploaderVersion` is newer than the running app.
3. Writes `offsets` into `%LOCALAPPDATA%\RslCompanion\calibrated-offsets.json` under the build hash —
   the same catalog a local calibration writes, so the map takes effect with no further plumbing.
   A published map **replaces** any existing entry for that hash: unlike two calibrations of one
   build, it is authoritative for every field it carries.
4. Re-probes the game. No app restart.

Nothing else is stored, and no extraction output is sent as part of this call.

---

## Privacy

The request carries **only the build identifiers**: a hash of a file every player of that version
has, the game version label, and the uploader version. No account data, no game state, nothing that
distinguishes one player on a build from another. The Bearer token identifies the user to the API as
it does on every other call.

The check is nonetheless **opt-in**: the app asks before the first lookup, and only a ticked
"Check automatically from now on" makes later lookups silent. That preference lives in
`%LOCALAPPDATA%\RslCompanion\settings.json` (`autoCheckBuildCertification`), never in the installed
program folder.

---

## Changelog

| Version | Date | Change |
|---|---|---|
| 1 | 2026-08-05 | Initial contract. `GET /api/extractor/offsets/{hash}` → `{certificationVersion, gameAssemblyHash, gameVersion, minUploaderVersion, publishedAt, offsets}`; 404 means "not published yet". |
