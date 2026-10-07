# Display the Titan Event milestones (schema 41)

Consumer-side prompt for **RaidTools**, written to be handed straight to an agent: paste it into a
Claude Code session opened on that repo. It builds on [`raidtools-events.md`](raidtools-events.md) and
[`raidtools-events-display.md`](raidtools-events-display.md), which are already implemented there
(`Models/TimeLimitedContent.cs`, `core/events.ts`, the event tiles in `mode-progress-tiles`).

It is a *summary*. [`export-schema.md`](export-schema.md) / [`.json`](export-schema.json) are the
contract (section **"Time-limited content" → Titan Event**, Changelog row 41). Where this file disagrees
with them, they win, and this one is stale and should be fixed.

---

## What changed on the wire

A **Titan Event** (the game calls it a "universal" event internally) is a weeks-long event whose points,
**Titan Points**, are earned in *other* events and tournaments. It arrives as an ordinary
`soloEvents[]` entry with **`soloTypeId` 4**. Up to schema 40 its `rewards` was `[]`. Since uploader
v1.45.0 (schema 41) it carries the whole milestone table:

```jsonc
{ "eventId": 4440, "questPrototypeId": 364440, "soloTypeId": 4, "title": "Ingenious Titan Event",
  "startsAt": "2026-10-05T09:00:00Z", "endsAt": "2026-10-22T09:00:00Z", "claimUntil": "2026-10-22T11:30:00Z",
  "points": 50,                       // Titan Points earned so far
  "claimedRewardIds": [],
  "rewards": [                        // 50 on event 4440, sorted by milestone, then points
    { "id": 1,  "points": 10,   "milestone": 1, "prize": { "resources": [{ "id": 2, "amount": 100000 }] } },
    { "id": 20, "points": 300,  "milestone": 2, "prize": { "avatarIds": [10740] } },
    { "id": 31, "points": 640,  "milestone": 4, "prize": { "randomGemstones": [{ "count": 1, "rarityIds": [4] }] } },
    { "id": 50, "points": 1500, "milestone": 5, "prize": { "champions": [{ "typeId": 10740, "count": 1 }] } }
    /* … */ ] }
```

Two additions in schema 41:

- **`rewards[].milestone`** (Titan Event only): the in-game reward tab, "Milestone 1" … "Milestone 5".
  On 4440 each tab holds 10 rewards (1: 10–100 pts, 2: 120–300, 3: 330–600, 4: 640–1,000, 5: 1,050–1,500).
- **`prize.randomGemstones`** (any prize): `[{ count, rarityIds, onlyShapeIds?, excludedShapeIds? }]`,
  gemstones drawn at random when the prize is taken. Before 41 it was only named in
  `otherKinds: ["RandomRelicStonesPrizes"]`. Frontier outposts 28 / 47 / 48 pay them too.

The next Titan Event will have the same shape with different prizes, thresholds and dates. Nothing
here may hardcode 5 tabs, 10 rewards or 1,500 points.

## How the event works

- **Thresholds are cumulative**, as on a points-tier event. A reward is **claimable** when
  `points ≥ reward.points` and `id ∉ claimedRewardIds`, **claimed** when `id ∈ claimedRewardIds`, else
  locked. Ids run across tabs (1–50), so they are the claim key.
- **Where Titan Points come from: item 10600.** A labelled event or tournament pays Titan Points as an
  ordinary tier prize, `prize.items[{ id: 10600 }]`. Event 4444 paid 10 + 20 + 50 over three tiers,
  which the game advertises as "+ 80 TP". So for every *other* entry in `soloEvents[]` and
  `tournaments[]`:
  - Titan Points on offer = Σ item-10600 amounts over its `rewards`
  - still to earn there = the same sum over tiers not in its `claimedRewardIds`
  On 2026-10-07 that gave Gear Hunters 80, Gear Enhancement 60, Spider Turn Attack 50, Ice Golem Turn
  Attack 40, each matching its in-game label.
- **Phases.** Until `endsAt` Titan Points can be earned. From `endsAt` to `claimUntil` (2.5 h on 4440)
  only claiming works. After `claimUntil` the existing `isExpired` filter drops the entry.

## Backend (RaidTools.Api)

1. `Models/TimeLimitedContent.cs`: add `public int? Milestone { get; set; }` to the event reward class,
   and a `RandomGemstones` list on the prize class (`PrizeRandomGemstones { Count, RarityIds,
   OnlyShapeIds?, ExcludedShapeIds? }`, `[BsonIgnoreExtraElements]`).
2. Nothing else on the server: it binds, stores and serves through the existing `soloEvents` path.
3. `RaidTools.Api.Tests/EventsImportTests.cs`: a schema-41 Titan entry round-trips `milestone` and
   `randomGemstones`; a schema-40 one (`rewards: []`) still imports.

## Frontend

**`core/events.ts`**, pure functions with specs in `events.spec.ts`:
- `TITAN_POINTS_ITEM = 10600`
- `titanPointsOffered(entry)` / `titanPointsRemaining(entry)` for any solo event or tournament
- `milestoneTabs(e)` → rewards grouped by `milestone`, in key order, with per-tab claimed / total
- `nextTitanReward(e)` → the lowest unclaimed threshold above `points`, for a "70 pts → next reward" line
- `toPrize` / `eventPrizeChips`: draw `randomGemstones` as "Random Epic Gemstone ×1" (rarity labels
  from the artifact rarity table; a multi-rarity entry joins them with "/").

**`mode-progress-tiles.component.ts`**: in `soloEventTile`, a branch **before** the points-tier check:
`if (e.soloTypeId === 4 && e.rewards.some(r => r.milestone != null)) { … }`, with its own icon (⚡). The tile shows:
- headline: `points` Titan Points of the last threshold, and the current tab ("Milestone 1 of 5")
- the `nextTitanReward` line
- reward rows grouped by tab, current tab expanded, same claimable / claimed styling as tier rows
- a "Where to earn" list: every other active entry with `titanPointsOffered > 0`, showing the points still
  to earn there, sorted by `endsAt`

On every *other* event and tournament tile, add a small "+N TP" badge when `titanPointsOffered > 0`.

A `soloTypeId` 4 entry with `rewards: []` (a pre-41 upload) falls through to the existing fallback tile
with "Sync again with uploader 1.45+".

## Traps

- **`milestone` is a tab, not a reward index or a level.** Don't number rewards by it.
- **`soloTypeId` 4 is the only signal.** The title changes per event ("Ingenious Titan Event"); never
  match on the word "Titan".
- **Claims are unverified until someone claims a milestone.** The uploader reads them from the same list
  as other solo events, which is what the game's shared quest shape implies. If a claimed milestone ever
  shows as claimable, the producer's assumption is wrong. Report that; don't patch around it.
- **Item 10600 is "Titan Points", not a champion.** Champion type 10600 (Vallaryn) is another id space.
- `TopRewards` (the banner's 5 headline prizes) are not on the wire; they repeat milestone prizes.
