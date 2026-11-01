# Dungeon artifact drop odds: what the client has, and where

This answers RaidTools `docs/dungeon-drop-tables-metadata-brief.md` (RaidTools commit 969d46d, dev).
Investigated 2026-11-01 on game build **11.85.5**, with the client running.

**Built the same day, by the recommended route.** RslCompanionMetadata
`tools/export-dungeon-drop-index.mjs` writes `exports/dungeon_drop_index.json` (contract:
`docs/dungeon-drop-index.md` there). It's a *required* Quick sync catalog, so every patch uploads it
to RaidTools as `DungeonDropIndex`. RaidTools' Gear Hunter calculators simulate dungeon runs from it.
The uploader and its export schema did not change.

**Set-drop events (2x/3x) don't matter to the points.** They multiply one set's chance only (Q4), and
Gear Hunter scores rank × rarity.

## Short answer

**The drop tables are on disk, per stage, in the static-data cache that RslCompanionMetadata already
reads.** Every campaign and dungeon stage record (`l.s[]`) carries an `f` block. It holds the weighted
roll for what a run drops, plus separate weight tables for an artifact's slot, rank, rarity and set
(and the same for accessories, with faction instead of set). No running client is needed, and the
live engine adds nothing the file doesn't already have.

**Recommended route: a RslCompanionMetadata static export**, keyed by `stageId`, beside
`goal_index.json` and `stage_encounter_index.json`. Don't use the uploader: this data is the same on
every account, and the uploader's payload carries only the signed-in account's own data. Drop-rate
events are the only part that's account- or time-dependent. They're server-sent (see Q4), so for now
RaidTools should take them as user input.

## Where it lives (Q1)

### (b) Static-data cache: found

`%USERPROFILE%\AppData\LocalLow\Plarium\Raid_ Shadow Legends\static-data\11.85.5\<hash>`, decoded by
`RslCompanionMetadata/tools/lib/rsl-static-data.mjs` (`loadStaticData`). Stage records are `l.s[]`
(2,900 on 11.85.5), and `f` is a sibling of the `m`/`e` blocks the stage encounter index already
reads.

Dragon's Lair Hard 10 (`stageId 2062010`), verbatim except the null `FlexibleRewardInfo` fields:

```json
"f": {
  "r":  [ {"t": 4, "p": 9950, "n": 1, "x": 1},
          {"t": 2, "p": 50,   "n": 1, "x": 1, "b": 1002} ],
  "c": 1, "u": false, "t": null,
  "ak": {"1":1,"2":1,"3":1,"4":1,"5":1,"6":1},
  "ar": {"5":6800,"6":3200},
  "ay": {"3":450,"4":320,"5":180,"6":50},
  "as": {"4":1,"7":1,"9":1,"11":1,"13":1,"20":1,"22":1,"25":1,"26":1},
  "ck": {}, "cr": {}, "cy": {}, "cs": {}, "ap": null
}
```

Read as: one roll per run (`c`). 99.5% it's an artifact, 0.5% an Ancient Shard (BMI 1002). Slot is
uniform over the 6 gear slots. 5★ 68% / 6★ 32%. Rare 45% / Epic 32% / Legendary 18% / Mythical 5%.
Set is uniform over Dragon's nine sets (Speed, Accuracy, Lifesteal, Daze, Frost, Destroy, Toxic,
Avenging, Stalwart, by `docs/artifact-enums.json`).

**Key → name pairing.** The class's properties sit together in `global-metadata.dat`, namespace
`SharedModel.Meta.Stages`, in this order: `RewardProbsByIndex`, `ArtifactKindIdProbs`,
`ArtifactRankIdProbs`, `ArtifactRarityIdProbs`, `ArtifactSetKindIdProbs`, `AccessoryKindIdProbs`,
`AccessoryRankIdProbs`, `AccessoryRarityIdProbs`, `AccessoryHeroFractionProbs`,
`AccessorySetKindIdProbs`. The same class also has methods `RollAvailableTimesPerType`,
`PopulateArtifactProbabilities` and `FillArtifactProbsByRank/KindId/Rarity`. Each key was checked
against its values, not only placed by position:

| Key | Field | How it was bound |
| --- | --- | --- |
| `r` | reward options (`FlexibleRewardInfo[]`) | `t/p/n/x/b/r` match the `FlexibleRewardInfo` keys that `rsl-rewards.mjs` already pairs by value; live class fields read with `EventProbe --classes` agree: `Type, Probability, MinCount, MaxCount, …` |
| `ak` | artifact slot (`ArtifactKindId`) | keys are always ⊆ 1–6 (gear) |
| `ar` | artifact rank | keys 1–6, and the stars rise with stage number |
| `ay` | artifact rarity | keys 1–6; 6 (Mythical) appears only on Hard and Event Dungeon 26+ |
| `as` | artifact set | keys are set ids from the dungeon's known pool (Dragon: Speed/Acc/Lifesteal…; Ice Golem: Life/Offense/Defense/Crit Rate…) |
| `ck` | accessory slot | keys always {7, 8, 9} = Ring / Amulet / Banner |
| `cr` / `cy` | accessory rank / rarity | the same value shapes as `ar` / `ay` |
| `cs` | accessory **faction** (`AccessoryHeroFractionProbs`) | 16 keys, 1–17 without 4, the faction ids |
| `ap` | accessory set (`AccessorySetKindIdProbs`) | `{65: 1}` on the Event Dungeon only: a set id. Null elsewhere, so accessories there get no set |
| `c` | probably the number of rolls per run | **position only**: 1 on every dungeon and campaign stage, 2/3 on Faction Wars Normal/Hard |
| `u`, `t` | not named | `false` / `null` on all 1,418 records. These go to `unmapped` |

`FlexibleRewardType` (`r[].t`) is the enum `rsl-rewards.mjs` already decodes: 1 Hero, 2 Bmi,
3 Resources, 4 **Artifact**, 5 Experience, 6 **Accessory**, 33 RelicCraftMaterial.

### (a) Live memory: not needed

The client has the same model in memory (`SharedModel.Meta.Stages`). Reading it would cost an IL2CPP
walk for data the disk file already gives in full, with no client running. The only drop-related
thing that is *only* in memory is the server-event list (Q4).

### Server vs client

The table is client data in a *shared* model namespace (`SharedModel.*` is the code the client and
server share). The client can't award loot: battle results come from the server. So the server rolls
the loot, presumably with this same model, since it's the shared one. That last step can't be proven
from the client. See "Not verified".

## How a drop is rolled (Q2)

- **Independent dictionaries, not a joint table.** Slot, rank, rarity and set are four separate
  weight maps, and the property names (`…KindIdProbs`, `…RankIdProbs`, …) say the same. So the chance
  of one exact piece is
  `P(artifact row) × P(slot) × P(rank) × P(rarity) × P(set)`, each normalized within its own map.
  Example, Dragon H10 6★ Mythical Speed boots: 0.995 × 1/6 × 0.32 × 0.05 × 1/9 ≈ **0.0295%** per run,
  before the main stat (Q5).
- **Count per run: 0 or 1 artifacts, never more.** `c = 1`, and `r[]` is one weighted pick among
  options, each with `MinCount = MaxCount = 1` for artifacts. Expected artifacts per run is the
  artifact row's share:
  - Dragon / Ice Golem / Fire Knight Normal: 79.4% at stage 1, rising 0.4% a stage to 85.0% at
    stage 15. The rest is Mystery Shards, Ancient Shards and XP Brew.
  - Normal 16–25 and all of Hard: 99.5% (0.5% Ancient Shard).
  - Event Dungeon: artifact 87.0% → 65.5%, accessory 12.5% → 34.0%, both per run.
- **Per stage, not per band.** Each stage has its own maps, and the rank/rarity curve moves almost
  every stage. Dragon, Ice Golem and Fire Knight share the curve, but not exactly: Hard 2, 5, 7 and 8
  differ between them by a few weight points (below). Keep it per stage.
- **Normal vs Hard.** Hard (10 stages) is a fixed 5★ 68% / 6★ 32% with Mythical rising
  0.5% → 5.0% (H1 → H10). Normal 25 tops out at Rare 55% / Epic 32% / Legendary 13%, with no Mythical.
  Normal ranks start at 1–2★ and reach 5–6★ by stage 20.

**Weights are relative, and the scale is not fixed.** Rank maps sum to 10,000 on dungeons and 100 on
the campaign. Rarity maps sum to 1,000 or 100. Several don't hit a round total, probably typos on
the design side:

| Stage | Map | Values | Sum |
| --- | --- | --- | --- |
| Dragon H2 `2062002` | rarity | 450/320/180/10 (the others have 530/320/140/10) | 960 |
| Dragon H5 `2062005` | rarity | 450/320/180/25 | 975 |
| Ice Golem H7, H8 | rarity | Legendary +5 against Dragon/Fire | 1,005 |
| Spider N10 `2099010` | accessory rank | `{4: 69, 5: 310}`, where N9/N11 are ~60/40 | 379 |
| Keeps, stages 4–19 | reward rows | | 10,001–10,002 |
| Sand Devil / Shogun 1–9 | reward rows | | 1,005 |

This is why the brief's "ship the raw weights" rule matters: normalize per map at the consumer. If
the server normalizes the same way, Spider N10 really drops 5★ accessories 82% of the time. If it
assumes a fixed scale, it doesn't. That can only be settled empirically (see "Not verified").

## Scope (Q3)

| Dungeon (region) | What `f` rolls |
| --- | --- |
| Dragon 206, Ice Golem 207, Fire Knight 208 | **artifacts** (gear, 6 slots), 9 sets each |
| **Spider 209** | **accessories only** (Ring/Amulet/Banner), faction-weighted (16 factions, uniform), no set |
| Event Dungeon 218, stages 1–30 | artifacts (Crit Rate, Crit Damage, Accuracy, Daze, Curing, Chronophage) **and** accessories (set 65, Chronophage). Stages 31–35 have no `f` |
| Campaign (area 1, all 336 stages) | artifacts (one set per region) plus champion drops. Region 12 also drops accessories |
| Keeps 201–205 | potions, Mystery/Ancient Shards, brews. **No artifacts** |
| Iron Twins 211–214 | Mortal Soul Coins / Immortal & Eternal Soul Essence. **No artifacts** |
| **Sand Devil 216** | **Oil** (artifact ascension material, 7001–7003). **No artifacts** |
| **Phantom Shogun 217** | **Extract** (accessory ascension material, 7011–7013). **No accessories** |
| Minotaur 210, Champion Proving Grounds 219 | no `f` at all |
| Faction Wars (area 5, all 630) | glyphs, forge materials, relic craft materials. **No accessories** in 11.85.5 |
| Doom Tower | 72 floors carry `f`, all resources |

So the brief's guesses need correcting. On 11.85.5, **accessories come from Spider's Den, the Event
Dungeon and campaign region 12**, not from Faction Crypts, Sand Devil or Phantom Shogun. For Gear
Hunter (action 21), which regions count depends on whether the game counts accessories as
"artifacts". This table says *where* each kind drops, not how action 21 scores them.

## Modifiers (Q4): server-side, but the client sees them

There's no multiplier or boost field in `f` or anywhere in the stage record. Drop boosts are
**server events**. `ServerEvent` (live fields: `TypeId`, `DateFrom`, `DateTo`, `Shards`,
`Fractions`, `Heroes`, …, **`Artifacts`, `Accessory`**, `ArtifactRemoval`, `SoulstoneGradeBoost`,
and an `IsDungeonEvent` property) arrives per account in `UserServerEventsData`. The client's
`SetDropEventContext` shows an active one with `_multiplier` and `_regionTypeId`. So a set-drop event
is a multiplier on one region's set, sent by the server. Double/triple-run events are server events
too (`IgnoreSeenDoubleRunServerEvents`, `IsActiveTripleRun`). Nothing was found for clan perks.

No set-drop event was active during this probe, so the `Artifacts` / `Accessory` payload shape was
not decoded. RaidTools should treat a boost as an input the user enters, as its brief suggested. If
that's ever automated, it's an uploader job (live, per account), and it means mapping that one
payload.

## Main stat (Q5): eligibility found, weights not

Static `a.i[]` lists each primary stat with the slots it may appear on (`k = {statKindId, isAbsolute}`,
`s = kinds`). For example: ATK%/HP%/DEF% on Chest, Gloves and Boots; C.RATE and C.DMG on Gloves; SPD
on Boots; RES and ACC on Chest and Banner. **There are no weights**, and no `PrimaryBonus…Prob` name
exists in the metadata. Assuming a uniform pick over the eligible stats is a guess, and should be
labelled as one.

## Energy (Q6)

It's next to the drops: `stage.w.p.v["1"]` (resource 1 = Energy). It's already exported as
`energyCost` in `stage_encounter_index.json`. Dragon/Ice/Fire/Spider Normal: 8 → 20. Hard: 20.

## Cross-check against the community tables (2026-11-01)

ayumilove's loot-table images ([page](https://ayumilove.net/raid-shadow-legends-loot-table-and-drop-rates/),
data credited to u/Rhbobo) are the most complete published per-stage tables. Comparing them with
11.85.5:

- **Normal 1–25 (Dragon / Ice Golem / Fire Knight): identical on every row.** Artifact 79.4% → 85%,
  then 99.5%. Mystery Shard and XP Brew 10.05% → 7.25%, Ancient Shard 0.5%. Every rank split
  (N16 39.80 / 52.03 / 8.17 = our `3980/5203/817`), every rarity split, every energy cost.
- **Spider's Den Normal: identical**, including the accessory type shares (Ring 100% at N1 → a
  three-way split from N14) and **N10's rank as 18.2% / 81.8%, flagged there as "Code Bug? Missing
  0"**. That's the same `{4: 69, 5: 310}` map listed above, normalized. So the community normalizes
  each map on its own too, and so does this pipeline.
- **Hard 1–10 rarity: identical, normalized the same way.** H2 46.88 / 33.34 / 18.75 / 1.04 is
  Dragon's off-scale `450/320/180/10` over 960, and H5's 2.57% Mythical is `25/975`.
- **Hard rank is the one difference.** Their table varies 5★ by stage (65.2–68.09%), while 11.85.5
  has a flat 68 / 32 on all ten stages. Their table is most likely from an older build. The effect is at most
  ~3 points of 6★ share.

Inteleria's [drop-rate guide](https://www.inteleria.com/raid-dungeon-drop-rates/) agrees on Normal 20
(73 / 27) and Normal 25 (68 / 32). Its Hard-mode text ("no Mythical before Hard 26", Hard running to
stage 30) contradicts both the game and Plarium's
[own announcement](https://raidshadowlegends.com/news/hard-mode-dungeons/) of 10 Hard stages, so
don't use it for Hard mode. Plarium publishes no percentages.

## Not verified

1. **That the server rolls with exactly this table.** It's the client's shared model, and the shapes
   are plausible: 0.5% Ancient Shard, Mythical only on Hard, Hard-10 Mythical at 5%. But nothing here
   observes a roll. The cheapest check is a few hundred logged Hard runs against the predicted
   rank/rarity split. That would also settle the normalization question above (Spider N10).
2. **The names of `c`, `u` and `t`.** `c` is placed by position and context only.
3. **Whether `Probability` on the reward rows is normalized or used against a fixed scale.** Same
   question as above; the off-by-1–5 row sums make it matter only slightly.

## If the owner says go

A `dungeon_drop_index.json` in RslCompanionMetadata (a node exporter beside
`export-stage-encounter-index.mjs`, reusing `rsl-static-data.mjs` and `rsl-rewards.mjs`), per stage:
`stageId`, `energyCost`, `rollsPerRun` (`c`), `rewards[]` (decoded `FlexibleRewardInfo` with a raw
`weight`), and `artifact` / `accessory` blocks of raw weight maps: `kind`, `rank`, `rarity`, `set`,
and `faction` for accessories. Names come from `artifact-enums` / `artifact_set_index`. `u`/`t`
go raw into `unmapped`. A `verification` block lists each map's sum, the off-scale maps above, and
every area-2 stage from `goal_index.json`, with or without a table. Campaign stages come almost for
free and count for Gear Hunter. No uploader or export-schema change is needed.

Reproduce: decode with `loadStaticData(findStaticDataFile().path)`, then read `l.s[]` where `i` is
the stageId, and its `.f`.
