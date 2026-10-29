# RaidTools: draw only points, board and Titan events

A prompt for the RaidTools frontend. **The owner's decision (2026-10-10): the Events group draws a tile
for exactly three solo-event types: `soloTypeId` 1, 2 and 4.** Every other type is stored as before
but drawn nowhere. Tournaments are not affected.

## The type table

`soloEvents[].soloTypeId` is the game's `GlobalEventSoloTypeId`. The member names below were read from
the live client (11.80.0). The ids follow declaration order, and every id seen live sits where that
order puts it.

| Id | Game name | What it is | Drawn |
|---|---|---|---|
| 1 | PointBased | Points tiers (Gear Enhancement Event, …) | **yes** |
| 2 | ThreeLine | Board events (Hero's Path, Wicked Path, …) | **yes** |
| 3 | Bingo | Deck of Fate (seen 2026-10-29; schema 45 adds its deck and board) | no |
| 4 | Universal | Titan Event | **yes** |
| 5 | LimitedHeroSummon | Champion summon pool | no |
| 6 | LimitedSoulSummon | Soul summon pool ("Support Soul Summon Pool") | no |
| 7 | CooperationEvent | Shared cooperation event, never seen | no |
| 8 | Conquest | Frontier Event (already hidden by `FRONTIER_TILE_SHOWN`) | no |

The current code and comments say "5 = summon pool" and know nothing of 6. That is half right: 5 and
6 are two different pools. The soul pool reached the generic fallback tile by elimination and showed
"0 pts" with nothing to claim.

## What to change

All of it is in `raidtools-frontend/src/app/core/events.ts`, plus its spec.

1. **Replace `soloEventShown` with an allow-list.** Use
   `SHOWN_SOLO_TYPES = new Set([1, 2, TITAN_SOLO_TYPE])`, and return
   `SHOWN_SOLO_TYPES.has(e.soloTypeId)`. It must be an allow-list, not a deny-list: a type the game
   adds later stays hidden until someone decides how to draw it.
   - Frontier still needs to come back when `FRONTIER_TILE_SHOWN` is flipped. Keep that:
     `|| (FRONTIER_TILE_SHOWN && e.soloTypeId === FRONTIER_SOLO_TYPE)`.
2. **Both callers already go through it, so nothing else needs a new filter:**
   - The tile loop in `mode-progress-tiles.component.ts` (`soloEventTile`).
   - `noActiveEventsText`, so "No active solo events" now means "none of the drawn types".
   - Check that the group header's count and "N to claim" use the same filtered list. A hidden
     event must not be counted in a number the user can't trace to a tile.
3. **Leave the rest alone.**
   - **The Titan "where to earn" sources** (`TitanSource[]`, built from every solo event and
     tournament) keep using the unfiltered list. A hidden event can still pay Titan Points, and that
     list is about the points, not about tiles.
   - `paysChampions` stays unfiltered too.
   - The API keeps storing every type. This is a display rule, never an import rule.
4. **Clean up the fallback.** In `soloEventTile` the last branch (🌀, "Rewards for this event are
   shown in game", the `soloTypeId === 5` hint) is now reached only by a type-1 event with no reward
   table. That is an ended event the server dropped from its catalog, like quest 364444. Either:
   - point that branch at the points-tier tile without tiers, or
   - delete the summon-pool wording, which no shown type can reach any more.
5. **Fix the comments.** `events.ts:74` and the backend's `TimeLimitedContent.cs:38` say
   "1 points, 2 board, 5 summon pool, 8 Frontier". Replace them with the table above, or a pointer to it.

## Tests (`events.spec.ts`)

- `soloEventShown` is true for 1, 2 and 4, and false for 3, 5, 6, 7, 8, 9 and 0.
  - Replace the current `[1, 2, 5]` expectation, which asserts the opposite for 5.
- With `FRONTIER_TILE_SHOWN` true, 8 is shown. Skip this case if the flag is a `const` that the spec
  can't flip.
- `noActiveEventsText` returns "No active solo events" when the only live solo event is a type 6.

## Don't

- Don't filter on `title`. Titles are server text in the player's language, and they change from run
  to run.
- Don't treat a missing `title` as a reason to hide. A type-1 event in its claim window arrives with
  no catalog fields (see `docs/export-schema.md`, "Where it comes from"). It is still a type-1 event
  and is still drawn.

Same standing as the other `raidtools-*.md` notes: a summary, never the contract.
