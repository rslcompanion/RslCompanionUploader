# Next session — RSL Companion

Paste this whole file as the opening prompt. **Read `CLAUDE.md` in each repo first** — they carry the
reasoning behind most of what follows, and this file is only the "what is open right now" layer on
top of them.

Rewritten 2026-08-25, right after **v1.16.0** shipped; **partially corrected 2026-09-03 when
v1.17.0 was cut** — the repo table and the "nobody reads schema 18" section below were rewritten,
the rest of the file has not been re-checked since 2026-08-25. **Check `git log` before trusting any
state below**: more than one session works in `D:\Codex\RslCompanionUploader`, including its
`extraction/` checkout, so a clean tree here is not evidence that nothing has moved.

---

## Where the repos stand (2026-09-03)

| Repo | State |
| --- | --- |
| `D:\Codex\RslCompanionUploader` (public) | released **v1.17.0** (2026-09-03) carrying schema 19 and all nine `statBreakdown` columns |
| `…\RslCompanionUploader\extraction` (private submodule) | `main` = `86bb833`, pointer matches |
| `D:\Codex\RslCompanionMetadata` (private) | `df4629d`; **working tree dirty** — MetadataStudio, StatBreakdownProbe, ClashProbe, `docs/clash-findings.md` |
| `D:\Codex\RaidTools` | the API + Angular frontend; its own TODO.md |

**Payload contract: schema 19.** v1.17.0 is the first release that sends it. Consumers still read
`payload.champions ?? payload.heroes`, and that fallback outlives the producer's half by a long way —
pre-1.14 installs keep sending `heroes` alone, and installs update opt-in.

## Schema 18/19 is consumed now — but only by installs that have updated

**RaidTools reads the statBreakdown, its source list and the Great Hall's area grid** as of
2026-09-03: the champion popup draws the game's Total Stats table with all nine producer columns plus
a tenth, Area Bonuses, computed per a location picked from the game's own dropdown (`RaidTools/docs/
stat-breakdown.md`). What is left is a **producer-side rollout problem, not a consumer gap**: every
account still on v1.16.0 uploads `statBreakdownSources: [basic, artifacts, greatHall, arena]`, and
the popup correctly draws four columns and names the five it is missing. The fix for a given account
is that account updating the app and re-syncing.

The fields, for reference:

- `affinityBonuses[]` / `areaBonuses[]` — the Great Hall's two tabs as account data. The **whole
  declared grid** rides on the wire (4×6 and 13×8), unbought tracks at `level: 0`, so a consumer can
  draw the screen without hardcoding axes.
- `champions[].statBreakdown` — the game's own Total Stats table per copy, with the client's
  blank-vs-zero distinction preserved.
- `statBreakdownSources` — which columns *that export* actually computed. A source missing from this
  list is "not modelled yet", which is a third state distinct from "contributes nothing".
- `champions[].elementId` — the copy's affinity, and the join key onto `affinityBonuses[]`.

Contract: [docs/export-schema.md](docs/export-schema.md) / [.json](docs/export-schema.json).
Derivation of the village tables: `extraction/docs/observatory-findings.md`.

**Two traps a consumer will hit, both stated in the schema and both easy to skip past:**

1. **`isAbsolute` is not constant inside either bonus table.** HP/ATK/DEF/C.DMG/IGN.DEF are fractions
   of the Basic Stat; **RES/ACC/SPD are flat amounts**. Reading the table as percentages computes
   `baseResistance × 80` where the game adds 80. That was a real bug on the producer side, and it
   verified clean against the client because the test account held level 0 in exactly those stats.
2. **`areaBonuses[]` will never appear in `statBreakdownSources`.** The game applies it for one
   location the player picks from a dropdown, so there is no per-champion number. Account level is the
   only place it is well defined.

## Open in this repo

1. **Bundle the WebView2 runtime bootstrapper in the installer** ([TODO.md](TODO.md),
   [installer/setup.iss](installer/setup.iss)). The whole UI is WebView2; Windows 11 ships it in-box,
   a fresh Windows 10 machine may not, and there the app shows only the fallback label while sign-in
   still works — a confusing half-broken state rather than an obvious one. Still the one open item
   with real user impact.
2. **Fold the native File/Help menu into the web top bar** — deliberately **deferred** until the
   web-UI direction has been lived with. See TODO.md before starting it on a whim.

## Open elsewhere

- **Boss catalog, two unemitted fields.** `boss_index.json` ships (321 entries), but `HeroForm`'s
  `AdditionalSkillTypeIds@+40` and `ChallengeSkillTypeIds@+48` are still not emitted. Additive change
  to a file that exists. Contract: `RslCompanionMetadata/docs/champion-index-contract.md`.
- **Data hygiene.** Accounts synced on build 11.70.0 *before 2026-08-09* hold `factionId: 0` and
  `roleId: null` for every hero, and `Template_<id>` names for the nine oldest base ids. Neither field
  distinguishes "could not read" from "has none" — **re-sync those accounts**; nothing to migrate
  server-side.
- **RaidTools' own TODO** (`D:\Codex\RaidTools\TODO.md`): rotate the Data Protection keys (still in git
  history), reset both legal documents to Version 1 before launch, robots.txt/sitemap.xml, GDPR
  mechanics (processor DPAs, Art. 27 representative, RoPA, breach procedure), and two decisions
  blocked on the owner — governing law, and an identifiable controller address. Also: rename
  `D:\Codex\RaidTools` to `D:\Codex\RslCompanion`.

## Release mechanics, so they don't get rediscovered

- Push a `v*` tag; `.github/workflows/release.yml` builds with the submodule, compiles the Inno
  installer and publishes the GitHub Release. ~2 minutes. CI needs `EXTRACTION_REPO_TOKEN` to fetch
  the private engine, and **the submodule pointer must already be pushed** — the workflow fails fast
  with a readable message when it is not.
- **`get.rslcompanion.com` needs no per-release action.** Cloudflare 301s it to
  `github.com/…/releases/latest/download/RslCompanionAccountDataExtractor-Setup.exe`, and GitHub
  resolves "latest" itself. What that *does* require is that every release keeps attaching the
  **unversioned** `-Setup.exe` asset — the URL depends on that filename.
- The update banner picks the **version-stamped** installer and never the `.msix` (self-signed, and it
  cannot install on a machine that has not already trusted the certificate).

## Working notes — the expensive lessons

- **Never re-add the clan roster export.** It is gone for **consent**, not cost. Collecting it is now
  provably free, and that must not be read as a reason to bring it back.
- **One account's holdings are not the game's structure.** The area-bonus doc claimed "which stats a
  location grants varies by location" — read off one player's *purchases*, where one location had two
  tracks levelled and another eight. All three Observatory tiers declare the same eight. This is the
  same shape of error that made the artifact set table wrong from id 4 onward, and it is why both
  village tables now ship the whole declared grid rather than only the bought cells.
- **A negative result from a scan is worth exactly as much as the scan's stride.** Four occurrences
  here (page-stride vault scan, region-capped klass scan, shard/soul quantity signatures, the
  `typeId < 100` floor).
- **A field that GATES other fields must be resolved by name, never left to calibration** —
  `Hero._type` being unresolved silently cost faction, role and typeId for an entire roster.
- **Verify a computed column on data that can actually disagree.** The flat-vs-percentage bug above
  passed a cell-for-cell check against the client because every cell that could have exposed it was
  blank on the test account.
- **`bin\Debug\` can hold more than one framework folder.** The project targets
  `net10.0-windows10.0.19041.0`; a stale `net10.0-windows\` sat beside it for two weeks, and running
  it produced a fifteen-day-old app that looked current. Check `LastWriteTime` before believing a run.
