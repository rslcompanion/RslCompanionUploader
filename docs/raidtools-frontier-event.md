# Display the Frontier Event (schema 39)

Consumer-side prompt for **RaidTools**, written to be handed straight to an agent: paste it into a
Claude Code session opened on that repo. It builds on [`raidtools-events.md`](raidtools-events.md) and
[`raidtools-events-display.md`](raidtools-events-display.md), which are already implemented there
(`Models/TimeLimitedContent.cs`, `core/events.ts`, the event tiles in `mode-progress-tiles`).

It is a *summary*. [`export-schema.md`](export-schema.md) / [`.json`](export-schema.json) are the
contract (section **"Time-limited content" → `frontier`**, Changelog row 39). Where this file disagrees
with them, they win, and this one is stale and should be fixed.

---

## What changed on the wire

A **Frontier Event** is the game's `ConquestEvent`: a map of outposts the player conquers by
completing quests. It arrives as an ordinary `soloEvents[]` entry with **`soloTypeId` 8**, and since
uploader v1.43.0 (schema 39) that entry carries a `frontier` block:

```jsonc
{ "eventId": 4447, "questPrototypeId": 364447, "soloTypeId": 8, "title": "Frightful Frontier",
  "startsAt": "2026-10-05T10:30:00Z", "endsAt": "2026-10-23T10:30:00Z", "claimUntil": "2026-10-24T10:30:00Z",
  "points": 4,                       // Frontier Points earned. Was always 0 before schema 39.
  "claimedRewardIds": [], "rewards": [],   // stays [] — the rewards are on the outposts
  "frontier": {
    "purchaseStatus": 0,             // raw; 0 = Explorer Pass not bought
    "unlockPoints": [ { "rarity": 2, "points": 15 }, { "rarity": 3, "points": 25 },
                      { "rarity": 4, "points": 40 }, { "rarity": 5, "points": 60 } ],
    "outposts": [                    // 22 on event 4447, sorted by id
      { "id": 1, "rarity": 1, "typeId": 2, "neighbourIds": [2, 9],
        "questPrototypeIds": [10860050, 10860450, 10860550, 10860700],
        "rewards": [ { "slot": 0, "track": 1, "prize": { "resources": [{ "id": 4, "amount": 25 }] } } /* … 4 */ ],
        "started": true, "completedQuestIds": [10860050, 10860450, 10860550, 10860700], "claimedSlots": [0, 1, 2, 3],
        "quests": [ { "questPrototypeId": 10860050, "completed": true, "condition": "Battle",
                      "countRequired": 10, "countCollected": 10, "points": 1 } /* … 4 */ ] },
      { "id": 3, "rarity": 1, "typeId": 1, "neighbourIds": [2, 4, 10, 11], "questPrototypeIds": [ … ],
        "rewards": [ … ], "started": false, "completedQuestIds": [], "claimedSlots": [], "quests": [] }
      /* … */ ] } }
```

`prize` is the same `eventPrize` shape as solo-event tiers. `toPrize` / `eventPrizeChips` in
`core/events.ts` already draw it, and that includes the Scarecrow soul grades and the avatar frame
this event pays.

## How the event works

You need this to draw it correctly:

- **Points.** Every outpost has 4 quests, and each quest pays **1 Frontier Point**. 22 outposts = 88
  points on event 4447.
- **Milestones = `unlockPoints`.** An outpost **opens** when (a) an adjacent outpost
  (`neighbourIds`) is completed **and** (b) `points` ≥ the threshold for its rarity. A rarity missing
  from `unlockPoints` needs no points. Outpost `typeId` 2 is the start (open from the beginning).
  `typeId` 5 is the **final outpost**, which opens only once every other outpost is completed. An outpost
  with `unlocksAt` also stays closed until that time.
- **Completed** = `completedQuestIds` holds every id in `questPrototypeIds`.
- **Rewards.** 4 slots per outpost. `track` 1 = **Basic** (free), 2 = **Explorer** (the paid
  **Explorer Pass**). A slot is claimed when its `slot` is in `claimedSlots`. A slot is claimable when the
  outpost is completed, the slot isn't claimed, and the track is 1 or the pass is bought.
- **Rarity** 1–5 = **Uncommon, Rare, Epic, Legendary, Mythical** Zone (game colours `#19D041`,
  `#2697FF`, `#E74AFF`, `#EE8100`, `#FF452B`).
- **Phases.** Before `endsAt`: quests and points count. From `endsAt` to `claimUntil`, the reward phase:
  nothing more can be earned, but slots can still be collected. After `claimUntil` the entry is expired,
  and the existing `isExpired` filter already drops it.

## Backend (RaidTools.Api)

1. `Models/TimeLimitedContent.cs`: add `public FrontierProgress? Frontier { get; set; }` to
   `SoloEventProgress`, plus classes mirroring the wire, field for field: `FrontierProgress
   {PurchaseStatus, UnlockPoints, Outposts}`, `FrontierUnlock {Rarity, Points}`, `FrontierOutpost {Id,
   Rarity, TypeId, UnlockAfterMinutes?, UnlocksAt?, NeighbourIds, QuestPrototypeIds, Rewards, Started,
   CompletedQuestIds, ClaimedSlots, AutoGivenSlots?, Quests}`, `FrontierReward {Slot, Track, Prize}`
   (reuse the existing prize class), `FrontierQuest {QuestPrototypeId, Completed, Condition,
   CountRequired, CountCollected, Points}`. `[BsonIgnoreExtraElements]` on every one, as the file's
   header requires.
2. Nothing else on the server: the block binds, stores and serves through the existing
   `soloEvents` path (`ConsolidatedJsonSyncAdapter` → `AccountSnapshot` → `GET /api/events`).
3. `RaidTools.Api.Tests/EventsImportTests.cs`: one test that imports a schema-39 entry with `frontier`
   and reads it back intact, and one that a schema-38 Frontier entry (no `frontier`, `points` 0) still
   imports with `Frontier == null`.

## Frontend

**`core/events.ts`**: add the wire types, plus pure functions with specs in `events.spec.ts`:
- `outpostCompleted(o)`
- `outpostState(o, f, points, now)` → `'completed' | 'open' | 'locked-points' | 'locked-path' | 'locked-time' | 'final-locked'`
  (open = `started` and not completed; locked-points names the shortfall, `threshold − points`)
- `frontierRarityLabel(r)` and `frontierTrackLabel(t)` (`Basic` / `Explorer`)
- `frontierProgress(f)` → completed outposts / total, claimed slots / total, and points / max.
  Take max as `4 × outposts.length`: every quest seen pays 1, and hidden outposts carry no quest
  states to sum.
- `nextMilestone(f, points)` → the lowest `unlockPoints` threshold above `points`, for a "15 pts → Rare
  outposts" line

**`mode-progress-tiles.component.ts`**: in `soloEventTile`, add a branch **before** the
points-tier check: `if (e.soloTypeId === 8 && e.frontier) { … }`, with its own icon (🧭). The tile shows:
- headline: `points` Frontier Points, completed outposts `n / 22`
- the milestone line from `nextMilestone`, or "All zones unlocked"
- reward rows grouped by outpost, using the same claimable / claimed styling as tier rows. Draw Explorer
  slots **locked** (not claimable) while `purchaseStatus` is 0, labelled "Explorer Pass"
- the open outposts' quests as `condition countCollected / countRequired` lines

A `soloTypeId` 8 entry **without** `frontier` (a pre-39 upload, or a failed read) falls through to the
existing fallback tile. Its `points` is 0 there, which is wrong, so for type 8 show "Sync again with
uploader 1.43+" instead of "Points 0".

A map view (outposts on their grid, coloured by rarity) is a nice follow-up, not part of this step. The
ids are cells of a hex-like grid (1–48 on 4447, only 22 used); `neighbourIds` is the adjacency.

## Traps

- **`quests: []` on an unrevealed outpost is not "no quests".** The server creates quest states only when
  an outpost opens. Show its 4 `questPrototypeIds` as "hidden", never as zero quests.
- **Quest prototype ids repeat across outposts** (10860450 is in Outposts 1 and 2). Key a quest by
  `(outpost id, questPrototypeId)`.
- **There is no quest text.** `condition` is the completion kind (`Battle`, `Hero`, `Artifact`, …) with
  counts. Render "Battle 4/10", don't invent a sentence.
- **`frontier` absent ≠ no map.** It means this upload could not read it. Keep the previous snapshot's
  `frontier` for the same `eventId` only if the existing events code already merges that way; it
  doesn't today (each snapshot replaces), and that is fine.
- **`purchaseStatus` is raw.** Only 0 has been seen. Treat anything non-zero as "bought" and flag it in a
  comment as unverified.
- **Rarity labels are unverified on screen**; the order of the game's five labels is the evidence. Keep
  them in one table so a correction is one edit.
- `TopRewards` (the event's 5 headline prizes) are not on the wire; don't look for them.
