# Next session — RSL Companion

Paste this whole file as the opening prompt. **Read `CLAUDE.md` in each repo first** — they carry the
reasoning behind most of what follows, and this file is only the "what is open right now" layer on
top of them.

Rewritten 2026-08-24. The version of this file that opened on "released **v1.8.0**" was fifteen
releases stale and walked through work that had already shipped; if something here looks equally
stale, check it against `git log` before believing it.

---

## Where the repos stand (2026-08-24)

| Repo | State |
| --- | --- |
| `D:\Codex\RslCompanionUploader` (public) | released **v1.15.0** (2026-08-21, tag `v1.15.0` = `67b2469`); `main` carries one unreleased commit, `00348a5` — update-check reporting + calibration deferral |
| `…\RslCompanionUploader\extraction` (private submodule) | `fdd1014`, clean — champions[]-only roster |
| `D:\Codex\RslCompanionMetadata` (private) | `3a64ed6`, **working tree dirty — see "In flight" below** |
| `D:\Codex\RaidTools` | the API + Angular frontend; its own TODO.md |

Payload contract: **schema 17** (`champions[]` only; `heroes[]` was dropped after one release of
emitting both). Consumers still read `payload.champions ?? payload.heroes`, and that fallback outlives
the producer's half by a long way — pre-1.14 installs keep sending `heroes` alone.

## In flight — do not start work that touches this

`RslCompanionMetadata` has **uncommitted** work from a parallel session: `docs/stat-breakdown-findings.md`,
`tools/StatBreakdownProbe/`, and modifications inside its own `extraction/` submodule checkout
(`Core/Il2Cpp/OffsetDatabase.cs`, `ExtractionService.cs`, `Extractors/HeroExtractor.cs`,
`Models/GameModels.cs`) plus `tools/ClashProbe/` and `docs/clash-findings.md`.

It decodes the game's **`StatBonusContext`** — the twelve sources behind the Total Stats overlay
(basic, artifacts, **greatHall**, arena, masteries, factionGuardians, empowerment, clan, blessing,
relics, area, total) — and is heading for a per-champion `statBreakdown` on the wire. Two things
matter for anyone picking work up here:

- **The extraction engine is one submodule shared by both repos.** Editing it from the uploader side
  while that session has it dirty is how two sessions produce one unmergeable file.
- `_hasValue` is the game's own **blank-vs-real-zero** flag. Whatever ships must keep absence absent;
  writing `0` for a source that does not apply throws away information the client itself keeps.

**Open question for the owner:** an account-level `greatHall` object (the Affinity Bonuses table
itself — per affinity, per stat, the levels the account has bought) is *not* the same thing as
`champions[].statBreakdown.greatHall` (that table's contribution to one champion). The second is in
flight above; the first is unclaimed. They are complementary, and neither is derivable from the other.

## Open in this repo

1. **Bundle the WebView2 runtime bootstrapper in the installer** ([TODO.md](TODO.md),
   [installer/setup.iss](installer/setup.iss)). The whole UI is WebView2, Windows 11 ships it in-box,
   a fresh Windows 10 machine may not — and there the app shows only the fallback label while sign-in
   still works, which is a confusing half-broken state rather than an obvious one. This is the one
   open item with real user impact.
2. **Fold the native File/Help menu into the web top bar** — deliberately **deferred** until the
   web-UI direction has been lived with. Do not start it on a whim; see TODO.md for what it involves.
3. **Cut a release when something user-visible accumulates.** `00348a5` alone is a UX fix that, by
   definition, cannot help anyone until they are already running it.

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

## Working notes — the expensive lessons

- **Never re-add the clan roster export.** It is gone for **consent**, not cost. Collecting it is now
  provably free, and that must not be read as a reason to bring it back.
- **A negative result from a scan is worth exactly as much as the scan's stride.** This codebase has
  paid for that four times (page-stride vault scan, region-capped klass scan, shard/soul quantity
  signatures, the `typeId < 100` floor).
- **A field that GATES other fields must be resolved by name, never left to calibration** —
  `Hero._type` being unresolved silently cost faction, role and typeId for an entire roster.
- **`bin\Debug\` can hold more than one framework folder.** The project targets
  `net10.0-windows10.0.19041.0`; a stale `net10.0-windows\` sat beside it for two weeks and running it
  produced a fifteen-day-old app that looked current. It has been deleted — if it reappears, the
  binary under test is not the one that was just built. Check `LastWriteTime` before believing a run.
