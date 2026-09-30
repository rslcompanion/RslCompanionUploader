# Display solo events, tournaments and the Forge Pass on the dashboard

Consumer-side prompt for **RaidTools**, written to be handed straight to an agent: paste it into a
Claude Code session opened on that repo. It is the **frontend step** that
[`raidtools-events.md`](raidtools-events.md) deferred. Do that one first: this prompt reads the
`GET …/events` endpoint and the stored blocks it adds.

It is a *summary*. [`export-schema.md`](export-schema.md) / [`.json`](export-schema.json) are the
contract (section **"Time-limited content"**, Changelog row 33); where this file disagrees with them,
they win, and this one is stale and should be fixed.

---

## Inputs

What the endpoint returns (uploader v1.33.0, schema 33), with expired entries already filtered out
server-side:

```jsonc
{ "timestamp": "2026-09-30T15:20:00Z",          // the snapshot's — "as of your last sync"
  "soloEvents": [
    { "eventId": 4428, "questPrototypeId": 364428, "soloTypeId": 1, "title": "Gear Enhancement Event",
      "startsAt": "2026-09-28T09:00:00Z", "endsAt": "2026-10-01T09:00:00Z", "claimUntil": "2026-10-02T09:00:00Z",
      "points": 4971, "claimedRewardIds": [1,2,3,4,5,6,7,8,9,10],
      "rewards": [ { "id": 1, "points": 100, "prize": { "resources": [{ "id": 1, "amount": 50 }] } }, /* … 12 */ ] },
    { "eventId": 4420, "soloTypeId": 2, "title": "Wicked Path Event", "points": 4341, "boardCurrency": 3841,
      "claimedRewardIds": [105],
      "rewards": [ { "id": 105, "cost": 500, "row": 1, "column": 5, "parentIds": [], "prize": { … } }, /* … 30 */ ] },
    { "eventId": 4416, "soloTypeId": 5, "title": "Supporters Summon Pool", "points": 55, "claimedRewardIds": [], "rewards": [] } ],
  "tournaments": [
    { "eventId": 4431, "tournamentKindId": 7, "title": "Fire Knight Tournament",
      "endsAt": "2026-10-01T11:00:00Z", "claimUntil": "2026-10-05T11:00:00Z",
      "points": 2054, "bracketIndex": 1, "claimedRewardIds": [1,2,3,4],
      "rewards": [ { "id": 1, "points": 250, "prize": { … } }, /* … 7 */ ] } ],
  "battlePass": { "passId": 1037, "kindId": 2, "status": 1, "points": 245,
    "tracks": [ { "trackId": 1, "claimedLevels": [1, /* … */ 24] }, { "trackId": 2, "claimedLevels": [] },
                { "trackId": 3, "claimedLevels": [] } ] } }
```

Each key can be **null**: the snapshot does not say. That is a different thing from `[]`, which
means nothing is active.

## Where it goes

The dashboard already has an **"Events & progress"** group that hosts `app-mode-progress-tiles`, with
tiles, reward rows, the roadmap popup and the "only unclaimed" switch. **Put the new tiles inside that
component, after the mode tiles.** Don't make a new page or a second tile component. Each event is one
more `ModeTile`, built by the same `tiles` computation, so collapse, expand, countdown and claimable
badges come for free. Put it behind the same `mode-progress` feature gate.

Files:
- `raidtools-frontend/src/app/core/events.ts` (new): wire types for the three blocks, plus the pure
  functions below. Keep them pure so they get specs, like `mode-rewards.ts` does.
- `raidtools-frontend/src/app/core/events.service.ts` (new): `get(accountId)`, shaped like
  `ModeProgressService`.
- `mode-progress-tiles.component.ts` / `.html` / `.css`: load it alongside mode progress (a failed
  fetch yields no event tiles, not an error), and turn it into tiles.

## One shared step: map `eventPrize` to the catalog's `Prize`

The event prize is almost the metadata `Prize` that `prizeChips()` already draws. So **convert it and
reuse `prizeChips`**; don't write a second chip renderer.

| event prize | `Prize` |
|---|---|
| `resources[] {id, amount}` | `resources[] {id, name, amount}` |
| `items[] {id, amount}` | `items[] {id, name, count: amount}` |
| `champions[] {typeId, count}` | `champions[] {typeId, name, count}` |
| `souls[] {championBaseId, rarityId, grade}` | `souls[] {baseTypeId, name, rarity, grade}` |
| `artifacts[]` | `artifacts[]` (same fields) |
| `avatarIds[]` / `frameIds[]` | `avatars[]` / `frames[]` |
| `otherKinds[]` | one extra chip, "+ more", whose hint lists the kinds |

**Names: the event prize carries ids only.** Build one id→name lookup, `eventPrizeNames()`, from what
the page already has, in this order:
1. Every prize in the loaded `ModeRewardsDto`. Walk it once and collect `resources`, `items` and
   `champions` names by id. It already names Basalt, chickens, XP boosts, tomes and Silver.
2. The champion catalog, for `champions[].typeId` and `souls[].championBaseId`.

Anything left gets `name: null`. `prizeChips` then draws `#id`, which is the rule the catalog already
follows. **Never invent a name.** Icons follow automatically: `iconName` is the name, joined to the
resource art by `keyOfName`.

## The tiles

One tile per event and per tournament, and one for the pass. Order: anything with rewards to claim
first, then by `endsAt` ascending, so the soonest deadline comes first. The key is
`event-${eventId ?? questPrototypeId}`, so the expanded state survives a re-sync.

**Every event and tournament tile:**
- `title`: the event's `title`, or `Event #questPrototypeId` when absent.
- Before `endsAt`: `endLabel` "Ends", `endAt` = `endsAt`.
- After `endsAt` and before `claimUntil`: `endLabel` "Claim by", `endAt` = `claimUntil`, plus a note
  "Event over — rewards still to claim". A tournament sits in this state for up to four days.
- A `TileLine` "As of last sync" carrying the snapshot time, because points move fast during an event.

### Points events (`soloTypeId` 1) and tournaments

Add `eventTrack(rewards, claimedRewardIds, points)` to `events.ts`, returning the existing
`RewardTrack` shape. **Don't call `rewardTrack()`.** That one keys `taken` by threshold, and here the
claims are tier **ids**. The logic is:
- `at` = `reward.points`, `key` = `String(reward.id)`
- the state is `collected` when the id is claimed, else `claimable` when `points ≥ at`, else `locked`
- `next` = the first locked step

Tile content:
- **Lines:** "Points" = `points`. On tournaments, also "Bracket" = `bracketIndex`, and "Rank" only when
  `position > 0`.
- **Summary** (collapsed): "`points` pts · next tier at `next.at`", with a bar from the previous tier
  up to `next.at`. With every tier collected: "All tiers collected" and a full bar.
- **One `RewardRow`** "Tiers": value "`collected` / `total`", `claimable` from the track,
  `nextLabel` "Next at `next.at`" with its chips, and `steps` for the roadmap popup
  (`at` = threshold as text, chips from the mapped prize). The unclaimed-only switch then works
  unchanged.

### Board events (`soloTypeId` 2)

These are a grid bought with points, not a bar. Add `boardCells(rewards, claimedRewardIds, boardCurrency)`:
- **taken:** the id is claimed
- **available:** every `parentIds` entry is taken (the entry row has `[]`)
- **affordable:** available and `cost ≤ boardCurrency`

Tile content:
- **Lines:** "Points earned" = `points`, "To spend" = `boardCurrency`.
- **Summary:** "`taken` / `total` cells · `affordable` affordable".
- **One `RewardRow`** "Board" whose `steps` go in row-then-column order.
  - Each step's `at` is "Row r · col c · cost".
  - Map the cell's state onto a step state: taken → `collected`, affordable → `claimable`,
    available but not affordable → `open`, blocked → `locked`.
  - `claimable` = the affordable count. That is the number of cells the player could buy now.

**Never draw `points` against `cost`.** Points are earned; `boardCurrency` is what is left. A cell's
cost is paid out of `boardCurrency`, so the gap between the two is spent, not missing.

### Summon pools (`soloTypeId` 5) and any other type

A "Points" line and a note: "Rewards for this event are shown in game." `rewards` is `[]` here, by
contract. For an unknown `soloTypeId` that has `points` on its rewards, use the points-event tile.
Otherwise use this one.

### The Forge Pass

One tile, keyed `forge-pass`, titled **"Forge Pass"**. Add " (ended)" to the title when `status` is 2.
The wire field is `battlePass`, but the player's word is Forge Pass.

- **Lines:** "Points" = `points`. Then one line per track: "Free", and "Track 2" / "Track 3" (see the
  traps), each valued "`claimedLevels.length` collected".
- **No end date, no level, no next reward.** Those need the pass's level table, which is static data
  headed for RslCompanionMetadata keyed by `passId`. Until it ships, add the note "Level and rewards
  need the pass catalog". **Don't hardcode pass 1037's thresholds** to fill the gap.
- When the catalog lands:
  - level = the highest level whose **cumulative** threshold is ≤ `points` (1037: L2 20, L3 30 …
    L50 500);
  - a level is claimable on a track when it has been reached and is not in that track's
    `claimedLevels`;
  - render each track as a `RewardRow` with `steps`, like the tiers.

## Traps

- **Null is not empty.** When the endpoint returns null for a key, draw nothing and add no "no events"
  line. `[]` may say "No active events", once, in the group.
- **Filter expiry on the client too.** The server filters at fetch time, but a tab left open crosses
  `claimUntil`. Drop a tile whose `claimUntil` has passed when the countdown ticks.
- **Track labels.** 1 = Free is certain; 2 and 3 are inferred to be Gold and Platinum. Label them
  "Track 2" / "Track 3", or "Gold" / "Platinum" with a "provisional" hint. An empty premium track does
  **not** mean it wasn't bought, so never say "not purchased".
- **Rank is the player's own, and often 0.** Don't show "Rank 0". Never fetch or join other accounts
  to build a leaderboard.
- **Tournament rewards are per bracket.** Draw this account's `rewards` as sent. Never share a
  tournament's rewards across accounts by `eventId`.
- **Summon pool points are unconfirmed** (55 vs 40 on the mapping account). Show the number without a
  "next tier" framing.
- **`title` is display text** in the language of the client that uploaded it. Key everything on
  `eventId ?? questPrototypeId`.

## Specs (next to `mode-rewards.spec.ts`)

- `eventTrack`: Gear Enhancement (points 4,971, claimed 1–10): 10 collected, 0 claimable, next at
  5,600. Raise points to 5,700: 1 claimable.
- `boardCells`: claimed `[105]`, currency 3,841. Every cell whose `parentIds` is `[105]` is available,
  and those costing ≤ 3,841 are affordable. A cell with an untaken parent is locked whatever its cost.
- The prize mapping: `items[].amount` becomes `count`, `souls[].championBaseId` becomes `baseTypeId`,
  an unknown id gets `name: null` and draws `#id`, and a non-empty `otherKinds` adds one "+ more" chip.
- A null `soloEvents` produces no tiles and no empty-state line; `[]` produces the empty-state line.

## Verification

With a v1.33.0 snapshot of the mapping account (2026-09-30):
- **3 event tiles:**
  - Gear Enhancement: 10 / 12, next at 5,600.
  - Wicked Path: board, 1 / 30 cells, 3,841 to spend.
  - Summon Pool: points only.
- **7 tournament tiles.**
  - The ones past `endsAt` read "Claim by": Dragon Turn Attack, Gear Ascension, Champion Training,
    Classic Arena Takedown.
  - Fire Knight is 4 / 7 with **1 to claim** (2,054 points against tier 5 at 1,800).
  - Soul Chase is 2 / 8 at 160 points, next at 400.
- **1 Forge Pass tile:** 245 points, Free 24 collected, and the catalog note.

Then:
- Check the tiles against the game's own event, tournament and pass screens. Nobody has done that
  yet, on either side.
- A pre-schema-33 snapshot must show none of these tiles, and nothing else on the dashboard may change.
