# Grim Forest map progress — read `completedSlotIds` (schema 31)

Consumer-side prompt for **RaidTools**, written to be handed straight to an agent: paste it into a
Claude Code session opened on that repo.

It is a *summary*. [`export-schema.md`](export-schema.md) / [`.json`](export-schema.json) are the
contract (section **"Mode progress"**, Changelog rows 30 and 31); where this file disagrees with them,
they win, and this one is stale and should be fixed.

---

## What changed on the producer

Uploader **v1.30.0** sends two per-difficulty fields on `grimForest.difficulties[]`:

| Field | Schema | What it is |
|---|---|---|
| `passedStageIds` | 30 | the **battles** won this rotation, as stage ids (`ZZZZ D SSS`, zones 1401–1404). Already read by `ConsolidatedJsonSyncAdapter` 4.8.0. |
| `completedSlotIds` | 31 | **new** — the **map progress**: every map slot completed this rotation, by slot number. Battles, chests, altars, random encounters and path nodes alike. |

A real v1.30.0 block (rotation 10, 2026-09-27), trimmed:

```jsonc
"grimForest": { "rotation": 10, "difficulties": [
  { "difficultyId": 1, "level": 23, "experience": 6860, "curioSlots": 4, "treasureHuntReceived": false,
    "passedStageIds": [14011007, /* … 78 */], "completedSlotIds": [1, 2, 3, 4, 5, 6, 7, 10, /* … 232 */, 389] },
  { "difficultyId": 2, "level": 30, "experience": 11650, "curioSlots": 6, "treasureHuntReceived": true,
    "passedStageIds": [14012002, /* … 135 */], "completedSlotIds": [1, 2, 3, /* … */, 403] }
] }
```

That Hard entry is a **fully cleared map** (the player confirmed it): 403 of 403 slots, but only 135
stages.

## The bug this fixes: the tile's denominator is wrong

`mode-progress-tiles.component.ts` draws `${name} stages` as `passedStageIds.size / GAME.grimForest.stagesPerDifficulty`
(211). **211 is not reachable.** It is every Grim Forest stage the game defines for a difficulty: 104
fixed battles plus a pool of 107 random-encounter stages, of which one map spawns only some (31 on
that Hard map). A cleared Hard map therefore renders **135 / 211 (64%)**. It is not wrong data. The
number it is divided by is wrong.

The per-difficulty map, from the client's static `FoggyForestSlotData` on 11.75.0 (both difficulties
alike): **403 slots = 104 fixed battles + 251 path hexes + 48 other nodes** (chests, altars, …).
Random encounters appear on slots. They are not extra slots.

## What to do

1. **Store it.** Add `List<int>? CompletedSlotIds` to `GrimForestDifficulty`
   (`RaidTools.Api/Models/ModeProgress.cs`), beside `PassedStageIds`. The adapter binds `grimForest`
   straight into that model, so the property is the whole backend change. Bump the adapter to
   **4.9.0** with the usual comment: MINOR, additive, a payload without it stores null — "not told",
   never "nothing completed".
2. **Type it.** Add `completedSlotIds?: number[] | null` to `GrimForestProgress.difficulties[]` in
   `raidtools-frontend/src/app/core/mode-progress.ts`.
3. **Show map progress from it.** In the Grim Forest tile, the primary bar per difficulty becomes
   `${name} map`: `new Set(completedSlotIds).size / slotsPerDifficulty`.
   - `slotsPerDifficulty` comes from the metadata catalog once it publishes the map layout
     (`mode_rewards.json` `grimForest.difficulties[d].slots`, being mapped separately). Until then, use a
     `GAME.grimForest.slotsPerDifficulty: 403` fallback, commented like `keysPerDay` (catalog wins).
   - Once the catalog carries each slot's kind, a breakdown ("battles 104/104 · chests 18/18") is a
     join of `completedSlotIds` onto it. Do not guess kinds from slot numbers.
4. **Keep `passedStageIds`, but stop dividing it by 211.** It still drives the XP / reward sums
   (join stage ids to `ModeRewards.grimForest.difficulties[d].stages`). Show it as a count ("135
   battles won"), or against the *fixed* battle count once the catalog provides it. Never against
   `stagesPerDifficulty`. Delete or rename that constant, because its only honest use is "size of the
   catalog".
5. **Remaining-work forecast.** `remaining` currently sums `211 − passed`. With the layout it becomes
   the battles on uncompleted slots. Without it, drop the forecast rather than overstate it by ~76
   battles per difficulty.

## Traps

- **Absent ≠ empty.** `completedSlotIds` is absent when the uploader could not read it, and on every
  pre-v1.30.0 uploader. `[]` means entered with nothing completed. Keep the partial-feed rule from the
  mode-progress work: a payload without the field must not blank a stored value.
- **Slot numbers are not stage ids** and do not overlap in meaning: `3` is a map position, `14012003`
  is a battle. Never join one onto the other's table.
- **Both are this rotation only.** Compare `grimForest.rotation` with the current one before showing
  either. A snapshot from rotation 9 reads as nothing done in rotation 10.
- **Normal and Hard use the same slot numbering (1–403)**, so a slot id means nothing without its
  `difficultyId`.
