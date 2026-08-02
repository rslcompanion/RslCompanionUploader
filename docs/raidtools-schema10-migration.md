# Implement export schema 10 (full artifact vault) in RaidTools

Consumer-side migration guide for the schema 9 + 10 changes, written to be handed straight to an
agent: paste it into a Claude Code session opened on the **RaidTools** repo.

It is a *summary*. [`export-schema.md`](export-schema.md) / [`.json`](export-schema.json) and
[`artifact-enums.json`](artifact-enums.json) are the contract; where this file disagrees with them,
they win, and this one is stale and should be fixed.

---

The RSL Companion uploader shipped **v1.6.0**, which changes the consolidated sync payload in a
**breaking** way. `ConsolidatedJsonSyncAdapter` is the reader that has to track it. Implement the
ingest side, the storage, and the read APIs.

Two changelog rows apply, both against the same v1.6.0 binary:

- **Schema 9** — the payload shape changed (this is the breaking part).
- **Schema 10** — no wire change at all, but the *meaning* of `setKindId` changed: the set names
  published before it were wrong, and the field turns out to carry two id spaces. If you are building
  this fresh you can read them as one migration; if any set labels are already stored anywhere, they
  must be re-derived.

## Read the contract first

It lives in the uploader's public repo — fetch these rather than working from this prompt alone,
because they are the authority and this prompt is a summary:

- `docs/export-schema.md` — prose contract; the artifacts section and changelog rows **9** and **10**.
- `docs/export-schema.json` — JSON Schema 2020-12; `$defs/artifact` and `$defs/artifactBonus`.
- `docs/artifact-enums.json` — id → name tables: `kinds`, `statKinds`, `ranks`, `rarities`, `sets`
  and `accessoryEffects`.

<https://github.com/rslcompanion/RslCompanionUploader/tree/main/docs>

## What changed

`artifacts[]` used to be ~1,858 records: the *equipped* artifacts only, all nine slots mixed
together, with `setKindId` / `rankId` / `rarityId` / `primaryStatId` / `level` hard-zero on every
record because artifact stats were thought to be unreadable.

Now:

- **`artifacts[]` is gear only** — `kindId` 1–6 — and lists everything the account **owns**, equipped
  or vaulted. ~2,850 records on a mature account.
- **`accessories[]` is new** — `kindId` 7–9 (ring, cloak, banner), same record shape. ~2,969 records.
- **Stats are real.** `setKindId`, `rankId`, `rarityId`, `level`, `ascendLevel` plus `primaryBonus`,
  `secondaryBonuses[]` (0–4) and `ascendBonus`.
- **`heroInstanceId` → `equippedByHeroId`**, and it is **nullable**: `null` means the piece is in the
  vault. Most records are null.
- **`primaryStatId` is gone** — superseded by `primaryBonus.statKindId` + its value.

## Six things that will bite if you skim

1. **Reading `artifacts[]` for accessories now silently returns none.** Every code path that touched
   artifacts must decide whether it wants gear, accessories, or both, and say so. This fails quietly,
   not loudly — it is the most likely regression.
2. **`artifacts.length` is an owned count, not an equipped count.** Anything that treated "record
   exists" as "equipped" must switch to `equippedByHeroId != null`.
3. **The schema-7 rule "a `0` stat means unknown" is reversed.** Zeroes are real values now. If any
   ingest code gates writes on `value != 0` to avoid persisting the old placeholder zeroes, that code
   will now drop legitimate data — `level: 0` (an un-upgraded drop) and `setKindId: 0` (no set) are
   both common and both true.
4. **The slot ids were documented wrong in schemas 7–8.** Correct order is the game's own:
   `1 Helmet, 2 Chest, 3 Gloves, 4 Boots, 5 Weapon, 6 Shield, 7 Ring, 8 Cloak (UI: "Amulet"),
   9 Banner`. The old docs said 1 Weapon / 2 Helmet / 3 Shield / 4 Gauntlets / 5 Chestplate. **Search
   the codebase for any hard-coded slot labels or slot-ordering constants and fix them** — they are
   mislabelling slots against production data today, independent of this migration.
5. **Bonus values: `isAbsolute` decides how `value` reads.** `true` → a flat amount
   (`{statKindId: 2, value: 240}` = +240 ATK). `false` → a **fraction**
   (`{statKindId: 2, value: 0.18}` = +18%, *not* 0.18%). A relative value can legitimately exceed
   `1.0` — `1.6` = +160% C.DMG on a maxed banner — so do not validate it as a 0–1 probability, and do
   not multiply by 100 twice.
6. **`setKindId` is two id spaces in one field, and the old set names were wrong** (schema 10).
   `0`–`66` are **sets** (`0` = no set, a real and common value). `1000`–`1004` are **accessory
   effects** — one item's own effect, no piece count, no set bonus — on ~2.6% of accessories and on
   no gear. Group "by set" across the 1000s and you invent five sets that don't exist; filter or
   branch on the range. Separately, every set name published before 2026-08-02 was wrong from id 4
   onward (`4 Critical Rate / 5 Accuracy / 6 Speed` where the game says `4 Speed / 5 Critical Rate /
   6 Crit Damage`; 47 was published as Stone Skin, which is actually 48). **Re-derive any stored set
   label from the current `artifact-enums.json`; do not migrate old ones forward.** And join on the
   id, never the name — `Cleansing`, `Bloodshield`, `Reaction` and `Revenge` exist in *both* spaces.

## Storage

Two collections, gear and accessories, keyed `(accountId, artifactId)`.

- `artifactId` is unique across **both** arrays and **stable for the life of the piece** — it
  survives upgrades and re-equips — so it is the natural primary key, not a surrogate.
- Index `equippedByHeroId`; it is the only field linking artifacts to champions, and every "what is
  this champion wearing" query goes through it.
- Index `kindId` and `setKindId`; those are the two filters a gear UI leads with.
- `revision` changes when the game changes the record. Persist it: it lets a later sync diff instead
  of rewriting ~5,800 rows every time.

**A snapshot is a full replace, not a delta.** A piece the player sold is communicated by *absence* —
there is no tombstone. Reconcile by replacing the account's whole set (or by diffing and deleting
what is missing); an upsert-only ingest will accumulate sold artifacts forever.

## Read APIs to expose

The uploader sends one payload per account, but the split exists so reads can be independent — this
is the point of the change, not an implementation detail:

| Endpoint | Notes |
|---|---|
| `GET /accounts/{id}/artifacts` | Gear. The common case. |
| `GET /accounts/{id}/accessories` | Accessories. Callers rarely want both at once. |
| `…?equipped=true\|false` | The equipped/vault split is the first axis every UI filters on. |
| `GET /accounts/{id}/artifacts/{artifactId}` | Single piece. Ids are unique across both categories, so either route on `kindId` or fall back to the other collection on a miss. |
| `GET /accounts/{id}/heroes/{instanceId}/artifacts` | A champion's loadout, via `equippedByHeroId`. |

Paginate the unfiltered collection reads — ~2,800 and ~2,970 rows are normal, and both grow.

## Id naming

`artifact-enums.json` carries `kinds` (slot), `statKinds`, `ranks`, `rarities`, `sets` and
`accessoryEffects`. Each table states how it was corroborated; carry these caveats into whatever you
build:

- **Sets (0–66)** are resolved from the game's own localized strings, each name confirmed against a
  matching description. Two entries are qualified in the file: id **9 (`Lifesteal`) is inferred**
  from its description and position rather than read directly, and ids **55/56 exist but are
  unnamed** (the client ships untranslated placeholders). Handle a missing name as "unknown set",
  not as "no set" — `0` is the no-set value.
- **Accessory effects (1000–1004)** are `Refresh`, `Cleansing`, `Bloodshield`, `Reaction`, `Revenge`,
  each with its effect text in the file. They are not sets; see trap 6.
- **Rarity names** have weaker provenance than the rest (no game enum was found under a discoverable
  name; the labels are corroborated only by the observed 1–6 range). Ids are authoritative, names are
  a convenience.
- **Set *bonuses* and piece counts are not in this file.** If you need "2-set: HP +15%", that catalog
  is separate and joins on the same id.

## Suggested order of work

1. Update the DTOs and `ConsolidatedJsonSyncAdapter` to accept the new shape; make an unknown/extra
   key a no-op, not a failure (the contract adds fields without a major bump).
2. Grep for existing artifact consumers and triage each against traps 1–4 above.
3. Migration for stored artifacts: the old rows carry placeholder zeroes that are indistinguishable
   from real zeroes once the new data lands, and any stored set label is from the wrong table
   (trap 6). Cleanest is to drop the old artifact data and let the next sync repopulate it — it was
   ids and nothing else.
4. Storage + indexes, then the read endpoints.
5. Verify against a real payload: totals and the unequipped split must both reconcile against the
   account's in-game counters. On the reference account: gear 2,851 total / 1,963 unequipped,
   accessories 2,969 / 2,000 (a snapshot from 2026-08-02 — these move with play, so compare against
   counters read at the same moment, not against these literals). Matching the totals alone is a weak test — the previous export matched
   a plausible count while missing two-thirds of the data.
6. Spot-check one set label end to end against the game UI (e.g. a piece with `setKindId: 47` should
   read **Protection**, not Stone Skin). That single check catches a stale copy of the old table
   wherever it might still be lurking.
