# Next session — RSL Companion

Paste this whole file as the opening prompt.

---

Context: three repos, clean and pushed as of 2026-08-09 — **no longer true**, see the status notes
per item below and `git status` in each. Nothing from the 2026-08-09 working session is committed.

- `D:\Codex\RslCompanionUploader` (public) @ `68bcdb4` — WinForms uploader, released **v1.8.0**
- `D:\Codex\RslCompanionUploader\extraction` (private submodule) @ `0d408ac` — extraction engine
- `D:\Codex\RslCompanionMetadata` (private) @ `40f1d7e` — game-metadata catalogs; its own
  `extraction/` submodule also points at `0d408ac`
- `D:\Codex\RaidTools` — the API + Angular frontend (separate; its own TODO.md)

Game build in use: **11.70.0**, in the shipped offset catalog. Read
`CLAUDE.md` in each repo first — they carry the reasoning behind most of what follows.

## 1. Finish verifying the v1.8.0 sign-in handoff (highest value, smallest effort)

The site and API are confirmed on the one-time `?code=` handshake (`POST /api/extractor/handoff`
returns 200 with a real session; the deployed JS bundle emits `sync?code=`). What was never
confirmed end to end is the **released binary** completing it.

**Status 2026-08-09: everything but the browser click is done.** v1.8.0 is installed at
`%LOCALAPPDATA%\Programs\RSL Companion Account Data Extractor\` (FileVersion 1.8.0.0), and launching
it reclaimed the `rslcompanion-extractor://` registration from the Debug build — verified in
`HKCU\Software\Classes\rslcompanion-extractor\shell\open\command`.

**Remaining: launch from rslcompanion.com and confirm the app opens signed in, not signed-out.**

⚠ The registration is claimed by **whichever build ran last** — the app re-registers on every
startup. Running `bin\Debug\...` again takes it back, and it did exactly that mid-session. Do the
site test before launching the Debug build again.

⚠ There is an **in-progress sign-in rework in the working tree** (`Forms/BrowserSignInForm.cs`
deleted; `Auth/SessionManager.cs`, `Auth/SessionProtection.cs`, `Forms/SignInForm.cs`,
`Forms/SignInPanel.cs`, `Forms/SessionSecurityForm.cs` added, plus `Program.cs` / `MainForm.cs`
changes). It is uncommitted and is **not** what the released v1.8.0 binary does — item 1 tests the
release, not the tree.

## 2. Point the download at v1.8.0

`get.rslcompanion.com/RslCompanionAccountDataExtractor-Setup.exe` should resolve to the v1.8.0
asset once (1) passes. Note the release is **unsigned** — the signing step in
`.github/workflows/release.yml` is still a stub, so SmartScreen will warn on first run.

## 3. Champion catalog: two known problems — BOTH DONE 2026-08-09

**a. `ChampionIndexCatalogExtractor` cold-calibration was broken. Fixed.** Root cause: the klass scan
returns every address whose first eight bytes are the klass pointer, and the hits that are not object
headers are **not evenly mixed in** — on 11.70.0 the first **182** in address order were all such
sites. `Calibrate` looked at the first 40 (structural discovery: the first 24), found nothing in
either, and threw. It only ever worked warm because a warm cache skips calibration entirely, and
`Extract` never noticed because it walks all ~8.7k hits and skips what does not parse.
Calibration now samples the whole list, with early exit at 8 corroborations.
Verified: cold run (cache deleted) → 8,357 types / 1,355 champions, **byte-identical to the warm
run**. The class doc comment's claim is now true rather than corrected away.

**b. `champion_index.json` size — trimmed.** `types[]` was 3.98 MB of 4.47 MB. The exporter now
writes **two shapes in one run** (`--out` full, `--slim-out` without `types[]`), so they cannot
drift. The uploader/installer ships the slim one (489 KB); the full one stays in
`RslCompanionMetadata\exports\`. Slim is a strict subset, so dropping the full file in still works.
Recorded in the uploader's CLAUDE.md, `installer/setup.iss`, the engine csproj, and the exporter's
README (which was also stale — it claimed a committed `offsets_cache.json` and a roster-bootstrapped
fallback, neither of which exists).

Regenerating the catalog requires Raid running with the **champion index screen opened** so all
`HeroType` templates load, then, from the metadata repo:

```
ChampionIndexExporter --out D:\Codex\RslCompanionMetadata\exports\champion_index.json --slim-out D:\Codex\RslCompanionUploader\extraction\exports\champion_index.json
```

## 4. Skill attribution: the last 2.6% — WRITTEN UP 2026-08-09

`docs/raidtools-skill-attribution.md` (public repo) is the consumer-side note, in the same standing
as the schema-10 migration guide: a summary, never the contract.

Measured against the 11.70.0 catalog while writing it: **336 of the 1,034 playable champions** hold
at least one skill below max ascension that is gone by max ascension — Apothecary swaps `303`→`304`
at asc3, Abbess `23104`→`23103` at asc3, Skavag `231002`→`231005` at asc1. For the other 698 the
max-ascension kit is a superset, so `champions{}` is already right for them. (The **374 of 1,355**
this used to say counted the 38 bosses in, from before the boss split.)

**DONE 2026-08-12 — and the mechanism changed, so ignore the plan this section describes.** The fix
is no longer "read `types[]` from the full catalog": `types[]` is retired, the slim/full pair no
longer exists, and each skill now carries the ascension span it is active for
(`{ typeId, fromAscension, toAscension? }`) on the champion's own row. One lookup answers every
ascension.

RaidTools implements it — `ChampionIndexSkill.IsActiveAt` is the test. `docs/raidtools-skill-attribution.md`
was rewritten to match; the version of it that predates 2026-08-12 hands an agent a code sample that
fails on both the lookup and the field name.

The one trap that survived the rewrite: match on the **range**, not just on membership. Both halves
of a swapped pair are in the list, so testing `typeId` alone credits an un-ascended copy with the
post-swap skill it does not have — the original bug, pointing the other way.

## 5. Boss catalog — SHIPPED 2026-08-11, two fields still unemitted

**The design question is answered.** `boss_index.json` exists, produced by the same
`ChampionIndexExporter` run, with its own schema: 321 entries (212 `kind: "boss"`, 109
`"locationOnly"`), split on `HeroType.BossData` rather than on `Fraction == 0`, keys never colliding
with `champion_index.json`. It is deliberately **not** bundled in this installer — nothing a user
owns is a boss. Contract: `RslCompanionMetadata/docs/champion-index-contract.md`.

**What is still open is narrower than this section implies:** boss forms currently emit
`element / index / role / skills` only. `HeroForm` declares `AdditionalSkillTypeIds@+40` and
`ChallengeSkillTypeIds@+48`, both read by `ReadIntCollection` and still **not emitted** into the
boss schema — verified against the shipped file, where Chimera's four forms carry ordinary `skills[]`
and nothing else. Adding them is now an additive change to a file that exists, not a catalog to
design. The findings that justified deferring them:
- *Additional*: six champions only, all drawing from a shared `200000`-block pool; the same ids
  appear verbatim across champions, and Kurosa carries an identical set on both forms.
- *Challenge*: entirely Chimera (base 26690–26720), forms 1–3, ids `8000101`–`8000127`.
Findings and offsets are documented on `ChampionFormInfo`. Design a boss catalog with its own shape
rather than bolting these onto the hero catalog.

## 6. `element` has no id→name table — CLOSED 2026-08-09, not needed

**Decided: no name table will be published.** The frontend already renders affinity from icons keyed
by the raw id, so nothing downstream needs names. `forms[].element` stays a raw id permanently —
this is now a decision, not an open gap. Do not reopen it as "the enum is nearly resolved".

The investigation below is kept only because it records what the game actually declares, and because
route B would settle `factionId` and the relic socket shapes too — which are separate questions.

Investigated via the new `BlessingProbe --elementnames`. Written up in the engine repo's
`docs/element-enum-findings.md`.

**Settled, from the game, twice over:** the enum is `Element` and declares exactly
`Magic, Force, Spirit, Void` with **no None/Unknown member**; `ElementExtensions` declares the same
four as icon-URL fields; the l10n table carries exactly those four labels under the template
`l10n:hero/element/{0}#label` (which is why it is slug-keyed and carries no ids). Catalog data uses
**1,2,3,4 and never 0**.

**Not settled: which id is which.** Four members, no sentinel, values starting at 1 ⇒ the members
carry *explicit* values — the same situation as `ArtifactSetKindId`, where declaration order shipped
a table that was wrong from id 4 on. IL2CPP does not expose enum constant values at runtime.

**To finish it (route A, ~1 minute):** open the champion roster's **affinity filter** in Raid, leave
it on screen, and re-run `BlessingProbe --elementnames`. `ElementFilterItemContext._element` is a
plain int at +80 and was found live holding `1`, but with `_name` null because the filter was never
shown this session. With it open, the items carry value *and* label — a self-identifying binding.

**Route B, worth more:** parse the v39 metadata field-default-value table. That settles every enum
permanently, including **`factionId`** (1–17, name-keyed the same way, also shipped unnamed) and the
provisional relic socket shapes.

Trap recorded while doing this: `l10n:hero/element/` is **reused for dungeon names** (`MagicKeep`,
`DragonsLair`, `SpiderCave`, …) — 10 of the 14 keys under that prefix are not elements.

## 7. Data hygiene

Any account synced on game build **11.70.0 before 2026-08-09** has a roster stored with
`factionId: 0` and `roleId: null` for every hero, and champions named `Template_<id>` for the nine
oldest base ids. Neither field distinguishes "could not read" from "has none". **Re-sync those
accounts**; nothing to migrate server-side.

## 8. RaidTools' own TODO

`D:\Codex\RaidTools\TODO.md` still has items that were never in scope here: rotate the Data
Protection keys (still in git history), reset both legal documents to Version 1 before launch,
robots.txt/sitemap.xml, GDPR mechanics (processor DPAs, Art. 27 representative, RoPA, breach
procedure), and two decisions blocked on you — governing law, and an identifiable controller
address. Also: rename `D:\Codex\RaidTools` to `D:\Codex\RslCompanion`.

## Working notes

- Never re-add the clan roster export. It is gone for **consent**, not cost — collecting it is now
  provably free (`AppModel.UserNotes`), and that must not be read as a reason to bring it back.
- A negative result from a scan is worth exactly as much as the scan's stride. This codebase has
  paid for that lesson four times (page-stride vault scan, region-capped klass scan, shard/soul
  quantity signatures, the `typeId < 100` floor).
- A field that GATES other fields must be resolved by name, never left to calibration —
  `Hero._type` being unresolved silently cost faction, role and typeId for an entire roster.
