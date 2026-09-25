# Store and serve mode progress — arenas, Doom Tower, Cursed City, Grim Forest, Siege

Consumer-side prompt for **RaidTools**, written to be handed straight to an agent: paste it into a
Claude Code session opened on that repo.

It is a *summary*. [`export-schema.md`](export-schema.md) / [`.json`](export-schema.json) are the
contract for the payload side (section **"Mode progress"**, Changelog rows 25 and 26); where this file
disagrees with them, they win, and this one is stale and should be fixed.

---

## What changed on the producer

Uploader **v1.23.0 (schema 25)** and **v1.24.0 (schema 26)** add six top-level objects to the
consolidated payload. Today `ConsolidatedJsonSyncAdapter` deserializes into a class that has none of
them, so **all six are dropped on import** — nothing below reaches rslcompanion.com until this lands.

| Key | What it carries |
|---|---|
| `classicArena` | points, `leagueId` (the tier the game *applies*), previous tier, this week's battles / wins / losses, defeats today, last weekly reset |
| `liveArena` | points, lifetime W/L, `maxPointsAchieved`, the permanent milestones claimed, daily flags, `victoriesForRegularReward` (the "Wins 18/35" quest, schema 26), and `season` — the season the account **last played** |
| `doomTower` | per difficulty: `stageIndicator`, `firstEnteredAt` |
| `cursedCity` | `rotation` number; per difficulty: "pass N stages" / "pass N awakening stages" rewards claimed, main-boss reward taken, milestones claimed |
| `grimForest` | `rotation` number; per difficulty: progression level (1–30), XP, curio slots, treasure hunt |
| `siege` (26) | current / last-finished cycle, last action, and per cycle (current + the last three) the milestone, resource and result rewards claimed |

A real v1.24.0 payload, trimmed:

```jsonc
"classicArena": { "points": 3358, "leagueId": 25, "previousLeagueId": 25, "battlesThisWeek": 30,
                  "victoriesThisWeek": 29, "lossesThisWeek": 9, "defeatsToday": 1,
                  "lastWeeklyRewardAt": "2026-09-21T08:00:27Z" },
"liveArena": { "points": 20733, "victories": 5302, "defeats": 4736, "lastSeenLeagueId": 1,
               "maxPointsAchieved": 20733, "takenMilestoneRewards": [910, 920, /* … */ 4800],
               "dailyRewardTaken": false, "victoriesForRegularReward": 18,
               "battlesToday": 0, "victoriesToday": 0,
               "season": { "number": 14, "points": 118, "victories": 14, "defeats": 3, "battles": 17,
                           "maxPointsAchieved": 118, "lastParticipationAt": "2026-09-23T13:15:43Z",
                           "takenMilestones": [20, 60, 100], "takenRepeatableChestSteps": [],
                           "leaderboardPosition": 0, "leaderboardRewardTaken": false } },
"doomTower": { "difficulties": [
  { "difficultyId": 1, "stageIndicator": 7011010, "firstEnteredAt": "2026-09-20T05:16:22Z" },
  { "difficultyId": 2, "stageIndicator": 7012010, "firstEnteredAt": "2026-09-07T15:19:53Z" } ] },
"cursedCity": { "rotation": 34, "difficulties": [
  { "difficultyId": 2, "takenStageRewards": [25, 50, 101], "takenAwakeningStageRewards": [6, 12],
    "mainBossRewardTaken": true, "takenMilestoneRewards": [10, 25, 40, /* … */ 500] } ] },
"grimForest": { "rotation": 10, "difficulties": [
  { "difficultyId": 2, "level": 30, "experience": 11650, "curioSlots": 6, "treasureHuntReceived": true } ] },
"siege": { "currentCycle": 58, "lastFinishedCycle": 57, "lastActionAt": "2026-09-14T11:46:04Z",
  "cycles": [ { "cycle": 57, "isCurrent": false, "takenMilestoneRewards": [0],
                "takenResourceRewards": [3, 5, 6], "siegeRewardTaken": false,
                "availableRewardGiven": false, "presetCount": 5 } /* … newest first */ ] }
```

Two rules hold for every block and are the whole reason this needs care rather than a straight copy:

1. **Absent means "this feed could not read it", never "the account has none".** The producer omits
   a block when its read could not be validated, rather than sending an empty one. Every pre-schema-25
   payload omits all six. Store **null**, and never let a later feed that omits a block blank one a
   previous feed stored.
2. **An empty list *inside* a present block is a real answer.** `takenMilestoneRewards: []` on a
   present `cursedCity` block means nothing has been claimed this rotation.

## Also changed: `account.liveArenaPoints`

Since schema 25, `account.liveArenaPoints` is the game's named `UserLiveArenaData.Points` instead of
a value-shape probe, so **the stored `AccountSnapshot.LiveArenaPoints` can jump between an older and a
newer snapshot of the same account.** That is a correction, not an anomaly — don't flag it, and prefer
`liveArena.points` wherever both exist. `account.liveArenaLeague` and `account.arena3x3League` are
still the unreliable probe. `account.arenaPoints` / `arenaLeague` were already the named read and
agree with `classicArena`.

## The change, end to end

Store each block as a **typed sub-document on `AccountSnapshot`**, the way `AreaBonusGrid` and
`BaseStatsCatalog` are stored. Don't give them their own collection the way `souls` has one. They
are one object per snapshot, not a list of records, and they are read together with the snapshot.
Mirror the wire shape exactly, so there is one shape to keep in sync.

1. **`RaidTools.Api/Sync/Adapters/ConsolidatedJsonSyncAdapter.cs`**
   - `ParserConsolidatedData`: add six nullable properties (`ClassicArena`, `LiveArena`, `DoomTower`,
     `CursedCity`, `GrimForest`, `Siege`) with parser classes matching the JSON above. The options
     are already `PropertyNameCaseInsensitive`, so camelCase binds.
   - Map them onto `RaidAccountDto`. **Null passes through as null** — no `?? new()`, which is exactly
     the "absent looks like empty" bug rule 1 forbids.
   - **Doom Tower field rename:** v1.23.0 payloads carry `rotationStartedAt`; v1.24.0 carries
     `firstEnteredAt`. It's the same value under two names. Declare both on the parser class and map
     `FirstEnteredAt ?? RotationStartedAt`. Uploader installs update opt-in, so v1.23.0 payloads keep
     arriving — the same reason `champions ?? heroes` still exists.
   - Bump `AdapterVersion` to **4.4.0** with a comment in the existing changelog style: MINOR,
     additive — a payload without the blocks stores null for each, which reads as "not told".

2. **`RaidTools.Api/Dtos/RaidDtos.cs`** — the six DTOs and the six nullable properties on
   `RaidAccountDto`. The DTOs can double as the API response shape.

3. **`RaidTools.Api/Models/AccountSnapshot.cs`** — six nullable properties, with the null-means-not-told
   doc comment `AreaBonusGrid` already carries. `[BsonIgnoreExtraElements]` on the new classes, so a
   field a later schema adds doesn't break reads of stored documents.

4. **`RaidTools.Api/Services/SyncManager.cs` — both write paths, or the second one will bite.**
   - The snapshot-creation block (the `new AccountSnapshot { … AreaBonusGrid = …, BaseStatsCatalog = … }`
     initializer): set all six from the DTO.
   - The partial-feed update block (where `StatBreakdownSources` / `AreaBonusGrid` / `BaseStatsCatalog`
     are only `Set` when the feed carried them): same treatment. **Only `Set` a block when it is
     non-null.** A champions-only feed carries none of these, and blanking them would erase an
     account's progress until its next full export. The comments already in that block explain this
     rule for the neighbouring fields; follow them.

5. **`RaidTools.Api/Controllers/RaidApiControllers.cs`** — one read endpoint, modelled on
   `GetAreaBonuses` (`ResolveSnapshotAsync`, 404 when the caller cannot see the account):
   `GET …/mode-progress?accountId=&syncMethod=` returning an object with the six blocks, each null
   when unknown. Pick the feature gate the eventual page will live under.

6. **Frontend** — out of scope for the storage change; do it as a second step once real snapshots
   carry data. See "Presenting it" for what the page must not get wrong.

## Presenting it — what the payload can and cannot answer yet

**Claimed is answerable now; "what's left" is not.** Every `taken*` list names rewards by the key of
the game's static reward table — a points threshold (Live Arena and Cursed City milestones) or a stage
count (Cursed City's 25 / 50 / 101 and 6 / 12). The reward *contents*, and the full list of thresholds
a "claimable / locked" view needs, are static game data. They are **not on the payload** and are
headed for the RslCompanionMetadata catalogs (`StaticLiveArenaData`, `StaticCursedCityData`,
`StaticFoggyForestData`, `StaticSiegeData`). Until they ship, show what was claimed and the
counters. Don't hardcode threshold lists into RaidTools to fake the rest.

**End dates are derivable, and all but one have been checked in-game** (2026-09-24). They come from
static settings, not the payload. If the page needs them before the metadata catalog ships, keep the
anchors in **one** helper with a comment saying the catalog replaces it:

| Mode | Rule | Status |
|---|---|---|
| Cursed City | rotation *n* ends at 2023-12-12 14:15 **UTC** + 30·*n* days | confirmed to the hour |
| Grim Forest | rotation *n* ends at 2025-12-10 + 30·*n* days | day confirmed; hour (11:00 vs 14:15) not |
| Live Arena | 42-day cycles from 2025-03-11 14:00: 28-day season **then** 14-day preseason; season 14 ends 2026-10-06 | confirmed |
| Doom Tower | global 30-day rotations, current one ends ≈ 2026-10-07 | **anchor not mapped** — don't compute from the payload (see traps) |
| Siege | `SiegeSettings.Schedule` | not mapped — show nothing |

**Classic Arena tiers are shipped: render the name and badge, never "League 25".** The static
half is `RslCompanionMetadata/exports/arena_league_index.json` (added 2026-09-25), keyed by
`classicArena.leagueId`. RaidTools serves it as metadata type `ArenaLeagueIndex` on
`GET /api/arena-league-index`; the full prompt is [raidtools-arena-leagues.md](./raidtools-arena-leagues.md). Each row carries the in-game `name` ("Gold V"), `minPoints` / `maxPoints`,
`nextLeagueId`, the HP/ATK/DEF `bonuses` and a `badgeUrl`
(`https://assets.rslcompanion.com/arena-leagues/<id>.png`). The ids are not contiguous — Bronze
I–IV 1–4, Silver I–IV 11–14, Gold I–V 21–25, Platinum 30, and 0 is unranked (Qualification) — so
look them up, don't compute them. "To next tier" is `next.minPoints - points` floored at 0 (blank
when `nextLeagueId` is null); a 0 means the promotion lands at the weekly reset, per the trap below.

## Traps

- **`doomTower.difficulties[].firstEnteredAt` is not the rotation start.** Rotations are global — one
  in-game reset for both difficulties — while this read Hard 09-07 and Normal 09-20 on one account.
  It is most likely when the account first entered that difficulty. **Never add 30 days to it.**
- **`stageIndicator` is not the player's floor.** It is the game's own name, shaped `70 D 1 FFF`, and
  went from Normal floor 39 to floor 10 during two hours of play — probably where the map is focused.
  Store it; don't render it as progress.
- **`liveArena.season` is the season last *played*.** A player who hasn't fought since season 15
  opened still reports 14 and its points. Compare `number` with the schedule before calling it current.
- **`classicArena.leagueId` is not derivable from `points`.** Classic Arena promotes and demotes
  weekly; mid-week, points can be past the next threshold while the applied tier is still last week's.
- **`difficultyId` is 1 = Normal, 2 = Hard**, and ids are 1-based. There is no difficulty 0.
- **Timestamps are UTC** (confirmed via Cursed City's reset). They are ISO-8601 with `Z`, and absent
  when unset.
- **Two Siege fields are unnamed or unconfirmed:** `takenResourceRewards` holds the game's
  `SiegeResourceRewardTypeId` values (3, 5, 6 observed) with no names mapped yet, and
  `availableRewardGiven` is the game's field name verbatim with its meaning unconfirmed. Store both,
  but don't label them in the UI yet.
- **Siege is the account's own state, by design.** The producer deliberately does not read clanmates'
  defence slots or the opposing clan — nothing on the payload describes another player. Don't
  "complete" it by joining other members' snapshots into a clan-wide Siege view without that
  conversation first; it would reintroduce the roster-without-consent problem from the other side.

## Verification

- **A v1.24.0 payload** (export from the uploader, or `consolidated.json` refreshed from one): all six
  blocks are on the stored snapshot, byte-comparable to the payload, and the new endpoint returns them.
- **A v1.23.0 payload** (schema 25): `doomTower` stores `firstEnteredAt` from `rotationStartedAt`, and
  `siege` and `liveArena.victoriesForRegularReward` are null / absent. Nothing errors.
- **A pre-schema-25 payload:** all six null; the import is otherwise unchanged.
- **A champions-only (partial) import after a full one:** all six blocks keep the values the full
  import stored. This is the test that catches a missed step 4.
- **A payload with one block missing** (delete `cursedCity` by hand): that block stores null, and the
  other five store normally. The blocks fail independently on the producer, so the consumer must not
  couple them either.
