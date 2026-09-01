# RaidTools — stat breakdown popup, and what-if stat modelling

Session kickoff prompt for the **RaidTools** side (`D:/Codex/RaidTools` — `RaidTools.Api` +
`raidtools-frontend`). Rewritten 2026-09-01 against **schema 19+**. Contract reference:
[export-schema.md](./export-schema.md) §`champions[].statBreakdown`.

The 2026-08-27 version of this doc was written when only four of the columns were modelled and the
headline problem was a 24% shortfall in the total. **That is no longer the situation** — read
"What changed" before reusing anything you remember.

---

## What is being built

Two features on the same data:

1. **A breakdown popup.** Click a champion's stat, see where every point came from — the same
   table the game shows under **Total Stats**.
2. **What-if modelling.** Let the user swap a stat (a substat, a whole piece, a set) and watch the
   totals recompute, without touching the account.

**RaidTools has zero `statBreakdown` handling today** — `ConsolidatedJsonSyncAdapter` doesn't parse
it, there's no DTO, no `champion-stats.ts` field, no component. This is greenfield on the consumer
side. The account payload already carries everything Feature 1 needs.

## What changed since the last version of this doc

| then (2026-08-27, schema 18) | now (schema 19+) |
|---|---|
| **4 columns modelled** — `basic, artifacts, greatHall, arena` | **9 columns** — add `masteries, guardians, empowerment, blessing, relics` |
| `total` was `sum(4 sources)` — Arbiter HP **62 097** vs a real **81 950**, a 24% gap you had to design around | `total` is `sum(9 sources)` = the game's own **Total** column (the "No Area Selected" state). It matches the Total Stats screen. |
| "show the gap as its own row" was the recommendation | **no gap row needed.** The only thing outside `total` is Area Bonuses, which the game itself only adds when the player picks a location from a dropdown — see below. |
| what-if editor blocked: artifact set tables published nowhere | **cleared** — `artifact_set_index.json` ships and RaidTools already ingests it (`MetadataType.ArtifactSetIndex`, commit `a55de5d`). |
| "two cells knowingly low" (Arbiter DEF/SPD, ~2%) | **resolved** — the artifact glyph had a second, rarity-scaled term that wasn't being read. The `artifacts` column is now exact. |

So Feature 1 is genuinely shippable end to end, and the popup can show a real Total.

---

## The contract

Per champion, `champions[].statBreakdown` is keyed by `StatKindId` **as strings** — the same ids as
`baseStats`:

```jsonc
"statBreakdown": {
  "1": { "isPercentage": false, "total": 77723,
         "sources": { "basic": 21135, "artifacts": 32085, "greatHall": 4227, "arena": 4650,
                      "masteries": 476, "guardians": 2113, "empowerment": 2113, "blessing": 8500,
                      "relics": 24 } },
  "7": { "isPercentage": true,  "total": 31, "sources": { "basic": 15, "artifacts": 16 } }
}
```

Top level: `"statBreakdownSources": ["basic","artifacts","greatHall","arena","masteries","guardians","empowerment","blessing","relics"]`.

Stat ids: `1` HP · `2` ATK · `3` DEF · `4` SPD · `5` RES · `6` ACC · `7` C.RATE · `8` C.DMG ·
`9` C.HEAL · `10` IGN.DEF.

Column order for display (the game's own): basic, artifacts, greatHall (labelled **Affinity
Bonuses**), arena (**Classic Arena**), masteries, guardians (**Faction Guardians**), empowerment
(**Empowerment**), blessing (**Blessing**), relics (**Relic**).

---

## Seven things that produce a wrong popup if you miss them

### 1. An absent source key means "contributes nothing" — never zero.

The game's own distinction: one `StatBonusContext` per stat row, every cell has a `_hasValue` flag,
a cell without one renders **blank**. Arbiter's SPD has no `greatHall` key because the Great Hall
grants him no speed, not because it grants zero. Draw a missing source as a blank cell or omit the
row, never as `0` or `–` implying a real zero.

### 2. A source missing from `statBreakdownSources` is a *third* state: not computed in this export.

Both cases look identical in the per-champion object — a missing key. The top-level list is the only
thing separating "the game gives this champion nothing here" from "this export's producer couldn't
compute this column". Today all nine are listed, but an **older payload** (schema 18) lists only
four, and a payload cut from a bundle that predates a column drops just that one. Drive the popup's
columns off `statBreakdownSources` for the champion's export, not off a hardcoded list. A column not
in it: show as unknown, or leave the column out — never as blank-implying-zero.

### 3. `total` matches the game's Total column, but the game screen can show *more*.

`total` = `sum(sources)` = every modelled column. It equals the client's `_totalStat`, which is
what the Total Stats screen shows **with "No Area Selected"**. If the player has picked a location
in the screen's *"Showing Area Bonuses for:"* dropdown, the game adds an **Area Bonuses** column on
top — and that is deliberately **not** in `statBreakdown` (it's a per-location choice, not a
champion constant; see `areaBonuses[]` at account level). So:

- Label the popup's number **"Total"** — it is honest against the default screen state.
- If you want to be bulletproof, add a footnote: *"excludes Area Bonuses (chosen per location)"*.
- Do **not** try to reconcile `total` with any aggregate you compute from gear alone — that
  aggregate is missing six columns and will read low.

### 4. `isPercentage` stats take percentage **points**.

For C.RATE, C.DMG, C.HEAL, IGN.DEF a non-absolute bonus adds points, not a fraction: 15% base +
37% gear + 5% masteries = **57%**, not 21%. Render with a `%` suffix, never multiply against a base.

### 5. Do not render stat `9` (C.HEAL).

The Total Stats overlay draws nine rows and C.HEAL is not one of them. The payload may still carry a
`statBreakdown["9"]` — an identical `{ "isPercentage": true, "total": 50, "sources": { "basic": 50 } }`
on every champion, because base C.HEAL is a flat 50 for everyone. It is a phantom the game never
shows; upstream is removing it. Filter stat id 9 out of the popup regardless of whether the key is
present.

### 6. The `blessing` column is the **awakening** bonus — not the chosen blessing.

It comes from the champion's `awakeningLevel` and `rarityId`, summed over grades 1..`awakeningLevel`.
**Which blessing the player picked contributes nothing to stats** — a champion with
`blessingId: 2102` and one with `blessingId: 1201` at the same awakening/rarity get the identical
`blessing` column. The gate is `blessingId > 0` (an awakened-but-unblessed champion shows a blank
`blessing` column). So: don't label the column with the blessing's name, don't try to explain its
value from the blessing's effect text, and don't surface it in a "your Soul Reap blessing gives you
X" way. It's "Awakening" in everything but the column's wire name.

### 7. `relics` is one relic, and gemstones don't touch it.

A champion can have exactly one activated relic (`HeroActivatedRelicsLimit` is 1). The `relics`
column is that relic type's stat bonus scaled by the relic's upgrade level. Socketed gemstones
grant *skills*, not stats — they contribute nothing to this column. `total` and the per-source value
already account for all of this; a what-if editor that lets the user "add a gemstone" must not move
the stat totals.

---

## Feature 1 — the popup, end to end

Follow the account-payload path, not the metadata-catalog pipeline — `statBreakdown` rides in the
consolidated upload, it is not its own `MetadataType`.

1. **`RaidTools.Api/Sync/Adapters/ConsolidatedJsonSyncAdapter.cs`** — the champion `.Select(h => …)`
   already maps `BaseStats`, `Skills`, gear ids. Add `StatBreakdown` (the per-stat dict) and read
   the top-level `statBreakdownSources` once into the sync context. Store on `ChampionDocument`
   (Mongo) the same way `BaseStats` is stored — a nested doc keyed by stat id.
2. **`ChampionDto` / the champion read endpoint** (`RaidApiControllers.cs` / `RaidDtos.cs`) — expose
   `statBreakdown` and `statBreakdownSources` on whatever the roster/detail view consumes.
3. **`raidtools-frontend/src/app/core/champion-stats.ts`** — add the model. It already has
   `ChampionBaseStats` and the gear-derived aggregate; the breakdown is a third shape:
   `Partial<Record<number, { isPercentage: boolean; total: number; sources: Partial<Record<string, number>> }>>`.
   Reuse `PERCENT_POINT_STAT_KINDS` / `isPercentPointStat` — same rule.
4. **The popup component** — a table: one row per stat the champion has a breakdown for (skip stat
   9), columns = the sources in `statBreakdownSources` order that this champion actually has a value
   for, a **Total** column, `%` suffix when `isPercentage`. Empty cell for an absent source. This is
   a second view alongside the existing aggregate stats — keep both; they answer different questions
   (aggregate = "what are this champion's stats"; breakdown = "where did they come from").

## Feature 2 — what-if modelling

The payload gives per-source **totals**, never per-piece attribution inside `artifacts`. To
recompute after a swap you rebuild the `artifacts` column yourself from `artifacts[]` +
`accessories[]` filtered on `equippedByHeroId`, plus the artifact set tables.

### Per-piece contribution

Each piece contributes `primaryBonus`, `ascendBonus`, and every `secondaryBonuses[]` entry. Per
line:

```
amount = value + powerUpValue                       // the glyph is a SECOND ADDEND on the same line
if isAbsolute                       -> amount                            // flat
else if statKindId in {7,8,9,10}    -> amount * 100                      // percentage POINTS
else                                -> amount * baseStats[statKindId]    // fraction of the BASIC stat
```

Dropping `powerUpValue` makes every glyphed champion short by exactly the glyph total — this has
already bitten an implementation once.

### Set bonuses

Folded into the same `artifacts` column ("the total Artifact bonus includes Artifact Set bonuses,
individual Artifact bonuses, and Accessory bonuses"). Two kinds, **opposite** arithmetic — and
RaidTools already has the table: `artifact_set_index.json` → `MetadataType.ArtifactSetIndex`,
`EffectiveBonus(setKindId, piecesWorn)` (API + client, one place). Use it.

- **Ordinary sets stack:** `worn / piecesRequired`, integer division.
- **Progressive (Corrupted) sets don't:** one entry per pieces worn 1–9, each the **complete running
  total** at that count — apply the highest tier the count reaches, once. The `stacks` flag on each
  entry says which is which.

### Rounding

Once per column from the unrounded sum, half to even, with a 6-decimal pre-snap:
`round(round(x, 6), banker's)`. The snap matters — float accumulation turns an exact `848.5` into
`848.5000000000001` and it rounds the wrong way. A contribution that rounds to 0 is **dropped**, not
shown as zero (trap 1).

### The other six columns

A gear-only what-if editor is the common case and needs none of these. If you want swaps to move the
*whole* total:

- **`greatHall`** — `affinityBonuses[]` is on the wire (`{elementId, statKindId, level, value,
  isAbsolute}`); select rows matching the champion's `elementId`. **`isAbsolute` varies within the
  table** — HP/ATK/DEF/C.DMG fractional, RES/ACC flat.
- **`masteries` / `guardians` / `empowerment` / `blessing` / `relics`** — the static tables that
  produce these live only in `RslCompanionMetadata/exports/stat_bonus_tables.json`, which is **not
  currently a RaidTools metadata type**. A what-if that changes a champion's masteries, guardian
  slots, empower level, awakening or relic can't be modelled until that bundle is published the way
  `artifact_set_index.json` was. File the ask if the product wants it; otherwise scope the editor to
  gear and note the limit in the UI.
- **`arena`** — a percentage of the Basic stat; the table isn't shipped, only the computed result.
- **`areaBonuses[]`** — shipped at account level, deliberately not a champion column (per-location
  dropdown). Don't fold it into a champion total.

---

## How to check your work

The upstream acceptance test (`StatBreakdownProbe verify`) diffs a computed breakdown against the
live client cell by cell. You can't run it from RaidTools, but use the discipline: open a champion's
Total Stats in the game and compare the popup column by column.

**Read blanks as blanks** — a blank matching a blank is not evidence. That is exactly how a Great
Hall flat-vs-percentage bug got through upstream (the account held level 0 in the two stats that
would have disagreed).

Reference champions on the current test account:

| champion | exercises |
|---|---|
| **Arbiter** (20540) | all nine columns non-trivial — the fullest single check |
| **Geomancer** (23403) | Epic empowerment at level 4 (cumulative), no relic |
| **Harima** (24033) | Legendary empowerment L2, a relic, an awakening/blessing column |
| **Karnage the Anarch** | Mythical, awakening grade 6 — the top of the blessing track |
| **Lydia the Deathsiren** (24219) | nine-piece progressive artifact set (what-if set arithmetic) |
| **Aox the Rememberer** (40429) | two ordinary stacking sets |
