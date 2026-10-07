# Store and show each account's Inbox

Consumer-side prompt for **RaidTools**, written to be handed straight to an agent: paste it into a
Claude Code session opened on that repo.

It is a *summary*. [`export-schema.md`](export-schema.md) / [`.json`](export-schema.json) are the
contract for the payload side (section **"`inbox`"**, Changelog row 40); where this file disagrees with
them, they win, and this one is stale and should be fixed. Titles come from
[`inbox-types.json`](inbox-types.json) (`typeId` → localization key) and the shared localization
dictionary, RslCompanionMetadata `exports/localization_en.json` (key → text).

---

## What changed on the producer

Uploader **v1.44.0 (schema 40)** adds one top-level key to the consolidated payload: `inbox`, the
account's in-game Inbox, meaning every reward waiting to be collected. `ConsolidatedJsonSyncAdapter`
deserializes into a class that has no such key, so **it is dropped on import** until this lands.

A real schema-40 payload, trimmed (102 items on the mapping account):

```jsonc
"inbox": {
  "items": [
    { "id": 24302, "typeId": 112, "read": true,
      "receivedAt": "2026-06-29T17:25:54Z", "expiresAt": "2026-10-07T17:25:54Z",
      "prize": { "items": [ { "id": 5, "amount": 1 } ] } },
    { "id": 26006, "typeId": 60, "read": true,
      "receivedAt": "2026-10-05T04:07:38Z", "expiresAt": "2027-01-13T04:07:38Z",
      "prize": { "resources": [ { "id": 2, "amount": 25000 } ] } },
    { "id": 26064, "typeId": 7, "read": true,
      "receivedAt": "2026-10-06T20:50:22Z", "expiresAt": "2027-01-14T20:50:22Z",
      "prize": { "artifacts": [ { "kindId": 9, "rankId": 6, "rarityId": 5, "setKindId": 0 } ] },
      "artifacts": [ { "artifactId": 166281, "kindId": 9, "setKindId": 0, "rankId": 6, "rarityId": 5,
                       "level": 0, "requiredFactionId": 7, "equippedByHeroId": null,
                       "primaryBonus": { "statKindId": 1, "value": 900, "isAbsolute": true, "powerUpValue": 0 },
                       "secondaryBonuses": [ { "statKindId": 4, "value": 5, "isAbsolute": true },
                                             { "statKindId": 2, "value": 0.07, "isAbsolute": false } /* … */ ],
                       "ascendBonus": null /* … the rest of the artifacts[] item shape */ } ] },
    { "id": 26072, "typeId": 132, "read": false,
      "receivedAt": "2026-10-07T11:47:35Z", "expiresAt": "2027-01-15T11:47:35Z",
      "prize": { "items": [ { "id": 3001, "amount": 1 } ] } }
  ] }
```

| Field | Meaning |
|---|---|
| `id` | unique per account, increasing in arrival order; stable while the item waits |
| `typeId` | the game's `InboxTypeId`, i.e. where the reward came from. Raw: see "Titles" |
| `read` | the player has opened it. *Not* "collected": a collected item is not in the list at all |
| `receivedAt` / `expiresAt` | UTC (verified). The game deletes an uncollected item at `expiresAt` |
| `prize` | **the same `eventPrize` shape as event rewards.** `EventPrize` and `toPrize`/`eventPrizeChips` already handle it |
| `artifacts` | only on an item whose prize has artifacts: the **full** records, in the top-level `artifacts[]` item shape, same order as `prize.artifacts` |
| `questPrototypeId`, `parentId`, `chestOrRandom` | read, but not yet seen populated. Store them and don't build on them |

Four rules, and they are why this needs care rather than a straight copy:

1. **Absent means "this feed could not read it", never "empty Inbox".** Store **null**, and never let a
   later feed that omits the key blank one a previous feed stored.
2. **A present `inbox` is the whole Inbox and replaces what was stored.** Collecting an item in game
   removes it from the list, and so does expiry, so `items: []` is a real answer: the Inbox is empty.
   Never merge item lists across syncs. A merge keeps collected rewards forever.
3. **Snapshots go stale.** An item past its `expiresAt` is gone in game even if no sync has said so.
   Every read must drop items whose `expiresAt` has passed.
4. **Overflow artifacts are not in the vault.** The producer checked: 0 of 62 overflowed pieces were
   also in `artifacts[]` / `accessories[]`. Collecting them in game moves them there, and the next
   sync finds them in the vault. So **never add `inbox.items[].artifacts` to the account's artifact
   collection or counts.** It would double-count once collected, and today it would inflate
   "artifacts owned" with pieces the player can't equip.

## The change, end to end

Same pattern as time-limited content: **a typed sub-document on `AccountSnapshot`**, mirroring the wire
shape exactly.

1. **`RaidTools.Api/Sync/Adapters/ConsolidatedJsonSyncAdapter.cs`**
   - `ParserConsolidatedData`: add `Inbox` (object?) with `Items` (`List<ParserInboxItem>`).
     `ParserInboxItem.Prize` reuses the parser class the events path already uses for `prize`, and
     `ParserInboxItem.Artifacts` reuses **`ParserArtifact`**, mapped with the existing `MapArtifacts`.
     Same record, same rules (Q32.32 already resolved, `isAbsolute`, glyph `powerUpValue` added).
   - **Keep inbox artifacts out of the vault path.** The loop that walks
     `data.Artifacts.Concat(data.Accessories)` must not see them.
   - Map onto `RaidAccountDto`. **Null passes through as null**, and `items: []` passes through as an
     empty list. Don't write `?? new()` in either direction.
   - Bump `AdapterVersion` one MINOR (4.15.0 → 4.16.0, or the next free one), with a comment in the
     existing changelog style: additive, and a payload without the key stores null.

2. **`RaidTools.Api/Models/`**: a new `InboxContent.cs` (or a section of `TimeLimitedContent.cs`):
   `AccountInbox { List<InboxItem> Items }` and `InboxItem { Id, TypeId, Read, ReceivedAt, ExpiresAt,
   EventPrize? Prize, List<Artifact>? Artifacts, int? QuestPrototypeId, int? ParentId, bool ChestOrRandom }`.
   Put `[BsonIgnoreExtraElements]` on both. `AccountSnapshot` gets a nullable `Inbox` with the
   null-means-not-told comment. `RaidDtos.cs` gets the DTO and the property on `RaidAccountDto`.

3. **`RaidTools.Api/Services/SyncManager.cs`: both write paths.**
   - The snapshot-creation initializer: set `Inbox`.
   - The partial-feed update block: **`Set` it only when non-null.** A present `{ items: [] }` is
     non-null and must be set. That is the player having collected everything.

4. **`RaidTools.Api/Controllers/RaidApiControllers.cs`**: `GET …/inbox?accountId=&syncMethod=`,
   modelled on `GetEvents`. Return `{ timestamp, items }`:
   - drop items whose `expiresAt` < now (UTC), and keep items with no `expiresAt`;
   - add each item's `title` server-side (see "Titles"), so every client gets the same text;
   - sort by `expiresAt` ascending, because what expires first is what the player must act on;
   - return `null` items (not `[]`) when the snapshot has no inbox (pre-40, or a failed read), so the
     page can tell "unknown" from "empty".
   Gate it the way `GetEvents` is gated (`mode-progress`), or under a new `inbox` feature if the owner
   wants to stage it separately. Ask, don't decide.

5. **A summary for every account at once.** "Show it for each account" means the account list or
   switcher needs per-account numbers without N detail calls. Add to whatever already returns the
   account list (the same place last-sync and counts come from): `inboxCount`, `inboxUnread`, and
   `inboxExpiresNext` (the soonest `expiresAt` among live items), all computed with the same expiry filter, and
   all null when the snapshot has no inbox.

6. **Frontend**, as its own step:
   - **Per-account badge** in the account list/switcher: `inboxCount`, with an emphasis when
     `inboxExpiresNext` is within 3 days ("12 rewards, 2 expire in 1 day"). Null renders nothing, not 0.
   - **An Inbox tile** beside the Events & progress tiles for the selected account: one row per item,
     showing the title (below), `eventPrizeChips(toPrize(item.prize, names))`, "received 3 days ago",
     and "expires in N days" (red under 3 days), plus an unread dot when `read` is false. Filter chips
     can mirror the game's own (All, Artifacts, Champions, Shop items, Other) by prize kind. Show
     `items.length / 400`. The capacity is `maxItems` in `inbox-types.json`, and a full Inbox
     means overflow is lost.
   - **Overflow artifacts get the real artifact card** (the one the artifacts page uses), built from
     `item.artifacts[i]`, not a slim chip. The player is deciding whether to collect or sell this
     piece, and that decision needs its substats.
   - Say "as of your last sync" with the snapshot timestamp. The Inbox changes every time the player
     plays.

## Titles

`typeId` is what the game titles an item by. Two files turn it into text, and the split is
deliberate:

1. **[`inbox-types.json`](inbox-types.json): `typeId` → `sourceKey`.** The game keeps this mapping
   in code (a switch), not in data, so it was read out of the client's compiled code and checked
   against the open Inbox (every id on the mapping account matched). 77 ids have a key; the other
   ids in 4–141 have no case in the client, so it cannot show such an item. Each entry also carries
   `text` (English) for convenience, plus `lifetimeDays`.
2. **The localization dictionary: `sourceKey` → text.** RslCompanionMetadata
   `exports/localization_en.json` is the client's whole text table: 31,296 keys covering Inbox
   sources, skills, champion types, quests, inventory items (`l10n:bmi/…`), and more. **This is the
   shared dictionary**: any later id table should carry keys and resolve them here, not copy text.

So:

- **Ingest the dictionary as a metadata catalog** (`MetadataType` `Localization`, uploaded like
  `ArenaLeagueIndex`; dev and prod each need it uploaded). It is 6.3 MB, so serve it by prefix, not
  whole: `GET /api/localization?prefix=l10n:inbox/` → `{ key: text }`. Cache by the catalog's
  `generatedAt`.
- **Ingest `inbox-types.json` too** (or fold it into a served catalog). A title must come from data,
  so a new type id arrives without a frontend change.
- **Title = `localization[types[typeId].sourceKey]`**, falling back to `types[typeId].text`, then to a
  neutral "Reward". Never zip the enum's member names onto ids by position: its values are explicit,
  and the artifact set table went wrong for years exactly that way.
- **Type 7 (Overflow)** can show a per-prize variant (`overflowVariants`), but the client's test for it
  is not decoded, and all 62 artifact overflows on the mapping account showed the generic "Overflow
  from full storage". Use the generic line.

## Prize contents

Exactly as for events, because it is the same shape:
- `resources[].id` is the payload's `resources[]` id space. Some ids are off that allowlist, so fall
  back to "Resource #id".
- `items[].id` is the game's inventory item space. Use the item names the events page already uses,
  and fall back to "Item #id" rather than guessing.
- `champions[].typeId` joins the champion catalog on type id. `artifacts[]` (slim) uses the
  `artifact-enums.json` ids.
- A non-empty `otherKinds` means "+ more", so a prize is never shown emptier than it is.

## Traps

- **`read` is not "collected".** Don't render read items as done. Every item in the list is still
  waiting.
- **Join on `sourceKey`, never on text.** Text changes with wording and language; the key does not.
- **The same Inbox can hold dozens of near-identical items** (62 single-artifact overflows on the
  mapping account). Group by `typeId` + prize kind in the tile, with a count, before listing them all.
- **`expiresAt` varies by type.** Most types last 100 days, energy gifts one day. Never compute it from
  `receivedAt`; the payload already carries the game's own deadline.
- **No sender.** The game records none on an item, including a friend's gift. Don't try to infer one.
- **Raid Mail is not on the payload.** The game's personal messages (`PersonalMessages`) were empty on
  the mapping account, and they ship when their filled shape has been seen. Don't fake an empty
  "Messages" section.

## Verification

- **A v1.44.0 payload:** `inbox` is stored byte-comparable to the payload. The endpoint returns it minus
  expired items, sorted by `expiresAt`.
- **A pre-schema-40 payload:** `Inbox` is null, the endpoint returns `items: null`, the account list
  shows no badge, and the import is otherwise unchanged.
- **A partial (champions-only) import after a full one:** `Inbox` keeps its stored value.
- **A full import carrying `inbox: { items: [] }`** after one with items: the stored list becomes empty
  and the badge disappears. This is the collected-everything case.
- **Overflow artifacts:** after importing the sample above, the account's artifact and accessory counts
  are unchanged, and the Inbox tile shows item 26064 as a full artifact card (6★ Legendary Banner,
  HP 900 primary, four substats).
- **Titles:** the sample's items read "Free Gift. From Us, To You" (60), "Overflow from full storage" (7)
  and "Grim Forest Reward" (132); an id missing from `inbox-types.json` reads "Reward".
- **Expiry:** set a stored item's `expiresAt` to yesterday. The endpoint and the badge drop it, and the
  stored document is untouched.
