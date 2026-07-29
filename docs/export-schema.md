# Export payload contract

**This file is the contract between the uploader and its consumers (RaidTools / rslcompanion.com).**
It describes exactly what `POST {ApiBaseUrl}/api/sync/consolidated/raw` receives.

- Machine-readable form: [`export-schema.json`](export-schema.json) (JSON Schema 2020-12).
- This repo is public, so consumers can reference both files without access to the private
  extraction engine.
- **Schema version: 3** — bump `schemaVersion` below and add a Changelog row on every wire change.

> **Maintenance rule:** any change to the emitted JSON — a new field, a renamed field, a changed type,
> a new resource id, a changed resource *name* — must update this file **and** `export-schema.json`
> **in the same commit** as the code change. See "Changing the contract" at the end.

---

## Transport

| | |
|---|---|
| Method / path | `POST {ApiBaseUrl}/api/sync/consolidated/raw` (path from `appsettings.json` → `Endpoints.SyncConsolidated`) |
| Default origin | `https://api.rslcompanion.com` |
| Content type | `application/json; charset=utf-8` |
| Auth | `Authorization: Bearer <Firebase ID token>` |
| Routing | **The payload is self-identifying.** It carries the in-game `accountId`; the server routes by that, not by a selected account. |

A single POST is one complete snapshot of one game account. It is a full replace, not a delta —
there is no partial/patch mode.

---

## Top-level envelope

```jsonc
{
  "accountId":  "95604564",                 // string — in-game account id; the routing key
  "account":    { … },                      // object — see below
  "timestamp":  "2026-07-29T07:44:24.990Z", // string — ISO-8601 UTC, when the snapshot was taken
  "resources":  [ … ],                      // array — always the full allowlist, see below
  "heroes":     [ … ],                      // array
  "artifacts":  [ … ],                      // array — often EMPTY, see caveat
  "factionGuardians": [ … ],                // array
  "uploaderVersion": "1.5.4",               // string — added by the app, not the engine
  "gameVersion":     "11.67.0"              // string|null — live Raid build; null if unreadable
}
```

`uploaderVersion` / `gameVersion` are stamped by the desktop app
([`MainForm.SerializeWithProvenance`](../Forms/MainForm.cs)), so they exist on the wire but **not**
on the engine's own `ConsolidatedProfile` model. Consumers reading a payload captured straight from
the engine (e.g. a probe dump) will not see them — treat both as optional.

### `account`

```jsonc
{
  "name": "Magikwolf", "level": 100, "accountId": "95604564",
  "arenaPoints": 682, "liveArenaPoints": 0,
  "arenaLeague": 0, "liveArenaLeague": 0, "arena3x3League": 0
}
```

`account.accountId` duplicates the top-level `accountId`; they are always equal.

---

## `resources[]`

```jsonc
{ "id": 1111, "name": "Mortal Soul Coin", "quantity": 15075 }
```

`quantity` is a 64-bit integer (Silver exceeds `int32`).

**Join on `id`. Never on `name`.** Names are human-readable labels that track in-game renames — three
of them changed in v1.5.4 alone. `id` is stable.

**The array always contains every allowlisted id**, including ones the account holds none of, which
are emitted with `quantity: 0`. So a missing id means "not in the allowlist", never "zero owned" —
and the array length only changes when the allowlist itself changes. Current allowlist: **49 ids**
(see `extraction/resource-allowlist.json` / `.md` in the engine for the full annotated table).

### Soul economy (corrected in v1.5.4 — read this if you consume these)

| id | name | notes |
|---:|---|---|
| 1111 | Mortal Soul Coin | **was** `Silver Soul Coin`, and exported a stale value |
| 1112 | Immortal Soul Coin | **was** `Gold Soul Coin`, and exported a stale value |
| 1113 | Eternal Soul Coin | **was** `Immortal Essence`, and exported a stale value |
| 1121 | Immortal Soul Essence | **new in v1.5.4** — previously not exported at all |
| 1122 | Eternal Soul Essence | **new in v1.5.4** — previously not exported at all |
| 10202 | Prism Crystals | |
| 10205 | Eternal Essence | legacy; still emitted, semantics unverified |
| 12001 / 12002 / 12003 | Mortal / Immortal / Eternal Soulstone | unchanged, were always correct |

Ids **10101, 10102, 10104, 10105, 10201, 10204** belong to a *defunct earlier* soul system. They are
**not** in the allowlist and are never emitted. They still exist in game memory holding stale values,
and mapping them onto the ids above is precisely the bug v1.5.4 fixed — so do not reintroduce that
mapping consumer-side either.

---

## `heroes[]`

```jsonc
{
  "name": "Achak the Wendarin",   // string|null — null when the champion index can't resolve it
  "instanceId": 52430,            // int64 — unique per owned copy; the join key for artifacts/guardians
  "baseTypeId": 5540,             // int32 — champion type, ascension digit stripped
  "factionId": 9,                 // int32 — 0 when unresolved; same id space as factionGuardians[].factionId
  "stars": 6, "ascensionLevel": 6, "level": 60,
  "experience": 0, "fullExperience": 3423557,
  "empowerLevel": 2,
  "locked": true, "inStorage": false, "inBathhouse": false,
  "awakeningLevel": 4,            // int32 0–6 (the game calls Awakening "DoubleAscend" internally)
  "blessingChosen": true,         // bool — a blessing is equipped; only ever true when awakeningLevel > 0
  "masteries": { … },             // object, always present — see below
  "isFactionGuardian": false      // bool — this copy is placed in an Academy guardian slot
}
```

`instanceId` identifies an owned copy; `baseTypeId` identifies the champion. Two copies of the same
champion share `baseTypeId` and differ in `instanceId`.

### `heroes[].masteries`

Always present, and **deliberately verbose so consumers never null-check**:

```jsonc
{
  "selected": [500212, 500313, 500324],              // int[] — chosen node ids; [] when none
  "unusedScrolls": { "basic": 0, "advanced": 49, "divine": 0 },
  "totalScrolls":  { "basic": 100, "advanced": 289, "divine": 0 }
}
```

- `selected` is an array (empty, never null).
- Both scroll dicts **always carry all three rarities** — `basic` (White) / `advanced` (Green) /
  `divine` (Red) — defaulting to `0`.
- `unusedScrolls` = currently unspent. `totalScrolls` = unspent **plus** what was already spent on
  `selected` (each node's activation cost, derived from its tier).

**Per-node metadata (name, tree, tier, cost) is intentionally NOT in this payload.** It is constant
game data, not account data. Join `selected` ids against the mastery catalog
(`mastery_index.json`) in the RslCompanionMetadata repo. The node id encodes its own position —
`500·tree·tier·slot`, tier being the tens digit — so tier and scroll rarity are derivable without a
table if needed.

---

## `factionGuardians[]`

```jsonc
{
  "factionId": 1, "rarityId": 4, "slotIndex": 0,
  "heroTypeId": 366,          // champion type WITH ascension digit (366 = base 360 at ascension 6)
  "heroBaseTypeId": 360,      // ascension stripped — THIS is what joins to heroes[].baseTypeId
  "firstHeroInstanceId": 50325,
  "secondHeroInstanceId": 3948,  // either may be null when that half of the slot is empty
  "consumed": false
}
```

A slot fuses up to **two copies** of one champion, hence the First/Second pair.

**Join on `heroBaseTypeId`, not `heroTypeId`.** The game's own field is named `HeroBaseTypeId` but
carries the ascension digit, which is a misnomer; joining the raw value onto `heroes[].baseTypeId`
silently matches only unascended champions. Both are published so consumers don't have to guess.

---

## `artifacts[]` — expect this to be empty

```jsonc
{
  "artifactId": 0, "setKindId": 0, "rankId": 0, "rarityId": 0,
  "kindId": 0, "primaryStatId": 0, "level": 0,
  "heroInstanceId": 0   // omitted when 0
}
```

**Artifact extraction is currently blocked upstream** — artifact *stats* moved to Unity ECS component
storage and the old singleton no longer exists. In practice this array arrives **empty**, and the app
skips artifacts entirely unless the export-artifacts option is on. Treat a populated `artifacts[]` as
optional/best-effort, and never assume non-empty.

---

## Consumer guidance

1. **Join on ids, never names.** `resources[].name`, `heroes[].name` are display labels and do change.
2. **Treat `resources[]` as complete.** Every allowlisted id is present; `0` means zero owned. A
   missing id means the allowlist changed.
3. **Additive changes are expected.** New resource ids and new hero fields get added without a major
   version bump — ignore unknown keys rather than failing.
4. **`artifacts[]` may be empty** (see above). `gameVersion` may be `null`.
5. **`account.accountId` == top-level `accountId`.** Route on the top-level one.
6. Server-side, `ConsolidatedJsonSyncAdapter` in RaidTools is the reader that must track this file.

---

## Changing the contract

When the emitted JSON changes:

1. Update this file **and** [`export-schema.json`](export-schema.json) in the same commit as the code.
2. Bump `schemaVersion` in the JSON Schema and the "Schema version" line at the top of this file.
3. Add a Changelog row below.
4. Call out the consumer impact in the commit message and the release tag.

New resource ids also require the four in-sync edits documented in the engine's `CLAUDE.md`
(`allowlistIds` + `resources` in `resource-allowlist.json`, `DefaultIds` in `ResourceAllowlist.cs`,
and `ResourceName` in `GameMaps.cs`).

### Changelog

| Schema | Uploader | Date | Change |
|---:|---|---|---|
| 3 | v1.5.4 | 2026-07-29 | Soul economy corrected. **New ids `1121` / `1122`** (Immortal / Eternal Soul Essence). **Renamed** `1111` → Mortal Soul Coin, `1112` → Immortal Soul Coin, `1113` → Eternal Soul Coin — values for all three were previously wrong. `resources[]` 47 → 49 entries. No structural change. |
| 2 | v1.5.2 | 2026-07-28 | Added top-level `uploaderVersion` and `gameVersion`. Added resource ids `6500` / `6501` (Rank 1/2 Chicken). |
| 1 | — | — | Baseline: `accountId`, `account`, `timestamp`, `resources`, `heroes`, `artifacts`, `factionGuardians`; `heroes[].masteries` as an object with `selected` / `unusedScrolls` / `totalScrolls`. |
