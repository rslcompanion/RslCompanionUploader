# Store and serve time-limited content — solo events, tournaments, Forge Pass

Consumer-side prompt for **RaidTools**, written to be handed straight to an agent: paste it into a
Claude Code session opened on that repo.

It is a *summary*. [`export-schema.md`](export-schema.md) / [`.json`](export-schema.json) are the
contract for the payload side (section **"Time-limited content"**, Changelog row 33); where this file
disagrees with them, they win, and this one is stale and should be fixed.

---

## What changed on the producer

Uploader **v1.32.0 (schema 33)** adds three top-level keys to the consolidated payload.
`ConsolidatedJsonSyncAdapter` deserializes into a class that has none of them, so **all three are
dropped on import** until this lands.

| Key | What it carries |
|---|---|
| `soloEvents[]` | each solo event the account is in: id, title, dates, `points`, `claimedRewardIds`, and the **reward table with contents** (`rewards[]`); board events add `boardCurrency` and per-cell `cost` / `row` / `column` / `parentIds` |
| `tournaments[]` | each tournament the account is in: id, title, dates, `points`, `bracketIndex`, own `position`, `claimedRewardIds`, and **its bracket's** tier table with contents |
| `battlePass` | the Forge Pass (the game's internal name): `passId`, `status`, `points`, and per reward track the levels collected |

A real v1.32.0 payload, trimmed:

```jsonc
"soloEvents": [
  { "eventId": 4428, "questPrototypeId": 364428, "soloTypeId": 1,
    "title": "Gear Enhancement Event", "titleId": 3516,
    "startsAt": "2026-09-28T09:00:00Z", "endsAt": "2026-10-01T09:00:00Z", "claimUntil": "2026-10-02T09:00:00Z",
    "points": 4971, "claimedRewardIds": [1, 2, 3, 4, 5, 6, 7, 8, 9, 10],
    "rewards": [
      { "id": 1,  "points": 100,  "prize": { "resources": [{ "id": 1, "amount": 50 }] } },
      { "id": 3,  "points": 625,  "prize": { "items": [{ "id": 8050, "amount": 5 }] } },
      { "id": 10, "points": 4775, "prize": { "champions": [{ "typeId": 10820, "count": 1 }] } },
      { "id": 11, "points": 5600, "prize": { … } }, { "id": 12, "points": 6500, "prize": { … } } ] },
  { "eventId": 4420, "questPrototypeId": 364420, "soloTypeId": 2, "title": "Wicked Path Event",
    "points": 4341, "boardCurrency": 3841, "claimedRewardIds": [105],
    "rewards": [
      { "id": 105, "cost": 500,  "row": 1, "column": 5, "parentIds": [],    "prize": { "resources": [{ "id": 1111, "amount": 500 }] } },
      { "id": 203, "cost": 1000, "row": 2, "column": 3, "parentIds": [105], "prize": { "items": [{ "id": 19002, "amount": 1 }] } } ] }
],
"tournaments": [
  { "eventId": 4431, "questPrototypeId": 3364431, "tournamentKindId": 7, "title": "Fire Knight Tournament",
    "startsAt": "2026-09-29T11:00:00Z", "endsAt": "2026-10-01T11:00:00Z", "claimUntil": "2026-10-05T11:00:00Z",
    "points": 2054, "bracketIndex": 1, "claimedRewardIds": [1, 2, 3, 4],
    "rewards": [ { "id": 1, "points": 250, "prize": { … } } /* … 7 tiers … */ ] }
],
"battlePass": { "passId": 1037, "kindId": 2, "status": 1, "points": 245,
  "tracks": [ { "trackId": 1, "claimedLevels": [1, 2, /* … */ 24] },
              { "trackId": 2, "claimedLevels": [] }, { "trackId": 3, "claimedLevels": [] } ] }
```

Three rules, and they are why this needs care rather than a straight copy:

1. **Absent means "this feed could not read it", never "no events".** The producer omits a key when its
   read failed. Store **null**, and never let a later feed that omits a key blank one a previous feed
   stored.
2. **`[]` is a real answer.** `soloEvents: []` on a present key means the account is in no active solo
   event. Unlike absent, it **does** replace what was stored.
3. **These are snapshots of things that expire.** An event is on the payload while its claim window is
   open (`claimUntil`). A snapshot stays in the database far longer than that. Every read must drop
   entries whose `claimUntil` has passed, or a page shows last week's tournament as live.

## Why reward contents are on this payload when Mode progress's are not

Mode progress sends only claimed *keys* and leaves contents to the RslCompanionMetadata catalogs,
because those tables are static game data. **Event and tournament tables are not.** The server sends
them per event, they exist in no static file, and they are gone within days. The payload is the only
place RaidTools will ever get them. So store `rewards[]` as sent. Don't try to join it to a catalog.

**The Forge Pass is the opposite.** Its levels and rewards *are* static data
(`StaticBattlePassData`), so the payload carries only points and collected levels. The level table is
headed for RslCompanionMetadata keyed by `passId`. Until it ships, see "Presenting it".

## The change, end to end

Same pattern as Mode progress: **typed sub-documents on `AccountSnapshot`**, mirroring the wire shape
exactly.

1. **`RaidTools.Api/Sync/Adapters/ConsolidatedJsonSyncAdapter.cs`**
   - `ParserConsolidatedData`: add `SoloEvents` (`List<…>?`), `Tournaments` (`List<…>?`) and
     `BattlePass` (object?), with parser classes matching the JSON above. `rewards[].prize` is one
     class with seven optional lists (`resources`, `items`, `champions`, `souls`, `artifacts`,
     `avatarIds`, `frameIds`) plus `otherKinds`. `amount` is a **double**.
   - Map onto `RaidAccountDto`. **Null passes through as null.** An empty list passes through as an
     empty list. Don't write `?? new()` in either direction.
   - Bump `AdapterVersion` one MINOR, with a comment in the existing changelog style: additive, and a
     payload without the keys stores null.

2. **`RaidTools.Api/Dtos/RaidDtos.cs`**: the DTOs, plus three nullable properties on `RaidAccountDto`.

3. **`RaidTools.Api/Models/AccountSnapshot.cs`**: three nullable properties with the
   null-means-not-told comment. Put `[BsonIgnoreExtraElements]` on every new class, including
   the prize classes. Prize kinds will grow, and a new one must not break reads of stored documents.

4. **`RaidTools.Api/Services/SyncManager.cs`: both write paths.**
   - The snapshot-creation initializer: set all three.
   - The partial-feed update block: **only `Set` a key when it is non-null.** A present `[]` is
     non-null and must be set, because it means the events ended. An absent key must leave the stored
     value alone. This is the one place where the rule differs from "only set when it has content".
     Get it wrong and a finished event lingers forever.

5. **`RaidTools.Api/Controllers/RaidApiControllers.cs`**: `GET …/events?accountId=&syncMethod=`,
   modelled on the mode-progress endpoint. Return `{ soloEvents, tournaments, battlePass }` with
   **expired entries filtered out** (`claimUntil` < now, UTC). Keep entries that have no `claimUntil`.
   Also return the snapshot's timestamp, so the page can say "as of your last sync".

6. **Frontend**: a second step, as with Mode progress. See below for what the page must not get wrong.

## Presenting it

**Solo points events (`soloTypeId` 1) and tournaments work the same way.** A tier is:
- **claimed** when its id is in `claimedRewardIds`;
- **claimable** when `points ≥ tier.points` and it is not claimed;
- **locked** otherwise.

"To next tier" is the smallest locked `points` minus the account's `points`. Show `endsAt` as the
points deadline and `claimUntil` as the claim deadline. They differ by up to four days on tournaments,
and "ended, 3 rewards still to claim" is the most useful thing this page can say.

**Board events (`soloTypeId` 2) are a grid, not a bar.** Lay cells out by `row` / `column`.
- **Taken** = id in `claimedRewardIds`.
- **Available** = every `parentIds` entry is taken. The entry row has `parentIds: []`.
- **Affordable** = `cost ≤ boardCurrency`.

`points` is what was earned; `boardCurrency` is what is left. Don't draw `points` against `cost`.

**Summon pools (`soloTypeId` 5)** carry `rewards: []`. Show the points only.

**Prize contents.**
- `resources[].id` is the same id space as the payload's `resources[]`. Name them from the resource
  index you already have. Some ids (1, 4) are not on that allowlist, so fall back to "Resource #id".
- `items[].id` is the game's inventory item space (potions, chickens, Basalt 19002…). There is no item
  index yet, so render "Item #id" rather than guessing.
- `champions[].typeId` joins the champion catalog on type id.
- `artifacts[]` uses the `artifact-enums.json` ids.
- If `otherKinds` is non-empty, show "+ more". That list exists so a prize is never shown as emptier
  than it is.

**The Forge Pass, before the metadata catalog ships.** Show `points` and, per track, the count of
collected levels. Don't compute a level, a "next level" or an end date without the catalog, and don't
hardcode pass 1037's table. When the catalog ships, the level reached is the highest level whose
**cumulative** threshold is ≤ `points` (1037: L2 20, L3 30 … L50 500). A level is claimable on a track
when it has been reached and is not in that track's `claimedLevels`.

## Traps

- **Track ids: 1 = Free is certain; 2 = Gold and 3 = Platinum are inferred.** Label the premium ones
  provisionally, or not at all.
- **An empty premium track doesn't mean the player hasn't bought it.** It only means nothing was
  collected there. The payload does not say what was bought.
- **`status`: 1 = active, 2 = ended.** When no pass is active the producer sends the newest one with
  status 2. Don't present it as current.
- **`position` is the account's own rank and is usually absent or `0`.** It is never anyone else's.
  Don't build a leaderboard from it, and don't join other accounts' snapshots into one without a
  consent conversation first. The producer deliberately reads no leaderboard, for the same reason the
  clan roster is gone.
- **`rewards` on a tournament is this account's bracket only.** Two accounts in the same tournament can
  have different tables. Never dedupe tournament rewards across accounts by `eventId`.
- **`eventId`, `title`, `titleId`, `startsAt`, `endsAt` and solo `rewards` can be absent** when the
  producer could not reach the event catalog. The account fields are still there. Key on
  `questPrototypeId`, which is always present, and show "Event #questPrototypeId" when the title is
  missing.
- **`title` is in the client's language**, as the server sent it. Store it as display text and never
  join on it.
- **Summon pool `points` is unconfirmed.** The game's `TotalPoints` read 55 while the per-day progress
  summed to 40 on the mapping account, and nobody has yet checked which one the screen shows.
- **Timestamps are ISO-8601 with `Z`, and are assumed UTC** from their `:00` alignment, not proven.
  Mode progress's times were confirmed UTC.
- **`tournamentKindId` has no name table yet.** The values seen are 5 champion training, 7 Fire
  Knight, 10 Classic Arena, 13 Tag Arena, 14 soul chase, 15 Dragon turn attack and 21 gear ascension.
  Show `title` instead.

## Verification

- **A v1.32.0 payload:** all three keys are stored byte-comparable to the payload, and the endpoint
  returns them minus anything whose `claimUntil` has passed.
- **A pre-schema-33 payload:** all three are null, and the import is otherwise unchanged.
- **A partial (champions-only) import after a full one:** all three keep their stored values.
- **A full import carrying `soloEvents: []`** after one that carried events: the stored list becomes
  empty. This is the test that catches the step-4 difference from Mode progress.
- **Expiry:** set a stored tournament's `claimUntil` to yesterday. The endpoint no longer returns it,
  and the stored document is untouched.
- **A board event:** with `claimedRewardIds: [105]`, cells whose only parent is 105 are available, and
  none of the others are.
