# Grim Forest shop + quests, Cursed City quests — "x / y claimed" rows with a details popup

Consumer-side prompt for **RaidTools**, written to be handed straight to an agent: paste it into a
Claude Code session opened on that repo.

It is a *summary*. [`export-schema.md`](export-schema.md) / [`.json`](export-schema.json) are the
contract (section **"Mode progress"**, Changelog row 34); where this file disagrees with them, they win,
and this one is stale and should be fixed.

**Decision (owner, 2026-10-03):** quests and rewards show on the mode tiles as **`x / y claimed`**, and
**clicking the row opens a popup with the details** (every quest or item, what it pays, its state).
The quest LIST lives in the metadata catalog; the account upload says only which are done.

---

## Inputs

### Account (export schema 34, uploader v1.36.0) — new on `grimForest.difficulties[]`

```jsonc
"grimForest": { "rotation": 10, "difficulties": [
  { "difficultyId": 2, /* … level, passedStageIds, completedSlotIds as before … */
    "shopPurchases": [ { "itemId": 2, "purchaseCount": 1, "boughtCurioRank": 2 },
                       { "itemId": 4, "purchaseCount": 1, "boughtCurioRank": 1 },
                       { "itemId": 8, "purchaseCount": 1 }, { "itemId": 9, "purchaseCount": 1 },
                       { "itemId": 10, "purchaseCount": 1 } ],
    "completedQuestIds": [10711001, 10711002, /* … */ 10711013],
    "claimedQuestIds":   [10711001, 10711002, /* … */ 10711013] }
] }
```

- `shopPurchases` — one entry per shop item bought at least once this rotation. An item not listed has
  not been bought. `boughtCurioRank` only on curio items.
- `completedQuestIds` — Grim Forest quest ids done this rotation. `claimedQuestIds` — of those, the ones
  whose reward was collected. **Completed but not claimed = claimable in game.**
- All three: `[]` is a real answer (nothing bought / done / claimed); **absent = not told** (older
  uploader or failed read) — store null, never `[]`, and draw "—", never "0 / 13".

### Catalog (`RslCompanionMetadata` `exports/mode_rewards.json`)

- **`grimForest.quests`** (new): `{ rotation, capturedAt, difficulties: { "1": [...], "2": [...] } }`,
  13 quests per difficulty on rotation 10. Each quest:
  ```jsonc
  { "id": 10711001, "name": "Win 25 Grim Forest Battles on Hard", "kind": "battle", "required": 25,
    "mapElementTypes": null,
    "prize": { "resources": [ { "id": 10202, "name": "Extra Grim Gold", "amount": 50 } ], "items": [] } }
  ```
  `kind` is `collect` (map elements of `mapElementTypes`, named by `grimForest.elementTypes`) or
  `battle`. The quests are server-sent, but **the same every rotation** (owner, 2026-10-03), so the
  list applies whatever `quests.rotation` says. Join on the id's place in its family (`id % 10000`),
  not the whole id: the `1071` prefix was rotation 10's and is not confirmed to stay.
- **`grimForest.difficulties[d].shop`** (already there): `[{ id, price: [{id, name, amount}], limit }]`,
  10 items per difficulty. What the items *are* is not in any game table yet. From the in-game shop
  strings: items 2–7 are curios, 8 / 9 / 10 are the small / medium / large chests, and item 1 (limit
  5) is unidentified. Label them "Curio", "Small chest", "Medium chest", "Large chest", and fall back to
  "Item {id}". Keep those labels in one map so the metadata side can replace them later.
- **`cursedCity.difficulties[d]`** (already there): `passedStages` {25, 50, 101}, `awakeningStages`
  {6, 12}, `mainBoss` — the Cursed City quests and their prizes.

## What to do

### Backend (`RaidTools.Api`)

1. `Models/ModeProgress.cs` → `GrimForestDifficulty`: add `List<GrimForestShopPurchase>? ShopPurchases`,
   `List<int>? CompletedQuestIds`, `List<int>? ClaimedQuestIds`, plus a `[BsonIgnoreExtraElements]`
   `GrimForestShopPurchase { int ItemId; int PurchaseCount; int? BoughtCurioRank }`. Doc-comment each
   with "null = not exported, never none", like `CompletedSlotIds`.
2. `ConsolidatedJsonSyncAdapter`: the `grimForest` block binds straight into the model, so the
   properties are the whole change. Bump `AdapterVersion` **4.11.0 → 4.12.0** (MINOR, additive: a payload
   without them stores null) with a changelog comment in the existing style.
3. Nothing changes for Cursed City on the backend: every input is already stored.

### Frontend

4. `core/mode-progress.ts`: add the three fields to `GrimForestProgress.difficulties[]`
   (`shopPurchases?: {itemId: number; purchaseCount: number; boughtCurioRank?: number}[] | null`,
   `completedQuestIds?: number[] | null`, `claimedQuestIds?: number[] | null`).
5. `core/mode-rewards.ts`: type `grimForest.quests` on `GrimForestRewardsDto`.
6. `mode-progress-tiles.component.ts` — **reuse the existing reward-track row** (`RewardRow` with
   `steps`, opened by the roadmap popup through `openTrack`). It already draws a value, a
   `done / claimable / open` state, a "n to claim" count, and a popup listing every step with prize
   chips (`ChipView`, from the same prize decoder the stage rewards use). Do not build a second popup.

   **Grim Forest tile, per difficulty (Hard first, as the bars are):**
   - **`{Name} quests`** — value **`{claimed} / {total} claimed`**, total = the catalog's quest count for
     that difficulty. `claimable` = completed − claimed, and the row is `claimable` when that is > 0,
     `done` when claimed = total, otherwise `open`. Popup: one step per quest, in catalog order.
     `at` = the quest name, `sub` = "{required} × …" when the name does not already say it, chips =
     its prize. The step's state is `collected` when it is in `claimedQuestIds`, `claimable` when it is
     in `completedQuestIds` only, otherwise not reached.
   - **`{Name} shop`** — value **`{bought} / {total} bought`**, counting purchases against limits:
     bought = Σ min(purchaseCount, limit), total = Σ limit (14 on 11.75.0: item 1's limit is 5). `done`
     when equal, otherwise `open` (the shop has no claimable state). Popup: one step per catalog item:
     label, price chips, `{purchaseCount} / {limit}`, and for a curio `rank {boughtCurioRank}`.
   - Catalog quests missing: value `{claimed} claimed`, no total, no popup.
     Field absent on the upload: value "—" with a hint that the uploader needs updating (the existing
     `NEEDS_UPLOADER`-style hint).

   **Cursed City tile, per difficulty:** a **`{Name} quests`** row beside the candle milestones row:
   value **`{claimed} / 6 claimed`** over the six quests, which are pass 25, 50 and 101 stages, pass 6
   and 12 awakening stages, and the main boss. The total is the catalog's count, so read it from there
   rather than hard-coding 6. Claimed comes from the upload: `takenStageRewards`,
   `takenAwakeningStageRewards` and `mainBossRewardTaken`. Popup steps, with prizes from the catalog's
   `passedStages`, `awakeningStages` and `mainBoss`:
   - "Pass N stages": reached when `passedStageIds.length >= N` (exact since schema 29; with no
     `passedStageIds`, fall back to the lower bound the tile already uses).
   - "Defeat the main boss": reached when the main-boss stage id (`1005 D 001`, e.g. `10052001` on Hard)
     is in `passedStageIds`.
   - "Pass N awakening stages": **no progress on the upload** (the game stores only the claim), so it is
     collected when claimed, and otherwise "not known" rather than "not reached". Don't count it
     toward "to claim".

7. Tests in `mode-progress.spec.ts` / the tiles spec, from the real block above:
   - Hard shows `13 / 13 claimed`, state `done`.
   - A quest that is completed but not claimed makes the row `claimable` with "1 to claim".
   - Shop shows `5 / 14 bought` for the block above (items 2, 4, 8, 9, 10). Normal shows `3 / 14`
     for items 8, 9, 10.
   - Absent fields draw "—", not `0 / 13`.
   - A catalog from another rotation still draws the total, including when the ids carry another
     family prefix.
   - Cursed City Hard on rotation 35 (48 stages passed, `takenStageRewards: [25]`) shows `1 / 6 claimed`,
     with "Pass 25 stages" collected and the others open, except awakening, which reads unknown.
     Normal (entered with nothing won) shows `0 / 6 claimed`.

## Verified values (2026-10-03, 11.75.0, Grim Forest rotation 10, Cursed City rotation 35)

- Grim Forest quests: 13 / 13 completed and claimed on both difficulties. Catalog names match the
  in-game list, e.g. "Collect 10 Chests on the Grim Forest Map on Hard" and "Win all Battles in a
  Nightmare Region (Purple) on Normal".
- Grim Forest shop, Hard: items 2 (curio rank 2), 4 (curio rank 1), 8, 9 and 10. Normal: 8, 9 and 10.
  Checked memory against memory, not yet against the shop screen.
