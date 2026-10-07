# Export payload contract

**This file is the contract between the uploader and its consumers (RaidTools / rslcompanion.com).**
It describes exactly what `POST {ApiBaseUrl}/api/sync/consolidated/raw` receives.

- Machine-readable form: [`export-schema.json`](export-schema.json) (JSON Schema 2020-12).
- This repo is public, so consumers can reference both files without access to the private
  extraction engine.
- **Schema version: 41** — bump `schemaVersion` below and add a Changelog row on every wire change.
- **This is now the only payload the uploader sends.** The separate clan export that used to carry a
  clan record and member roster is gone — see `clanId` below and Changelog 13.
- Champion **role** ids are named in [`role-names.json`](role-names.json), artifact slot / stat /
  rank / set ids in [`artifact-enums.json`](artifact-enums.json), and relic socket shapes, relic
  upgrade currencies, plus `relicTypes` / `gemstoneTypes` name tables in
  [`relic-enums.json`](relic-enums.json) — static game metadata, not account data (see
  `champions[].roleId`, `artifacts[]` and `relics[]` below).
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
  "arenaTeam":  { … } | null,               // object|null — Classic Arena's one saved team
  "arena3v3Teams": [ … ],                   // array or ABSENT — Tag Team (3v3) Arena's saved teams
  "siegePresets":  [ … ],                   // array or ABSENT — Siege's per-slot presets
  "soloEvents":  [ … ],                     // array or ABSENT — active solo events (schema 33)
  "tournaments": [ … ],                     // array or ABSENT — active tournaments (schema 33)
  "battlePass":  { … },                     // object or ABSENT — one pass; deprecated by battlePasses (schema 33)
  "battlePasses": [ … ],                    // array or ABSENT — one entry per pass kind, with dates (schema 38)
  "inbox":       { "items": [ … ] },         // object or ABSENT — the in-game Inbox (schema 40)
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

**`account.liveArenaPoints` is the named field since schema 25** — `UserLiveArenaData.Points`, the same
number as [`liveArena.points`](#mode-progress--classicarena-livearena-doomtower-cursedcity-grimforest-siege).
Schemas ≤ 24 filled it from a value-shape probe that could land on the wrong field. `arenaPoints` /
`arenaLeague` were already the named read (see `classicArena` for the fuller picture);
`liveArenaLeague` and `arena3x3League` are still the probe and should not be relied on.

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
and the array length only changes when the allowlist itself changes. Current allowlist: **145 ids**
(see `extraction/resource-allowlist.json` / `.md` in the engine for the full annotated table).

### Shards & Remnants (corrected in schema 23)

| id | name | notes |
|---:|---|---|
| 1203 / 1202 / 1303 / 1301 / 1302 | Mystery / Ancient / Void / Sacred / Primal Shard | unchanged ids and names — see the collision note below |
| 10650 | Prism Crystals | **new id in schema 23**; was emitted as `10202`, which was never Prism Crystals |
| 8100 | Cursed Remnants | unchanged (the game's internal name is `ParticleSummon`) |
| 3000 | Primal Quartz | unchanged (the game's internal name is `MythicalDust`) |

**`10202` "Prism Crystals" was a Grim Forest currency.** The game's `ResourceTypeId` enum calls it
`FoggyForest_Hard_Gold` and its own strings **Extra Grim Gold**, so every schema ≤ 22 payload carried a
Grim Forest balance under the Prism Crystals label — 2,755 on the reference account, against 55 Prism
Crystals on the Summoning Portal screen. `10205` "Eternal Essence" was **Extra Grim Charms**. Both are
gone in schema 23. Real Prism Crystals is the game **item** `10650`. **Consumer rule: discard stored
`10202` / `10205` values — there is nothing in them to migrate — and expect no `10650` history before
schema 23.**

**The five shard ids are also the game's ids for something else.** In its resources store `1202` /
`1203` are Live Arena Crests and `1301` / `1302` / `1303` are Cursed City Keys / Cursed Candles /
Occult Cursed Candles; four unrelated items (Grim Forest map chests, Fusion Tokens) were also mapped
onto them. Schema ≤ 22 exported correct shard counts only because the shard read ran last and
overwrote those values. Schema 23 takes shards from the shard store alone, so **if that read fails the
shards export `0`** — before, they would have exported a Crest count (Mystery Shard 30,225). The wire
shape is unchanged.

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

### Forge materials (new in schema 22 — previously dropped entirely)

Both kinds of Forge crafting material: **47 gear forge materials** and **44 relic craft materials**,
91 ids. Ids are the game's own `ResourceTypeId` values, read from the client's runtime enum table;
names are the game's own localized strings.

**Each family's tiers run consecutively from its first id** — gear `+0` Rare, `+1` Epic, `+2`
Legendary; relic `+0` Rare, `+1` Epic, `+2` Legendary, `+3` Mythical. **That does not hold across
families**: 681, 684 and 687 are three different families packed three apart. Use the table, never
arithmetic across it. The tier is also in the name — `"Dragon Bones (Epic)"` — but names are labels:
join on `id`, never parse the name.

**Gear forge materials**

| ids | name | | ids | name |
|---|---|---|---|---|
| 601 | Magisteel (single tier) | | 684–686 | Dreadhorn Plates |
| 602 | Corehammer (single tier) | | 687–689 | Fae Spheres |
| 611–613 | Willstone ⚠ | | 697–699 | Instinct Stones |
| 621–623 | Bloodstone | | 6900–6902 | Bolster Stones |
| 631–633 | Nether Eggs | | 6910–6912 | Defiant Chunks |
| 641–643 | Scarab Claws | | 6920–6922 | Righteous Alloy |
| 651–653 | Magma Cores | | 6923–6925 | Slayer Scales |
| 661–663 | Frost Spines | | | |
| 671–673 | Dragon Bones | | | |
| 681–683 | Griffin Feathers | | | |

⚠ **The label on 611–613 is the one name not read directly.** The game's enum calls them
`Forge_Soulstone*`; the name is paired with the localized "Willstone" by elimination. The id and the
quantity are solid either way.

**Relic craft materials** — named by the Forge's relic group they craft

| ids | name | group | | ids | name | group |
|---|---|---|---|---|---|---|
| 4100–4103 | Ocular Masses | Chimera | | 4160–4163 | Radiant Sunlily | Discovery |
| 4110–4113 | Regal Masques | Alice's Adventure | | 4170–4173 | Gleaming Fyrgems | Forge Pass |
| 4120–4123 | Gilded Medallions | Clan | | 4180–4183 | Bloodbriars | Grim Forest |
| 4130–4133 | Runed Obsidian | Live Arena | | 4190–4193 | Celestial Crystals | Wings of Winter |
| 4140–4143 | Spiteful Remains | Faction Wars | | 4200–4203 | Floral Amberstone | Coalescence |
| 4150–4153 | Twysted Horns | Chaos Awakes | | | | |

**An account's history for these 91 ids begins at schema 22.** Like the relic economy before it, they
were absent from the exclusive allowlist, so every earlier export discarded them however many the
account held — do not read their absence in an older snapshot as a zero balance. Several gear materials
read exactly `10000` on a mature account, which looks like a storage cap; that is an observation, not
something the payload enforces.

### Soul economy (corrected in v1.5.4 — read this if you consume these)

| id | name | notes |
|---:|---|---|
| 1111 | Mortal Soul Coin | **was** `Silver Soul Coin`, and exported a stale value |
| 1112 | Immortal Soul Coin | **was** `Gold Soul Coin`, and exported a stale value |
| 1113 | Eternal Soul Coin | **was** `Immortal Essence`, and exported a stale value |
| 1121 | Immortal Soul Essence | **new in v1.5.4** — previously not exported at all |
| 1122 | Eternal Soul Essence | **new in v1.5.4** — previously not exported at all |
| 12001 / 12002 / 12003 | Mortal / Immortal / Eternal Soulstone | unchanged, were always correct |

**`10202` and `10205` are no longer emitted (schema 23).** They were listed here as "Prism Crystals"
and "Eternal Essence"; the game's `ResourceTypeId` enum and its own strings say they are Grim Forest
currencies — **Extra Grim Gold** and **Extra Grim Charms**. Prism Crystals is now id **`10650`**, in
[Shards & Remnants](#shards--remnants-corrected-in-schema-23).

Ids **10101, 10102, 10104, 10105, 10201, 10204** are **not** in the allowlist and are never emitted.
This file used to call them a *defunct earlier* soul system; they are Grim Forest currencies too
(Grim Coins, Grim Gold, Redraw Token, Grim Charms, and the Extra Grim variants), holding live values.
Mapping them onto the soul ids above is precisely the bug v1.5.4 fixed — so do not reintroduce that
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
a resource read that failed outright still returns a **full-length array of 145 zeroes** — not an
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
  { "typeId": 15101, "level": 6, "formIndex": 0 },
  { "typeId": 15103, "level": 6, "formIndex": 0 },
  { "typeId": 15104, "level": 5, "formIndex": 0 }
]
```

This copy's skills, how far the player has upgraded each, and **which of the champion's forms each one
is on**. Always present and sorted by `typeId`, so two snapshots diff cleanly. Every champion has at
least one skill — an **empty array is a failed read**, though unlike `champions[]` itself there is no
schema constraint enforcing it.

- **`typeId` is the skill's identity and the catalog join key.** It is stable across every copy and
  every ascension of a champion. **Treat it as opaque — join it, don't derive it** (see below).
- **Slots are not dense.** The example above is a real, complete Kael: three skills numbered
  `…01`, `…03`, `…04`. Do not infer a missing skill from a gap, and do not assume `count == max slot`.
- **`level` is 1-based.** `1` is an un-upgraded skill, so **books applied = `level - 1`**. Observed
  range on a mature account is 1–9.
- **`formIndex` is which form the skill belongs to** — `0` the base form, `1` a transformation's
  second form — matching `forms[].index` in `champion_index.json`. **Absent, never `0`, when
  unresolved**, so group with `?? "unknown"` and never with `?? 0`. New in schema 20.

#### `formIndex`: why the producer answers this

A transforming champion carries **both** forms' whole skill blocks on every copy, which is why
`skills[]` can hold 10–12 entries for a champion the player thinks has five. Alaz the Sunbearer
(base `8630`) reports `86301…86305` **and** `886301…886305`; the first five are form 0, the second
five form 1. Without `formIndex` there is nothing in the payload that says so.

A consumer *could* recover it from `champion_index.json`, and until schema 20 that was the only way —
see [`raidtools-skill-attribution.md`](raidtools-skill-attribution.md), which documents the join and the
trap in it. **The trap is the reason this moved to the producer.** Ascension does not only *add* skills,
it **replaces** them on 336 of the 1,044 playable champions, and both halves of a swapped pair sit in
the same form's list, so a correct consumer-side join has to evaluate the per-skill ascension span that
the catalog carries — at the copy's own `ascensionLevel`, not the champion's max. Here that question
never arises: the copy's own `Hero._type` **is** its ascension variant, so the form list read from the
live process is already the kit that copy actually has.

Resolution order, and what each outcome means:

| | |
|---|---|
| **The game's own `HeroForm.SkillTypeIds`** | The normal case, and exact for the running build. |
| **The bundled champion catalog** | For copies whose shared `HeroType` the client has never hydrated — the same lazy-hydration gap that leaves `roleId` null on ~19% of a mature roster. Tried first at the copy's own ascension, honouring the catalog's per-skill span; then again ignoring the span, which is safe because a skill id belongs to exactly one form across every ascension of its champion, and which is what answers when the bundled catalog is simply older than the client. |
| **Absent** | Neither source knew the skill — in practice a champion newer than the bundled catalog on a copy the client had not rendered. **A consumer must not read this as form 0.** |

> Two forms of one champion sharing a skill id has never been observed — 0 ids across all 1,040
> champions in the catalog appear under two form indexes — so this is a partition, not a tagging. If
> that ever changes, the lowest form index wins and the value stays deterministic.

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

> That is a statement about *this* payload, not about the column. A consumer holding this array and a
> champion's `statBreakdown` has everything it needs to draw the Area Bonuses column itself, once per
> picked location — which is what RaidTools does (`RaidTools/docs/stat-breakdown.md` § *The tenth
> column*). It has to reproduce the client's arithmetic to do it: `isAbsolute` varies within the
> table, IGN.DEF is percentage points, and the column rounds half-to-even **from the raw value**
> with no six-decimal pre-snap, since it is a single `base × fraction` per stat rather than a sum.

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
  "secondHeroInstanceId": 3948,  // null when that half is empty, or when the pair was sacrificed
  "consumed": false,             // the game's flag, passed through — NOT "the champions are gone"
  "championsDeleted": false      // schema 21: the two copies were sacrificed and no longer exist
}
```

A slot fuses up to **two copies** of one champion, hence the First/Second pair.

**`championsDeleted: true` means the pair no longer exists on the account.** Both instance ids are
then `null` and neither copy is in `champions[]`; the slot is still filled. Name it from
`heroBaseTypeId`. The game stores the champion's type id where the instance ids were, so the producer
detects it (ids equal the slot's type id and no roster copy owns them) and nulls them rather than
publishing ids that join onto nothing — or onto an unrelated copy that happens to own that number.

**`consumed` is not a deletion signal.** Slots reading `consumed: true` can still have both copies in
the roster (owner-confirmed on a live account, 2026-09-13). What the flag means is not established;
never drop a copy from a roster on it.

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

## `arenaTeam`, `arena3v3Teams[]`, `siegePresets[]` — saved teams

**New in schema 19.** A saved, recallable team — pick heroes once, reuse them next time without
picking again — exists for exactly three areas of the game. This is not an incomplete first pass at
a bigger feature: it was checked, and no other area has one (see "What is *not* here" below).

```jsonc
"arenaTeam": {
  "combatPower": 396565,
  "leaderSlotIndex": 0,
  "heroes": [
    { "heroTypeId": 3566, "inventoryHeroId": 20540, "slot": 1, "grade": 6, "level": 60, "empowerLevel": 1 },
    { "heroTypeId": 9196, "inventoryHeroId": 49430, "slot": 2, "grade": 6, "level": 60, "empowerLevel": 0 }
  ]
},
"arena3v3Teams": [
  { "combatPower": 387189, "leaderSlotIndex": 0, "heroes": [ … 4 heroes … ] },
  { "combatPower": 691872, "leaderSlotIndex": 0, "heroes": [ … 4 heroes … ] },
  { "combatPower": 558054, "leaderSlotIndex": 0, "heroes": [ … 4 heroes … ] }
],
"siegePresets": [
  { "slot": 0, "heroIds": [59442, 39072, 52814, 68310] },
  { "slot": 1, "heroIds": [56238, 20540, 38789, 49430] },
  { "slot": 2, "heroIds": [45758] },
  { "slot": 3, "heroIds": [38534] }
]
```

- **`arenaTeam` is `null` when the account has no saved team, and also when the value could not be
  validated this run** — the same accepted ambiguity `clanId` already has on this payload; never
  infer "the team was cleared" from `null`.
- **`arena3v3Teams[]` and `siegePresets[]` distinguish "not read" from "read, and empty."** ABSENT
  means the read could not be validated this run (an offset drifted, or the branch wasn't attached) —
  the same rule `affinityBonuses[]`/`areaBonuses[]` already use, for the same reason: collapsing the
  two into an empty array would make a failed read look identical to "you have nothing saved here."
  A **present but empty** array is a real, normal state — the account simply hasn't set that slot yet.
- **`arenaTeam`/`arena3v3Teams[].heroes[]` carry a stat SNAPSHOT** (`grade`, `level`, `empowerLevel`)
  taken when the team was last saved or used — it can be stale against the live roster. Join
  `inventoryHeroId` onto `champions[].instanceId` for the current numbers; use the snapshot only to
  know what the saved team looked like when it was set.
- **`siegePresets[].heroIds` carries bare ids, no snapshot** — Siege reads the live roster at attack
  time rather than freezing one, so there is nothing to snapshot. `slot` is list order (0-based), not
  a map-stable slot identifier; do not assume it survives the player rearranging presets.
- **`arena3v3Teams[]` is normally 3 entries and `siegePresets[]` normally 4** — the game's own slot
  counts for those modes — but treat both as "however many the account has set," not a fixed length:
  an account that hasn't touched every slot yet reports fewer.

### What is *not* here, and why it never will be

**Every PvE stage mode** — Dungeons, Doom Tower, Cursed City, Faction Wars, Event Dungeon, Foggy
Forest, Champion's Journey — has **no saved team to read, in principle**. This was checked by walking
every field of the game's own stage-data object, not inferred from an empty extractor: it carries
battle *results* (win/loss, stars) for every one of those modes and not one team/roster field anywhere
in the subtree. The client does not remember what you picked for a dungeon stage or a Doom Tower
floor — you choose fresh from your roster every time you enter, and nothing about that choice
persists. There is nothing missing from this payload for those areas; there is nothing to send.

**Clan Boss (Chimera/Hydra) team history** is also not here, for a different reason: it exists, but
per-*attack*, not as a reusable preset, and reaching it requires the same clan-wide record whose
member roster is withheld above — consent, not cost. See the extraction engine's
`docs/clash-findings.md` for the structure, if it is ever revisited.

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

  "primaryBonus":   { "statKindId": 2, "value": 120,  "isAbsolute": true,  "level": 0,
                      "powerUpValue": 0, "powerUpRarityId": 0 },
  "secondaryBonuses": [
    { "statKindId": 2, "value": 0.09, "isAbsolute": false, "level": 1, "powerUpValue": 0.01, "powerUpRarityId": 0 },
    { "statKindId": 7, "value": 0.14, "isAbsolute": false, "level": 2, "powerUpValue": 0,    "powerUpRarityId": 0 },
    { "statKindId": 1, "value": 0.05, "isAbsolute": false, "level": 0, "powerUpValue": 100,  "powerUpRarityId": 1 }
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

### Glyphs — `powerUpValue` is a second addend, and you must add it

`powerUpValue` is the **glyph** applied to that stat line (the game calls it "Power Up"), `0` when
there is none. It reads exactly like `value` — same Q32.32 scaling — and it **inherits the line's
`isAbsolute`**, because a glyph upgrades the substat it sits on: a flat HP line gets flat HP, an HP%
line gets percentage points.

> **A line's real contribution is `value + powerUpValue`.** They are not folded together, and a
> consumer that sums only `value` under-reports every glyphed champion.

**Test for a glyph with `powerUpValue != 0`, not with `powerUpRarityId`.** On a live account of
29,283 stat lines, 5,495 carry a glyph, and **4,091 of those report `powerUpRarityId: 0`** — so `0`
there does *not* mean "no glyph". Only `0` and `1` have been observed, no line has a non-zero rarity
with a zero glyph value, and what the id actually distinguishes is **not established**. Treat it as
opaque until it is.

> ⚠️ **These were not emitted before schema 18, and this file used to say they never would be** —
> described as "the upgrade screen's preview of the next roll, null on every record". That was wrong.
> The evidence behind it was a single account that had glyphed nothing, and absence from one
> inventory is not evidence about a field. Diffed against the game's own Total Stats screen on a
> glyphed champion, the amounts missing from a computed gear total were exactly these values, on
> every stat at once (HP 1242, ATK 114, DEF 4, SPD 4, RES 2, ACC 3, and 0 on the two crit stats that
> account had not glyphed). **Consumer impact: gear totals computed from schema ≤ 17 payloads are
> low for any glyphed champion**, and nothing in an old payload says by how much — re-sync rather
> than trying to correct in place.

`_rarityBasedPowerUpValue` remains unexported: it read `0` on every line of a glyphed account,
including the glyphed ones, so nothing is known about what it means. A field's name is not evidence.

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

The **names** are the one catalog field that ships today: [`relic-enums.json`](relic-enums.json)
carries `relicTypes` (ids 1–94) and `gemstoneTypes` (ids 1–120) as `typeId → name`, read from the
game's own l10n (2026-08-03, re-captured and corrected 2026-09-08 — the first gemstone pass was off
by one). This is metadata beside the schema — the payload gains no name field and this is not a
schema bump. Rarity, group and bonus text are still absent.

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

## `souls[]` — Awakening Soul inventory

New in **schema 24**. Every Awakening Soul the account owns — champion-bound material that raises
`champions[].awakeningLevel` — decoded from the game's own key on `UserGameData →
UpdatableUserDoubleAscendData`. **Not** `resources[]`' Soulstone/Soul Essence entries (an unrelated
account-wide currency the game's UI also happens to call "Soul" — see the Changelog row for schema
23's soul-economy corrections) and **not** a spare duplicate champion copy sitting in the roster,
Vault or Reserve Vault (that raises `ascensionLevel`, a different mechanic entirely).

```jsonc
"souls": [
  {
    "championBaseId": 9190,   // a champions[].typeId with no ascension digit — may be a champion
                               // the account has never summoned
    "isPerfect": true,        // false = Split
    "level": 4,                // 1-6, the awakening grade this soul is good for
    "championRarity": 5        // the champion's rarity, carried in the soul's own id
  }
]
```

### Perfect vs Split — how each is actually used

The two kinds apply differently, and the payload states which without a consumer needing a lookup
table:

- **Perfect** (`isPerfect: true`) applies to a champion of `championBaseId` any time that champion's
  current `awakeningLevel` is **below** `level`.
- **Split** (`isPerfect: false`) applies **only** when the champion's current `awakeningLevel` is
  **exactly** `level - 1`.

Confirmed against the game's own Altar of Souls screen: a Perfect soul's star row fills 1..`level`
in order; a Split soul's row shows the same star count with only the star at position `level` lit.

### Not stackable, and no instance id

The game allows at most one soul per `(championBaseId, isPerfect, level)` at a time, so this record
has no separate id — the three fields together are already unique within an account. A consumer
diffing two snapshots for "souls gained/spent" can key on the tuple directly.

### The key-decode formula, for anyone re-deriving this

```
key = (kind*100 + level*10 + championRarity) * 100000 + championBaseId
prefix = key / 100000
isPerfect = prefix >= 100
level = (prefix % 100) / 10
championRarity = prefix % 10
championBaseId = key % 100000
```

**The champion field is five digits.** Until 2026-09-23 this split the key at `10000`, which is exact
below base id 10000 and cuts the leading `1` off every champion from 10000 up: The Cowardly Lion
(`10710`) exported as `710` (Axeman), Cinda Forgeheart (`10490`) as `490` (Yeoman), with the correct
`championRarity` — so the rarity no longer matched the champion. **1.22.1 and earlier ship the
truncated ids**; a consumer that wants those souls back can retry a sub-10000 id at `+10000` when its
rarity disagrees with the catalog (RaidTools does, on read). No schema change: same field, full value.

Verified live (11.75.0, account Magikwolf): 40 owned keys decoded against every real champion in
`champion_index.json`, with `championRarity` matching that champion's real rarity on all 40, and
three shop listings literally named "Pestilus/Aothar/Captain Temila Split Soul" decoded to those
exact champions (rarity 4, confirmed Epic) with the single lit star in each matching the decoded
`level` exactly. Owned-soul count matched the in-game Soul Collection tally exactly (156).

---

## Mode progress — `classicArena`, `liveArena`, `doomTower`, `cursedCity`, `grimForest`, `siege`

New in **schema 25**. Where the account stands in the rotating and seasonal modes, and **which of
their rewards it has already claimed**. Five top-level objects, each read independently and each
**absent when its read could not be validated** — never an empty object, which would make a failed
read look like an account that has not started the mode. Every field is resolved by name off the
game's own classes (`UserArenaData`, `UserLiveArenaData`, `UserStageData.DoomTowerData` /
`CursedCityData` / `FoggyForestData`); the whole read costs ~20 ms.

```jsonc
"classicArena": {
  "points": 3358, "leagueId": 25, "previousLeagueId": 25,
  "battlesThisWeek": 30, "victoriesThisWeek": 29, "lossesThisWeek": 9, "defeatsToday": 1,
  "lastWeeklyRewardAt": "2026-09-21T08:00:27Z"
},
"tagTeamArena": {                                     // schema 27
  "points": 1190, "leagueId": 14, "lastRatingUpdateAt": "2026-09-25T05:55:38Z"
},
"liveArena": {
  "points": 20733, "victories": 5302, "defeats": 4736, "lastSeenLeagueId": 1,
  "maxPointsAchieved": 20733,
  "takenMilestoneRewards": [910, 920, 930 /* … 55 thresholds … */, 4800],
  "dailyRewardTaken": false, "victoriesForRegularReward": 18,   // the "Wins 18/35" quest bar (schema 26)
  "battlesToday": 0, "victoriesToday": 0,
  "season": {
    "number": 14, "points": 118, "victories": 14, "defeats": 3, "battles": 17,
    "maxPointsAchieved": 118, "lastParticipationAt": "2026-09-23T13:15:43Z",
    "takenMilestones": [20, 60, 100], "takenRepeatableChestSteps": [],
    "leaderboardPosition": 0, "leaderboardRewardTaken": false
  }
},
"doomTower": { "rotation": 71, "goldKeys": 0, "silverKeys": 15, "difficulties": [   // rotation, floorsCompleted: 28; keys: 32
  { "difficultyId": 1, "stageIndicator": 7011049, "firstEnteredAt": "2026-09-20T05:16:22Z", "floorsCompleted": 49 },
  { "difficultyId": 2, "stageIndicator": 7012010, "firstEnteredAt": "2026-09-07T15:19:53Z", "floorsCompleted": 120 }
]},
"cursedCity": { "rotation": 34, "keys": 0, "difficulties": [   // keys: schema 32
  { "difficultyId": 2, "takenStageRewards": [25, 50, 101], "takenAwakeningStageRewards": [6, 12],
    "mainBossRewardTaken": true, "takenMilestoneRewards": [10, 25, 40 /* … */, 500],
    "passedStageIds": [10012001, 10012002 /* … */, 10042025, 10052001] }   // passedStageIds: schema 29
]},
"grimForest": { "rotation": 10, "keys": 20, "difficulties": [   // keys: schema 32
  { "difficultyId": 2, "level": 30, "experience": 11650, "curioSlots": 6, "treasureHuntReceived": true,
    "passedStageIds": [14012002, 14012003 /* … */, 14042103],       // schema 30
    "completedSlotIds": [1, 2, 3 /* … */, 403],                       // schema 31
    "shopPurchases": [ { "itemId": 2, "purchaseCount": 1, "boughtCurioRank": 2 },
                       { "itemId": 8, "purchaseCount": 1 } /* … */ ],   // schema 34
    "completedQuestIds": [10711001, 10711002 /* … */, 10711013],     // schema 34
    "claimedQuestIds":   [10711001, 10711002 /* … */, 10711013] }    // schema 34
]}
```

`difficultyId` is 1 = Normal, 2 = Hard throughout. Timestamps are the game server's clock, emitted as
UTC (`…Z`) and omitted when unset.

### "Claimed" is a set of ids — the rewards themselves are static game data

Every `taken*` list names rewards **by the key the game's static reward table uses**: a points
threshold (Live Arena milestones, Cursed City milestones) or a stage count (Cursed City "pass N
stages" / "pass N awakening stages"). The lists are sorted and carry no order. The reward *contents*
— what each threshold pays out — are the same for every account, so they are **not** on this
payload; they belong in the RslCompanionMetadata catalogs (`StaticLiveArenaData`,
`StaticCursedCityData`, `StaticFoggyForestData`). A reward is **collected** when its key is in the
list, and **claimable** when the account has reached it (e.g. `maxPointsAchieved ≥` threshold) but its
key is absent.

### Rotations and seasons — this payload carries the *number*, not the end date

`cursedCity.rotation` and `grimForest.rotation` are the game's own rotation counters (`Revision`),
and `liveArena.season.number` is the season. End dates follow from static settings, which the
metadata side will publish:

| Mode | Anchor | Period | Rotation *n* ends at |
|---|---|---|---|
| Cursed City | `CursedCitySettings.StartTime` 2023-12-12 14:15 UTC | 30 days | anchor + 30·*n* days (34 → 2026-09-27 14:15 UTC) — **confirmed** |
| Grim Forest | `FoggyForestSettings.StartTime` 2025-12-10 | 30 days | anchor + 30·*n* days (10 → 2026-10-06) — day confirmed; hour (11:00 vs the 14:15 refresh) not |
| Live Arena | `LiveArenaSeasonsSettings.FirstSeasonStartTime` 2025-03-11 14:00 | 42-day cycle: 28-day season **then** 14-day preseason | season 14 → 2026-10-06 (preseason to 10-20) — **confirmed** against the in-game countdown |
| Doom Tower | **global** — `doomTower.rotation` (schema 28) is the number; the anchor is static data | 30 days (`UpdateTowerMinutes` 43200) | current rotation ends ≈ 2026-10-07 (in-game "1w 6d" on 2026-09-24); the anchor is not yet mapped |

These are derived from the game's settings and match the counters on the mapping account. **Live
Arena is confirmed** (2026-09-24): the in-game "1w 5d" countdown matches season 14 ending 2026-10-06,
which settles that each 42-day cycle opens with the season, and the ticked milestones (20/60/100)
match `takenMilestones`. **Cursed City is confirmed to the hour**, which settles the time zone: the
settings are **UTC** (its "3d 5h" countdown matches 14:15 UTC; 14:15 local would have read 3d 2h).
Grim Forest matches to the day. **Doom Tower rotations are global** — both difficulties showed one
"1w 6d" countdown — so `firstEnteredAt` (schema 25's `rotationStartedAt`) is *not* a rotation start (see below).

### Three fields that are not what they look like

- **`liveArena.season` is the season the account last played**, not necessarily the current one. A
  player who has not fought since a new season opened still reports the old season's number and
  points. Compare `number` with the schedule before labelling it current.
- **`doomTower.difficulties[].stageIndicator` is not progress.** It is the game's `StageIndicator`
  verbatim, shaped `70 M D FFF` (tower map, difficulty, floor), and on the mapping account it went
  from Normal floor 39 to floor 10 over two hours of play — it tracks where the tower map is focused,
  most likely. Shipped because it is cheap and may prove useful; do not render it as "current floor".
  **Progress is `floorsCompleted`** (below).
- **`doomTower.difficulties[].floorsCompleted` (schema 28) is the highest floor cleared this
  rotation**, 0–120. Floors clear in order, so every floor up to it is passed and its first-clear
  reward granted. Source: the account-wide `UserStageData.BattleResultsByStageId`, whose Doom Tower
  entries are the current tower map's `StageStats` only; a floor counts when `Passed` and its
  `PassedAt` is at or after the rotation start (`UserDoomTowerData.LastUpdate`, 2026-09-07 12:12:10
  UTC for rotation 71 — the `ModeSchedule` anchor to the minute). **Not** `UserDoomTowerData`'s own
  dictionary of the same name, which is lifetime win/loss counts on all three maps. `0` is a
  difficulty the account has entered with nothing cleared; the field is **absent** — never 0 — when it
  could not be read reliably: no rotation start, stages from more than one tower map, or a passed set
  that is not exactly 1..N. Verified live (11.75.0, 2026-09-25): Normal 49, Hard 120.
- **`doomTower.rotation` (schema 28) is `UserDoomTowerData.Id`** — the rotation the account is in at
  export (71 on 2026-09-25, equal to the shared `DoomTowerData.Id`). An export from an earlier rotation
  reads as 0 floors now; compare it with the current rotation before showing `floorsCompleted`.
- **`doomTower.difficulties[].firstEnteredAt` is not when the rotation started** — which is why schema
  26 renamed it from schema 25's `rotationStartedAt`. It is the game's `StartTimeByDifficulty`, and on
  the mapping account Hard read 2026-09-07 and Normal 2026-09-20 while the game showed one shared reset
  ~2026-10-07. Rotations are global; this is most likely when the account first entered that
  difficulty this rotation. Never add 30 days to it to get an end date. Read
  `firstEnteredAt ?? rotationStartedAt` to cover v1.23.0 payloads.
- **`cursedCity.difficulties[].passedStageIds` (schema 29) is every stage won this rotation** — a
  set of stage ids, not a highest stage, because the four districts are played in any order. Ids are
  `RRRR D SSS`: districts 1001–1004 hold stages 1–25, 1005 is the main boss (stage 1), so a
  difficulty has 101; `10011001` is district 1001 stage 1 on Normal, `10012001` the same on Hard.
  They are the same keys as the metadata catalog's `mode_rewards.json`
  `cursedCity.difficulties[d].stages`, which is how a consumer joins each stage to its first-clear
  reward. Source: the account-wide `UserStageData.BattleResultsByStageId` (as for Doom Tower), a stage
  counting when `Passed` with `PassedAt` at or after that difficulty's `FirstVictoryTimeInRotation`
  (which equalled the earliest `PassedAt` to the second on the mapping account) — **not** a lifetime
  win/loss counter, and not `UserCursedCityData`'s own per-stage battle-result lists. Sorted
  ascending; order carries no meaning. `[]` is a difficulty entered with nothing won; the field is
  **absent** — never `[]` — when it could not be read reliably (stage results unreadable, or passes
  present with no first-victory time to bound the rotation). Sanity check a consumer can make: the
  count is at least the highest claimed `takenStageRewards` entry. Verified live (11.75.0, rotation 34,
  2026-09-26): Normal 101, Hard 101, each equal to the catalog's full stage set, matching the claimed
  "pass 101 stages" quest. Compare `rotation` with the current one before showing it.
- **`grimForest.difficulties[].passedStageIds` (schema 30) is every stage won this rotation** — the
  same field as Cursed City's, a set of stage ids. Ids are `ZZZZ D SSS` over zones 1401–1404 (the stage
  catalog's `groupId`); stage numbers are not contiguous (1–20, then 101…, 201…, …), so a consumer
  checks the zone and difficulty digit, never a stage range. `14011007` is zone 1401 stage 7 on Normal,
  `14012006` zone 1401 stage 6 on Hard. They are the keys of the metadata catalog's `mode_rewards.json`
  `grimForest.difficulties[d].stages`. **They are stages, not map slots**: a difficulty's map has ~400
  slots, most of them path nodes; 104 are fixed battles, and random map elements (roaming bosses and
  other encounters) put further battles on other slots. Both kinds are in the set, so its size is not
  bounded by 104. Source: `UserStageData.BattleResultsByStageId`, a stage counting when `Passed` with
  `PassedAt` at or after that difficulty's `StageData.FirstVictoryTimeInRotation` (equal to the earliest
  `PassedAt` to the second on the mapping account). `[]` is a difficulty entered with nothing won; the
  field is **absent** — never `[]` — when it could not be read reliably. Verified live (11.75.0,
  rotation 10, 2026-09-27) against the client's own map rather than by eye: the stages on the account's
  completed slots — fixed battle slots from the static map plus each slot's random-element stage —
  equal the exported set exactly, Normal 57 + 21 = 78 and Hard 104 + 31 = 135. Compare `rotation` with
  the current one before showing it.
- **`grimForest.difficulties[].completedSlotIds` (schema 31) is the map progress** — every map slot
  completed this rotation, by slot number: battles, chests, altars, random encounters and path nodes
  alike, where `passedStageIds` lists only the battles' stages. Slots are numbered 1–403 on 11.75.0,
  the same numbering on both difficulties, so a fully cleared map is 403 entries. Source: the
  difficulty's own `StageSlots` (`UserFoggyForestStageSlot.Completed`), per-rotation state. The
  **layout** — how many slots there are and what each one is (the static map's
  `FoggyForestSlotData {StageSlot, StageId, ElementType}`: on 11.75.0, 104 fixed battles, 251 path
  hexes and 48 other nodes per difficulty) — is static data, not on this payload; a consumer takes the
  denominator and the node kinds from the metadata catalog. `[]` = nothing completed; **absent**, never
  `[]`, when it could not be read. Verified live (11.75.0, rotation 10): Hard 403 of 403 (map fully
  cleared, as the player confirmed), Normal 232.
- **`grimForest.difficulties[].shopPurchases` (schema 34) is the Grim Forest shop** — one entry per
  shop item bought at least once this rotation: `itemId`, `purchaseCount`, and for a curio item the
  `boughtCurioRank`. Source: the difficulty's `ShopData.ShopItems` (`UserFoggyForestShopItem {Id,
  PurchasesCount, BoughtCurioRank}`; the rank is a `Nullable<int>`). The shop itself — 10 items per
  difficulty, each with a price and a purchase limit — is static data in the metadata catalog
  (`mode_rewards.json` `grimForest.difficulties[d].shop`, same ids); an item absent from this list has
  not been bought. `[]` = nothing bought; **absent** when it could not be read. Verified live (11.75.0,
  rotation 10): Hard items 2 (curio rank 2), 4 (curio rank 1), 8, 9, 10; Normal 8, 9, 10.
- **`grimForest.difficulties[].completedQuestIds` / `claimedQuestIds` (schema 34) are the GRIM FOREST
  QUESTS** — quest prototype ids completed this rotation, and of those the ones whose reward was
  collected. How many are complete is the array's length; the quest **list** (names, targets, prizes) is
  catalog data in the metadata repo (`grimForest.quests`), not on this payload. The quests are ordinary
  server-sent `QuestState`s (no client static data defines them), recognised by their completion:
  `ByFoggyForest` (collect map elements) or `ByBattle` with `AreaTypeId` 14, taken by prototype family
  (id / 10 000) so a Grim Forest achievement with the same battle condition is not mistaken for one.
  13 per difficulty on rotation 10 (`10710001–013` Normal, `10711001–013` Hard). **absent** when not read.
  Verified live (11.75.0, rotation 10): 13 / 13 completed and claimed on both difficulties.
- **Keys in hand (schema 32): `doomTower.goldKeys` / `silverKeys`, `cursedCity.keys`, `grimForest.keys`**
  — how many of each mode's key the account holds at export, one balance per mode shared by both
  difficulties (the game has one pool). They are the account's resources `700`, `701`, `1301` and
  `10000` (Distorted Energy). None of them rides in `resources[]`: they were never on its allowlist,
  and `1301` is the **Sacred Shard** there, because the game reuses that id for Cursed City Keys, so
  these fields are the only place the key balance appears. A forecast starts from this stock plus the
  daily regen (`RslCompanionMetadata` `ModeSchedule` `*.keys`). **Absent, never 0,** when the resources
  dictionary was not read this run; `0` is a real empty stock. A mode block that is absent carries no
  keys either. Verified live 2026-09-28 (11.75.0): Doom Tower Gold 0 / Silver 15, Cursed City 0,
  Grim Forest 20 — the same values through the export, a direct read of the dictionary, and the in-game
  keys panel (0/10, 15/10, 0/8, 20/30). **A balance can exceed the daily amount** (Silver 15/10 shows
  "FULL"), so never cap it at the schedule's `amount`.
- **`classicArena.leagueId` is not a function of `points`.** Classic Arena promotes and demotes
  weekly; mid-week, points can sit past the next threshold while the tier the game applies is still
  last week's. `leagueId` is that applied tier.
- **`tagTeamArena` (schema 27) is Tag Team / 3v3 Arena**, off `UserGameData → UpdatableArena3x3Data`
  (class `UserArena3X3Data`, which derives from `UserArenaData`). `points` is the inherited
  `ArenaPoints`; `leagueId` is the class's own `LeagueId` — **Tag Team's ladder, not Classic Arena's
  id space**, so don't look it up in the Classic league table. `lastRatingUpdateAt` is
  `LastRatingUpdateTime`, when the game last re-rated the tier. The inherited weekly counters read 0
  on a live 3v3 account and are not exported. Prefer this over `account.arena3x3League`, the old
  value-shape probe.

### `siege` — the account's own Siege state (schema 26)

```jsonc
"siege": {
  "currentCycle": 58, "lastFinishedCycle": 57, "lastActionAt": "2026-09-14T11:46:04Z",
  "cycles": [
    { "cycle": 58, "isCurrent": true,  "takenMilestoneRewards": [],     "takenResourceRewards": [],
      "siegeRewardTaken": false, "availableRewardGiven": false, "presetCount": 4 },
    { "cycle": 57, "isCurrent": false, "takenMilestoneRewards": [0],    "takenResourceRewards": [3, 5, 6],
      "siegeRewardTaken": false, "availableRewardGiven": false, "presetCount": 5 }
    // … the history the client keeps, normally three cycles, newest first
  ]
}
```

Off `UserGameData → UpdatableUserSiegeData`. **Only this account's own state.** The same object graph
reaches clanmates' defence assignments and the opposing clan; neither is read, for the same reason
the clan roster is not — nothing on this payload describes another player. The account's defence
presets stay where they were, in `siegePresets[]`.

- `takenResourceRewards` are the game's `SiegeResourceRewardTypeId` values; their names are not yet
  mapped. `takenMilestoneRewards` are indices into the cycle's milestone track.
- `availableRewardGiven` is the game's field name verbatim; what it means is not yet confirmed.
- **Not here yet:** per-hero use counts and the last battle — both are null outside the battle
  phase, and are left out until they have been seen populated.
- **Not on this payload at all:** the Siege schedule behind the in-game phase timer
  (`SiegeSettings.Schedule`) and the reward, tier, bonus and trap tables (`StaticSiegeData`). Those
  are static game data for the metadata catalogs.

### `clanBosses` — Demon Lord, Hydra, Chimera (schema 35)

```jsonc
"clanBosses": {
  "demonLord": {
    "keys": 0,                                       // resource 300, truncated: the game held 0.731
    "bossStartedAt": "2026-10-03T10:14:03Z",        // schema 36
    "nextResetAt":   "2026-10-04T10:08:18Z",        // schema 36
    "difficulties": [ { "difficultyId": 4, "keysSpent": 1, "damage": 202434606 },   // schema 37
                      { "difficultyId": 5, "keysSpent": 1, "damage": 78697208 } ],
    "battlesByDay": [ { "date": "2026-10-02", "battles": 2 }, { "date": "2026-10-03", "battles": 2 } /* … */ ],
    "chestBossRevisionByDifficulty": { "0": 830, "1": 1362, "2": 1891, "3": 1931, "4": 1932, "5": 1932 }
  },
  "hydra":   { "keys": 2, "nextResetAt": "2026-10-07T08:26:44Z",
               "difficulties": [ { "difficultyId": 2, "keysSpent": 1, "damage": 722609449 } ],   // schema 37
               "battlesByDay": [ { "date": "2026-09-25", "battles": 3 } /* … */ ] },
  "chimera": { "keys": 2, "nextResetAt": "2026-10-09T11:30:00Z",
               "battlesByDay": [ { "date": "2026-09-25", "battles": 2 } /* … */ ] }
}
```

Off `UserGameData → UpdatableUserAllianceData`. **Only this account's own state** — the clan-wide boss
records (every member's damage) are reachable and are not read, the same rule as the clan roster.

- **`battlesByDay`** — battles per day for that boss, which is keys spent: the game's own per-day activity
  log (`BattleCountByArea`, areas 4 / 8 / 13). Oldest first; days with no battle are left out; the client
  keeps about 37 days. `[]` = no battles in that window. Count a Hydra / Chimera week from the days at or
  after the previous reset (`nextResetAt` − 7 days).
- **`keys`** — keys in hand at export (resources 300 / 1000 / 1050), whole numbers. The game accrues Clan
  Boss Keys continuously (it holds e.g. 0.731); the export **truncates**, so that is 0 — only keys that can
  be spent count. ABSENT, never 0, when the resources dictionary was not read.
- **`nextResetAt`** — when the Hydra week (`NextRaidRefreshTime`) / Chimera keys (`NextKeysRefreshTime`)
  reset, UTC. Days left = this − now. **Demon Lord (schema 36):** `bossStartedAt` / `nextResetAt` are the
  clan's current boss start and its replacement, read off the clan record (`AllianceBossData.StartTime`,
  `NextRefreshTime`). The time **slides by minutes from day to day** (10:14:03 → 10:08:18 UTC), so never
  derive it from a fixed hour. A snapshot whose `nextResetAt` has passed describes an earlier boss.
- **`chestBossRevisionByDifficulty`** — per Demon Lord difficulty, the boss revision (one boss per day)
  whose chest the account last took, current clan only. It says which difficulties are being hit (those
  on the highest revision), **not** how many keys went into each. **Key = the game's dictionary key, 0–5
  = Easy … Ultra-Nightmare** (the game's difficulty enum is 1–6, the key one less; confirmed by the owner 2026-10-03: Nightmare and Ultra-Nightmare hit on the latest boss (4, 5), Brutal not yet (3, one revision behind)).
- **`difficulties` (schema 37)** — keys spent and damage per difficulty: the Demon Lord's on the CURRENT
  boss (the one `bossStartedAt` started), Hydra's and Chimera's THIS WEEK (up to `nextResetAt`). Only
  difficulties attacked; `[]` = none yet; ABSENT when the clan record could not be read. Read from the
  clan's boss record, **this account's own row only** (`AttackInfoByUserId`, keyed by account id) — the
  other members' rows are never read. `difficultyId` is the game's own: Demon Lord 0–5 = Easy …
  Ultra-Nightmare; Hydra 0–3 = Normal, Hard, Brutal, Nightmare; Chimera 1–6 = Easy … Ultra-Nightmare (each confirmed in game 2026-10-03: Hydra 2 = Brutal, Chimera 5 = Nightmare).
- **Not exported:** champions used per attack (`HeroInfos`) and team power.
- Each sub-block is present whenever `clanBosses` is; the whole block is ABSENT when the alliance data
  could not be validated.

---

## Time-limited content — `soloEvents`, `tournaments`, `battlePass`

New in **schema 33**. The events and tournaments the account is in right now, what it has earned and
claimed in each, and its passes (one per kind since **schema 38**, with season dates). Same rules as Mode progress: each block is **absent when
its read could not be validated**, and the two arrays are **`[]` when the read worked and nothing is
active** — never the other way round. Claims are id sets. Only the account's own data: tournament
leaderboards and cooperation-event leaderboards describe other players and are never read. Cost ~80 ms,
no memory scan. Full mapping: the engine's `docs/events-findings.md`.

```jsonc
"soloEvents": [
  { "eventId": 4428, "questPrototypeId": 364428, "soloTypeId": 1,
    "title": "Gear Enhancement Event", "titleId": 3516,
    "startsAt": "2026-09-28T09:00:00Z", "endsAt": "2026-10-01T09:00:00Z", "claimUntil": "2026-10-02T09:00:00Z",
    "points": 4971, "claimedRewardIds": [1, 2, 3, 4, 5, 6, 7, 8, 9, 10],
    "rewards": [
      { "id": 1, "points": 100, "prize": { "resources": [{ "id": 1, "amount": 50 }] } },
      { "id": 3, "points": 625, "prize": { "items": [{ "id": 8050, "amount": 5 }] } },
      { "id": 10, "points": 4775, "prize": { "champions": [{ "typeId": 10820, "count": 1 }] } }
      /* … 12 tiers … */ ] },
  { "eventId": 4420, "questPrototypeId": 364420, "soloTypeId": 2, "title": "Wicked Path Event",
    "points": 4341, "boardCurrency": 3841, "claimedRewardIds": [105],
    "rewards": [
      { "id": 105, "cost": 500, "row": 1, "column": 5, "parentIds": [], "prize": { "resources": [{ "id": 1111, "amount": 500 }] } },
      { "id": 203, "cost": 1000, "row": 2, "column": 3, "parentIds": [105], "prize": { "items": [{ "id": 19002, "amount": 1 }] } }
      /* … 30 cells … */ ] }
],
"tournaments": [
  { "eventId": 4431, "questPrototypeId": 3364431, "tournamentKindId": 7, "title": "Fire Knight Tournament",
    "startsAt": "2026-09-29T11:00:00Z", "endsAt": "2026-10-01T11:00:00Z", "claimUntil": "2026-10-05T11:00:00Z",
    "points": 2054, "bracketIndex": 1, "claimedRewardIds": [1, 2, 3, 4],
    "rewards": [ { "id": 1, "points": 250, "prize": { … } } /* … 7 tiers … */ ] }
],
"battlePass": { "passId": 1037, "kindId": 2, "status": 1, "points": 365,
  "startsAt": "2026-09-16T09:00:00Z", "endsAt": "2026-10-14T09:00:00Z",
  "tracks": [ { "trackId": 1, "claimedLevels": [1, 2, /* … */ 36] },
              { "trackId": 2, "claimedLevels": [] }, { "trackId": 3, "claimedLevels": [] } ] },
"battlePasses": [
  { "passId": 1037, "kindId": 2, "status": 1, "points": 365,
    "startsAt": "2026-09-16T09:00:00Z", "endsAt": "2026-10-14T09:00:00Z",
    "tracks": [ { "trackId": 1, "claimedLevels": [1, 2, /* … */ 36] },
                { "trackId": 2, "claimedLevels": [] }, { "trackId": 3, "claimedLevels": [] } ] },
  { "passId": 2004, "kindId": 3, "status": 2, "points": 2820,
    "startsAt": "2026-05-19T11:30:00Z", "endsAt": "2026-07-20T11:30:00Z",
    "tracks": [ { "trackId": 1, "claimedLevels": [1, 2, /* … */ 25] },
                { "trackId": 2, "claimedLevels": [] }, { "trackId": 3, "claimedLevels": [] } ] }
]
```

### Where it comes from

Every one of them is a **quest** on the account (`UserQuestData.OpenedStates`). A solo event is a quest
with `GlobalEventSoloTypeId` set; a tournament is one completed `ByTournament`, whose `GlobalRatingInfo`
holds the account's points, bracket, own rank and **its bracket's** tier table with a `Taken` flag per
tier. The event's id, title, dates and — for solo events — reward table come from the **server-sent
event catalog** (`GlobalEvents`), not static data. **That is why reward contents ride on this payload
while Mode progress rewards do not:** these tables exist only while the event runs, in no static file,
so a consumer has nowhere else to get them. When the catalog cannot be read, `eventId`, `title`,
`titleId`, `startsAt`, `endsAt` and a solo event's `rewards` are omitted and everything else still
ships; key on `questPrototypeId`, which is always present.

### "Active"

An event is on the payload when its quest is **open and its claim window (`claimUntil`) has not
closed** — so a tournament that ended yesterday but still has prizes to take is here, and one whose
prizes expired is not. Events the account has never entered (the in-game teasers) are not here. The
deadline is compared as UTC; that the client's times are UTC is inferred from their `:00` alignment,
not proven.

### Fields that are not what they look like

- **`points` on a board event (`soloTypeId` 2) is points *earned*.** The board is bought cell by cell
  with the same points, so `boardCurrency` is what is left to spend, and `points − boardCurrency` is
  the cost of the cells taken. A board cell carries `cost` (and `row`, `column`, `parentIds`) instead of
  `points`. Observed: 4,341 earned = 3,841 unspent + 500 for cell 105.
- **Solo-event `points` is `TotalPoints` when the game sets it, else the sum of the per-day progress.**
  On the Supporters Summon Pool (`soloTypeId` 5) the two disagreed on the mapping account (55 vs 40);
  which one the screen shows is not yet confirmed. Summon pools carry no reward table (`rewards: []`).
- **Tournament claims come from per-tier `Taken` flags**, not an id list (the game's id list is null on
  tournament quests); `claimedRewardIds` is the set of tier ids with the flag set.
- **`position` is the account's own rank** and is often absent or `0` (observed on tournaments with no
  ranking); it is never anyone else's.
- **`rewards` on a tournament is the account's bracket only.** Tables differ between brackets; position
  (leaderboard) rewards are not exported.
- **`prize` is a trimmed `UserPrize`**: `resources` (the `ResourceTypeId` space of `resources[]`, though
  not every id is on that allowlist), `items` (inventory item ids — potions, chickens, Basalt 19002…),
  `champions` (type ids), `souls`, `artifacts` (the `artifacts[]` id spaces), `avatarIds`, `frameIds`,
  and since schema 41 `randomGemstones`: `count` gemstones drawn at random when the prize is taken, from
  `rarityIds` (the game's `ItemRarity`, the same 1–6 as artifacts' `rarityId`), optionally limited to
  `onlyShapeIds` or excluding `excludedShapeIds` (the socket shapes in `relic-enums.json`). Before 41 it
  was only named in `otherKinds`. Any other populated kind is named in `otherKinds` rather than dropped,
  so an empty-looking prize never is. Amounts are numbers (resources are stored as doubles).

### `battlePasses[]` — every pass kind, with its season (schema 38)

The game calls every pass `BattlePass` internally. The wire keeps that name because `forge` already
means the forge materials in `resources[]` (schema 22). The account keeps every pass it ever had (42 on
the mapping account). Sometimes two kinds run at once, which a single object could not carry, hence the
array:

- **At most one entry per `kindId`**, sorted by `kindId`: the pass of that kind with `status` 1
  (running), else the newest one of that kind (`status` 2, ended).
- **`[]`** = the read worked and the account has no pass with a recorded kind. **Absent** = the passes
  could not be read. A failed read is never sent as `[]`.
- **`kindId` 0 is never sent.** It is what the game recorded on the oldest Forge Passes (1000–1011,
  ended in 2023), before kinds existed.

| `kindId` | Pass (in-game name) | internal family | ids | levels |
|---:|---|---|---|---:|
| 2 | **Forge Pass** | `forge-pass` | 1000-series | 50 |
| 3 | **Champion Pass** | `hero-pass` | 2000-series | 25 |
| ? | Pioneer Pass | `novice-pass` | 4000-series | 25 |
| ? | Apprentice Pass | `apprentice-pass` | 3000-series | 20 |

2 and 3 are confirmed by joining the account's passes to the game's static pass families. **There is no
"Hero Path"**: the `hero-pass` family is shown in game as the Champion Pass. The static pass has no kind
field, so the Pioneer and Apprentice kinds will only be known from an account that has one; a consumer
should key on `kindId` and treat an unknown one as a pass it has no label for.

**Fields.** `points` is `EarnedPoints`. `tracks` is the game's `CollectedLevelsByTypeId`: per reward
track, the levels whose reward was collected. **Track ids (the game's `BattlePassTypeId`):** 1 = Free is
certain. 2 = the paid track (the Forge Pass's Gold, the Champion Pass's Elite) and 3 = Platinum are
inferred. Track 3 first appears on pass 1028, the first pass with Platinum intro art, but the enum's
numeric values are not read. A track key can be present with nothing behind it: Champion Passes carry an
empty track 3 and have no Platinum rewards. An empty premium track means nothing was collected there, not
that it was not bought.

**`startsAt` / `endsAt`** (UTC, ISO 8601 with `Z`, the same format as `soloEvents[]`):

- `startsAt` is the static pass's `Start`. `endsAt` is when points stop counting: `Start` +
  `DurationDays`. **The game stores no end instant; those two fields are its whole definition of the
  season.** Checked: on pass 1037 the sum (2026-10-14 09:00) is exactly the deadline the server put on
  every one of the pass's challenge quests.
- A pass timed **per player** (static `CategoryId` 2, the Apprentice Pass: 25 days from the player's own
  activation) uses the account's `StartTime` and `UserDurationDays` instead. This branch has not been
  seen on a live account.
- Each is **omitted** when it could not be read (the static catalog is unreachable, or the pass is not
  in it). `endsAt` is also omitted on a pass the game force-stopped, whose real end is not recorded.
- **There is no `claimUntil` on passes.** Unlike events, the game defines no claim window after a pass
  ends, so the field is never sent.

### `battlePass` — deprecated, one pass

Kept so consumers of schemas 33–37 keep working, with the same fields as a `battlePasses[]` entry
(including the schema-38 dates). It is **the running Forge Pass (`kindId` 2, `status` 1) if there is
one, else the newest pass of any kind.** Before schema 38 it was the first running pass of any kind,
which is the same pass whenever a Forge Pass is running. **It will be removed in the first schema
released on or after 2027-01-05.** Read `battlePasses[]`.

### Levels and points are static data

**Levels and rewards are not here**: they are static data, in RslCompanionMetadata
`exports/battle_pass_index.json` keyed by `passId`. This is the same split as Mode progress. Thresholds
are **cumulative**, and level 1 is free at 0:

- **Forge Pass:** 50 levels, L2 = 20, then +10 a level, L50 = 500. Not a flat 10 a level: the first
  step costs 20. 365 points is level 36, and the account had collected exactly 36 free levels.
- **Champion Pass / Pioneer Pass:** 25 levels, irregular steps of 10–20, L25 = 410 (pass 2000: +17 a
  level to 408).
- **Apprentice Pass:** 20 levels, +20 a level, L20 = 380.

**There is no daily points cap in the game's data.** Points are the rewards of the pass's own challenge
quests, which the server sends per pass. On Forge Pass 1037: 4 daily challenges × 5 = **20 a day**, and
5 weekly challenges × 12 = **60 a week**, over 4 weeks of challenges. That is at most 800 points against
500 for level 50. These are observations of one pass, not a rule. They are not exported.

### `frontier` — the Frontier Event map (schema 39)

A **Frontier Event** (internally `ConquestEvent`) is a `soloEvents[]` entry with `soloTypeId` **8**, so
title and dates arrive the usual way. Its reward table is not a tier list but a **map**, sent by the
server for that event only, so it rides on the entry as `frontier`, beside the account's progress. Its
`rewards` stays `[]`. **Since schema 39 its `points` is the account's Frontier Points**
(`UserConquestEvent.EarnedPoints`); before, it read 0.

```jsonc
{ "eventId": 4447, "soloTypeId": 8, "title": "Frightful Frontier",
  "startsAt": "2026-10-05T10:30:00Z", "endsAt": "2026-10-23T10:30:00Z", "claimUntil": "2026-10-24T10:30:00Z",
  "points": 4, "claimedRewardIds": [], "rewards": [],
  "frontier": {
    "purchaseStatus": 0,
    "unlockPoints": [ { "rarity": 2, "points": 15 }, { "rarity": 3, "points": 25 },
                      { "rarity": 4, "points": 40 }, { "rarity": 5, "points": 60 } ],
    "outposts": [
      { "id": 2, "rarity": 1, "typeId": 1, "neighbourIds": [1, 3, 9, 10],
        "questPrototypeIds": [10860200, 10860850, 10860750, 10860450],
        "rewards": [ { "slot": 0, "track": 1, "prize": { "items": [{ "id": 10002, "amount": 1 }] } },
                     { "slot": 2, "track": 2, "prize": { "resources": [{ "id": 3000, "amount": 25 }] } } /* … 4 */ ],
        "started": true, "completedQuestIds": [], "claimedSlots": [],
        "quests": [ { "questPrototypeId": 10860200, "completed": false, "condition": "Battle",
                      "countRequired": 10, "countCollected": 0, "points": 1 } /* … 4 */ ] }
      /* … 22 outposts */ ] } }
```

**How the event works** (from the data and the game's own strings):

- Each outpost has **4 quests**, and each quest pays **1 Frontier Point** (item 26000). That is 88
  points across the 22 outposts of event 4447.
- An outpost opens when an **adjacent outpost is completed** and the account has **the points its rarity
  needs** (`unlockPoints`): Rare 15, Epic 25, Legendary 40, Mythical 60. These are the event's
  milestones. Outpost 1 (`typeId` 2) is open from the start. The final outpost (`typeId` 5) opens only
  once all the others are completed. A time-limited outpost carries `unlockAfterMinutes` / `unlocksAt`
  (none on 4447).
- **Rarity** 1–5 = Uncommon, Rare, Epic, Legendary, Mythical Zone, which are the game's five labels in
  order. This is not yet checked against the screen.
- Each outpost has **4 reward slots**, each on **track 1 = Basic** (free) or **track 2 = Explorer** (the
  paid Explorer Pass). A slot is collected when it is in `claimedSlots`. An outpost is completed when
  `completedQuestIds` holds all of its `questPrototypeIds`.
- **`quests` is `[]` on an outpost the game has not revealed.** The server creates its quest states only
  when it opens, so until then only the prototype ids are known. Prototype ids repeat across outposts,
  so key a quest on (outpost, prototype). There is no quest-type id or text on a quest: `condition` is
  the completion kind (Battle, Hero, Artifact, …) with its counts.
- After `endsAt` the event is in its reward phase: no more points or quests, but slots can still be
  claimed until `claimUntil`.
- `purchaseStatus` is raw: 0 = Explorer Pass not bought; other values have not been seen.
- `TopRewards`, the event's five headline prizes, have no points and are not exported.

### Titan Event — milestones (schema 41)

A **Titan Event** (internally a "universal" event) is a `soloEvents[]` entry with `soloTypeId` **4**. It
runs for weeks (event 4440 "Ingenious Titan Event": 2026-10-05 → 10-22, claims until 10-22 11:30), and
its points are **Titan Points**, which the account does not earn in the Titan Event itself. They are
prizes in other events and tournaments, the "specially labeled" ones. Since schema 41 its `rewards`
carries the milestone table, which before read `[]`. The table is sent by the server per event, like
every other event's, and the next Titan Event will have different prizes, thresholds and dates in the
same shape.

```jsonc
{ "eventId": 4440, "questPrototypeId": 364440, "soloTypeId": 4, "title": "Ingenious Titan Event",
  "startsAt": "2026-10-05T09:00:00Z", "endsAt": "2026-10-22T09:00:00Z", "claimUntil": "2026-10-22T11:30:00Z",
  "points": 50, "claimedRewardIds": [],
  "rewards": [
    { "id": 1, "points": 10, "milestone": 1, "prize": { "resources": [{ "id": 2, "amount": 100000 }] } },
    { "id": 2, "points": 20, "milestone": 1, "prize": { "items": [{ "id": 10001, "amount": 1 }] } },
    /* … */
    { "id": 11, "points": 120, "milestone": 2, "prize": { "items": [{ "id": 8001, "amount": 5 }] } },
    { "id": 31, "points": 640, "milestone": 4, "prize": { "randomGemstones": [{ "count": 1, "rarityIds": [4] }] } },
    { "id": 50, "points": 1500, "milestone": 5, "prize": { "champions": [{ "typeId": 10740, "count": 1 }] } } ] }
```

- **`milestone` is the reward tab**, "Milestone 1" … "Milestone 5" in game (`l10n:universal-event/reward-tab`).
  On 4440 each tab holds 10 rewards: 1 → 10–100 points, 2 → 120–300, 3 → 330–600, 4 → 640–1,000,
  5 → 1,050–1,500. It is present on Titan Event rewards only.
- **`points` is a cumulative threshold**, as on a points-tier event, and the account's `points` is its
  Titan Points total (the sum of its per-day progress; the game leaves `TotalPoints` unset). A reward is
  claimable when `points` ≥ its threshold and its `id` is not in `claimedRewardIds`.
- **Reward ids run across the tabs** (1–50 on 4440), so `claimedRewardIds` keys them as on any solo
  event. That the game records Titan claims in the same list is assumed from the shared quest shape;
  no claim has been observed yet.
- **Where Titan Points come from is already on the payload.** Titan Points are inventory item **10600**
  (`l10n:bmi/name?id=10600` = "Titan Points"), and a labelled event or tournament pays them as an
  ordinary tier prize: `prize.items` with `id` 10600. Gear Hunters Event 4444 paid 10 + 20 + 50 = 80 over
  three of its tiers, which is the "+ 80 TP" in its internal label. So, for every other entry in
  `soloEvents[]` and `tournaments[]`, the Titan Points on offer are the sum of item 10600 over its
  `rewards`, and the ones still to come are that sum over unclaimed tiers.
- `TopRewards`, the five headline prizes on the event's banner, are not exported. They repeat
  milestone prizes and carry no threshold.

### Verified

Titan Event (schema 41), live on 11.75.0 (2026-10-07): event 4440, 50 milestones in 5 tabs read in full,
every prize decoded (`otherKinds` empty), 50 Titan Points, nothing claimed. Item 10600 summed over the
open labelled events matches each one's internal "+ N TP" label: Gear Hunters 80, Gear Enhancement 60,
Spider Turn Attack 50, Ice Golem Turn Attack 40. The same run decoded the Frontier Event's three
random-gemstone slots (outposts 28 / 47 / 48: Epic, Legendary, Mythical). Frontier (schema 39), live on 11.75.0 (2026-10-05): event 4447, 4 Frontier Points = 4 completed quests
at 1 point each; Outpost 1 completed with slots 0–3 claimed; Outposts 2 and 9 started, one quest at 4/10;
unlock thresholds 15 / 25 / 40 / 60. Passes (schema 38), live on 11.75.0 (2026-10-05): Forge Pass 1037 at 365 points with free levels 1–36
collected, 2026-09-16 09:00 → 2026-10-14 09:00; Champion Pass 2004 ended at 2,820 points with 1–25
collected, 2026-05-19 11:30 → 2026-07-20 11:30. Live on 11.75.0 (2026-09-30), memory against memory: every solo event's points equal its per-day
progress (Gear Enhancement 4,527 + 431 + 13 = 4,971, with tiers 1–10 ≤ 4,775 claimed and 11 at 5,600
not), board currency plus cell costs equal points, and the pass's level count matches its thresholds.
**Not yet checked against the in-game screens.**

---

## `inbox` — the in-game Inbox (schema 40)

Every reward waiting in the account's Inbox, read off `UserGameData → UpdatableUserInboxData.Items`.
One list walk, no scan (~30 ms).

```jsonc
"inbox": {
  "items": [
    { "id": 26006, "typeId": 60, "read": true,
      "receivedAt": "2026-10-05T04:07:38Z", "expiresAt": "2027-01-13T04:07:38Z",
      "prize": { "resources": [ { "id": 2, "amount": 25000 } ] } },
    { "id": 26064, "typeId": 7, "read": true,
      "receivedAt": "2026-10-06T20:50:22Z", "expiresAt": "2027-01-14T20:50:22Z",
      "prize": { "artifacts": [ { "kindId": 9, "rankId": 6, "rarityId": 5, "setKindId": 0 } ] },
      "artifacts": [ { "artifactId": 166281, "kindId": 9, "rankId": 6, "rarityId": 5, "level": 0,
                       "primaryBonus": { "statKindId": 1, "value": 900, "isAbsolute": true, … },
                       "secondaryBonuses": [ … 4 ], "equippedByHeroId": null, … } ] },
    { "id": 26072, "typeId": 132, "read": false,
      "receivedAt": "2026-10-07T11:47:35Z", "expiresAt": "2027-01-15T11:47:35Z",
      "prize": { "items": [ { "id": 3001, "amount": 1 } ] } }
  ] }
```

- **It is the whole Inbox.** Collecting an item removes it from the game's list, and so does expiry.
  A present `inbox` replaces what a consumer stored, and `items: []` is an empty Inbox. **Absent means
  unread** — keep the stored block, never blank it.
- **`typeId` is the game's `InboxTypeId`**, the source the Inbox names on each item ("Overflow from full
  storage", "Free Gift. From Us, To You", "Cursed City Reward" …). It is raw.
  [`inbox-types.json`](inbox-types.json) maps every id to its **`sourceKey`**, the key into the game's
  localization dictionary (RslCompanionMetadata `exports/localization_en.json`), with the English text
  beside it. The mapping is a switch in the game's code, read out of it (77 of 138 ids carry a title),
  and was checked against the open Inbox for every id on the mapping account. An id with no entry gets a
  neutral title.
- **`prize` is the event-reward shape** (`eventPrize`): the same decoder reads both, so anything that
  draws an event reward draws an Inbox item. Item ids are the inventory id space (`items`), resource ids
  the `resources[]` one.
- **An overflowed artifact arrives twice: slim in `prize.artifacts`, full in `artifacts`.** A full vault
  sends new gear to the Inbox as a real piece with an id and rolled substats. `artifacts` carries it in
  the `artifacts[]` item shape, same order as the prize. It is **not** in the top-level
  `artifacts[]`/`accessories[]` (0 of 62 on the mapping account), so adding the two never double-counts;
  collecting it in game moves it into the vault, where the next export finds it.
- **`read` is "opened", not "collected".** A collected item is gone from the list.
- **Timestamps are UTC**, verified: item 26072 was delivered during the mapping session and read
  `11:47:35Z` four minutes before 11:51 UTC. `expiresAt` is the game's deletion instant; most types
  live 100 days, energy gifts one.
- **No sender, anywhere.** `InboxItem` records none, including on a friend's gift, so the block
  describes only the account.
- **Not here: Raid Mail.** `UserInboxData.PersonalMessages` (`Id`, `ValidTill`, `IsSeen`, `IsRead`,
  `IsReplied`, `IsRewardTaken`) was empty on the mapping account. It ships when its filled shape has been
  seen. `questPrototypeId`, `parentId` and `chestOrRandom` are read but have not been seen populated.

Verified live on 11.75.0 (2026-10-07): 102 items, `typeId` 7 ×62, 12 ×2, 60 ×13, 112 ×24, 132 ×1. With the
Inbox open, the game's own item views showed the same 102 ids with the titles `inbox-types.json` gives.

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
| 41 | v1.45.0 | 2026-10-07 | **Additive: the Titan Event's milestones, and `randomGemstones` prizes** (see [Titan Event](#titan-event--milestones-schema-41)). A Titan Event (internally a "universal" event, `soloTypeId` 4) sent `rewards: []`. Its milestone table is now in `rewards`, every reward with its Titan Point threshold, its prize and the new `milestone` (the in-game "Milestone 1–5" tab). Titan Points are item 10600, so the Titan Points a labelled event or tournament pays are already in its `rewards`. Separately, `eventPrize.randomGemstones` (`count`, `rarityIds`, optional `onlyShapeIds` / `excludedShapeIds`) decodes `RandomRelicStonesPrizes`, which before was only named in `otherKinds`. It applies to every prize, Frontier outposts and the Inbox included. Read live off event 4440 (11.75.0, 50 milestones in 5 tabs). A consumer that ignores both is exactly as correct as on schema 40. |
| 40 | v1.44.0 | 2026-10-07 | **Additive: `inbox`** — the in-game Inbox (see [`inbox`](#inbox--the-in-game-inbox-schema-40)): every reward waiting to be collected, each with `id`, `typeId` (the game's `InboxTypeId`; titles via the new [`inbox-types.json`](inbox-types.json)), `read`, `receivedAt` / `expiresAt` (UTC), `prize` (the event-reward shape) and, for an overflowed artifact, the full `artifacts[]` record with stats — which is not also in the vault. The whole Inbox: `items: []` = empty, absent = unread. No sender is recorded. Verified live (11.75.0, 102 items). A consumer that ignores it is exactly as correct as on schema 39. |
| 39 | v1.43.0 | 2026-10-05 | **Additive: `soloEvents[].frontier` — the Frontier Event map and progress; corrective: a Frontier Event's `points`** (see [Time-limited content](#time-limited-content--soloevents-tournaments-battlepass)). A Frontier Event (internally `ConquestEvent`, `soloTypeId` 8) now carries `frontier`: every outpost with its rarity, type, neighbours, optional unlock time, 4 quest ids and 4 reward slots (track 1 Basic, 2 Explorer), plus the account's state per outpost (started, completed quests, claimed slots) and its revealed quests (condition, counts, points), and `unlockPoints`, the Frontier Points each rarity needs (4447: 15 / 25 / 40 / 60). Its `points` was 0 on every Frontier Event and is now the account's Frontier Points. Its `rewards` stays `[]`. Absent on other events, and when unread. Verified live (11.75.0, event 4447). A consumer that ignores `frontier` is exactly as correct as on schema 38, except that it now sees a Frontier Event's real points. |
| 38 | v1.43.0 | 2026-10-05 | **Additive: `battlePasses[]`, and `startsAt` / `endsAt` on every pass; `battlePass` is deprecated and changes selection** (see [Time-limited content](#time-limited-content--soloevents-tournaments-battlepass)). `battlePasses[]` carries every pass kind the account has, one entry per `kindId`: the running pass of that kind, else its newest. This matters when two run at once, e.g. the Forge Pass and the Champion Pass. `kindId` 2 = Forge Pass and 3 = Champion Pass (internally `hero-pass`; there is no "Hero Path") are confirmed. Kind 0, the 12 oldest Forge Passes, is never sent. `[]` = no pass, absent = unread. Each pass now has `startsAt` (static `Start`) and `endsAt` (`Start` + `DurationDays`; the game stores no end instant, and on pass 1037 this equals the server's challenge-quest deadline). Both are omitted when unread, and `endsAt` on a force-stopped pass. There is no `claimUntil` on passes: the game defines no claim window. **`battlePass` stays until the first schema released on or after 2027-01-05** and is now the running Forge Pass, else the newest pass of any kind (before: the first running pass of any kind). Track ids are unchanged: 1 = Free certain, 2/3 inferred. Level tables went to RslCompanionMetadata `battle_pass_index.json`, not the payload. Verified live (11.75.0). A consumer that ignores the new fields is exactly as correct as on schema 37, except while a Champion Pass runs without a Forge Pass, when `battlePass` now names the newest pass rather than the Champion Pass. That is the same pass unless an older Champion Pass is still running. |
| 37 | v1.39.0 | 2026-10-03 | **Additive: `clanBosses.{demonLord,hydra,chimera}.difficulties[]`** — `{ difficultyId, keysSpent, damage }` per difficulty attacked: the Demon Lord's current boss, Hydra's and Chimera's current week. Read from the clan's boss record, this account's own row only. Verified live (11.75.0): the key balances fell by exactly the keys read. A consumer that ignores it is exactly as correct as on schema 36. |
| 36 | v1.38.0 | 2026-10-03 | **Additive: `clanBosses.demonLord.bossStartedAt` / `nextResetAt`** — the clan's Demon Lord boss start and next reset, UTC, read off the clan record (`AllianceBossData`). The reset slides by minutes daily, so it is read, not computed. Absent when the clan record could not be read. A consumer that ignores them is exactly as correct as on schema 35. |
| 35 | v1.37.0 | 2026-10-03 | **Additive: `clanBosses`** — Demon Lord, Hydra and Chimera: keys in hand (resources 300 / 1000 / 1050, whole keys; the Demon Lord's accrues continuously and is truncated), battles per day from the game's activity log (~37 days), the Hydra and Chimera reset times, and per Demon Lord difficulty the boss revision whose chest was last taken (difficulty key 0–5 = Easy…Ultra-Nightmare). Own state only. Verified live (11.75.0). A consumer that ignores it is exactly as correct as on schema 34. |
| 34 | v1.36.0 | 2026-10-03 | **Additive: Grim Forest shop and quests** — `grimForest.difficulties[].shopPurchases` (items bought this rotation: `itemId`, `purchaseCount`, curio `boughtCurioRank`; prices and limits in the metadata catalog's `shop`) and `completedQuestIds` / `claimedQuestIds` (Grim Forest quest prototype ids; the quest list is catalog data). Absent, not `[]`, when unread. Verified live (11.75.0, rotation 10). A consumer that ignores them is exactly as correct as on schema 33. |
| 33 | v1.33.0 | 2026-09-30 | **Additive: three new top-level blocks — `soloEvents[]`, `tournaments[]`, `battlePass`** (see [Time-limited content](#time-limited-content--soloevents-tournaments-battlepass)). The solo events and tournaments the account is in and can still act on (open quest, claim window not closed), each with points, claimed reward ids and — because these tables exist in no static data — the reward table with trimmed prize contents; tournaments carry the account's own bracket and rank only, never a leaderboard. `battlePass` is the Forge Pass (internally `BattlePass`): the active pass or the newest, with points and collected levels per track (1 = Free; 2/3 = Gold/Platinum, inferred); its level table is static data and not here. Absent when unread, `[]` when nothing is active. Titles, dates and solo rewards come from the server-sent catalog and are omitted, not failed, when it is unreachable. Verified live (11.75.0) memory-against-memory; not yet against the in-game screens. A consumer that ignores them is exactly as correct as on schema 32. |
| 32 | v1.31.0 | 2026-09-28 | **Additive: keys in hand** — `doomTower.goldKeys` / `silverKeys`, `cursedCity.keys`, `grimForest.keys`: each mode's key balance at export (resources `700` / `701` / `1301` / `10000`), one per mode for both difficulties (see [Mode progress](#mode-progress--classicarena-livearena-doomtower-cursedcity-grimforest-siege)). Not in `resources[]` (`1301` is the Sacred Shard there). Absent, not 0, when the resources dictionary was not read. A consumer that ignores them is exactly as correct as on schema 31. |
| 31 | v1.30.0 | 2026-09-27 | **Additive: `grimForest.difficulties[].completedSlotIds`** — the map progress: every map slot completed this rotation (battles, chests, altars, random encounters, path nodes), by slot number 1–403, from the difficulty's `StageSlots`. The map layout is static data and not on the payload. Absent, not `[]`, when it cannot be read. Verified live (11.75.0, rotation 10): Hard 403/403, Normal 232. Ships in the same release as schema 30. A consumer that ignores it is exactly as correct as on schema 30. |
| 30 | v1.30.0 | 2026-09-27 | **Additive: `grimForest.difficulties[].passedStageIds`** — every stage won this rotation, as a set of stage ids (`ZZZZ D SSS`, zones 1401–1404; the metadata `mode_rewards.json` grimForest stage keys), from passed `StageStats` counted from the difficulty's `StageData.FirstVictoryTimeInRotation` (see [Mode progress](#mode-progress--classicarena-livearena-doomtower-cursedcity-grimforest-siege)). Stages, not map slots: fixed battles and random-element battles both count. `[]` when entered with nothing won; absent, not `[]`, when it cannot be read reliably. Verified live (11.75.0, rotation 10) against the client's map: Normal 78, Hard 135, exact. RaidTools' `ConsolidatedJsonSyncAdapter` 4.8.0 already reads it. A consumer that ignores it is exactly as correct as on schema 29. |
| 29 | v1.29.0 | 2026-09-26 | **Additive: `cursedCity.difficulties[].passedStageIds`** — every stage won this rotation, as a set of stage ids (`RRRR D SSS`, 101 per difficulty; the metadata `mode_rewards.json` stage keys), from passed `StageStats` counted from the difficulty's `FirstVictoryTimeInRotation` (see [Mode progress](#mode-progress--classicarena-livearena-doomtower-cursedcity-grimforest-siege)). `[]` when entered with nothing won; absent, not `[]`, when it cannot be read reliably. Verified live (11.75.0, rotation 34): Normal 101, Hard 101. RaidTools' `ConsolidatedJsonSyncAdapter` 4.7.0 already reads it. A consumer that ignores it is exactly as correct as on schema 28. |
| 28 | v1.28.0 | 2026-09-25 | **Additive: `doomTower.rotation` and `doomTower.difficulties[].floorsCompleted`** — the rotation number (`UserDoomTowerData.Id`) and the highest floor cleared this rotation, 0–120 (passed `StageStats` of the current tower map, counted from the rotation start; see [Mode progress](#mode-progress--classicarena-livearena-doomtower-cursedcity-grimforest-siege)). `floorsCompleted` is absent, not 0, when it cannot be read reliably. Verified live (11.75.0): rotation 71, Normal 49, Hard 120. Also corrected: `stageIndicator` is shaped `70 M D FFF` (tower map, difficulty, floor), not `70 D 1 FFF`. A consumer that ignores the new fields is exactly as correct as on schema 27. |
| 27 | v1.27.0 | 2026-09-25 | **Additive: new top-level `tagTeamArena`** — Tag Team (3v3) Arena `points`, `leagueId` (Tag Team's own ladder) and `lastRatingUpdateAt`, off `UserArena3X3Data` (see [Mode progress](#mode-progress--classicarena-livearena-doomtower-cursedcity-grimforest-siege)). Absent when its read fails. Verified live (11.75.0): 1,190 points, league 14. A consumer that ignores it is exactly as correct as on schema 26. |
| 26 | v1.24.0 | 2026-09-24 | **Additive: new top-level `siege`** — the account's own Siege cycle state and claimed rewards, never other players' (see [`siege`](#siege--the-accounts-own-siege-state-schema-26)). **Additive: `liveArena.victoriesForRegularReward`** — the "Wins N/35" quest progress. **BREAKING (field rename): `doomTower.difficulties[].rotationStartedAt` → `firstEnteredAt`**, because it is not the rotation start (rotations are global; confirmed in-game). Same value — read `firstEnteredAt ?? rotationStartedAt`. Also recorded: Cursed City and Live Arena end dates confirmed in-game, settings times are UTC. |
| 25 | v1.23.0 | 2026-09-24 | **Additive: five new top-level objects — `classicArena`, `liveArena`, `doomTower`, `cursedCity`, `grimForest`.** Where the account stands in each mode and which rewards it has claimed; see [Mode progress](#mode-progress--classicarena-livearena-doomtower-cursedcity-grimforest-siege). Each is absent when its read fails. **Behaviour change: `account.liveArenaPoints` is now the named `UserLiveArenaData.Points`** instead of a value-shape probe, so its value can change for the same account. Reward contents and rotation end dates are static data and not on this payload. A consumer that ignores the new keys is exactly as correct as on schema 24. |
| 24 | v1.22.0 | 2026-09-15 | **Additive: new top-level `souls[]` — the real Awakening Soul inventory.** See [`souls[]`](#souls--awakening-soul-inventory) above. Champion-bound material (`championBaseId`, `isPerfect`, `level`, `championRarity`) decoded from `UserGameData → UpdatableUserDoubleAscendData`'s own key, and **not** the Soulstone/Soul Essence currency already in `resources[]` (see schema 23's soul-economy note) or a spare duplicate champion copy (Ascension material, unrelated to this). A consumer that ignores the field is exactly as correct as it was on schema 23. Verified live (11.75.0, account Magikwolf): 156 owned souls decoded, matching the in-game Soul Collection tally exactly; 40 sample keys' `championRarity` matched every one of those champions' real rarity in `champion_index.json`, and three shop listings literally named "Pestilus/Aothar/Captain Temila Split Soul" decoded to those exact champions with the single lit star in each matching the decoded `level`. **Consumer impact:** RaidTools' previous "Champion Souls" tab (its own schema-independent feature, not part of this contract) treated a spare Vault/Reserve Vault duplicate as a "soul" — that was never derived from this payload and is being replaced with a real reader of this field. |
| 23 | v1.20.0 | 2026-09-14 | **Corrective: `resources[]` 146 → 145 entries — `10202` and `10205` removed as mislabelled, `10650` Prism Crystals added; shard counts no longer fall back to unrelated values.** See [Shards & Remnants](#shards--remnants-corrected-in-schema-23). The game's `ResourceTypeId` enum, its own localized strings and the in-game screens agree: (1) **`10202` "Prism Crystals" was Extra Grim Gold** (`FoggyForest_Hard_Gold`) — every schema ≤ 22 payload carried a Grim Forest balance under that label (2,755 on the reference account against 55 Prism Crystals on the Summoning Portal bar), and **real Prism Crystals is the game item `10650`**, which the exclusive allowlist had been dropping; (2) **`10205` "Eternal Essence" was Extra Grim Charms** (`FoggyForest_Hard_TreasureHuntPart`) and is gone with no replacement; (3) **the five shard ids are also the game's ids for Live Arena Crests (`1202`/`1203`) and Cursed City Keys / Candles (`1301`–`1303`)**, and four unrelated items (Grim Forest map chests, Fusion Tokens) were mapped onto them too — schema ≤ 22 exported correct shard counts only because the shard read ran last, and would have exported Mystery Shard = 30,225 (a Crest count) had it failed; shard ids and names are unchanged. `3000` Primal Quartz, `4000` Starstone and `8100` Cursed Remnants were checked and are correct (the enum's `MythicalDust` / `Meteor` / `ParticleSummon` are internal names). The ids `10101`/`10102`/`10104`/`10105`/`10201`/`10204` this file called a "defunct earlier soul system" are Grim Forest currencies as well; still never emitted. **Consumer impact:** (1) **discard every stored `10202` / `10205` value** — there is nothing to migrate, and a consumer mapping `10202` into a Prism Crystals field (RaidTools' `MapAccountResources` → `PrismCrystals`) has been storing Extra Grim Gold and must switch to `10650`; (2) `10650` history begins here — absence in an older snapshot is not zero; (3) **a shard read that fails now exports `0`**, so a sudden all-zero shard set is a failed read, not a spent portal; (4) RaidTools' `PrismShards` field is fed from `1302`, which is the Primal Shard — right value, misleading name. |
| 22 | v1.20.0 | 2026-09-14 | **Additive: `resources[]` 55 → 146 entries — the 91 Forge crafting materials, which every earlier export DROPPED.** 47 gear forge materials (`601`, `602`, `611`–`699`, `6900`–`6925`) and 44 relic craft materials (`4100`–`4203`); see [Forge materials](#forge-materials-new-in-schema-22--previously-dropped-entirely). Nothing changes shape and no existing id changes. Same defect as schema 11's relic economy and the chickens and soul essences before it: the engine's resource allowlist is exclusive and none of these ids was on it. They sit in the same resources dict as Starstone, so they cost nothing to read. **Ids are the game's own `ResourceTypeId` values**, bound from the client's runtime enum reflection table rather than inferred from order, and **names are the game's localized strings** read from its localization dictionaries. Verified against the live client (11.75.0): all 91 present and named, and every quantity equal to a raw read of the resources dict taken at the same moment. Not yet checked against the in-game Forge screen. **Consumer impact:** (1) an account's history for these ids begins here — absence in an older snapshot is not a zero balance; (2) each family's tiers run consecutively from its first id, but not across families (681/684/687 are three families), so use the table; (3) **the label on 611–613 ("Willstone") is paired by elimination** — the game's enum calls them `Forge_Soulstone`; (4) a consumer that maps resource ids into fixed fields (RaidTools' `MapAccountResources`) drops all 91 until it adds somewhere to put them. |
| 21 | v1.19.0 | 2026-09-13 | **One new field, additive: `factionGuardians[].championsDeleted`.** `true` when the slot's two copies were sacrificed and no longer exist; **both instance ids are then `null`** where earlier schemas sent the champion's type id in their place (Courtier's Banner Lords Rare slot: `2040 / 2040` on a type-2040 slot, no Courtier in the roster). The slot is still filled and still counts toward the `guardians` stat column. **Consumer impact:** schema ≤ 20 payloads keep arriving (updates are opt-in) and still carry the type id as both "instance ids" — recognise that shape (both ids equal the slot's type id, neither owned) or it flags a copy that does not exist, or an unrelated one sharing the number. **Also a correction, no shape change: `consumed` does not mean the champions are gone.** Underpriest Brogni and Storm Herald Hekaton read `consumed: true` with both copies still in the roster, owner-confirmed; what the flag means is not established. |
| 20 | v1.18.0 | 2026-09-04 | **One new field, additive: `champions[].skills[].formIndex` — which of the champion's forms each skill is on.** A consumer that ignores it is exactly as correct as it was on schema 19. `0` is the base form, `1` a transformation's second form, matching `forms[].index` in `champion_index.json`. **It is absent, never `0`, when unresolved**, because `0` is a real answer — group with `?? "unknown"`, never with `?? 0`; this is the same nullability rule as `roleId`, and for the same reason. **Why this moved to the producer rather than staying a catalog join:** a transforming champion carries both forms' whole skill blocks on every copy (Alaz the Sunbearer reports `86301…86305` *and* `886301…886305`), and recovering the split consumer-side means honouring the per-skill ascension span in the catalog's `forms[].skills[]` — ascension **replaces** skills on 336 of the 1,044 playable champions, and both halves of a swapped pair sit in the same form list, so a membership test alone credits an un-ascended copy with a skill it does not have. The producer never faces that question: a copy's `Hero._type` **is** its own ascension variant, so the form list read from the live process is already that copy's kit. Copies whose shared `HeroType` the client never hydrated — the same ~19% gap that leaves `roleId` null — fall back to the bundled catalog evaluated at the copy's own `ascensionLevel`; measured against a real 915-champion roster that fallback attributed 3,005 of 3,005 skills, closing the 2.6% residual [`raidtools-skill-attribution.md`](raidtools-skill-attribution.md) was written to explain. **That note is now history for schema ≥ 20 payloads and still current for older ones**, which the uploader's opt-in updates guarantee will keep arriving. |
| 19 | v1.17.0 | 2026-08-27 | **Three new top-level fields, all additive: `arenaTeam`, `arena3v3Teams[]`, `siegePresets[]` — saved teams.** A consumer that ignores every one of them is exactly as correct as it was on schema 18. A saved, recallable team exists for exactly three areas of the game — Classic Arena (one team), 3v3/Tag Team Arena (per-slot, usually 3) and Siege (per-map-slot, usually 4) — and **the negative result is the more important half of this change**: every PvE stage mode (Dungeons, Doom Tower, Cursed City, Faction Wars, Event Dungeon, Foggy Forest, Champion's Journey) was checked field by field and carries no team data whatsoever, only battle results — the game keeps no saved team for any of them, so there is nothing this payload could add for those areas even in principle. `arenaTeam`/`arena3v3Teams[].heroes[]` carry a stat snapshot (`grade`/`level`/`empowerLevel`) from when the team was last saved, which can be stale against `champions[]` — join on `inventoryHeroId` for current numbers. `siegePresets[].heroIds` is bare ids with no snapshot, because Siege reads the live roster at attack time rather than freezing one. **`arena3v3Teams[]`/`siegePresets[]` follow the same null-vs-empty rule as `affinityBonuses[]`/`areaBonuses[]`**: absent means the read wasn't validated this run, a present (possibly empty) array means it was and the account genuinely has that many saved. `arenaTeam` does not yet make that distinction — `null` covers both "no team saved" and "couldn't validate," the same accepted ambiguity `clanId` already carries. Full structural writeup, including the field-by-field PvE walk that produced the negative result: the extraction engine's `docs/team-findings.md`.  **Also new in v1.17.0, and NOT a schema change because no field changes shape: `statBreakdownSources` now declares all nine columns** — `basic, artifacts, greatHall, arena, masteries, guardians, empowerment, blessing, relics` — where v1.16.0 declared the first four. That list has always been the payload's own statement about itself, precisely so it could grow without a schema bump, and a consumer that reads it rather than hardcoding four columns needs no change at all. The schema-18 row below says "today it is `basic, artifacts, greatHall, arena`"; that was true of v1.16.0 and is the point of the field, not a contradiction. **`areaBonuses[]` still never appears in it** — RaidTools now draws that column from the account-level grid, per a location the player picks, which is the only way it can be drawn at all. |
| 18 | v1.16.0 | 2026-08-24 | **Four new fields, all additive: `champions[].statBreakdown`, `champions[].elementId`, top-level `statBreakdownSources`, and the two account-level Great Hall tables `affinityBonuses[]` / `areaBonuses[]`.** A consumer that ignores every one of them is exactly as correct as it was on schema 17. **`statBreakdown` is the game's own Total Stats table per copy** — per stat, the total plus one entry per source — and it reproduces the client's own blank-vs-zero distinction: an absent source contributes nothing and must never be drawn as `0`. **`statBreakdownSources` exists because a third state is possible**: a column this producer does not compute yet looks identical, from the per-champion object, to one that contributes nothing. The list says which columns are real in *this* export, so an old payload keeps describing what it modelled; today it is `basic, artifacts, greatHall, arena`. **`elementId` is the copy's affinity** (raw id, `null` = could not read, never "no affinity"), and it is what joins a champion to its `affinityBonuses[]` row. **The two Great Hall tables are account data and are two tables, not one**: the affinity table has six stat tracks with an uneven curve, the area table has eight (adding Speed and Ignore Defence) with a linear one, and every location offers the same eight tracks. **The whole declared grid is sent, not just what the account has bought** — 24 affinity entries and 104 area entries — with unbought tracks at `"level": 0`, so a consumer can draw the screen without hardcoding the axes; a genuinely absent pair means the game has no such track. Both carry `level` alongside `value` because neither derives the other without the static per-level table, which is not on the wire. **`isAbsolute` is not constant inside either table** — HP/ATK/DEF/C.DMG/IGN.DEF are fractions, RES/ACC/SPD are flat — and reading it as "percentage throughout" computes `baseResistance × 80` where the game adds 80; that was a real bug in the first implementation, and it survived verification against the client because the account it was checked on held level 0 in exactly those two stats. **`areaBonuses[]` will never appear in `statBreakdownSources`**: the game applies it per a location the player picks from a dropdown, so there is no per-champion number — account level is the only place it is well defined. **A fifth field is new to the wire and was nearly missed: `artifacts[].powerUpValue` + `powerUpRarityId`, the glyph.** It is a **separate addend to `value`**, and this contract previously declared the opposite — that the game's `_powerUpValue` was an upgrade-screen preview that would never be exported — so the JSON Schema did not declare the property at all. **Gear totals computed from schema ≤ 17 payloads are therefore low for every glyphed champion**, by an amount no old payload records; re-sync rather than correcting in place. |
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
