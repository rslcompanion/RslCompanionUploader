# RaidTools — stat breakdown popup, and what-if stat modelling

Session kickoff prompt for the RaidTools side. Written 2026-08-27 against **schema 19**.
Contract reference: [export-schema.md](./export-schema.md) §`champions[].statBreakdown`.

---

## What is being built

Two features on the same data:

1. **A breakdown popup.** Click a champion's stat, see where every point came from — the same
   table the game shows under **Total Stats**.
2. **What-if modelling.** Let the user swap a stat (a substat, a whole piece) and watch the totals
   recompute, without touching the account.

Feature 1 is shippable from what the payload already carries. **Feature 2 is not, and the gap is
upstream — read "The blocker" before estimating it.**

### This is a SECOND view, and it will disagree with the one you already have

RaidTools today shows aggregated stats per stat type. The breakdown view sits alongside that — it
drills into one stat and says where its points came from. Keep both; they answer different
questions.

**But they will not add up to the same number, and that has to be designed for rather than
discovered.** The existing aggregate reflects the champion's actual stats. `statBreakdown` covers
only four of the ten sources the game has. On Arbiter's HP that is 62 097 against a real 81 950 —
the same champion, the same stat, two screens of the same app, a 19 853 gap.

A user who sees that will assume one screen is broken. Three ways out, in order of preference:

1. **Never show a total on the breakdown view.** Show the source rows and let the aggregate view own
   the total. The two screens then never make competing claims.
2. **Show the gap as its own row** — "other sources (not yet itemised): 19 853" — computed as
   `aggregateTotal − sum(sources)`. Honest, and it stays correct automatically as upstream lands the
   remaining columns.
3. Label the breakdown's number explicitly as a partial ("from 4 of 10 sources"). Weakest option;
   users read numbers, not captions.

Option 2 is the one worth the effort: it turns the shortfall from an embarrassment into the feature
that tells the user masteries and blessings are doing real work. It needs no upstream change —
`statBreakdownSources` already tells you which four are itemised, so the row can name what is
missing.

---

## The contract

Per champion, keyed by `StatKindId` **as strings**, same ids as `baseStats`:

```jsonc
"statBreakdown": {
  "1": { "isPercentage": false, "total": 62097, "sources": { "basic": 21135, "artifacts": 32085, "greatHall": 4227, "arena": 4650 } },
  "4": { "isPercentage": false, "total": 392,   "sources": { "basic": 110, "artifacts": 282 } },
  "7": { "isPercentage": true,  "total": 31,    "sources": { "basic": 15, "artifacts": 16 } }
}
```

Top level: `"statBreakdownSources": ["basic", "artifacts", "greatHall", "arena"]`.

Stat ids: `1` HP, `2` ATK, `3` DEF, `4` SPD, `5` RES, `6` ACC, `7` C.RATE, `8` C.DMG, `9` C.HEAL,
`10` IGN.DEF.

---

## Five things that will produce a wrong popup if you miss them

### 1. `total` is NOT the champion's total. Do not label it "Total".

It is the sum of the **modelled sources only** — literally `sources.Values.Sum()` in the builder.
Six real columns are not modelled yet: masteries, faction guardians, empowerment, blessing, relics
and area bonuses.

The size of this is not a rounding detail. Arbiter, HP:

| | |
|---|---|
| `statBreakdown["1"].total` | **62 097** |
| what the game's own screen shows | **81 950** |

That is a **24% understatement**, and a popup that prints 62 097 next to the word "Total" beside a
game client showing 81 950 is a bug report. Label it for what it is — "from these sources" — or sum
the sources yourself and show the same caveat. If you need the champion's true total, it is not in
this object.

### 2. An absent source means "contributes nothing" — never zero.

This is the client's own distinction, not a convention invented upstream: the game builds one
`StatBonusContext` per stat row, every cell carries a `_hasValue` flag, and a cell without one
renders **blank**. Rendering `0` throws away information the game deliberately keeps. Arbiter's SPD
has no `greatHall` key because the Great Hall grants him no speed — not because it grants him zero.

### 3. A source missing from `statBreakdownSources` is a *third* state: not modelled yet.

Both cases look identical inside the per-champion object — a missing key. The top-level list is the
only thing that separates "the game gives this champion nothing here" from "we cannot compute this
column yet". Drive the popup's empty states off that list. A column absent from it should be shown
as unknown or omitted entirely, never as a dash implying zero.

### 4. `isPercentage` stats take percentage **points**.

For C.RATE, C.DMG, C.HEAL and IGN.DEF, a non-absolute bonus adds points, not a fraction of the
base: 15% base plus 37% from gear displays as **57%**, not 21%. Render these with a `%` suffix and
never multiply them against the base.

### 5. Two cells are knowingly low right now.

Arbiter (instance 20540) is short **DEF 55** and **SPD 11** in the `artifacts` column — about 2%.
Tracked as item 7 in the metadata repo's `TODO.md`. The fields ship anyway, deliberately, because a
slightly low number beats an absent source that would read as "contributes nothing". Do not build
anything that assumes the artifacts column is exact to the unit.

---

## Feature 2: what-if modelling

To recompute after a swap you need the composition rules, because the payload gives you per-source
**totals**, never per-piece attribution inside `artifacts`. You must rebuild that column yourself
from `artifacts[]` and `accessories[]` filtered on `equippedByHeroId`.

### Per-piece contribution

Each piece contributes `primaryBonus`, `ascendBonus`, and every entry of `secondaryBonuses`. For
each bonus line:

```
amount = value + powerUpValue           // the glyph is a second addend on the SAME line
if isAbsolute            -> amount                      // flat
else if statKindId in {7,8,9,10} -> amount * 100         // percentage POINTS
else                     -> amount * baseStats[statKindId]   // fraction of the BASIC stat
```

`powerUpValue` is the glyph and is easy to drop — doing so makes every glyphed champion short by
exactly the glyph total, which has already happened once upstream.

Percentages apply to the **Basic** stat, never to a running total. The columns are summed against
the base, not compounded against each other.

### Rounding

Round **once per column**, not per line: `round(round(x, 6), half-to-even)`. The 6-decimal snap
matters — float accumulation turns an exact `848.5` into `848.5000000000001`, which stops being a
midpoint and rounds the wrong way. A contribution that rounds to 0 is **dropped**, not published as
zero (see trap 2).

### Set bonuses — and the blocker

Set bonuses are folded into the same `artifacts` column; the game says so on its own screen ("the
total Artifact bonus includes Artifact Set bonuses, individual Artifact bonuses, and Accessory
bonuses"). Two kinds, with **opposite** arithmetic:

- **Ordinary sets stack.** Six pieces of a two-piece set grant it three times — `worn / required`,
  integer division, not a completeness test.
- **Progressive sets do not.** Sets 35, 36, 47, 48 and 58–66 (the Corrupted ones) carry one entry
  per number of pieces worn, 1–9, and each entry is the **complete running total** at that count,
  not a per-step gain. Apply exactly one — the highest tier the count reaches. The in-game tooltip
  shows the steps instead (`3 pcs +12% SPD`, `5 pcs +12% SPD`), and the data shows the totals
  (`5pc = SPD 24%`); they agree only if you do not add them twice.

**The blocker: neither table is published anywhere.** Not in the account payload, not in
`exports/`. They exist only inside the extractor, as an internal detail used to compute
`statBreakdown`. Concretely that means:

| swap the user makes | can RaidTools model it today? |
|---|---|
| change a substat's value on a piece | **yes** — per-piece rules above are sufficient |
| change a substat's *stat kind* | **yes** |
| swap a whole piece for one of the same set | **yes** |
| swap a piece for a **different set**, or change how many pieces of a set are worn | **no** — needs the set tables |

So scope Feature 2 to substat editing first; it is genuinely useful and fully supported. Whole-piece
swaps need an upstream change before they can be correct — and a swap that silently ignores the set
bonus is worse than one that refuses, because the number still looks plausible.

**The upstream ask, stated precisely** so it can be filed against `RslCompanionMetadata`: publish an
artifact set index — for each `setKindId`, the pieces required and the stat bonuses of each ordinary
tier, plus for the thirteen progressive sets the full 1–9 tier table, each entry as
`{statKindId, value, isAbsolute}`. The extractor already reads all of it (`ArtifactSets`, 30 tiers,
and `ArtifactSubSets`, 117 progressive tiers); it is a publishing gap, not a research one. The other
account-level tables are already shipped this way — `affinityBonuses[]` and `areaBonuses[]` — so
follow their shape.

### The other columns, if you want modelling to move the totals too

- **`greatHall`** — `affinityBonuses[]` is shipped: `{elementId, statKindId, level, value,
  isAbsolute}`. Select the rows matching the champion's `elementId`. **`isAbsolute` is not constant
  within this table**: HP/ATK/DEF/C.DMG are fractions, RES and ACC are flat `+5…+80`. Treating the
  whole table as fractional computed `baseRES × 80` where the game adds 80, and the bug survived
  review because the test account held level 0 in exactly those two stats.
- **`arena`** — a percentage of the Basic stat. The table itself is not shipped; only the computed
  result is.
- **`areaBonuses[]`** is shipped but is deliberately **not** a per-champion column — the player
  picks one location from a dropdown, so a single number per champion would be meaningless. Do not
  fold it into a champion total.

---

## How to check your work

The upstream repo has an acceptance test that diffs a computed breakdown against the live client
cell by cell (`StatBreakdownProbe verify`). You cannot run it from RaidTools, but you can use the
same discipline: open a champion's Total Stats in the game, and compare your popup column by column
against it.

Pick champions that exercise what you changed, and **read blanks as blanks**. A blank matching a
blank is not evidence — that is exactly how the Great Hall bug above got through. Good reference
champions on the current account: **Lydia the Deathsiren** (a full nine-piece progressive set),
**Aox the Rememberer** (two ordinary stacking sets), and **Arbiter** (the two knowingly-low cells).
