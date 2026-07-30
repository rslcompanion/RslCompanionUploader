# Clan export payload contract

**This file is the contract between the uploader and its consumers (RaidTools / rslcompanion.com).**
It describes exactly what `POST {ApiBaseUrl}/api/sync/clan/raw` receives.

- Machine-readable form: [`clan-export-schema.json`](clan-export-schema.json) (JSON Schema 2020-12).
- This repo is public, so consumers can reference both files without access to the private
  extraction engine.
- **Schema version: 1** — bump `schemaVersion` below and add a Changelog row on every wire change.
- The account snapshot is a **different** payload with its own contract:
  [`export-schema.md`](export-schema.md) / [`.json`](export-schema.json). The two join on
  `clanId` ↔ `clan.id`.

> **Maintenance rule:** any change to the emitted JSON — a new field, a renamed field, a changed type
> — must update this file **and** `clan-export-schema.json` **in the same commit** as the code
> change. See "Changing the contract" at the end.

---

## Transport

| | |
|---|---|
| Method / path | `POST {ApiBaseUrl}/api/sync/clan/raw` (path from `appsettings.json` → `Endpoints.SyncClan`) |
| Default origin | `https://api.rslcompanion.com` |
| Content type | `application/json; charset=utf-8` |
| Auth | `Authorization: Bearer <Firebase ID token>` |
| Routing | **The payload is self-identifying.** It carries the in-game `accountId` of the account whose clan this is; the server routes by that, not by a selected account. |

A POST is one complete snapshot of one clan, as seen from one account. It is a full replace of that
clan's roster, not a delta.

---

## Why this is a separate export

Worth knowing before designing the consumer, because it determines how often this data arrives and
how stale it will be relative to the account snapshot.

The account export reads everything through pointer chains off the game's account object and takes
about **4 seconds**. Nothing clan-related except the clan *id* hangs off that object: the clan record
and the player-name cache have to be found by scanning the entire game process, which measures
**18–31 s cold and ~7 s warm** (warm = a second run inside the same game session).

So the two were split:

| | Account export | Clan export |
|---|---|---|
| Endpoint | `/api/sync/consolidated/raw` | `/api/sync/clan/raw` |
| Cost | ~4 s | 18–31 s |
| Trigger | "Update user data" | "Export clan" — a separate button |
| Clan data | `clanId` only | the full record + roster |

**Practical consequence: a clan payload may never arrive at all, and when it does it is likely to be
much older than the account snapshots around it.** A user can sync their account daily and export
their clan once. Consumers must treat clan detail as independently-aged data joined on the id, never
as something guaranteed to accompany an account sync.

---

## Top-level envelope

```jsonc
{
  "accountId": "95604564",                 // string — in-game account id; the routing key
  "timestamp": "2026-07-30T09:12:44.317Z", // string — ISO-8601 UTC, when the snapshot was taken
  "clan":      { … } | null,               // object|null — see below
  "uploaderVersion": "1.5.5",              // string — added by the app, not the engine
  "gameVersion":     "11.67.0"             // string|null — live Raid build; null if unreadable
}
```

- `accountId` is the **reporting** account — the player whose game was read. It is always also
  present in `clan.members[].id`, since the clan was selected by finding the roster that contains it.
- `uploaderVersion` / `gameVersion` are stamped by the desktop app
  ([`MainForm.SerializeWithProvenance`](../Forms/MainForm.cs)), so they exist on the wire but **not**
  on the engine's own `ClanProfile` model. A payload captured straight from the engine (a probe dump)
  will not have them — treat both as optional.

---

## `clan` — object or `null`

The account's own clan (the game calls it an *Alliance*) and its roster.

```jsonc
{
  "id": 3734897,                  // int64 — clan id; stable, and the key to join accounts by clan
  "name": "Unimatrix Zero One",   // string — display name
  "abbreviation": "Boȑg",         // string — clan tag; may be empty
  "level": 19,                    // int32
  "leaderId": 10000001,           // int64 — always one of members[].id
  "membersLimit": 30,             // int32 — capacity, NOT the current member count
  "members": [
    { "id": 10000001, "name": "ExampleLeader" },    // int64 id + display name
    { "id": 95604564, "name": "Magikwolf" },        // the reporting account, always present
    { "id": 10000002, "name": "" }                  // cached profile not loaded — id only
  ]
}
```

- **`clan` is `null` when the account is in no clan**, and also when the game client hasn't cached
  the clan record yet (the in-game Clan screen loads it lazily). Both are normal. The desktop app
  does not POST in this case, so a `null` on the wire should be rare — but accept it rather than
  rejecting the request.
- **Only the reporting account's own clan is ever sent.** The game client caches other clans too
  (CvC opponents, clan-browser results); the engine picks the one whose roster contains
  `accountId`, so a stranger's clan can never arrive here.
- **`membersLimit` is capacity, not headcount.** Use `members.length` for the current size.

### `members[]`

- **`id` is the same id space as the top-level `accountId`** — that is what makes a clanmate lookup
  possible. A member id may or may not correspond to an account the server has ever seen; most
  won't. Do not assume a member row can be foreign-keyed to a registered account.
- **`name` may be an empty string.** The ids always come through; the names come from a separate
  client-side profile cache that fills lazily, so a member the client hasn't loaded a profile for
  arrives id-only. On the reporting account's own clan this is rare — expect names for all members
  in practice — but handle the empty case, and prefer an existing stored name over an empty one.
- **Order is the game's own** (not sorted, not by rank). Do not depend on it.
- Ids are unique within `members[]`.
- Per-member stats (power, contribution, rank, join date, activity) exist in the game client and are
  deliberately **not** exported.

> ### Privacy note for consumers
>
> **This payload contains data about people other than the uploading user** — their in-game account
> ids and display names — collected from one member's game client. Nothing else the uploader sends
> does. It is the account snapshot's privacy profile inverted, and it deserves handling to match:
> scope access to the clan, do not expose a member's id or name outside it, and delete roster rows
> when the clan record is deleted rather than leaving orphans keyed by player id.

---

## Consumer guidance

1. **Route on the top-level `accountId`**, exactly as with the account export.
2. **Replace, don't merge, the roster.** `members[]` is a complete snapshot of the clan at
   `timestamp`. A member absent from a newer payload has left (or was kicked); a member absent from
   an *older* one has not.
3. **Last-write-wins by `timestamp`, not by arrival.** Two members of one clan can both export, and
   nothing orders their requests. Compare `timestamp` before overwriting a stored roster, or a
   stale export will resurrect a departed member.
4. **Multiple accounts legitimately report the same `clan.id`.** That is the point of the id — it is
   the join key across accounts. Key clan storage by `clan.id`, not by the reporting `accountId`.
5. **Do not derive clan membership changes from the account export.** Its `clanId` is `null` both
   for "no clan" and for "could not read", and it carries no roster to diff.
6. **Ignore unknown keys.** Additive changes here are expected and will not bump a major version.
7. **An empty `members[]` should not happen** (the clan is selected *by* the reporting account being
   in its roster), but it is not worth a 400 — store nothing and accept.

---

## Changing the contract

When the emitted JSON changes:

1. Update this file **and** [`clan-export-schema.json`](clan-export-schema.json) in the same commit
   as the code.
2. Bump `schemaVersion` in the JSON Schema and the "Schema version" line at the top of this file.
3. Add a Changelog row below.
4. Call out the consumer impact in the commit message and the release tag.

### Changelog

| Schema | Uploader | Date | Change |
|---:|---|---|---|
| 1 | v1.5.5 | 2026-07-30 | Baseline. New endpoint `POST /api/sync/clan/raw` and new payload: `accountId`, `timestamp`, `clan` (`id`, `name`, `abbreviation`, `level`, `leaderId`, `membersLimit`, `members[]` of `{id, name}`), plus the app-stamped `uploaderVersion` / `gameVersion`. Carries the roster that briefly lived on the account export as `clan` (never released — see `export-schema.md` schema 4 → 5). |
