# Uploaded account data × metadata catalogs — how the two are joined

Consumer-side prompt for **RaidTools**, written to be handed straight to an agent: paste it into a
Claude Code session opened on that repo.

It is a *summary*. [`export-schema.md`](export-schema.md) / [`.json`](export-schema.json) are the
contract for the payload side; `RaidTools.Api/Models/MetadataRevision.cs` (`MetadataType`) is the
list of catalogs on the other side. Where this file disagrees with either, they win, and this one is
stale and should be fixed.

Unlike the other `raidtools-*.md` prompts, this one is not about a new field. It covers the rule every
one of them depends on, plus the places where RaidTools currently breaks it.

---

## The split

The uploader sends **account state**: which things the account has, and how far each has been
taken. The payload is ids and numbers: `typeId`, `setKindId`, `statKindId`, `blessingId`,
`masteries[]`, `leagueId`, `claimedRewardIds`. It carries **no** static game data. A catalog says
what an id *is* (name, description, icon, reward contents, per-level tables); the payload says only
that the account holds it.

The **metadata catalogs** say what those ids mean. They are cut in `RslCompanionMetadata` from the
game client, uploaded through `POST /api/admin/metadata`, and stored as `MetadataRevision`s, one
active revision per `MetadataType`. They are the same for every account and change only when
Plarium changes the game.

Joining the two is **RaidTools' job, at read time, by id**. Three consequences:

1. **Never copy a catalog value into a stored account record.** A name written into a snapshot is
   frozen at import. When the next catalog revision renames it, fixes a label, or fills in a name
   that shipped untranslated, stored records keep the old text. Store the id and join on render (or
   in the read endpoint).
2. **Never keep a second copy of a table a catalog already serves.** A hand-maintained frontend map
   next to a served catalog is two sources that drift. Section "What to fix" lists the ones that
   exist today.
3. **Never join on names.** `resources[].name` and `champions[].name` are display labels, and they
   change. The artifact set space and the accessory-effect space share four names (`Cleansing`,
   `Bloodshield`, `Reaction`, `Revenge`).

## The join map

Each payload id, the catalog that names it, and how RaidTools consumes it today. "Served" means a
public read endpoint exists. "No reader" means the type can be uploaded, but nothing reads it back.

| Payload field | Catalog (`MetadataType`) | Read route | Today |
|---|---|---|---|
| `champions[].typeId` / `baseTypeId` | `PlayableChampions` (`champion_index.json`) | imported into `playable_champions`, joined server-side | ✅ |
| `champions[].skills[].typeId` | `SkillCatalog` | joined server-side (`SkillCatalogService`) | ✅ |
| `champions[].skills[].formIndex` (schema 20) | `PlayableChampions` `forms[]`, for pre-20 payloads only | — | see [skill-form-index](raidtools-skill-form-index.md) |
| `champions[].baseStats` | `HeroBaseStats`, for **unowned** champions only | `/api/hero-base-stats/resolve`, `/meta` | ✅ |
| `champions[].blessingId` | `BlessingIndex` | `/api/blessing-index` | ✅ |
| `champions[].masteries[]` | `MasteryIndex` | **none** | ❌ no reader; frontend uses hand-kept `MASTERY_MAP` |
| `champions[].roleId` | `RoleIndex` | **none** | ❌ no reader; uploader also ships `docs/role-names.json` |
| `champions[].elementId`, faction ids, aura ids | `AuraEnums` | `/api/aura-enums` | ⚠️ served, but its `name` is the internal constant; faction ids and labels come from `faction.service.ts`'s own map |
| `artifacts[]` / `accessories[]` `setKindId` | `ArtifactSetIndex` | `/api/artifact-set-index` | ⚠️ served, but names still come from `ARTIFACT_SET_NAMES` |
| `artifacts[]` `kindId`, `rankId`, `rarityId`, `statKindId` | none (uploader's `docs/artifact-enums.json`) | — | frontend `artifact-enums.ts`, transcribed. Acceptable while no catalog exists |
| `relics[].typeId` | `RelicIndex` | `/api/relic-index` | ⚠️ served, but the picker uses hand-kept `RELIC_NAMES` |
| `gemstones[].typeId` | `GemstoneIndex` | `/api/gemstone-index` | ⚠️ served, but `GEMSTONE_NAMES` is hand-kept |
| `areaBonuses[].locationId` | none | — | `area-locations.ts`, measured in-game. Correct until a catalog exists |
| `resources[].id` | `ResourceTypes` | **none** | ❌ no reader; the payload's own `name` is the label |
| `classicArena.leagueId` | `ArenaLeagueIndex` | `/api/arena-league-index` | ✅ see [arena-leagues](raidtools-arena-leagues.md) |
| mode `taken*` / `claimed*` lists | `ModeRewards` + `ModeSchedule` (upload together) | `/api/mode-rewards`, `/api/mode-schedule` | ✅ see [mode-progress](raidtools-mode-progress.md) |
| `cursedCity` stage ids | `CursedCityRotationIndex` | `/api/cursed-city-rotation-index` | ✅ |
| `grimForest.difficulties[].completedSlotIds` | the published map (see its prompt) | — | see [grim-forest](raidtools-grim-forest-map-progress.md) |
| `soloEvents[]` / `tournaments[]` `rewards[]` | **none needed**: the reward table travels in the payload | — | see [events](raidtools-events.md); item/champion *names* inside a prize still join as above |
| `battlePass` levels | a pass catalog, not built yet | — | levels render as numbers until it lands |

Check the "Today" column against the code before acting on it. It was read on 2026-09-30.

## Which side wins

When the payload and a catalog both answer the same question, **the payload wins for anything the
account owns.** It was read off the account's own copy, while the catalog describes the champion in
general.

- **`baseStats`** depends on the copy's ascension, rank and level. A `baseTypeId` lookup reads
  unascended numbers for every ascended copy. Use the catalog only for champions the player does not
  own.
- **`formIndex`** (schema 20+) is the copy's own kit. Use `formIndex ?? <catalog derivation>`,
  because pre-20 uploaders keep sending skills without it (installs update opt-in).
- **`roleId`** is `null` on roughly 19% of copies (the client never hydrated that champion's type).
  Fall back to the champion catalog's role; do **not** coalesce to `0`, which is Attack.
- **`champions ?? heroes`**: pre-1.14 uploaders send only `heroes`.

The catalog wins only for what the payload deliberately does not carry: names, descriptions, icons,
reward contents, per-level tables.

## Absent, null, 0, unknown

These rules come from the payload contract, and a catalog join must not erase the difference between
them:

- **Absent** means the uploader could not read the value. Render "unknown", store null, and never let
  a later partial feed blank a stored value.
- **`null`** is field-specific. For `equippedByHeroId`, `null` means the piece is in the vault, not
  "unknown wearer". Read each field's own section in `export-schema.md`.
- **`0`** is a real value: role 0 is Attack, set 0 is "No set", base Accuracy is genuinely 0.
- **An id the catalog doesn't know** is the ordinary result of version skew (next section). Render it
  as `#<id>` or "Unknown (<id>)", and keep the row. Never drop it, and never map it to 0 or to the
  nearest name. An unknown set is not "no set".

## Version skew is the normal case

The game updates first. Then the uploader follows, and a regenerated catalog follows after that,
each on its own schedule. So at any moment the payload can be **newer** than the active catalog
revision (a champion released yesterday) or **older** (a player on an old uploader whose ids have
since been renamed).

- Every catalog has `generatedAt`, and most also carry the `gameVersion` they were cut from. The
  payload has `gameVersion` (what the export ran against) and, for base stats,
  `baseStatsCatalog.gameVersion` (what the numbers were computed from).
- A catalog whose `gameVersion` trails the payload's makes the join **suspect, not wrong.** Surface
  it (a quiet "catalog older than this export" hint for admins), and keep rendering. Discarding a
  join because the catalog is one build behind trades a handful of wrong labels for no labels at all.
- **Paired catalogs must come from the same producer run**: `ModeSchedule` + `ModeRewards`,
  `RelicIndex` + `GemstoneIndex`, `PlayableChampions` + `AuraEnums`. They share a `generatedAt`, and
  a pair cut from two sessions can mislabel ids while looking fine. If the admin upload does not
  already warn when a pair's `generatedAt` differs, make it warn.

## Two environments, two sets of catalogs

Dev (`api-dev.rslcompanion.com`) and prod have **separate databases**, so each has its own
`metadata_revisions`. A catalog uploaded to one is not on the other.

- Upload each catalog to **both** environments, in the same pairs. A dev site whose catalogs lag prod
  shows version skew that doesn't exist in prod, and the reverse.
- It is fine for dev to run a **newer** catalog than prod, since that is what dev is for. The join
  rules above already handle an unknown id, so nothing breaks if it is.
- The uploader (1.32+) sends the account to whichever API launched it, so a dev account is joined
  against dev's catalogs. There is no cross-environment lookup, and there should not be one.

## What to fix

In order. Each item is independent and small enough for one PR.

1. **Give `MasteryIndex` and `RoleIndex` a reader, or retire the types.** Both can be uploaded and
   nothing reads them back. The `BlessingIndex` note in `MetadataType` already names the problem:
   "a type that can be uploaded and is consumed by nothing is a file that silently goes stale." Add a
   read service and a public GET (the shape of `BlessingIndexController`). Then move
   `champions-roster-page.component.ts`'s `MASTERY_MAP` onto it. `MASTERY_MAP` carries an emoji the
   catalog does not; keep that as a small local presentation map keyed by id, not a copy of the
   names.
2. **Make `AuraEnums` the source of which faction ids exist.** `faction.service.ts` keeps its own
   id→name map, and its header says to keep it in step with `FactionGuardianGrid.FactionIds` by
   hand. **`AuraEnums` names are the game's internal constants** (`KnightsRevenant`,
   `CriticalChance`), not display text, so they cannot replace the labels. Instead, take the *set of
   ids* from `AuraEnums.factions` and keep the display label as a presentation map keyed by id. A
   faction the catalog has and the map lacks then renders as `Unknown (#id)` instead of vanishing
   from every filter. If display names are wanted from the catalog, the fix belongs to the producer:
   add the localized name to `aura_enums.json` in `RslCompanionMetadata`. Keep `getFactionIcon` as
   the single icon source.
3. **Move `RELIC_NAMES` and `GEMSTONE_NAMES` onto the served indexes.** Both headers say they are
   hand-kept subsets of what `RelicIndex` / `GemstoneIndex` serve, and `GEMSTONE_NAMES` already
   shipped one off-by-one capture.
4. **Move `ARTIFACT_SET_NAMES` onto `ArtifactSetIndex`** (its entries have `name`). Keep the
   deliberate gaps as gaps: ids the index has no name for render "Unknown set (<id>)", not "No set".
   `ARTIFACT_SLOT_NAMES`, rarity and stat names have no catalog yet, so they stay transcribed from
   the uploader's `docs/artifact-enums.json`. Note the gap in that file's header.
5. **One unknown-id helper.** Every join in the frontend should fall back through the same function
   (`label(id, map) ?? 'Unknown (#' + id + ')'`) instead of each component choosing between
   `undefined`, `''`, `'?'` and `0`.
6. **`ResourceTypes` and `ArtifactSetTypes`**: both are declared and neither has a reader, and
   `ArtifactSetIndex` supersedes `ArtifactSetTypes`. Decide whether to delete them, and record the
   decision in `MetadataType` so they are not rediscovered as "missing features".

Do **not**:

- Move catalog data into `ConsolidatedJsonSyncAdapter`'s stored records (rule 1).
- Ask the uploader to start sending names. The payload is ids by design: a name on the wire is frozen
  per upload, and the catalogs exist so one upload of a corrected table fixes every account at once.
- Fetch the whole `StageEncounterIndex`. It is served one stage at a time on purpose (8 MB).

## Verified against

RaidTools `main` at `b3d78ee` (2026-09-30): `MetadataType.All` (19 types), public routes under
`RaidTools.Api/Controllers`, and the frontend's id→name tables under `raidtools-frontend/src/app`.
Uploader side: `export-schema.md` schema 33, uploader v1.33.0.
