# Grim Forest — stages completed vs remaining (read `completedSlotIds`, schema 31)

Consumer-side prompt for **RaidTools**, written to be handed straight to an agent: paste it into a
Claude Code session opened on that repo.

It is a *summary*. [`export-schema.md`](export-schema.md) / [`.json`](export-schema.json) are the
contract (section **"Mode progress"**, Changelog rows 30 and 31); where this file disagrees with them,
they win, and this one is stale and should be fixed.

**Decision (owner, 2026-09-28): the Grim Forest tile shows stages completed versus remaining, and
nothing else. Every map node is a stage**: battles, chests, altars and path nodes alike. The count is
`completedSlotIds` against the map's node count. No per-node rewards and no node-kind breakdown on the
tile.

---

## Inputs

- **Account** (uploader v1.30.0): `grimForest.difficulties[d].completedSlotIds` (schema 31), the map
  nodes completed this rotation, by node number (1–403). Absent = not told (older uploader or failed
  read), never "none". `[]` = entered with nothing completed.
- **Catalog** (`RslCompanionMetadata` `0a0a202`, `ModeRewards` → `grimForest.map`):
  `map.difficulties[d].slotCount`, the number of nodes on this rotation's map (403 on both difficulties
  in rotation 10), and `map.rotation`.

A real v1.30.0 block (rotation 10, 2026-09-27), trimmed:

```jsonc
"grimForest": { "rotation": 10, "difficulties": [
  { "difficultyId": 1, "level": 23, "passedStageIds": [ /* 78 */ ], "completedSlotIds": [1, 2, 3, /* … 232 */ 389] },
  { "difficultyId": 2, "level": 30, "passedStageIds": [ /* 135 */ ], "completedSlotIds": [1, 2, 3, /* … */ 403] }
] }
```

Hard is a fully cleared map (the player confirmed it): **403 / 403**. Normal: **232 / 403, 171
remaining**.

## The bug this fixes

The tile draws `passedStageIds.size / GAME.grimForest.stagesPerDifficulty` (211), so a fully cleared
Hard map renders **135 / 211 (64%)**. `passedStageIds` holds the *battles* only, and 211 is the game's
whole stage pool, of which a rotation's map uses 104 plus whatever random encounters spawn. Neither is
the player's notion of a stage. Replace that bar and delete `stagesPerDifficulty`.

## What to do

1. **Store it.** Add `List<int>? CompletedSlotIds` to `GrimForestDifficulty`
   (`RaidTools.Api/Models/ModeProgress.cs`), beside `PassedStageIds`. The adapter binds `grimForest`
   straight into that model, so the property is the whole backend change. Bump the adapter to
   **4.9.0**: MINOR, additive, a payload without it stores null — "not told", never "nothing
   completed".
2. **Type it.** Add `completedSlotIds?: number[] | null` to `GrimForestProgress.difficulties[]` in
   `raidtools-frontend/src/app/core/mode-progress.ts`.
3. **Show it.** Per difficulty (Hard first, as today):
   - **completed** = `new Set(completedSlotIds).size`
   - **total** = `ModeRewards.grimForest.map.difficulties[d].slotCount`
   - **remaining** = total − completed

   Render it as `${name} stages: completed / total (remaining left)`. Use the catalog's total only when
   `map.rotation === grimForest.rotation`. If the map is missing or from another rotation, fall back to
   `GAME.grimForest.stagesPerMap: 403` and comment it as the fallback (the catalog wins), like
   `keysPerDay`. Never use 211.
4. **Drop the old stages bar and its forecast.** `passedStageIds` stays stored, but the tile no longer
   uses it. The keys/remaining forecast built on `211 − passed` goes with it.

The catalog also carries each node's kind and reward (`map…slots`, `stages`, `chests`, `extraSlots`) if
a rewards-left view is wanted later. It is deliberately out of scope here.

## Traps

- **Absent ≠ empty.** Keep the partial-feed rule from the mode-progress work: a payload without
  `completedSlotIds` must not blank a stored value.
- **This rotation only.** Compare `grimForest.rotation` with the current rotation before showing it. A
  rotation-9 snapshot reads as nothing done in rotation 10.
- **Normal and Hard use the same node numbering (1–403)**, so a node number means nothing without its
  `difficultyId`. They are not stage ids (`14012003` is a battle, `3` is a node) and are never joined
  onto the catalog's `stages`.
