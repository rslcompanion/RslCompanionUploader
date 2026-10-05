# Next session — RSL Companion

Paste this whole file as the opening prompt. **Read `CLAUDE.md` in each repo first** — they carry the
reasoning behind most of what follows, and this file is only the "what is open right now" layer on
top of them.

Rewritten 2026-10-04, when **v1.41.0** was cut; brought up to **v1.42.1** on 2026-10-05. The "Open elsewhere" list is carried over from
2026-09-03 and was not re-checked. **Check `git log` before trusting any state below**: more than one
session works in `D:\Codex\RslCompanionUploader`, including its `extraction/` checkout, so a clean
tree here is not evidence that nothing has moved.

---

## Where the repos stand (2026-10-05)

| Repo | State |
| --- | --- |
| `D:\Codex\RslCompanionUploader` (public) | released **v1.42.1**. Payload **schema 37** (shipped in v1.39.0). Builds with 0 warnings. |
| `…\RslCompanionUploader\extraction` (private submodule) | pinned at `c903beb` (schema 37) |
| `D:\Codex\RslCompanionMetadata` (private) | not re-checked |
| `D:\Codex\RaidTools` | the API + Angular frontend; `main` and `dev` branches; its own TODO.md |

**RSL Companion is not live yet.** Everything goes to `main` and ships as a plain `vX.Y.Z` tag.
There is no `dev` branch in this repo.

## What changed in 1.40 – 1.42

- **1.40: a website launch reaches an already-open app.** Before, only the sign-in panel listened
  for forwarded launches, so a signed-in window dropped "Update Data" from the site. `MainForm` is
  now the single receiver (`SingleInstance.SetHandler`). It brings the window forward, redeems the
  code at once, and switches session when idle. See CLAUDE.md, "A launch that arrives while the
  app is already open".
- **1.40: dev builds can ship as pre-releases** (`v1.2.0-dev.N`). Production installs never see
  them. Installs on a dev server, or already running a pre-release, are offered them. **Never used
  yet.** The first `-dev` tag is the test.
- **1.41: the site's "Update Data" runs the export,** not only the sign-in (`SiteUpdateRequest`).
  It runs once Raid's account is readable, waits up to 10 minutes, and never on a launch from the
  app's own Sign In.
- **1.41: the Help menu lives in the page's top bar.** The native `MenuStrip` is hidden and stays
  the record of the items' state. It reappears only if WebView2 fails.
- **1.41: the installer brings the WebView2 runtime** on machines without it (fresh Windows 10).
- **1.42: a mismatch is reported, never blocked.** When the site names a card (`&account=<id>`)
  and Raid is on a different account, the update syncs the account open in Raid and says so in
  one sentence. 1.42.1 dropped a second sentence that told the user what to do.
- **2026-10-05: the v1.39.0 "Update Data dropped by an open app" report was re-checked** against
  the code. Every requirement in it (forwarding with ack, allow-list, prompt redemption, foreground,
  401/403/429/503 messages) is already in 1.40+. Nothing to fix; the reporter needs to update.

## Open in this repo

1. **The site half of the account-mismatch notice.** Since 1.42 the app reads `&account=<in-game id>`
   on the launch URI, and when Raid is on a different account it syncs that one anyway and says so
   (the owner chose informing over blocking). The site doesn't send the parameter yet, and its
   post-click message still says to click "Export account". Prompt:
   [docs/raidtools-update-data-account.md](docs/raidtools-update-data-account.md).
2. **Confirm the site's `/handoff/status` check on prod** reports "launched" for an already-open
   1.40+ app. The fix was verified from the app's side (redeemed ~1 s after the click), not from
   the site's.
3. **Code signing.** Releases are unsigned. Avast locked a fresh installer once on 2026-10-04, and
   SmartScreen warns. The workflow has a stubbed signing step.
4. **The WebView2 bootstrap path is untested on a machine without the runtime.** The detection
   was checked against this machine's registry, and the compile was checked with and without the
   file. A Windows 10 VM without WebView2 is the real test.

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
