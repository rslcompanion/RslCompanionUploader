# Next session — RSL Companion

Paste this whole file as the opening prompt. **Read `CLAUDE.md` in each repo first** — they carry the
reasoning behind most of what follows, and this file is only the "what is open right now" layer on
top of them.

Rewritten **2026-10-09**, updated after **v1.47.1** the same day. The "Open elsewhere" list is carried over from 2026-09-03
and was not re-checked. **Check `git log` before trusting any state below**: more than one session
works in these repos (the uploader and its `extraction/` checkout, and RaidTools through two
worktrees), so a clean tree is not evidence that nothing has moved.

---

## Where the repos stand (2026-10-09)

| Repo | State |
| --- | --- |
| `D:\Codex\RslCompanionUploader` (public) | released **v1.47.1**. Payload **schema 41**. **Proprietary `LICENSE`** (all rights reserved; no reuse of code or offsets). |
| `…\RslCompanionUploader\extraction` (private submodule) | `8beb3a2`: known builds **11.80.0**, 11.75.0, 11.70.0 |
| `D:\Codex\RslCompanionMetadata` (private) | `920971b` |
| `D:\Codex\RSL-game-assets` | `bd5598e` (push to `master` deploys assets.rslcompanion.com) |
| RaidTools (`D:\Codex\RaidTools`; worktree `RaidTools-cloud-hosting` holds `dev`) | prod at `a30aa3c` (memory-map endpoint, Titan tile), released 2026-10-09. `dev` has moved on since (another session) — check before releasing. |

**Uploader:** everything goes to `main` and ships as a plain `vX.Y.Z` tag; there is no `dev` branch.
**RaidTools is the opposite:** push to `dev` (auto-deploys the dev environment), and only the owner's
**Release to prod** action moves `main`. Never push RaidTools `main` — it was done once on 2026-10-08
and another session had to merge it back.

## What changed in 1.43 – 1.46.1

- **1.43 / schema 39 — Frontier Event** map and progress (`soloEvents[].frontier`). The site's tile is
  built and **archived** (`FRONTIER_TILE_SHOWN = false`); don't touch it unasked.
- **1.44 / schema 40 — the in-game Inbox** (`inbox.items[]`), titles via `docs/inbox-types.json`.
- **1.45 / schema 41 — Titan Event milestones** (`soloTypeId` 4, `rewards[].milestone`) and
  `prize.randomGemstones`. Titan Points are item **10600**, paid as tier prizes by labelled events.
  **Verified on screen** on 11.80.0: points, reachable milestones and claims (`[1, 2]` after claiming two).
- **Prize names on the site** come from `account-resources/index.json`, whose rows now carry the game's
  `kind` + `id` (metadata `tools/publish-account-resources.py`). 4162 = Legendary Radiant Sunlily,
  1122 = Eternal Soul Essence.
- **1.46 — the Help menu is "Actions"**, drawn as a dropdown; every user-facing menu path says
  `Actions → …`.
- **1.46.1 — Raid 11.80.0 is a known build**, and class RVAs an export learns are written back into the
  build's local calibration (`KnownOffsets.RecordLearnedRvas`). Without the AppModel RVA the liveness
  check cannot run and a dead `User` can pass for the live one; that is what emptied the events read on
  11.80.0 the morning it shipped.
- **The memory-map endpoint exists at last** (RaidTools `dev`): `GET /api/extractor/offsets/{hash}`
  had answered 404 since v1.10 because the route was never built, so every player on a new Raid build
  paid the local calibration scan. Admins publish with `PUT /api/admin/extractor-offsets/{hash}`, or
  from the uploader: **Actions → Publish memory map for this Raid version** (admin-only, on `main`).

- **1.47.1 — setup robustness.** A wrong learned class RVA heals: it is replaced once a run proves it
  wrong and its own value right (`Il2CppRuntime.KlassRvaResolvesFor`). Catalog files are written
  atomically (`KnownOffsets.WriteAtomically`). A **second failed setup of one build in a session** says,
  in the banner and the log, that this version can't set the Raid version up by itself and a new version
  has to be downloaded, and runs an update check.

## Open in this repo

1. ~~Publish the 11.80.0 map~~ — **done 2026-10-09**: RaidTools released (`a30aa3c`), uploader v1.47.0,
   and the map published to prod and dev (Cloud Run logs "Published extractor memory map for build
   A66241F0…"). **On every Raid update from now on:** an admin on the new build runs Actions → Publish
   memory map, **signed in to prod** — the action publishes to the session's server, and the first try
   went to dev. Then ship the build in `known-offsets.json` with the next release.
2. ~~The site half of the account-mismatch notice~~ — **done** in RaidTools `d9976b9`, on prod.
   Still worth a live check with two accounts: Update Data on the card Raid is *not* on should sync the
   open account and show the app's one-sentence notice.
3. **Confirm the site's `/handoff/status` check on prod** reports "launched" for an already-open
   1.40+ app (verified only from the app's side).
4. **Code signing.** Releases are unsigned; SmartScreen warns, and Avast locked a fresh installer once.
   The workflow has a stubbed signing step.
5. **The WebView2 bootstrap path is untested** on a machine without the runtime (a Windows 10 VM).
6. **Event numbers still unchecked on screen**: the checklist at the end of
   `extraction/docs/events-findings.md` (summon pool 55 vs 40, tournament claims, Forge Pass level).
7. **The `-dev` pre-release channel has never been used.** The first `v…-dev.N` tag is the test.
8. **Protect the engine from reuse.** The LICENSE forbids it; the engine DLL still decompiles to
   near-source. Offsets themselves cannot be hidden (the app must read them, and IL2CPP dumpers re-derive
   them), so the effort goes into the engine: NativeAOT or an obfuscator — see the feasibility notes from
   2026-10-09 if recorded, and re-check Avast on any obfuscated build.
9. **Rate-limit and log `GET /api/extractor/offsets/{hash}`** (RaidTools, on `dev`), so bulk map
   downloads are slow and visible.

## Open elsewhere (carried over from 2026-09-03, not re-checked)

- **Boss catalog, two unemitted fields.** `boss_index.json` ships, but `HeroForm`'s
  `AdditionalSkillTypeIds@+40` and `ChallengeSkillTypeIds@+48` are still not emitted. Additive change
  to a file that exists. Contract: `RslCompanionMetadata/docs/champion-index-contract.md`.
- **Data hygiene.** Accounts synced on build 11.70.0 *before 2026-08-09* hold `factionId: 0` and
  `roleId: null` for every hero, and `Template_<id>` names for the nine oldest base ids. Neither field
  distinguishes "could not read" from "has none". **Re-sync those accounts**; nothing to migrate
  server-side.
- **RaidTools' own TODO** (`D:\Codex\RaidTools\TODO.md`): rotate the Data Protection keys (still in git
  history), reset both legal documents to Version 1 before launch, robots.txt/sitemap.xml, GDPR
  mechanics (processor DPAs, Art. 27 representative, RoPA, breach procedure), and two decisions
  blocked on the owner — governing law, and an identifiable controller address. Also: rename
  `D:\Codex\RaidTools` to `D:\Codex\RslCompanion`.

## Release mechanics, so they don't get rediscovered

- Push a `v*` tag; `.github/workflows/release.yml` builds with the submodule, fetches and
  signature-checks the WebView2 bootstrapper, compiles the Inno installer and publishes the GitHub
  Release. ~2–3 minutes. CI needs `EXTRACTION_REPO_TOKEN` to fetch the private engine, and **the
  submodule pointer must already be pushed**.
- `vX.Y.Z` is a normal release, marked latest. `vX.Y.Z-label` is a pre-release, never latest. The
  exe, installer and MSIX get the numeric part; the label is in the informational version.
- **`get.rslcompanion.com` needs no per-release action.** Cloudflare 301s it to
  `github.com/…/releases/latest/download/RslCompanionAccountDataExtractor-Setup.exe`, and GitHub
  resolves "latest" itself. That requires every release to keep attaching the **unversioned**
  `-Setup.exe` asset. It also requires that no pre-release is ever marked latest by hand.
- The update banner picks the **version-stamped** installer and never the `.msix` (self-signed, and it
  cannot install on a machine that has not already trusted the certificate).
- Compiling the installer locally needs `installer\redist\MicrosoftEdgeWebview2Setup.exe` first (see
  the note in `setup.iss`); ISCC stops with an explanation without it.
- Merging PRs from a Claude session needs a `Bash(gh pr merge:*)` allow rule in
  `.claude/settings.local.json`. Auto mode blocks it otherwise, and it also blocks Claude from adding
  that rule itself.

## Working notes — the expensive lessons

- **Never re-add the clan roster export.** It is gone for **consent**, not cost. Collecting it is now
  provably free, and that must not be read as a reason to bring it back.
- **An event with an optional listener is a place to lose data.** The single-instance pipe raised
  forwarded launches to whoever subscribed. Only the sign-in panel did, so a signed-in window dropped
  them silently for months. Anything carrying a single-use value needs exactly one owner, plus a
  queue for when the owner isn't ready.
- **A 404 from a lookup can mean the route was never built.** The memory-map endpoint answered 404
  for two months and every caller read it as "nothing published yet". Check the server has the
  route before reading 404 as an answer.
- **One account's holdings are not the game's structure.** The area-bonus doc claimed "which stats a
  location grants varies by location" — read off one player's *purchases*. All three Observatory
  tiers declare the same eight. This is why both village tables ship the whole declared grid.
- **A negative result from a scan is worth exactly as much as the scan's stride.** Four occurrences
  here (page-stride vault scan, region-capped klass scan, shard/soul quantity signatures, the
  `typeId < 100` floor).
- **A field that GATES other fields must be resolved by name, never left to calibration** —
  `Hero._type` being unresolved silently cost faction, role and typeId for an entire roster.
- **Verify a computed column on data that can actually disagree.** The flat-vs-percentage bug passed
  a cell-for-cell check against the client because every cell that could have exposed it was blank
  on the test account.
- **Running a local build re-registers `rslcompanion-extractor://` to that exe.** Put the HKCU
  registration back to the installed exe afterwards, or the site's buttons start launching the
  build folder.
- **`bin\Debug\` can hold more than one framework folder.** The project targets
  `net10.0-windows10.0.19041.0`; a stale `net10.0-windows\` sat beside it for two weeks, and running
  it produced a fifteen-day-old app that looked current. Check `LastWriteTime` before believing a run.
