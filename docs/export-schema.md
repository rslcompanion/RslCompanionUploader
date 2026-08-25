# Export payload contract

**This file is the contract between the uploader and its consumers (RaidTools / rslcompanion.com).**
It describes exactly what `POST {ApiBaseUrl}/api/sync/consolidated/raw` receives.

- Machine-readable form: [`export-schema.json`](export-schema.json) (JSON Schema 2020-12).
- This repo is public, so consumers can reference both files without access to the private
  extraction engine.
- **Schema version: 18** — bump `schemaVersion` below and add a Changelog row on every wire change.
- **This is now the only payload the uploader sends.** The separate clan export that used to carry a
  clan record and member roster is gone — see `clanId` below and Changelog 13.
- Champion **role** ids are named in [`role-names.json`](role-names.json), artifact slot / stat /
  rank / set ids in [`artifact-enums.json`](artifact-enums.json), and relic socket shapes plus the
  relic upgrade currencies in [`relic-enums.json`](relic-enums.json) — static game metadata, not
  account data (see `champions[].roleId`, `artifacts[]` and `relics[]` below).
- `champions[].baseStats` uses the **same `statKindId` space** as the artifact bonuses, so no extra
  table is needed to read it. The catalog it is computed from (`hero_base_stats.json`) ships with the
  uploader rather than here, and a consumer needs it only for champions the player does *not* own —
  see `champions[].baseStats` below.

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
  "champions":  [ … ],                      // array — the roster; was "heroes" before schema 16
  "artifacts":  [ … ],                      // array — ALL gear owned (kindId 1-6), with stats
  "accessories": [ … ],                     // array — ALL rings/cloaks/banners (kindId 7-9)
  "relics":     [ … ],                      // array — ALL relics owned, with their gemstone sockets
  "gemstones":  [ … ],                      // array — ALL gemstones owned, socketed or not
  "factionGuardians": [ … ],                // array
  "statBreakdownSources": [ … ],            // array or ABSENT — which statBreakdown columns are real
  "affinityBonuses": [ … ],                 // array or ABSENT — Great Hall ▸ Affinity Bonuses
  "areaBonuses": [ … ],                     // array or ABSENT — Great Hall ▸ Area Bonuses
  "clanId":     20000001 | null,            // int64|null — the account's clan; null when in none
  "clanName":   "Bastion" | null,           // string|null — display only; null is "says nothing"
  "baseStatsCatalog": { … },                // object or ABSENT — provenance of champions[].baseStats
  "uploaderVersion": "1.5.9",               // string — added by the app, not the engine
  "gameVersion":     "11.67.0"              // string|null — live Raid build; null if unreadable
}
```

`uploaderVersion` / `gameVersion` are stamped by the desktop app
([`MainForm.SerializeWithProvenance`](../Forms/MainForm.cs)), so they exist on the wire but **not**
on the engine's own `ConsolidatedProfile` model. Consumers reading a payload captured straight from
the engine (e.g. a probe dump) will not see them — treat both as optional.

### `baseStatsCatalog` — which catalog the computed stats came from

```jsonc
"baseStatsCatalog": { "generatedAt": "2026-08-20T18:12:14Z", "gameVersion": "11.71.0" }
```

Top level rather than per hero, because one export uses exactly one catalog. **Absent when no catalog
was loaded** — in which case no hero carries `baseStats` either.

> ⚠️ **This is not the top-level `gameVersion`, and the two come apart in the ordinary case.**
> `gameVersion` is the build the export **ran against**; this is the build the numbers were
> **computed from**. A user who updates Raid before a refreshed catalog ships exports with
> `gameVersion: "11.72.0"` and stats derived from an 11.71.0 catalog — **a payload that looks current
> and is not.**

It exists because a computed `baseStats` block is a *snapshot*, and without this field a stale one is
indistinguishable from a fresh one. Plarium rebalances base stats; when that happens every block
already stored server-side is wrong until each user re-exports, and nothing else in the payload says
so. Two cuts are told apart by `generatedAt` — that is the catalog's version identity.

**Consumer rule: a `baseStats` block whose catalog `gameVersion` trails the payload's `gameVersion`
is _suspect, not wrong_.** Most rebalances touch few champions, so flag it for re-sync — surface it,
schedule a refresh, prefer a newer payload — but do **not** discard the numbers. Discarding trades a
small, bounded error for no data at all.

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
and the array length only changes when the allowlist itself changes. Current allowlist: **55 ids**
(see `extraction/resource-allowlist.json` / `.md` in the engine for the full annotated table).

### Relic economy (new in schema 11 — previously dropped entirely)

| id | name | notes |
|---:|---|---|
| 4000 | Starstone | levels a relic up; `relics[].level` steps by 3 |
| 19001–19005 | Rank 1–5 Basalt | Rank N Basalt raises a rank-N relic to rank N+1 |

**These six were never exported before schema 11**, not because accounts held none but because the
allowlist is *exclusive* and none of them was on it. **An account's history for these ids therefore
begins at schema 11** — do not read their absence in an older snapshot as a zero balance. This is
the third occurrence of the same defect (Rank 1/2 Chickens, then the Immortal/Eternal Soul Essences),
which is why the allowlist file now records how each addition was verified.

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

## `champions[]`

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
  "blessingId": 1202,             // int32 — equipped blessing id (0 = none); only ever non-zero when awakeningLevel > 0
  "roleId": 0,                    // int32 0–5 or NULL — champion role; null, never 0, when unresolved
  "baseStats": {                  // object or ABSENT — this copy's Basic Stats, keyed by statKindId
    "1": 5100, "2": 426, "3": 367,  //   HP / ATK / DEF — scaled by this copy's rank and level
    "4": 103, "5": 30, "6": 0,      //   SPD / RES / ACC — rank- and level-invariant
    "7": 15, "8": 50, "9": 50, "10": 0
  },
  "skills": [ … ],                // array, always present — see below
  "masteries": { … },             // object, always present — see below
  "isFactionGuardian": false      // bool — this copy is placed in an Academy guardian slot
}
```

`instanceId` identifies an owned copy; `baseTypeId` identifies the champion. Two copies of the same
champion share `baseTypeId` and differ in `instanceId`.

### Renamed from `heroes[]` — read `champions ?? heroes`

**`heroes[]` is gone as of schema 17.** Schema 16 emitted both arrays byte for byte identical so
consumers could move; 17 removes the old name. Every consumer's migration is still one expression,
and it is correct on every schema era at once:

```js
const roster = payload.champions ?? payload.heroes;   // 17, 16, and everything before 16
```

> ⚠️ **A consumer still reading `heroes` alone is broken by schema 17**, and broken silently:
> `payload.heroes` is `undefined`, which is not an error but an **empty roster** — apply it and the
> account's champions are wiped. Read `champions` first.

Why the rename: every other surface already said *champion* — the game's UI, the uploader's own log
lines, RaidTools' `playable_champions` table, and the consumer's own element type, which is literally
called `ParserChampion` and read out of a field called `Heroes`. Only this array said *hero*.
"Hero" is the game's **internal class name** (`Hero`, `HeroType`, `HeroForm`), which is why the
extraction engine's own types keep it: those mirror the IL2CPP metadata so memory-layout work stays
diffable against a type dump. A wire contract is not memory layout, and it should read the way the
player's screen does.

**Why it took two schemas.** Emitting `champions` alone at the rename would have left RaidTools'
`data.Heroes` **null** on every import — not an error, an *empty roster*, exactly the silent wipe the
never-empty invariant above exists to prevent. So the producer emitted both (16), the consumer moved,
and only then did the producer drop the old name (17).

**The two halves retire on very different clocks, and only one of them has happened.** The producer
has stopped emitting `heroes`. A **consumer's `heroes` fallback must outlive that by a wide margin**,
because the uploader is installed on user machines and updates are opt-in — installs older than
v1.14 keep sending `heroes` **alone** for as long as they run. Dropping the fallback is gated on
refusing pre-1.14 uploaders, which is a separate decision from this one and should not be bundled
with it.

#### What did **not** get renamed, and why

`factionGuardians[].heroTypeId` / `.heroBaseTypeId` / `.firstHeroInstanceId` / `.secondHeroInstanceId`
and `artifacts[].equippedByHeroId` **keep their names.** Two reasons, and they point the same way:
those fields name the *game's own* fields (`HeroBaseTypeId` is documented below precisely because the
game's name for it is misleading), and they are join keys onto `instanceId` rather than the roster
itself. Renaming them would multiply the breaking surface of this change several times over for no
gain in clarity — the thing a reader has to understand about `equippedByHeroId` is what it joins to,
which is stated where it is defined. If you want them renamed, that is its own schema bump with its
own deprecation window, not a rider on this one.

### `champions[]` is never empty — treat a zero-length array as a corrupt payload

The game gives every new player a starter champion, so **an account with zero champions does not
exist**. A zero-length `champions[]` is always a failed read, never an empty roster.

That distinction matters because the failure is otherwise silent: `champions: []` is structurally valid,
so a consumer applying it faithfully would **wipe the account's entire champion roster** on what was
really a bad memory read. Two causes are known — a game that hadn't finished loading its roster, and
a recalibration that rediscovered the roster's memory offset wrongly.

The uploader now refuses to send one (extraction fails loudly instead), and the JSON Schema declares
`minItems: 1` so a consumer rejects it too. **Server-side, treat a payload failing that constraint as
"discard and keep what you have", not as an update.**

### `resources[]` is never all-zero — and here length proves nothing

The same reasoning applies to resources, but **the failure wears a different shape, so the check has
to be different**. Every allowlisted id is emitted unconditionally (that is the guarantee above), so
a resource read that failed outright still returns a **full-length array of 49 zeroes** — not an
empty one. Checking the length would never catch it.

What is impossible is the array being *all* zero: every account holds at least some Silver. The
schema expresses that with a `contains` constraint requiring at least one `quantity >= 1`, and the
uploader refuses to send an all-zero array. **A consumer seeing one should discard the payload and
keep what it has** — applying it zeroes the account's entire inventory.

Both guards exist because these two sections are the ones with a provable "this state cannot occur"
invariant. Other sections have no equivalent, so a corrupt read there is still only detectable by
comparison against previous data.

### `champions[].roleId` — champion **metadata**, carried here for convenience

The champion's role: **0 Attack, 1 Defense, 2 Health, 3 Support, 4 Evolve, 5 Xp**. Names, display
labels and localization keys are in [`role-names.json`](role-names.json). The ids and their order are
the game's own `HeroRole` enum, read off the live client — `Health` is the internal name for what the
UI labels **HP**, and `Evolve` / `Xp` are the fodder champions (Chickens and XP brew food).

> ⚠️ **`null` means unresolved. `0` does not — `0` is Attack.** Unlike `factionId`, this field has no
> spare sentinel, so a consumer that coalesces `null → 0` silently relabels a fifth of the roster as
> Attack champions. Check for null explicitly.

**This is static game data, not account data.** A role is a property of the *champion*, so every copy
of a champion reports the same value and the field is a denormalized convenience — it saves a catalog
join for the common case, nothing more. It is published as an int, with the id→name table shipped
separately, for exactly the reason `masteries.selected` publishes bare node ids: names are labels,
ids are the contract.

**Expect roughly a fifth of a mature roster to be `null`, and do not treat that as an error.** The
field is read from the champion's shared type object, which the game client **hydrates lazily** — a
copy it has never rendered has no type object at all, and mostly those are never-opened food
champions. The engine already recovers what it can by backfilling from another copy of the same
`baseTypeId` (156 of 340 unresolved copies on the mapping account), but a champion with *no* hydrated
copy cannot be resolved from the account's memory at all.

**So for complete coverage, join a champion metadata catalog on `baseTypeId` and treat that as
authoritative** — it covers every champion in the game rather than the subset this account has had
rendered. Use this field as a fallback, not the other way round.

### `champions[].baseStats` — the game's **Basic Stats** column, resolved for this copy

```jsonc
"baseStats": { "1": 5100, "2": 426, "3": 367, "4": 103, "5": 30,
               "6": 0, "7": 15, "8": 50, "9": 50, "10": 0 }
```

Keys are the game's **`StatKindId` as strings** — the same ids `artifacts[].primaryBonus.statKindId`
and every other bonus record already carry, named in [`artifact-enums.json`](artifact-enums.json):

| id | stat | id | stat |
|---:|---|---:|---|
| 1 | Health | 6 | Accuracy |
| 2 | Attack | 7 | Critical Rate |
| 3 | Defence | 8 | Critical Damage |
| 4 | Speed | 9 | Critical Heal |
| 5 | Resistance | 10 | Ignore Defence |

**This is what a percentage artifact bonus is a percentage of.** A bonus with `isAbsolute: false`
carries a *fraction* (`0.18` = +18%), and without a base stat there is nothing to take 18% of — so
before this field every percentage HP/ATK/DEF roll had to be dropped from a displayed total, which on
a geared champion is most of its stats.

**Basic Stats is the game's own name for it**, not an invented scope. Raid's champion screen breaks
a Total down into columns:

```
Basic Stats | Artifacts | Affinity Bonuses | Classic Arena | Masteries |
Faction Guardians | Empowerment | Blessing | Relic | Area Bonuses | Total
```

This field is **the first column and nothing else**. Great Hall (the game calls it Affinity Bonuses),
arena, masteries, guardians, empowerment, blessings, relics and area bonuses are separate columns in
the game and separate data in this payload — the export already carries the vault, the masteries and
the blessing id — so folding any of them in here would double-count for every consumer that also
reads those.

That same screen also settles **how the bonuses combine: they are percentages of Basic Stats, and
they add rather than compound.** For Incubus at 3★ 21 with Basic Stats 3,285 / 328 / 274, the game
shows Affinity Bonuses `+657 / +66 / +55` (20%) and Classic Arena `+723 / +72 / +60` (22%), and the
Total is `3285 + 657 + 723 = 4,665` — neither percentage computes off the other's result.

> ⚠️ **Absent means unknown. Never coalesce a missing `baseStats` — or a missing key inside one — to
> `0`.** A `0` that *is* present is a real zero: base Accuracy is genuinely 0 on 6,806 of the 7,166
> champion variants, while base Resistance is never below 30.

**Which catalog these numbers came from is stated on the payload**, as the top-level
[`baseStatsCatalog`](#basestatscatalog--which-catalog-the-computed-stats-came-from) — read it before
trusting a stored block, because a computed stat is a snapshot and a rebalance invalidates it
silently.

The property is omitted entirely when the copy's **level or star rank could not be read** (`level: 0`
has always meant "unreadable", and `stars` silently falls back to 5, which would otherwise feed the
growth multiplier and emit confident nonsense), when the level exceeds its rank's cap, and when the
catalog predates the champion — a game update can add champions before the catalog is re-cut.

#### Unlike `roleId`, this is **not** champion-constant — prefer it, don't treat it as a fallback

`roleId` above is a property of the champion, so it is pure denormalization and a catalog keyed on
`baseTypeId` is the better source. **`baseStats` is not.** The stored base value depends on the
copy's **ascension**, and the displayed value additionally on its **star rank** and **level**:

```
m     = rank[stars] × level[stars] ^ ((L − 1) / (maxLevel[stars] − 1))

HP    = round(base[1] × m) × 15      ATK = round(base[2] × m)      DEF = round(base[3] × m)
SPD, RES, ACC, C.RATE, C.DMG, C.HEAL, IGN.DEF = the stored value, unchanged at every rank and level
```

So a consumer **cannot** recover this from a champion catalog alone; it would need the copy's rank and
level too, plus the growth table. The export carries all three and resolves it in one place, against
the client the model was validated on — which is why this field is the preferred source for a champion
the player owns, not a convenience copy of something better available elsewhere.

A consumer still wants the underlying catalog (`hero_base_stats.json`, shipped with the uploader and
produced by RslCompanionMetadata) for champions the player does **not** own — champion pages, planning,
"what would this champion look like". The two are complementary: this field reaches a correct number
sooner for owned copies, and the catalog covers everything else.

### `champions[].elementId` — the copy's affinity, and `null` means unknown

The game's `Element` — Magic / Force / Spirit / Void — as a **raw id**, deliberately unnamed here.
The frontend already renders affinity from icons keyed by that id, so a name table would be one more
thing to keep in sync for no consumer benefit; the same decision `forms[].element` makes in the
champion index.

**`null` is "could not read", not "has no affinity".** Every champion has one. The client hydrates a
champion's shared type lazily, so a copy the player has never rendered has nothing to read it from —
the same mechanism that leaves `roleId` null on part of a mature roster. The exporter backfills from
another copy of the same `baseTypeId` first, which is legitimate because affinity is
champion-constant, and only then gives up.

Consumers need it for exactly one thing: selecting this champion's row out of `affinityBonuses[]`.

### `champions[].statBreakdown` — the game's own Total Stats table

Per stat, the total plus one entry per contributing source, keyed by `StatKindId` **as strings** —
the same ids as `baseStats`:

```jsonc
"statBreakdown": {
  "1": { "isPercentage": false, "total": 19650, "sources": { "basic": 14865, "artifacts": 4488, "greatHall": 297 } },
  "8": { "isPercentage": true,  "total": 158,   "sources": { "basic": 63, "artifacts": 85, "greatHall": 10 } }
}
```

**An absent source means "contributes nothing", never zero.** This is the client's own distinction,
not a convention invented here: the game builds one `StatBonusContext` per stat row, every cell
carries a `_hasValue` flag, and a cell without one renders **blank** rather than `0`. Reproducing it
as `0` throws away information the game keeps.

**A source absent from `statBreakdownSources` is a third thing again: not modelled yet.** The columns
are landing one at a time, and the per-champion object cannot express the difference — both cases look
like a missing key. So the top-level list is what tells them apart, and **a consumer must not draw
either as a real zero**, nor present a Total as complete without noting which columns went into it.

`isPercentage` marks the stats the game displays as percentages (C.RATE, C.DMG, C.HEAL, IGN.DEF). For
those, a non-absolute bonus is **percentage points**, not a fraction of the base: 15% base plus 37%
from gear shows as 57%, not 21%.

`total` is the number on the game's screen. It is rounded the way the client rounds — once per column,
half to even — so a consumer that re-sums `sources` under a different rule can legitimately land one
off; prefer the field.

Absent entirely on a champion whose `baseStats` could not be computed, because there is then no base
for a percentage to apply to and a breakdown of the bonuses alone would silently disagree with the game.

### `champions[].skills`

```jsonc
"skills": [
  { "typeId": 15101, "level": 6 },
  { "typeId": 15103, "level": 6 },
  { "typeId": 15104, "level": 5 }
]
```

This copy's skills and how far the player has upgraded each. Always present and sorted by `typeId`,
so two snapshots diff cleanly. Every champion has at least one skill — an **empty array is a failed
read**, though unlike `champions[]` itself there is no schema constraint enforcing it.

- **`typeId` is the skill's identity and the catalog join key.** It is stable across every copy and
  every ascension of a champion. **Treat it as opaque — join it, don't derive it** (see below).
- **Slots are not dense.** The example above is a real, complete Kael: three skills numbered
  `…01`, `…03`, `…04`. Do not infer a missing skill from a gap, and do not assume `count == max slot`.
- **`level` is 1-based.** `1` is an un-upgraded skill, so **books applied = `level - 1`**. Observed
  range on a mature account is 1–9.

#### Why `typeId` looks derivable but is not

`typeId` is usually `baseTypeId * 10 + slot` — 3,027 of 3,082 skills on the mapping account — which
makes it tempting to compute rather than join. Two things break that, and both are normal data:

| | |
|---|---|
| **Second forms** | A champion with a transformation **also** carries its other form's whole block, at `800000 + the own-block id`: Alaz the Sunbearer (base `8630`) reports `86301…86305` **and** `886301…886305`. 10 champions, 53 skills here — and this is why `skills[]` can hold 10–12 entries when a champion "has 5 skills". |
| **Borrowed skills** | A skill can sit in a *different champion's* id block outright. Ezio Auditore (base `10270`) and Edward Kenway (base `10280`) both carry `102505`, which belongs to neither. |

So `Math.floor(typeId / 10) === baseTypeId` is **not** an invariant, and a consumer that filters on it
silently drops transformation skills. Join on `typeId` and let the catalog say what it is.

> **The per-skill maximum level is deliberately not in this payload, and you need it to say anything
> about progress.** Caps vary by skill, and `"level": 5` is meaningless — maxed or half-done — without
> knowing whether that skill's cap is 5 or 9. The cap, along with the skill's name, description and
> cooldown, is constant game data; join it from a skill catalog on `typeId`. This is the same split as
> `masteries.selected` (ids here, node metadata in `mastery_index.json`).

Note the engine cannot read the cap either: the static `SkillType` object each `Skill` would point at
is null on the live account graph (`_allSkillTypes` was populated for 18 of 957 champions), so this is a
genuine boundary, not an omission that could be filled in later on this path.

### `champions[].masteries`

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

## `affinityBonuses[]` and `areaBonuses[]` — the Great Hall's two tabs

The Great Hall building has two tabs and they are **two different tables**, so they are two arrays.
Both are **account data**: one purchase feeds every champion it applies to, so neither is repeated per
champion.

```jsonc
"affinityBonuses": [ { "elementId": 4, "statKindId": 2, "level": 3,  "value": 0.04, "isAbsolute": false },
                     { "elementId": 4, "statKindId": 5, "level": 10, "value": 80,   "isAbsolute": true  } ],
"areaBonuses":     [ { "locationId": 3, "statKindId": 1, "level": 6,  "value": 0.12, "isAbsolute": false },
                     { "locationId": 3, "statKindId": 4, "level": 10, "value": 20,   "isAbsolute": true  } ]
```

**The whole grid the game declares is sent** — 4 × 6 = 24 affinity entries and 13 × 8 = 104 area
entries on game build 11.71.0 — so a track the account has not bought arrives with `"level": 0`
rather than being omitted. That is deliberate: it makes the payload **self-describing**, so a consumer
draws the screen straight from it instead of hardcoding an axis list that would be wrong the day
Plarium adds a location.

A pair that *is* absent therefore means the game has no such track — C.RATE in the affinity table is
the standing example. An absent **array** means the table could not be read, which is **not** the same
as an account that has bought nothing, and must not be rendered as an empty Great Hall.

**Both the level and the value are carried, and neither derives the other.** The level is what the
player sees and plans against ("3/10"); the value is what arithmetic needs. Recovering one from the
other requires the static per-level table, which stays in the client and is not on the wire — so
dropping either half would make this array strictly less useful than the screen it describes.

**`isAbsolute` is not constant within a table.** Health, Attack, Defence, Critical Damage and Ignore
Defence are **fractions** of the champion's Basic Stat; **Resistance, Accuracy and Speed are flat
amounts**. Assuming percentages throughout computes `baseResistance × 80` where the game simply adds
80. Same field, same meaning, as on an artifact bonus.

**The two tables are not the same table keyed differently**, and a consumer that treats them as one
will be wrong in three separate ways:

| | `affinityBonuses[]` | `areaBonuses[]` |
| --- | --- | --- |
| keyed by | `elementId` — join on `champions[].elementId` | `locationId` — raw id, 1–13 on game build 11.71.0 |
| stat tracks | six: 1 HP, 2 ATK, 3 DEF, 5 RES, 6 ACC, 8 C.DMG | eight: those (bar none) plus **4 SPD** and **10 IGN.DEF** |
| per-level curve | uneven steps (…14, 17, 20 %) | linear (2, 4, 6 … 20 %) |
| which tracks exist | the same six for every affinity | the same eight for every location |

C.RATE (7) is genuinely absent from the affinity table: the Great Hall has no Critical Rate track.

**A caution, from getting this wrong once.** On a real account the *bought* cells vary wildly by
location — two tracks levelled at one location, eight at another — which reads as "each location
grants different stats" and is false; all three Observatory tiers declare the same eight. One
inventory is never evidence about the game's grid, and the full-grid export exists partly so no
consumer has to make that inference at all.

**`areaBonuses[]` is the one bonus table that will never appear in `statBreakdownSources`.** The game's
own Total Stats overlay applies it only for a location the player picks from a dropdown ("Showing Area
Bonuses for:"), so there is no single per-champion number to publish — collapsing it to one would be
meaningless. At account level it is perfectly well defined, which is why it lives here instead.

Location ids ship raw for the same reason `elementId` does. On game build 11.71.0 they run 1–13 in
three tiers (1+8, 2–5+9+10, 6+7+11–13); the tier is an internal grouping and is not on the wire, since
all three currently carry identical values.

## `factionGuardians[]`

```jsonc
{
  "factionId": 1, "rarityId": 4, "slotIndex": 0,
  "heroTypeId": 366,          // champion type WITH ascension digit (366 = base 360 at ascension 6)
  "heroBaseTypeId": 360,      // ascension stripped — THIS is what joins to champions[].baseTypeId
  "firstHeroInstanceId": 50325,
  "secondHeroInstanceId": 3948,  // either may be null when that half of the slot is empty
  "consumed": false
}
```

A slot fuses up to **two copies** of one champion, hence the First/Second pair.

**Join on `heroBaseTypeId`, not `heroTypeId`.** The game's own field is named `HeroBaseTypeId` but
carries the ascension digit, which is a misnomer; joining the raw value onto `champions[].baseTypeId`
silently matches only unascended champions. Both are published so consumers don't have to guess.

---

## `clanId` — int64 or `null`, and `clanName` — string or `null`

The clan this account belongs to (the game calls a clan an *Alliance*).

```jsonc
"clanId":   20000001,
"clanName": "Unimatrix Zero One"
```

- **`null` means "no clan is being reported"** — the account is in none, *or* the value could not be
  read and validated. Both are normal; never treat `null` as "left the clan", and never clear a
  previously known clan association on a `null` (see below).
- **`clanId` is stable and is the join key.** Two accounts that share a clan report the same
  `clanId`, so clan grouping works from this payload alone — no roster required.
- **`clanName` is a label, never a key.** Clan names are not unique and a leader can change one at
  any time. `null` means "this export says nothing about the name" — the account may be in no clan,
  or the name simply wasn't readable on this run. Never overwrite a stored name with a `null`, and
  never treat a changed name as a changed clan.

Consequently, **do not derive "the user left their clan" from this payload**. This export cannot
distinguish "no clan" from "unreadable", and it carries no membership list to diff against.

### What is *not* here, and why it never will be

The clan's **member roster** — every clanmate's id, name, rank, level and power. It is not omitted
for cost. As of schema 14 the whole clan cache is a pointer walk off the client root, and the roster
sits beside the name at the same price; an earlier revision of this file blamed an 18–31 s memory
scan, and that is simply no longer true.

It is omitted because **it describes people who are not the user**. Clanmates never installed this
app and never agreed to anything, and RSL Companion builds clan membership from each member
importing their own account instead — the same list, reached by consent. Nothing the uploader sends
now describes anyone but the signed-in user, and cheapness is not an argument for changing that.

---

## `artifacts[]` and `accessories[]` — the complete vault, with stats

**Two arrays, one record shape.** `artifacts[]` holds **gear** (`kindId` 1–6: helmet, chest, gloves,
boots, weapon, shield); `accessories[]` holds **rings, cloaks and banners** (`kindId` 7–9). Both carry
every piece the account owns — equipped *and* sitting in the vault.

They are split because the game splits them: gear and accessories are separate inventories with
separate counters, a consumer almost never needs both in the same request, and keeping them apart
lets each be stored and served on its own (see [Storage and read APIs](#storage-and-read-apis)).

```jsonc
{
  "artifactId": 854,          // int32 — instance id, unique across BOTH arrays. The join key.
  "kindId": 5,                // int32 1..9 — slot. Decides which array the record is in.
  "setKindId": 3,             // int32 — the set; 0 = no set. Names: artifact-enums.json
  "rankId": 4,                // int32 1..6 — stars
  "rarityId": 4,              // int32 1..6 — quality tier
  "level": 12,                // int32 0..16 — upgrade level
  "ascendLevel": 0,           // int32 0..6 — ascension
  "requiredFactionId": 0,     // int32 — faction lock; 0 = none. Same ids as champions[].factionId
  "isActivated": true,        // bool
  "equippedByHeroId": 48444,  // int64|null — joins champions[].instanceId; null = in the vault
  "sellPrice": 8550,          // int32 — silver from selling
  "price": 136800,            // int32 — silver cost of the next upgrade
  "failedUpgrades": 0,        // int32 — failures since the last success (the game's pity counter)
  "rerollsCount": 0,          // int32
  "ascendRerollsCount": 0,    // int32
  "revision": 215316,         // int32 — server-side revision of this record; useful for diffing

  "primaryBonus":   { "statKindId": 2, "value": 120,  "isAbsolute": true,  "level": 0 },
  "secondaryBonuses": [
    { "statKindId": 2, "value": 0.09, "isAbsolute": false, "level": 1 },
    { "statKindId": 7, "value": 0.14, "isAbsolute": false, "level": 2 },
    { "statKindId": 1, "value": 0.05, "isAbsolute": false, "level": 0 }
  ],
  "ascendBonus": null         // object|null — present exactly when ascendLevel > 0
}
```

### Bonus records — `isAbsolute` decides how `value` reads

| `isAbsolute` | Meaning | Example |
|---|---|---|
| `true` | Flat amount | `{"statKindId": 2, "value": 120}` = **+120 ATK** |
| `false` | Fraction of the champion's base stat | `{"statKindId": 2, "value": 0.18}` = **+18% ATK** |

**`value` is a number, not a percentage — `0.18` means 18%, not 0.18%.** It is derived from the
game's own Q32.32 fixed-point storage (raw ÷ 2³²) and rounded to 6 decimals, so a relative value can
legitimately exceed 1.0 (`0.8` = +80% C.DMG on a maxed glove). `level` is how many times that
substat has been rolled up — `0` on an un-upgraded line, not "missing".

> ⚠️ **Schemas 9, 10 and 11 emitted every one of these values at exactly double the game's.** The
> divisor was 2³¹. A 6★ +16 speed boot reported `90` where the game shows 45, `1.2` where the game
> shows 60%. This affects `primaryBonus`, `secondaryBonuses[]` **and** `ascendBonus`, on gear and
> accessories alike, and nothing on the record distinguishes an old row from a corrected one — so
> **re-sync affected accounts rather than halving stored values in place.** Sanity check for a
> consumer: no artifact main stat may exceed the game's 6★ +16 table (SPD 45, HP 4080, ATK/DEF 265,
> ACC/RES 96, HP%/ATK%/DEF%/C.RATE 60%, C.DMG 80%; banner HP 6120, ATK/DEF 398).

`primaryBonus` is present on every record. `secondaryBonuses` holds 0–4 entries. `ascendBonus` is
non-null exactly when `ascendLevel > 0` (1,213 of 1,213 on the reference account).

### Id tables

`kindId`, `statKindId` and `rankId` are named in [`artifact-enums.json`](artifact-enums.json) — static
game metadata, shipped here for the same reason as `role-names.json`: the payload carries opaque ints
and nothing in it says what they mean.

| `kindId` | Slot | | `kindId` | Slot |
| --- | --- | --- | --- | --- |
| 1 | Helmet | | 6 | Shield |
| 2 | Chest | | 7 | Ring |
| 3 | Gloves | | 8 | Cloak (the UI's "Amulet") |
| 4 | Boots | | 9 | Banner |
| 5 | Weapon | | | |

> ⚠️ **This table changed in schema 9 and the old one was wrong.** Up to schema 8 this file listed
> 1 = Weapon, 2 = Helmet, 3 = Shield, 4 = Gauntlets, 5 = Chestplate. The correct order is the game's
> own `ArtifactKindId` enum, above, confirmed independently by which primary stat each slot always
> rolls (slot 1 is Health on all 420 records → helmet; slot 5 Attack on all 485 → weapon; slot 6
> Defence on all 517 → shield; slot 4 Speed on 388 of 516 → boots). A consumer that hard-coded the
> old names is mislabelling slots today.

**`setKindId` carries two id spaces in one field.** `0`–`66` are artifact **sets** (`0` = no set — a
real value, and a common one). `1000`–`1004` are **accessory effects**: a single item's own effect,
not a set, with no piece count and no set bonus. They appear on ~2.6% of accessories and on no gear.
A consumer grouping "by set" must exclude that range or it will invent five sets that don't exist.
Both tables, with the effect text, are in `artifact-enums.json`.

> ⚠️ **The set names published before 2026-08-02 were wrong from id 4 onward.** The old table read
> 4 Critical Rate / 5 Accuracy / 6 Speed where the game says 4 Speed / 5 Critical Rate / 6 Crit
> Damage, and 47 Stone Skin where the game says 47 Protection (Stone Skin is 48). The current table
> is resolved from the game's own localized strings, with each name confirmed against a matching
> description. Re-derive any stored set labels.

### Reconciling against the in-game counters

Each array reconciles exactly against what the player sees, both in total and unequipped. Measured
live 2026-08-02 (account Magikwolf, game 11.67.0):

| | in game | payload |
|---|---:|---:|
| Gear total | 2,851 | `artifacts.length` = **2,851** |
| Gear unequipped | 1,963 | `equippedByHeroId == null` → **1,963** |
| Accessories total | 2,969 | `accessories.length` = **2,969** |
| Accessories unequipped | 2,000 | `equippedByHeroId == null` → **2,000** |

**Use this as the acceptance test** — matching the totals *and* the unequipped splits is a far
stronger signal than a record count that merely looks plausible.

> **These are a snapshot, not constants.** They move whenever the player farms, sells or equips
> anything — the same account read eight hours earlier that day gave 2,811 / 1,922 gear. What holds
> is the *relationship*: each array's length equals the in-game total for that category **at the
> moment of the snapshot**, and the null-`equippedByHeroId` count equals the unequipped total. Test
> against counters read at the same time, never against the literals above.

> **One category is still absent: the mailbox.** Unclaimed items (a couple of hundred accessories on
> the reference account) belong to no inventory until the player collects them, and appear in neither
> these arrays nor the in-game counters they reconcile against. "Owned" here means "in the vault or
> equipped".

---

## `relics[]` and `gemstones[]` — the relic system, complete

New in **schema 11**. Nothing resembling these arrays existed before, so there is nothing to migrate:
a consumer that ignores both is exactly as correct as it was on schema 10.

Two arrays, for the same reason gear and accessories are two arrays — the game counts them as two
inventories with two counters, and **most gemstones are in no relic at all** (303 of 543 on the
reference account). Nesting gemstones under the relic holding them would have hidden 56% of that
inventory, which is precisely the failure that made schema 8's equipped-only `artifacts[]` look
plausible while missing two thirds of the data.

```jsonc
"relics": [
  {
    "id": 5,                    // instance id, stable across upgrades and re-equips
    "typeId": 12,               // the shared RelicType — join key into a relic catalog
    "rank": 4,                  // 1-5 observed; Rank N Basalt (19000+N) raises N to N+1
    "level": 12,                // Starstone (4000) levels it; steps by 3 — 0,3,6,9,12,15
    "isActivated": true,
    "equippedByHeroId": 52004,  // int64 → champions[].instanceId, or null when in storage
    "sockets": [
      { "shapeKindId": 5, "stoneId": 202 },   // stoneId → gemstones[].id
      { "shapeKindId": 1, "stoneId": 23  }
    ]
  }
],
"gemstones": [
  {
    "id": 202,
    "typeId": 17,               // the shared RelicStoneType — join key into a gemstone catalog
    "isActivated": true,
    "socketedInRelicId": 5      // → relics[].id, or null when in storage
  }
]
```

### What is deliberately *not* here

`typeId` on both arrays is a **join key, not data**. A relic's name, rarity, group, skill and stat
bonuses hang off the shared `RelicType`, and a gemstone's off `RelicStoneType` — those describe the
game, not the account, exactly like champion skill names and mastery-node metadata. They belong in a
catalog keyed on `typeId`. Reading them per record would also be unsafe: the client hydrates shared
type objects **lazily**, the same trap that silently exported `champions[].factionId` as `0` for 340 of
957 champions before 2026-08-01.

### The two ends of the socket join agree by construction

`relics[].sockets[].stoneId` and `gemstones[].socketedInRelicId` are the same fact from both
directions; the second is derived by inverting the first, so it is a convenience for consumers that
index gemstones directly, not a second source of truth. Verified live: 240 socketed gemstones, zero
dangling references, zero disagreements between the two directions.

Watch the id spaces — `sockets[].stoneId` is a **gemstone** id and does **not** join to `relics[].id`.

### `shapeKindId` — the id is solid, the label is not

A gemstone only fits a socket of its own shape, so `shapeKindId` decides what can go where. The
game's `RelicStoneShapeKindId` enum declares five members (Circle, Triangle, Square, Diamond,
Pentagon) and the live data carries exactly five values, 1–5 — but **which member is 1 is inferred
from declaration order and is not independently confirmed.** IL2CPP enum members can carry explicit
values, which is exactly how the artifact set table came to be wrong from id 4 onward. Join on the
id; treat the names in [`relic-enums.json`](relic-enums.json) as provisional, and read the
provenance block there before persisting a label.

### Reconciling against the in-game counters

Measured live 2026-08-03 (account Magikwolf, game 11.67.0):

| | in game | payload |
|---|---:|---:|
| Relics total | 377 | `relics.length` = **377** |
| Relics unequipped | 240 | `equippedByHeroId == null` → **240** |
| Gemstones total | 543 | `gemstones.length` = **543** |
| Gemstones unsocketed | 303 | `socketedInRelicId == null` → **303** |

Same caveat as the artifact counters above: **a snapshot, not constants.** Test the relationship
against counters read at the same moment.

One relic per champion is what this account shows — 137 relics across 137 champions, none with two —
but that is an observation about the game's current rules, not something the payload enforces. The
field is a per-relic hero reference, so handle a hero appearing more than once.

---

## Storage and read APIs

Not part of the wire contract — this is the shape the payload is built for, recorded so the server
side and the uploader stay deliberately aligned.

The upload is **one call** carrying the whole account; the split matters on the *read* side. Store
gear and accessories as two collections keyed by `(accountId, artifactId)`, and serve at minimum:

| Read | Why |
|---|---|
| `GET /accounts/{id}/artifacts` and `…/accessories` | The common case: one category at a time, never both. |
| `…?equipped=true|false` | The equipped/vault split is the axis every UI filters on first. |
| `GET /accounts/{id}/artifacts/{artifactId}` | Single-piece lookup. Ids are unique across **both** categories, so a not-found in one is worth retrying in the other — or route on `kindId`. |
| `GET /accounts/{id}/heroes/{instanceId}/artifacts` | A champion's loadout: index on `equippedByHeroId`, which is the only field linking the two. |

Two properties of the data worth exploiting: `artifactId` is stable for the lifetime of the piece
(it survives upgrades and re-equips), and `revision` changes when the game changes the record — so a
sync can diff on `revision` instead of rewriting a ~5,800-row vault every time.

Because a snapshot is a **full replace**, a piece the player sold is gone by absence, not by a
tombstone: reconcile by replacing the account's set, or deletes will never land.

---

## Consumer guidance

1. **Join on ids, never names.** `resources[].name`, `champions[].name` are display labels and do change.
2. **Treat `resources[]` as complete.** Every allowlisted id is present; `0` means zero owned. A
   missing id means the allowlist changed.
3. **Additive changes are expected.** New resource ids and new hero fields get added without a major
   version bump — ignore unknown keys rather than failing.
4. **`artifacts[]` is gear, `accessories[]` is rings/cloaks/banners** — same record shape, split by
   `kindId`, both covering the whole vault. `equippedByHeroId` is `null` for a vaulted piece, so
   never treat `null` as "unknown wearer". Both may be empty on a brand-new account, and
   `gameVersion` may be `null`. Unlike schema 7–8, a `0` in a stat field is now a **real** `0`.
5. **`account.accountId` == top-level `accountId`.** Route on the top-level one.
5b. **`champions[].roleId` is nullable and `0` is a real value** (Attack). Never coalesce `null → 0`.
    Roles and skill metadata are *game* data joined by id — `role-names.json` here, a skill catalog
    for `skills[].typeId`; this payload carries ids and levels only.
6. **Clan rosters do not arrive from this uploader at all**, and there is no second payload that
   carries one — the clan export was withdrawn in schema 13. A clan's member list is the set of
   accounts reporting the same `clanId`, built by each member importing their own account, not
   something one player's client reports about everyone else. `clanName` (schema 14) is a label for
   display; group and join on `clanId`.
7. Server-side, `ConsolidatedJsonSyncAdapter` in RaidTools is the reader that must track this file.

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
| 18 | v1.16.0 | 2026-08-24 | **Four new fields, all additive: `champions[].statBreakdown`, `champions[].elementId`, top-level `statBreakdownSources`, and the two account-level Great Hall tables `affinityBonuses[]` / `areaBonuses[]`.** A consumer that ignores every one of them is exactly as correct as it was on schema 17. **`statBreakdown` is the game's own Total Stats table per copy** — per stat, the total plus one entry per source — and it reproduces the client's own blank-vs-zero distinction: an absent source contributes nothing and must never be drawn as `0`. **`statBreakdownSources` exists because a third state is possible**: a column this producer does not compute yet looks identical, from the per-champion object, to one that contributes nothing. The list says which columns are real in *this* export, so an old payload keeps describing what it modelled; today it is `basic, artifacts, greatHall, arena`. **`elementId` is the copy's affinity** (raw id, `null` = could not read, never "no affinity"), and it is what joins a champion to its `affinityBonuses[]` row. **The two Great Hall tables are account data and are two tables, not one**: the affinity table has six stat tracks with an uneven curve, the area table has eight (adding Speed and Ignore Defence) with a linear one, and every location offers the same eight tracks. **The whole declared grid is sent, not just what the account has bought** — 24 affinity entries and 104 area entries — with unbought tracks at `"level": 0`, so a consumer can draw the screen without hardcoding the axes; a genuinely absent pair means the game has no such track. Both carry `level` alongside `value` because neither derives the other without the static per-level table, which is not on the wire. **`isAbsolute` is not constant inside either table** — HP/ATK/DEF/C.DMG/IGN.DEF are fractions, RES/ACC/SPD are flat — and reading it as "percentage throughout" computes `baseResistance × 80` where the game adds 80; that was a real bug in the first implementation, and it survived verification against the client because the account it was checked on held level 0 in exactly those two stats. **`areaBonuses[]` will never appear in `statBreakdownSources`**: the game applies it per a location the player picks from a dropdown, so there is no per-champion number — account level is the only place it is well defined. **Also documented, not new to the wire:** `artifacts[].powerUpValue` (the glyph, a separate addend to `value`, previously in the JSON Schema only). |
| 17 | v1.15.0 | 2026-08-21 | **`heroes[]` is removed. `champions[]` is the only roster field.** The other half of the schema-16 rename, and the deliberately boring one: nothing changes shape, nothing is added, the duplicate array simply stops being sent. A consumer already reading `champions` — or the migration expression `payload.champions ?? payload.heroes` — needs no change at all and will not notice. **A consumer still reading `heroes` alone breaks completely**, and breaks in the worst way available: `payload.heroes` is `undefined`, which is not an error but an *empty roster*, and applying it wipes the account's champions. That is precisely the failure schema 16 existed to prevent, which is why the two shipped as separate schemas with the consumer migrated in between rather than as one rename. **The reverse deprecation is untouched and runs much longer.** This stops the PRODUCER emitting `heroes`; it says nothing about a consumer's fallback, which must outlive it by a wide margin, because the uploader is installed on user machines and updates are opt-in — installs older than v1.14 keep sending `heroes` **alone** for as long as they run. Retiring the fallback is gated on refusing pre-1.14 uploaders, a separate decision that is not this one. **What this buys:** the roster stops being duplicated on the wire, roughly −800 KB on a ~900-champion account. **Unchanged, again:** `factionGuardians[].heroTypeId` / `.heroBaseTypeId` / `.first`/`.secondHeroInstanceId` and `artifacts[].equippedByHeroId` keep their names — they name the game's own fields and join onto `instanceId`, not onto the roster. |
| 16 | v1.14.0 | 2026-08-21 | **`heroes[]` is renamed to `champions[]`. BOTH are emitted, byte for byte identical, and `heroes[]` is removed in schema 17.** Nothing else changes: same items, same constraints, same never-empty invariant. **Migration is one expression — `payload.champions ?? payload.heroes`** — which also handles every schema before 16, so a consumer can adopt it now and be correct on all three eras at once. Why: every other surface already said *champion*. The game's UI says it, the uploader's own log lines say it, RaidTools' table is `playable_champions`, and the consumer's own element type is literally `ParserChampion` — read out of a field called `Heroes`. Only this array said *hero*, which is the game's INTERNAL class name (`Hero`, `HeroType`, `HeroForm`). The extraction engine's own C# types keep that name deliberately, because they mirror the IL2CPP metadata so memory-layout work stays diffable against a type dump; a wire contract is not memory layout. **Why it takes two schemas rather than one:** emitting `champions` alone today would leave RaidTools' `data.Heroes` null on every import — not an error, an *empty roster*, which is exactly the silent wipe the never-empty invariant exists to prevent. Producer emits both, consumer moves, then producer drops the old name. **The two halves retire on different clocks, and this is the part to get right.** The producer stops emitting `heroes` in schema 17; a **consumer's `heroes` fallback must outlive that by a wide margin**, because the uploader is installed on user machines and updates are opt-in — old installs keep sending `heroes` alone for as long as they run. Dropping the fallback is gated on refusing pre-1.14 uploaders, a separate decision. **Not renamed:** `factionGuardians[].heroTypeId` / `.heroBaseTypeId` / `.firstHeroInstanceId` / `.secondHeroInstanceId` and `artifacts[].equippedByHeroId` keep their names — they name the game's own fields and are join keys onto `instanceId`, not the roster, so renaming them would multiply the breaking surface for no gain in clarity. **Cost, stated plainly:** the roster is duplicated on the wire for one schema — roughly +800 KB on a ~900-champion account, against a payload already several MB of artifact vault. That is the price of not breaking a live consumer, and it is temporary. |
| 15 | v1.13.0 | 2026-08-21 | **New `heroes[].baseStats` — additive; nothing else changes.** A consumer that ignores it is exactly as correct as it was on schema 14. Each owned copy now carries the game's own **Basic Stats** column — the numbers before any gear, Great Hall, arena, mastery, guardian, empowerment, blessing, relic or area bonus — keyed by the same `StatKindId` the artifact bonuses already use, so no new id table is involved. **This is the number a percentage artifact bonus is a percentage of.** A bonus with `isAbsolute: false` carries a fraction (`0.18` = +18%), so until now every percentage HP/ATK/DEF roll had to be dropped from a displayed total — on a geared champion, most of its stats. It is resolved at export rather than left to the consumer because **it is not champion-constant**: the stored value depends on the copy's ascension and the displayed one on its rank and level (`m = rank[stars] × level[stars] ^ ((L−1)/(cap−1))`, then `×15` for HP after rounding, and only Health/Attack/Defence scale at all). The export already carries all three, so it is the one place that can resolve it without a second join, and the formula sits next to the data it was validated against instead of being reimplemented per consumer. **Consumer impact: prefer this over a champion catalog for owned copies** — unlike `roleId`, it cannot be recovered from a catalog keyed on `baseTypeId`. The catalog (`hero_base_stats.json`, bundled with the uploader) is still wanted for champions the player does *not* own; the two are complementary, not alternatives. **Absent means unknown and must never be coalesced to `0`** — the property is omitted when the copy's level or star rank could not be read (`stars` silently falls back to 5, which would otherwise feed the growth multiplier and emit confident nonsense), when the level exceeds its rank's cap, and when the catalog predates the champion; individual stat keys are omitted on the same terms. A `0` that *is* present is a real zero: base Accuracy is genuinely 0 on 6,806 of 7,166 champion variants, while base Resistance is never below 30. **A second new field comes with it: top-level `baseStatsCatalog` `{generatedAt, gameVersion}`, the provenance of every `baseStats` block in the payload** — absent exactly when no hero carries base stats. It is not redundant with the top-level `gameVersion`, and the two come apart in the ordinary case: `gameVersion` is the build the export *ran against*, `baseStatsCatalog.gameVersion` is the build the numbers were *computed from*, so a user who updates Raid before a refreshed catalog ships sends `gameVersion: "11.72.0"` with stats derived from an 11.71.0 catalog — a payload that looks current and is not. It exists because a computed stat is a **snapshot**: a Plarium rebalance silently invalidates every block already stored server-side until each user re-exports, and without this field a stale block is indistinguishable from a fresh one. **Consumer rule: a block whose catalog build trails the payload's `gameVersion` is _suspect, not wrong_** — most rebalances touch few champions, so flag it for re-sync rather than discarding numbers that are almost certainly still right. The model was validated to zero error against the client's own computed stat blocks on ten champions across seven factions and four rarities. |
| 14 | v1.8.0 | 2026-08-07 | **New top-level `clanName` (string or `null`) — additive; nothing else changes.** A consumer that ignores it is exactly as correct as it was on schema 13. This closes the gap schema 13 opened: with the clan export withdrawn, no export named a clan at all, so a clan with no other source of a name displayed as `Clan #<id>` permanently. **The interesting part is the cost, because the previous entry in this file was wrong about it.** Both this file and the engine held that the clan's name needed an 18–31 s full-memory scan and was therefore unaffordable on a ~4 s export. It does not. The clan cache hangs off `AppModel`, the client-root **static singleton** that sits *above* the account object — `klass → static_fields → instance → AllianceNotes → Dictionary<clanId, AllianceNote>` — so the name is a pointer walk keyed by the `clanId` this payload already carried: **measured 5 ms warm, 3.3 s the first time a game build is seen** (a one-off klass lookup, then cached in the shipped offset catalog). The old figure came from searching for the record by class identity across all of memory, having concluded it was unreachable because a breadth-first walk *downward* from the account object finds nothing — which is true, and irrelevant, because the owner is upstream. Verified end to end on a client that had been open for minutes with no actions taken and the clan screen never opened, so the record is not populated on demand. **Consumer impact: `null` is not "no name"** — it is "this export says nothing", so never overwrite a stored name with it. Names are not unique and are editable by the clan leader: join and group on `clanId`, and use `clanName` for display only. Absent key (schema ≤13) and explicit `null` mean the same thing. **The member roster is still not emitted and this does not reopen that** — it is now equally cheap and remains excluded because it describes people other than the user; see "What is *not* here" above. |
| 13 | v1.8.0 | 2026-08-07 | **No wire change to this payload — but the *other* payload is gone.** The uploader's second export, `POST /api/sync/clan/raw` (the clan record plus the member roster with clanmates' display names), has been **removed from the app**: the button, the endpoint config, and the code path. `clan-export-schema.md` / `.json` are deleted with it. Nothing here gains or loses a field; `clanId` is unchanged and is now the only clan data the uploader emits anywhere. Why: RSL Companion stopped ingesting the roster. It described **other people** — clanmates who never installed this app and never agreed to anything — and clan membership is now built from each member importing their own account instead, which reaches the same list by consent rather than by one player's client reporting on everyone else's. Continuing to scan for it would have been ~25 s of work per run for data the server discards. **Consumer impact:** a consumer that also read the clan payload must stop expecting it — accounts sharing a clan are found by grouping on `clanId`, and a clan's *name* now comes from the consumer's own store, not from an export. A consumer that only ever read this payload is unaffected and needs no change. |
| 12 | v1.6.2 | 2026-08-03 | **Every artifact and accessory stat value was DOUBLE the game's in schemas 9–11; this halves them to the truth.** No field changes shape, name or type — only the numbers in `artifacts[]` and `accessories[]` `primaryBonus.value`, `secondaryBonuses[].value` and `ascendBonus.value` change, and they change for every record. The engine read the game's `BonusValue._value` as fixed point scaled by 2³¹; it is **Q32.32**, so the divisor is 2³². The error was uniform, which is why nothing downstream flagged it: a 6★ +16 speed boot exported `90` (game: 45), a maxed glove `1.6` C.DMG (game: 80%), the strongest C.RATE substat 64% (real cap 32%), the largest ascension SPD bonus 24 (real cap 12). Corrected, all thirteen 6★ +16 main-stat maxima reproduce the game's table exactly. **Consumer impact: every artifact stat stored from schemas 9–11 is wrong and must be re-synced, not patched in place** — the payload carries nothing that distinguishes a doubled row from a corrected one, so a consumer cannot tell which of its stored rows to halve. Any derived figure computed off those stats — champion totals, gear scores, build rankings, "best piece" sorts — is invalid for the same window. The bug ran from schema 9 (2026-08-02), i.e. from the first release that shipped artifact stats at all; no correct artifact stat has ever been published before this. Reported by a user who recognised 90 SPD as physically impossible. |
| 11 | v1.6.2 | 2026-08-03 | *(This row said v1.6.1 — no such release was ever tagged. Schema 11 reaches users in **v1.6.2**, alongside schema 12.)* **New `relics[]` and `gemstones[]`; six new resource ids that were previously being DROPPED.** Two changes, both additive — nothing existing changes shape, and a consumer that ignores the new arrays is exactly as correct as it was on schema 10. (1) **The relic system is exported for the first time.** `relics[]` is every relic the account owns, equipped and stored, each with its gemstone sockets (377 total / 240 unequipped on the reference account); `gemstones[]` is every gemstone, socketed or not (543 / 303 unsocketed). Two arrays rather than one because the game counts them as two inventories and **56% of gemstones sit in no relic** — nesting them would have hidden all 303. Both `typeId`s are join keys into a relic catalog, not data: name, rarity, group and stat bonuses live on shared type objects the client hydrates lazily, so exporting them per record would produce holes that look like values. The socket join is verified in both directions (240 socketed, zero dangling refs). `sockets[].shapeKindId`'s **names are provisional** — the enum has five members and the data five values, but the member-to-value binding is inferred from declaration order, the same assumption that made the artifact set table wrong from id 4 onward; join on the id. New static-metadata file [`relic-enums.json`](relic-enums.json). (2) **`resources[]` 49 → 55 entries, and this half is a data-loss fix, not a feature.** `4000` Starstone and `19001`–`19005` Rank 1–5 Basalt are the relic upgrade currencies. None was on the engine's *exclusive* resource allowlist, so **every export ever made discarded all six regardless of how many the account held** — the third time this has happened, after Rank 1/2 Chickens (2026-07-28) and the Immortal/Eternal Soul Essences (2026-07-29). Ids were read from a live dump and matched against in-game balances rather than inferred from a numbering pattern: the five Basalt ranks matched 10/17/10/5/1 *in rank order*, Starstone matched 19,466. **Consumer impact: an account's Basalt and Starstone history begins at this schema** — their absence before now was never evidence the player had none. |
| 10 | v1.6.0 | 2026-08-02 | **No wire change — `setKindId`'s meaning is corrected and split.** Same payload, same fields, same uploader binary; what changes is what the ids mean, so a consumer storing set *labels* must re-derive them. Two parts. (1) **The set names were wrong from id 4 onward** in everything published before this: the table read 4 Critical Rate / 5 Accuracy / 6 Speed where the game says 4 Speed / 5 Critical Rate / 6 Crit Damage, and 47 Stone Skin where the game says 47 Protection with Stone Skin at 48; the tail (60 Bloodshield, 61 Clan Boss, 62 Debuffer, 65 Provoke, 66 Soulbound) was not set names at all. [`artifact-enums.json`](artifact-enums.json) now carries the game's own localized strings, each confirmed against a matching description (`1 Life` ↔ "2 Set: HP +15%"). (2) **`setKindId` carries two id spaces**: 0–66 are sets, **1000–1004 are accessory effects** — one item's own effect, no piece count, no set bonus — occurring on 76 of 2,969 accessories and on no gear. Group "by set" over the 1000s and you invent five sets that do not exist. Join on the id, never the name: Cleansing, Bloodshield, Reaction and Revenge appear in both spaces. |
| 9 | v1.6.0 | 2026-08-02 | **BREAKING — artifacts are now the complete vault with real stats, and split into `artifacts[]` (gear) + a new `accessories[]`.** `artifacts[]` changes meaning: it was ~1.9k equipped ids across all nine slots with every stat `0`; it is now **every gear piece the account owns** (2,811 on the reference account, 1,922 of them unequipped) with real `setKindId` / `rankId` / `rarityId` / `level` / `ascendLevel`, plus `primaryBonus`, `secondaryBonuses[]` and `ascendBonus` — the actual stat lines. Rings, cloaks and banners moved out to **`accessories[]`** (2,969 / 2,000 unequipped), same record shape. `heroInstanceId` is renamed **`equippedByHeroId`** and is now nullable: `null` means the piece is in the vault, which is most of them. `primaryStatId` is **gone** — the primary stat is `primaryBonus.statKindId` with its value. **Consumer impact, in order of how badly it bites:** (1) a consumer reading `artifacts[]` for accessories now silently sees none — read both arrays; (2) `artifacts.length` is no longer an equipped count, it is an owned count, so anything treating a record's presence as "equipped" must switch to `equippedByHeroId != null`; (3) the schema 7 rule "a `0` stat means unknown" is **reversed** — `0` is now a real value, and code gating writes on non-zero will drop legitimate zeroes; (4) **`kindId`'s slot names were wrong in schemas 7–8** — the correct order is the game's `ArtifactKindId` (1 Helmet, 2 Chest, 3 Gloves, 4 Boots, 5 Weapon, 6 Shield), not the 1 Weapon / 2 Helmet / 3 Shield this file used to claim, so a consumer that hard-coded labels is mislabelling slots today. New static-metadata file [`artifact-enums.json`](artifact-enums.json) names slot / stat / rank / set ids. Why now: the "artifact stats live in Unity ECS and are unreachable" conclusion behind schema 7 was false. It rested on a full-memory scan that reported the game's `CachedArtifacts` object no longer existed — that scan stepped one address per 4 KB page, testing 1 candidate in 512. The object was there all along, holding a `Dictionary<int, Artifact>` of the entire vault. Cost: +3 s on the first export of a game session (+5 s more the first time a game build is unknown), ~0.3 s on later exports in the same session. |
| 8 | v1.5.9 | 2026-08-01 | **New `heroes[].skills[]` and `heroes[].roleId`; `heroes[].factionId` silently gets more accurate.** `skills[]` is one `{typeId, level}` per skill on that copy, sorted by `typeId`, always present (3,082 records across 957 heroes on the mapping account). `level` is **1-based** — books applied is `level - 1` — and the per-skill **cap is not here**, so `"level": 5` cannot be read as maxed without a skill catalog joined on `typeId` (the engine cannot read the cap either: the static `SkillType` is null on the account graph). **`typeId` is opaque**: it is usually `baseTypeId*10 + slot`, but a champion with a second form also carries that form's block at `800000 + own id`, and some skills sit in another champion's block entirely, so deriving it instead of joining drops data. `roleId` is the game's `HeroRole` enum — `0` Attack, `1` Defense, `2` Health/"HP", `3` Support, `4` Evolve, `5` Xp — named in the new [`role-names.json`](role-names.json). **Consumer impact: `roleId` is nullable and `null` ≠ `0`**, because `0` is Attack; expect ~19% null on a mature roster (the client hydrates a champion's shared type lazily) and prefer a champion metadata catalog keyed on `baseTypeId` as authoritative — role is champion-constant game data, and this field is a denormalized convenience. Same root cause fixed a pre-existing silent bug: `factionId` came off that same lazily-hydrated object and had been exporting `0` for 340 of 957 heroes; both fields are now backfilled from another copy of the same `baseTypeId`, recovering 156. `factionId` keeps `0`-as-unknown for compatibility. |
| 7 | v1.5.8 | 2026-07-31 | **`artifacts[]` is now populated — equipped ids only, all stats `0`.** Previously it always arrived empty and consumers were told to expect that; it now carries one record per equipped artifact (~1.9k on a mature account) with **real** `artifactId`, `kindId` (slot 1–9) and `heroInstanceId`, and **`setKindId` / `rankId` / `rarityId` / `primaryStatId` / `level` hard-zero on every record** because artifact stats live in Unity ECS storage the engine still cannot decode. **Consumer impact: a `0` stat is "unknown", not a value.** A consumer that renders them literally will show every artifact as rank-0/level-0, and one that persists them will overwrite known-good stat data with nulls — gate writes on the field being non-zero. The array covers **equipped artifacts only**; unequipped inventory is absent, so it is not an "artifacts owned" count. Why now: the id map was always readable, but the extractor was looking for stat-bearing objects that no longer exist, so enabling it used to yield 0 records for ~2.5 s of scanning; reading `HeroArtifactData.ArtifactIdByKind` instead gives 1,861 records in ~34 ms. |
| 6 | v1.5.6 | 2026-07-30 | **`heroes` and `resources` now declare their "cannot occur" states.** No wire change — both document invariants that always held. `heroes` gets `minItems: 1`: every account has at least a starter champion, so a zero-length array is a failed read, never an empty roster. `resources` gets `minItems: 1` **plus a `contains` requiring at least one `quantity >= 1`** — because every allowlisted id is emitted unconditionally, a failed resource read returns a full-length array of zeroes rather than an empty one, so length proves nothing and all-zero is the real signal. Both were silently postable and would have wiped a roster or an inventory server-side; the uploader now fails extraction instead of sending either, and consumers should treat a payload failing these constraints as "discard, keep what you have". |
| 5 | v1.5.6 | 2026-07-30 | **BREAKING — `clan` is replaced by `clanId`.** The v4 `clan` object (`id`, `name`, `abbreviation`, `level`, `leaderId`, `membersLimit`, `members[]`) is **gone from this payload**; the top level now carries `clanId` (int64 or `null`) and nothing else clan-related. A consumer written against v4 reading `clan` will see `undefined`. Why: building the v4 object cost two full-memory scans of the game (18–31 s on a ~4 s export), so the roster moved to its own export and endpoint — `clan-export-schema.md`, which is where `name` / `members[]` then lived (**that export was withdrawn in schema 13 and its contract files deleted**; the link is left unlinked here because the file no longer exists). `clanId` is the free read. This payload no longer contains any data about other players. |
| 4 | — | 2026-07-29 | **New top-level `clan`** (object or `null`) with the full roster. **Superseded by 5 before release — no shipped uploader ever emitted it.** |
| 3 | v1.5.4 | 2026-07-29 | Soul economy corrected. **New ids `1121` / `1122`** (Immortal / Eternal Soul Essence). **Renamed** `1111` → Mortal Soul Coin, `1112` → Immortal Soul Coin, `1113` → Eternal Soul Coin — values for all three were previously wrong. `resources[]` 47 → 49 entries. No structural change. |
| 2 | v1.5.2 | 2026-07-28 | Added top-level `uploaderVersion` and `gameVersion`. Added resource ids `6500` / `6501` (Rank 1/2 Chicken). |
| 1 | — | — | Baseline: `accountId`, `account`, `timestamp`, `resources`, `heroes`, `artifacts`, `factionGuardians`; `heroes[].masteries` as an object with `selected` / `unusedScrolls` / `totalScrolls`. |
