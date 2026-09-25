# RaidTools: Classic Arena tier names, badges and "to next tier"

A prompt for an agent working in the RaidTools repo (frontend). It is a **summary of the contract,
not part of it**. If it ever disagrees with `RaidTools/docs/arena-league-index.md`,
`RslCompanionMetadata/exports/arena_league_index.json` or [export-schema.md](./export-schema.md),
those win and this is what needs fixing.

---

## The task

The dashboard's Classic Arena tile shows **"Tier: League 25"** and **"To next tier: —"**. The game
shows that account as **Gold V**, with a gold badge, 59 points short of Platinum. The data to render
it properly is now served by the API. Wire the tile to it. **The upload payload is unchanged**:
`classicArena.leagueId` is the same raw id as since schema 25.

### What already exists (backend, done)

- **Metadata type `ArenaLeagueIndex`**, uploaded on `/admin/metadata` → **Arena Leagues** from
  `RslCompanionMetadata/exports/arena_league_index.json`. The backend, its tests and the admin tile
  are in place. See `docs/arena-league-index.md` for the model, the table and the refresh steps.
- **`GET /api/arena-league-index`** (authorized) serves the active revision whole, in the producer's
  shape, plus `isLoaded` / `revisionNumber`:

```jsonc
{
  "isLoaded": true, "revisionNumber": 1,
  "schemaVersion": 1, "gameVersion": "11.75.0", "generatedAt": "2026-09-25T…Z",
  "leagues": [
    { "id": 0,  "name": "Qualification", "family": "qualification", "minPoints": 0,    "maxPoints": 899,
      "nextLeagueId": 1,  "bonuses": { "hp": 0, "atk": 0, "def": 0 }, "badgeUrl": "…/arena-leagues/0.png" },
    { "id": 25, "name": "Gold V", "family": "gold", "tier": 5, "minPoints": 3200, "maxPoints": 3499,
      "nextLeagueId": 30, "bonuses": { "hp": 0.22, "atk": 0.22, "def": 0.22 }, "badgeUrl": "…/arena-leagues/25.png" },
    { "id": 30, "name": "Platinum", "family": "platinum", "minPoints": 3500,
      "bonuses": { "hp": 0.25, "atk": 0.25, "def": 0.25 }, "badgeUrl": "…/arena-leagues/30.png" }
    // … 15 rows in all: 0, 1–4, 11–14, 21–25, 30
  ]
}
```

Nulls are omitted, not written: `tier` is absent on Qualification and Platinum, and `maxPoints` and
`nextLeagueId` are absent on Platinum. Type them optional.

- **Badges** are live at `https://assets.rslcompanion.com/arena-leagues/<id>.png`, 256×256
  transparent PNGs, one per id including `0.png`.

### What to build (frontend)

1. **`core/arena-league-index.service.ts`**: copy the shape of `core/gemstone-index.service.ts`,
   which has a DTO interface, a small `ArenaLeagueIndex` class holding a `Map<number, …>`,
   `EMPTY_ARENA_LEAGUE_INDEX`, and a root service with `load()` (one request per session,
   `catchError` → empty, `shareReplay(1)`) and a `catalog` signal. **Do not copy its lookup guard**:
   `gemstone()` rejects `typeId > 0`, but league `0` is valid. Use `id != null`.
2. **`core/mode-progress.ts`**: delete `GAME.classicArena.tierThresholds` and its "not mapped yet"
   comment. The catalog replaces it. Keep `resetPeriodDays`.
3. **`core/components/mode-progress-tiles/mode-progress-tiles.component.ts`**: inject the service,
   call `load()` once (it is shared), and have `classicArena()` read the `catalog()` signal so the
   tile re-renders when the catalog lands:
   - **Tier:** the league's `name`, plus its badge. `ModeTile.icon` is an emoji string today, so add
     an optional `iconUrl` and let the template prefer it; the other tiles keep their emoji. Build the
     URL as `assetUrl(\`/arena-leagues/${id}.png\`)` and don't use the payload's absolute `badgeUrl`,
     so dev and prod hosts both resolve.
   - **To next tier:** `next = catalog.league(current.nextLeagueId)`, value
     `max(0, next.minPoints - points)` with the next league's name, e.g. "59 (Platinum)". With no
     `nextLeagueId`, show "Top tier". When the difference is 0 or less, say so plainly
     ("0 — promotes at the weekly reset"), never a negative number.
   - **Optional:** when `previousLeagueId !== leagueId` and both are known, a "Promoted from Gold IV" /
     "Relegated from …" hint. Compare the two leagues' `minPoints` to tell which way it went.
   - **Catalog not loaded or id unknown:** keep today's `League ${id}` with no badge, and a hint that
     arena leagues aren't published yet (not loaded) or that the id is new to the catalog (unknown).
     Never throw.

## Traps

1. **Ids are not contiguous and are not ranks.** Bronze 1–4, Silver 11–14, Gold 21–25, Platinum 30.
   Look ids up. Never compute a name, the next tier, or an order from the id.
2. **Use `nextLeagueId`, not "the first threshold above my points".** Today's code finds the next tier
   by filtering thresholds on `points`. That is wrong in exactly the case that matters: the game
   promotes and relegates **weekly**, so mid-week an account can hold more points than its league's
   `maxPoints`. The tier is `leagueId`, full stop.
3. **`0` is a real league (Qualification)**, and it is falsy in JavaScript. Any `leagueId ? …`,
   `|| fallback`, or `> 0` guard silently turns a fresh account into missing data.
4. **Bonuses are Classic Arena only.** If you show them, show them as percentages (0.22 → +22%).
   Keep them **out** of roster and priorities stat totals, which say "arena bonuses are not included"
   on purpose.
5. **Classic Arena only.** `liveArena.lastSeenLeagueId` and `account.arena3x3League` use different
   league tables. Don't look them up here.

## Verification

- Backend is already covered (`ArenaLeagueIndexTests`). Frontend type-check plus the cases below.
- The mapping account (leagueId 25, points 3441) renders **Gold V**, the gold "V" badge with five
  stars, and **"59 (Platinum)"**.
- `leagueId: 30` → Platinum, "Top tier".
- `leagueId: 0` → Qualification with its badge, not "League 0" and not blank.
- `leagueId: 24, points: 3300` (past Gold V's floor mid-week) → still **Gold IV**, "0 — promotes at
  the weekly reset".
- Unknown id (e.g. 27) → "League 27", no badge, no error.
- Catalog unavailable (`isLoaded: false` or the request fails) → "League {id}" as today; the rest of
  the dashboard is unaffected.

**Before any of this shows on the live site**, an admin must upload
`RslCompanionMetadata/exports/arena_league_index.json` on `/admin/metadata` → **Arena Leagues** and
activate it. There is one database and it is production, so that upload is a deliberate step, not
part of the code change.
