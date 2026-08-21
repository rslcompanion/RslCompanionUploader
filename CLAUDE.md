# RslCompanionUploader — Claude Code scope

## What this app is

A signed-in Windows (WinForms, .NET 10) companion for **rslcompanion.com** ("RSL Companion
Account Data Extractor"). It authenticates against the site's Firebase project
(`raid-account-manager`), lists the accounts linked to the signed-in user, and sends
Raid: Shadow Legends account data to the RSL Companion API.

The app's job is to read the live Raid process memory via the **private extraction engine** (git
submodule at `extraction/`, repo `RslCompanionExtraction`) and POST what it finds. The payload is
self-identifying (it carries the in-game `accountId`); the server routes by that id. Extraction
runs off the UI thread; engine console output is redirected into the activity log.

There is **one export**:

| Action | Payload | Endpoint | Cost |
| --- | --- | --- | --- |
| **Update user data** | consolidated profile (resources, champions, guardians, the whole artifact vault, relics, gemstones, `clanId` + `clanName`) | `/api/sync/consolidated/raw` | ~5 s first run of a game session, ~1 s after |

**There used to be a second one — "Export clan" → `/api/sync/clan/raw` — and it is gone. Do not
bring it back.** It posted the clan record plus a roster of every clanmate's id and display name,
read out of the exporting player's client. RSL Companion stopped ingesting rosters: those people
never installed this app and never agreed to anything, so clan membership is now built from each
member importing their *own* account, and the server endpoint no longer exists. The engine still has
`ExtractClanAsync` — nothing calls it.

**The clan's id *and name* both ride on the consolidated payload, and both are free** (schema 14).
The id is two pointers off `UserGameData`; the name is a pointer walk from `AppModel` — see
`ClanExtractor.TryReadClanName` in the engine. **The roster is reachable by that same walk and is
still not emitted.** That is the whole point: the reason was never cost, and now that cost is
measurably zero, the consent reason stands on its own. Don't let a future "but it's cheap now"
reopen it.

**Artifacts are the one thing that pays a scan on this path, deliberately.** The vault is not
reachable from the account object either, but it stays on the consolidated path: it is the account's
own data, it costs ~3 s rather than ~25, and the cost is a one-off per game session — the instance
address is cached for the session and the class RVA ships in the offset catalog. Gear and
accessories are kept apart in the *payload* (`artifacts[]` / `accessories[]`) rather than in separate
uploads, so one call still snapshots the account while a consumer can load either alone.

## UI: native shell + WebView2 page

The **entire UI is one full-window WebView2 page** ([Forms/AppShell.cs](Forms/AppShell.cs)), styled to
match rslcompanion.com. [Forms/MainForm.cs](Forms/MainForm.cs) is a thin native shell: title bar + a
File/Help `MenuStrip`, hosting `AppShell` docked fill. `MainForm` stays the backend — it runs the
status poll, extraction and API calls, and **pushes a single view-state** into the shell (signed-in
flag, user, connection status, update banner, accounts, busy + which action is busy, frontend URL).
The page posts back seven actions: `export`, `signIn`, `signOut`, `refresh`, `openUrl`,
`installUpdate`, `logDetail`. Check for
updates, recalibrate and about stay native menu items calling straight into `MainForm` — no bridge
needed. There is no uncovered-build bridge action or banner: covering an uncovered build is triggered
automatically from `MainForm`, not from anything the page posts back.

**The update banner downloads the update; it does not open GitHub, and it never closes the app.**
Clicking it fetches the release's Inno installer ([UpdateInstaller.cs](UpdateInstaller.cs)) and checks
it against the `.sha256` published beside it in the same release. The release page it used to open
handed the user six assets and asked them to pick; "a new version is available" means they already
decided. The banner carries the percentage while the download runs and stops accepting clicks, so a
second click can't start a second download. `UpdateChecker` picks the version-stamped
`…-Setup-<v>.exe` — the name the checksum file and the release notes refer to — and never the
`.msix`, which is self-signed and cannot install onto a machine that hasn't already trusted the
certificate.

**Neither dead end opens a browser — both report into the banner and offer a link.** A packaged
(MSIX/Store) build must never overwrite itself with an Inno install, and a release with no installer
asset has nothing to run; those say so and link the release page. A failed download says *why*
(`DescribeDownloadFailure` names the likely cause — antivirus holding the finished file is the one
seen in the wild), **stays clickable as its own retry**, and puts `UpdateChecker.DownloadPageUrl`
(get.rslcompanion.com, the unversioned installer) beside it as the manual way out. Launching a
browser instead was worse on both counts: it threw away the retry, and it answered a failure the user
was never told about by opening a tab — which is indistinguishable from the banner just being a link
to GitHub, and got reported as exactly that. The link is a child of the banner and stops the click
propagating, so taking it doesn't also start the retry underneath. **A cancellation is only silent
when the form's token actually cancelled it** — `HttpClient` reports its own 15-minute timeout as
`TaskCanceledException` too, and swallowing that froze the banner mid-percentage with `_updating`
stuck true, which no later click could clear.

**Nothing is replaced until the user restarts, and both ways of restarting work.** A verified
download is staged, and the banner says so while staying clickable; the click runs the installer
`/SILENT /relaunch=1` and closes the app so the new build comes back up (`installer/setup.iss` has a
silent-mode `[Run]` entry gated on that parameter). Quitting applies it too —
`ApplyStagedUpdateOnExit` runs the same installer from `FormClosed`, deliberately **without**
`relaunch`, because reopening a window someone just closed is not what they asked for. Both paths are
guarded by `_installerLaunched` so a restart-click followed by the exit hook cannot start two
installers. **Do not restore the old behaviour of installing the moment the download finishes**: it
closed the app under a user who might be mid-export, and the restart is free to wait.

**Automatic checks are opt-in, asked once, and revocable.** `AskAutoUpdateIfUnanswered` puts the
question on the first run that finds `autoUpdateChecksChosen` false — same shape as the stay-signed-in
question, and for the same reason: off is the default, so silence would decide by omission. Saying yes
starts `PollUpdatesAsync` — once now, then hourly — and Help ▸ Check for updates automatically toggles
it later (toggling counts as answering, so nobody is asked about something they already set). Saying
no leaves Help ▸ Check for updates working, so it costs discovery, not the ability to update.
**The check does not require a session.** It used to run from `EnterSignedInAsync`, which meant anyone
who never signed in was never told a release existed — including the release covering the Raid build
about to block them.

The app opens its main window **before authenticating** (like Postman). [Program.cs](Program.cs) now
does *no* authentication at all — it hands `MainForm` the launch code (if any) and starts the message
loop. `MainForm.RestoreSessionAsync` runs on Load: redeem the launch code, else restore the saved
session. **That work moved off the startup path deliberately** — both legs make a network call, and
the Windows Hello option prompts the user, so doing it before the first paint gives them a hang with
nothing on screen to explain it. The window opens signed-out and fills in when the session arrives;
the game-status pill works throughout. Clicking Sign In opens [SignInPanel](Forms/SignInPanel.cs) and,
on success, `MainForm.EnterSignedInAsync` loads accounts and enables export. Sign out drops back to
the signed-out state in place (no process restart).

The page is a top bar (brand + connection pill + Sign In button / identity), an optional update
banner, the accounts grid, an "Open RSL Helper" bar (opens `MainForm.HelperUrl()` via `openUrl`), and
a collapsible activity console.

**The window sizes itself in `ApplyStartupBounds` (on handle creation), never in the constructor.** A
`Form` only rescales assigned bounds when `AutoScaleDimensions` is set, which it is not — so
`Width = 1210` was applied as *device* pixels and the window opened at 605×374 logical on a 200%
display, with the WebView2 page (which is DPI-aware) getting a ~605 px CSS viewport and a scrollbar
across the accounts pane. The design numbers are 96-DPI units, scaled by `DeviceDpi` and clamped to the
monitor's work area. Don't move them back into the constructor: `DeviceDpi` isn't known there.

**Tiles carry the last-sync *instant*, not a "14 min ago" label.** `AppShell.Tile.LastSyncIso` is
ISO-8601 and the page renders the wording (`syncLabel`), re-texting it every 30 s (`tickSyncLabels`,
which retexts nodes rather than rebuilding the grid — a rebuild would throw away the button under the
user's cursor). Formatting it in C# froze the age at render time, so a window left open showed the gap
between the last sync and the last tile rebuild — reading hours fresher than the truth.
`MainForm.PollAccountsAsync` additionally re-reads the accounts every 5 minutes via
`LoadAccountsAsync(silent: true)`, which skips the busy flag and the narration: counts, clan and
last-sync are server-side facts that change without this app doing anything.

**The activity console has two levels, and the default is the one a player can read.** `MainForm.Log`
takes `detail:` — false (the default) for lines written for the user, true for diagnostics. The
engine's `Console` output is *all* diagnostic: `LogEngineLine` sends every line at the detail level and,
from the phase markers alone, emits a plain-language progress line ("Reading your champions…") via the
`PhaseProgress` map. Phase names there are stable identifiers in `ExtractionService`; an unrecognised
one produces no plain line rather than leaking the raw name. API results carry the same split —
`UploadResult` has a user `Message` and a diagnostic `Detail` (status code + body) instead of one
string with the raw response in it.

**Detail lines are hidden, never dropped.** The page keeps every line and filters on render, so the
console's "Details" toggle explains the export that already ran instead of requiring the user to
reproduce it — which is the whole point, since the person who wants the trace is reporting a problem
that already happened. The choice persists (`activityLogDetail` in `settings.json`), and the collapsed
header always summarises with a plain line so Details-off never shows an address there.

`HelperUrl()` is `AppConfig.FrontendUrl` plus **`?account=<in-game id>`** whenever the running game is
on an account the profile has already imported, so the site opens on the account being played rather
than on whatever that browser last selected. The id needs no translation: `GET /api/accounts` reports
the in-game id as both `id` and `userId`. The site consumes it in `ActiveAccountService` (RaidTools
frontend), which reads the param at *module load* — the auth guards redirect without preserving query
params, so anything later is too late — writes it to its `raidtools.activeAccountId` storage, and
strips it from the address bar. Clearing the stored sync method alongside it is deliberate: it makes
the navbar re-pin the preferred (Extractor) snapshot for that account. "Raid not running" is stated once, by the top-bar pill — the accounts
grid never repeats it as a tile. A running-but-unimported account shows a "new account detected"
tile, and the profile matching the running game turns green (all others keep a black border).

**Tiles are status and cannot be selected — except the one tile the running game is on, which carries
the game-reading action.** Its target is never chosen by the user: it reads the live process, so the
only account it could ever act on is the one being played. A new (unimported) account's tile offers
"Add this game account"; a matched existing one offers "Update user data". Both post the same
`export` action — the server create-or-updates by the in-game id in the payload. When no game is
reachable, no tile carries buttons at all.

`SetBusy` takes a *kind* (`"export"`, or null for background work like an account reload) so the page
can show progress on the button that is running rather than only greying everything out, with the
engine's own phase lines in the activity log alongside.

Because the whole UI is WebView2, the runtime (preinstalled on Win11) is load-bearing; if it's
missing, `AppShell` shows a plain fallback label instead of the page. Sign-in does **not** depend on
it — that flow is the user's own browser plus a native panel, so it still works on a machine where
the runtime is absent.

**An uncovered game build resolves itself, automatically, with no button to click.** When Raid updates
ahead of a release, `MainForm.UpdateReportPrompt` fires the moment the status poll notices a build
`CoveredByShippedCatalog` doesn't know: it asks RSL Companion for a published memory map
(`Endpoints.BuildCertification`) and installs it into the user's own `calibrated-offsets.json`, and
only falls back to the ~35–50 s local calibration scan when the server has none. The server lookup is
still **opt-in** — a TaskDialog with a "Check automatically from now on" verification box, one offer
per build per session, the tick persisted to `settings.json` — but nothing here waits for the user to
notice or click a banner first. Both the certify check and the calibration own a
once-per-build-per-session guard, so re-running this on every poll tick is safe. It stops re-triggering
once *any* local map exists, certified or self-calibrated — `CoveredByShippedCatalog` alone would keep
firing for a user who already fixed it.

**A calibration result is only trusted if it looks like a real account.** `ExtractionService.CalibrateAsync`
extracts without throwing even when the offsets are wrong — a bad scan can read zeroed or garbage
fields just as easily as it can crash. Since `KnownOffsets.Export` writes into a catalog that
`TryResolve` treats as ground truth forever afterwards (a known hash is never recalibrated), the result
is validated (a parseable positive account id, a non-empty name) before it is allowed to touch that
file. A result that fails validation returns `Success: false` and nothing is written, leaving whatever
was already in the catalog — a prior good calibration, or nothing — untouched.

**Both `TryCertifyBuildAsync` and `TrySelfCalibrateAsync` hold `SetBusy(true)` for their duration**,
same as export — the accounts grid's action buttons disable while either is in flight, since a click
landing mid-scan or mid-apply would race the process attach the scan already owns.

**`ExportAccountAsync` validates before uploading, not just on the calibration path.** The same
"parseable id, non-empty name" bar used for calibration is applied to every extracted profile right
before it's sent to RSL Companion — a bad read can slip through fine on a `Update user data` click
even when calibration itself reported success earlier in the session. A failing check logs and returns
without calling `UploadConsolidatedAsync`.

File-based JSON import (`resources` / `champions`) used to live here but was moved to the
rslcompanion.com metadata tooling — do not reintroduce it in this app.

## Public repo / private engine split

This repo is **public**; the extraction engine is **private** and optional at build time:

- With the submodule present (`git submodule update --init`, needs access to the private repo),
  the build defines `EXTRACTION` and the "Update user data" button works.
- Without it, the project still builds and runs — extraction code paths are `#if EXTRACTION`
  and the button is hidden. With no engine there is nothing left to do but sign in and view the
  (empty) accounts pane.
- Engine internals, data files (`offsets_cache.json`, `exports/champion_index.json`,
  `resource-allowlist.json`), limitations, and vendoring rules are documented **in the
  private repo's CLAUDE.md** — do not re-document them here.

## Sign-in: the real browser, always

Clicking Sign In shows [SignInPanel](Forms/SignInPanel.cs), and **the panel opens on an invitation —
it does not launch anything.** It explains that sign-in finishes in the browser, carries the
stay-signed-in checkbox, and offers an "Open my browser to sign in" button; the user's **real default
browser** goes to `/connect-extractor` only on that click. That page mints a one-time handoff code
and launches `rslcompanion-extractor://sync?code=…`; Windows routes it to this app, `SingleInstance`
forwards it to the waiting panel, and [Auth/ExtractorHandoff.cs](Auth/ExtractorHandoff.cs) redeems it
for a Firebase **custom token**.

**Do not put the browser launch back on `Sign In` itself.** Pressing a button in a desktop app and
having a different program take the foreground — before anything on screen has said why — reads as
the app acting on its own. The invitation state also puts the stay-signed-in choice in front of the
user *before* they leave, instead of behind the window that just covered this one. Once the browser
has been opened the panel switches to the waiting state (spinner, "Open my browser again"); a launch
that throws drops back to the invitation rather than stranding the user waiting for a browser that
never opened.

**Hosting that page in an embedded WebView2 was built, tested and abandoned. Do not rebuild it.** It
looks obviously better — one window, no browser bounce, and the handoff code never touches a command
line — and it does not work, for reasons no amount of layout work fixes:

- **Google and Microsoft will not complete a consent flow in an embedded browser.** They screen the
  surface, and `signInWithPopup` opens a chromeless second WebView2 with no address bar. The user
  reaches a real provider page that then goes nowhere.
- **The popup has no opener to answer.** Suppress the popup window and `window.open()` returns null;
  host it and the flow still has to postMessage back. Navigating the main view to the popup's URL —
  the obvious fix — destroys the handshake outright and presents as the app hanging with no error.
- **`signInWithRedirect` in-window works around the popup but not the surface check**, and it drags a
  `connect` redirect-intent through the site's auth service purely for the app's benefit.
- **Stale script fails silently.** The WebView2 profile outlives releases, so a cached bundle quietly
  runs an older sign-in flow against a site that has moved on.

The real browser already has the user's session, password manager and 2FA, and is the surface the
providers actually support. The site keeps its `?embed=1` mode from that attempt (unused, harmless —
it only activates on the parameter, which this app no longer sends).

**The app still gets its own session, and that is the point.** It is not a copy of the browser's.
`/connect-extractor` hands over a code, not credentials, and `signInWithCustomToken` mints tokens that
belong to this install — so a token on this disk is never worth a browser session, and the two are
independently revocable. **Do not "simplify" this into reading the browser's session** (Chrome
app-bound-encrypts its store, and it is an infostealer pattern) or into an in-app password form (it
would lock out every Google and Microsoft user and put a password back into a desktop process).

**`SignInPanel` earns its place with the checkbox, not the status.** The browser signs the *user* in,
but only the app can be asked whether the *app* should stay signed in, because only the app writes to
this disk — so the question is asked while the browser works and read when the code comes back.

The app registers `rslcompanion-extractor://` under HKCU on every startup
([ProtocolHandler.cs](ProtocolHandler.cs)); the installer also registers it at install time. A code
arriving on the launch URI at startup (site-initiated: dashboard → "Sync New Account") is redeemed by
`MainForm.RestoreSessionAsync`.

**It used to be `?rt=<firebase refresh token>`, and that is a protocol break, not a refactor.** Windows
hands a protocol URI to its handler as *process arguments* — readable by any local process and
routinely logged by EDR agents, Sysmon and crash reporters — so a refresh token there was a
credential that mints ID tokens indefinitely, sitting on a command line. The code is worth one
sign-in for ~60 s, is single-use, and only the SHA-256 of it is stored server-side. `rt` is
deliberately **not** still accepted: the site no longer sends it, and reading it would keep the old
credential path alive on the one surface it was removed from. Server contract:
`docs/extractor-handoff.md` in the RaidTools repo. **An uploader older than this cannot sign in from
the website at all** — the launch URI carries a parameter it does not read.

## Staying signed in

Once signed in, the app never needs the website again: it holds a Firebase **refresh token**, and
Firebase refresh tokens do not expire on their own. [Auth/SessionManager.cs](Auth/SessionManager.cs)
owns restore/persist/forget; [Auth/CredentialStore.cs](Auth/CredentialStore.cs) owns the file at
`%LOCALAPPDATA%\RslCompanionUploader\creds.dat`.

**Persistence is opt-in, and the opt-in is total.** [Auth/SessionProtection.cs](Auth/SessionProtection.cs)
is the single stored value (`sessionProtection` in `settings.json`), chosen on the sign-in window and
changeable later from Help ▸ Session security without signing out:

| Level | What is stored | What it stops |
| --- | --- | --- |
| `None` (default) | **nothing** — no token, no email, no display name | n/a; session ends with the process |
| `WindowsAccount` | refresh token, DPAPI CurrentUser | another Windows user, disk theft |
| `WindowsHello` | the above, additionally AES-GCM under a TPM-held key | **code running as the signed-in user** |

At `None` the file does not exist. Identity fields used to be saved regardless "for convenience" —
they are identity, the choice is the only mandate for keeping any of it, and a file that exists only
when consent was given is a file whose presence answers "did I agree to this?".

**`sessionProtectionChosen` is separate from the level, and the difference matters.** A launch from
the website signs the app in with no sign-in screen, so there is no checkbox to read — and treating
the `None` default as an answer would mean silently never remembering anyone who arrives that way.
`MainForm.AskProtectionIfUnansweredAsync` asks **once ever** on that path, after the UI is up. The
sign-in panel and Help ▸ Session security also set the flag; nothing asks twice.

Hello does not hand out key material, it signs, so the blob carries a random challenge and the AES key
is SHA-256 of the Hello signature over it ([Auth/HelloProtector.cs](Auth/HelloProtector.cs)). This
relies on Hello signing with **RSASSA-PKCS1-v1_5, which is deterministic**. Which factor is offered —
face, fingerprint, PIN — is Windows' decision, not ours. If the credential is lost (Hello reset, TPM
cleared) the saved session is simply unreadable and the user signs in again; every caller treats a
Hello failure as "no saved session" rather than an error to surface. **If Hello was chosen and then
declined, nothing is written and the preference resets** — silently storing it unprotected would be
the opposite of the request.

The DPAPI entropy constant is a public constant in a public repo and therefore **adds no secrecy**. It
is retained only so files written by earlier versions stay readable; the protection comes from DPAPI's
CurrentUser scope and, optionally, Hello. Don't churn it thinking it does something.

**A failed refresh only clears the saved session when Firebase actually rejected it**
(`TOKEN_EXPIRED`, `USER_DISABLED`, `INVALID_REFRESH_TOKEN`, …). Anything else — no network yet, DNS
not up, a 503 — leaves the file alone. It used to clear on *any* exception, so launching before the
network was ready silently discarded a good session and sent the user back to the website. Guessing
wrong in this direction costs one sign-in; in the other it throws away a valid session because Wi-Fi
was slow.

**Sign-out asks which kind it is**, because they are different actions. Plain sign-out forgets this
PC. "Sign out everywhere" also calls `POST /api/auth/logout` (which already existed — it blacklists
the presented ID token and calls Admin `RevokeRefreshTokens`) and clears the shared WebView2 profile.
**Firebase revokes per user, not per device**, so "everywhere" genuinely signs the browser out too;
that is stated on the button rather than in a follow-up confirmation, because it is the surprising
part and afterwards is too late to read it.

## Export payload contract — keep the pair in sync

One doc pair, prose + JSON Schema 2020-12:

- [docs/export-schema.md](docs/export-schema.md) / [.json](docs/export-schema.json) — what
  `POST /api/sync/consolidated/raw` receives. Read by RaidTools' `ConsolidatedJsonSyncAdapter`.

`docs/clan-export-schema.md` / `.json` are **deleted** (schema 13), with the clan export they
described. Old links to them in the changelog rows are left unlinked on purpose.

Alongside them, two **static game metadata** files — not payloads, since every account sees the same
tables, but they ship here because the payload's ids are opaque without them:
[docs/role-names.json](docs/role-names.json) for `champions[].roleId`, and
[docs/artifact-enums.json](docs/artifact-enums.json) for the artifact `kindId` (slot), `statKindId`,
`rankId`, `rarityId` and `setKindId`. Same rule applies: if one of those enums gains a member, that
file and the schema pair change together. Each table in the artifact file states how it was
corroborated, and the weaker ones say so — two of them replaced tables that were wrong for years.

**The roster array is `champions[]` since schema 16, with `heroes[]` emitted alongside it byte for
byte until schema 17.** Every other surface already said champion — the game's UI, this app's log
lines, RaidTools' `playable_champions`, and the consumer's own `ParserChampion` element type read out
of a field called `Heroes`. "Hero" is the game's *internal* class name, which is why the engine's C#
types keep it: they mirror the IL2CPP metadata so memory-layout work stays diffable against a type
dump. **The two-step is not optional** — emitting `champions` alone would leave RaidTools'
`data.Heroes` null on every import, which is an empty roster rather than an error, and that is the
silent wipe the never-empty invariant exists to prevent. Consumers migrate with
`payload.champions ?? payload.heroes`. **The consumer's fallback outlives the producer's field by a
long way**, because installs update opt-in and old ones keep sending `heroes` alone; retiring it
there is gated on refusing pre-1.14 uploaders, a separate decision. The `*HeroId` join keys
(`equippedByHeroId`, `heroTypeId`, `heroBaseTypeId`, `first`/`secondHeroInstanceId`) deliberately keep
their names: they name the game's own fields and join onto `instanceId`, not the roster.

**`champions[].baseStats` (schema 15) is the one field that is *computed*, and it is deliberately
computed here rather than left to consumers.** It is the game's own **Basic Stats** column — the
numbers before any gear, Great Hall, arena, mastery, guardian, empowerment, blessing, relic or area
bonus — keyed by the same `statKindId` the artifact bonuses use. It exists because an artifact bonus
with `isAbsolute: false` carries a *fraction* (0.18 = +18%), so without a base stat there is nothing
to take 18% of and every percentage roll has to be dropped from a displayed total.

**Unlike `roleId`, it is not champion-constant**, which is the whole argument for resolving it at
export: the stored value depends on the copy's *ascension*, the displayed one additionally on its
*rank* and *level*. A consumer cannot recover it from a catalog keyed on `baseTypeId`, so this is not
plain denormalization and consumers should prefer it over their own catalog for champions a player
owns. They still need the catalog for champions the player does *not* own — the two are
complementary, not alternatives.

The engine side is `HeroBaseStatsCatalog` (extraction submodule), reading the bundled
`exports/hero_base_stats.json`. **Every coefficient comes out of that file — the growth multipliers,
the level caps, the health ×15, even which stat kinds scale — and none is hardcoded**, because a
Plarium rebalance changes them and the swappable file is the point. Two things it must keep getting
right: the key is `baseTypeId + ascension` (base stats differ per ascension on 1,021 of 1,040
champions, so a `baseTypeId` lookup reads unascended numbers for every ascended copy), and the level
term is **exponential**, not linear — both forms agree at level 1 and at the cap, and the linear one
is wrong by 3–5% in between.

**The payload says which catalog produced the numbers** — top-level `baseStatsCatalog`
`{generatedAt, gameVersion}`, absent exactly when no hero carries base stats. **This is not the
top-level `gameVersion`**: that is the build the export *ran against*, this is the build the numbers
were *computed from*, and they come apart in the ordinary case — a user who updates Raid before a
refreshed catalog ships sends the new `gameVersion` with stats from the old catalog. A computed stat
is a snapshot, so without this a stale block is indistinguishable from a fresh one, and a rebalance
invalidates every stored block until each user re-exports. The consumer rule stated in the schema doc
is that a trailing catalog build makes a block **suspect, not wrong** — flag it for re-sync, never
discard it. Logging which catalog loaded answers whoever is debugging the run; this answers the
consumer holding the block months later, which is where the question actually gets asked.

**Absent is not zero, anywhere in that block.** The property is omitted when the copy's level or star
rank could not be read — `Stars` silently falls back to 5, which is fine for a star count but would
feed the growth multiplier — when the level exceeds its rank's cap, and when the catalog predates the
champion. A `0` that *is* present is a real zero: base Accuracy is genuinely 0 on 6,806 of the 7,166
variants.

**The catalog updates without a rebuild**, on the `KnownOffsets` pattern: a copy downloaded to
`%LOCALAPPDATA%\RslCompanion\hero_base_stats.json` beats the one beside the exe when its
`generatedAt` is newer. **One file wins whole; the two are never blended** — unlike offset entries,
which describe one build and merge per field, two base-stat catalogs can be cut from different game
builds, and mixing one's champion stats with the other's growth table yields plausible wrong numbers.
[HeroBaseStatsUpdate.cs](HeroBaseStatsUpdate.cs) validates a served catalog by parsing it with the
same code that would have to read it, and **every failure degrades to the bundled copy silently** —
an export without base stats is an export missing one optional property, never a failed export.
**`Endpoints.HeroBaseStats` is `/api/hero-base-stats/catalog`, and the suffix is load-bearing.** The
bare route serves nothing: RaidTools' `HeroBaseStatsController` answers *per champion* (`/resolve`,
`/meta`) precisely so a browser never fetches 2.2 MB or re-implements the growth rounding. **This app
is the documented exception** — it computes a whole roster on the user's desktop against a live game
process, and writes the file so a later export needs no network at all, so per-champion resolution is
~900 round trips it may not be able to make. `/catalog` exists for exactly this caller.

**The response must keep the producer's top-level keys** (`growth`, `champions`, `statKinds`,
`generatedAt`), because `HeroBaseStatsCatalog.Parse` validates them and the body is then written to
disk verbatim. Extra sibling fields are fine; wrapping the catalog in an envelope the way `/meta` does
breaks this **silently** — the client rejects it, falls back to the bundled copy, and the symptom is
indistinguishable from "the endpoint isn't deployed". A test in RaidTools
(`HeroBaseStatsTests.The_catalog_response_keeps_the_producers_top_level_shape`) pins that shape from
the other side.

A third pair runs the **other way** — it is what the uploader *receives*:
[docs/build-certification-schema.md](docs/build-certification-schema.md) / [.json](docs/build-certification-schema.json)
is the response to `GET /api/extractor/offsets/{gameAssemblyHash}`, the memory map for a game build
this release predates. Same maintenance rule.

[docs/raidtools-schema10-migration.md](docs/raidtools-schema10-migration.md) is the consumer-side
migration guide for the schema 9 + 10 artifact changes, written to be handed straight to an agent
working on RaidTools. It is a **summary of the contract, not part of it** — if it ever disagrees with
the schema pair, the schema pair wins and the guide is what needs fixing.

[docs/raidtools-skill-attribution.md](docs/raidtools-skill-attribution.md) is a second note of that
kind: how a consumer attributes an owned copy's `skills[].typeId` to a form **at that copy's own
ascension**. Same standing — a summary, never the contract. It exists because ascension does not only
add skills, it also replaces them (336 of the 1,034 playable champions hold a skill below max
ascension that is gone by max; 374 counts the 38 bosses in too, which is the figure older notes
quote), so a `champions{}.forms[]` holding only the max-ascension kit attributed 97.4% of
owned skills and structurally could not do better. Each skill now carries the ascension span it is
active for, which closes it from the champion's own row.

**The bundled `exports/champion_index.json` is a verbatim copy of
`RslCompanionMetadata/exports/champion_index.json`** — one file, one shape. The slim/full pair this
note used to describe is gone: `types[]` was 89% of the old catalog, folding it into the champion
made the single file smaller than the slim copy had been, and the `--slim-out` flag went with it.
Refresh after a game patch by **copying**, never by a separate export run; if the copy disagrees
with the metadata repo, the metadata repo is right.

It holds **playable champions only**. `boss_index.json` is a separate file under a separate schema
and is deliberately **not** bundled — this path names heroes an account owns, and no account owns a
boss. A boss id falls back to `Template_<id>`, which is correct. `LoadChampionCatalog` reads `name`,
`faction` and `role` and nothing else, so the reshape of `forms[]` did not touch this path. The rule
for both files: `RslCompanionMetadata/docs/champion-index-contract.md`.

They live in this repo precisely because it is public, so consumers can reference them without access
to the private engine. **Nothing the uploader sends now describes anyone but the signed-in user** —
that became true when the clan roster export was withdrawn, and it is a property worth keeping.

**Any change to an emitted JSON must update that pair's two files in the same commit as the code
change** — a new/renamed/retyped field, a new resource id, or even a changed resource *name*. Bump
`schemaVersion` in the JSON Schema plus the "Schema version" line in the `.md`, add a Changelog row,
and state the consumer impact in the commit message and release tag. The contract is only useful if
it is never behind the code.

Note the payload has two fields the engine's own `ConsolidatedProfile` model does not:
`uploaderVersion` and `gameVersion` are stamped on at serialize time by
`MainForm.SerializeWithProvenance`, so engine-level dumps legitimately lack them (the schema marks
them optional for that reason).

## Config

`appsettings.json` (next to the exe):

| Key | Purpose | Default |
| --- | --- | --- |
| `ApiBaseUrl` | RSL Companion API origin | `https://api.rslcompanion.com` |
| `Endpoints.SyncConsolidated` | Parser sync path for "Update user data" | `/api/sync/consolidated/raw` |
| `Endpoints.BuildCertification` | Memory-map lookup for an uncovered game build | `/api/extractor/offsets` |
| `Endpoints.HeroBaseStats` | Newer champion base-stat catalog for `champions[].baseStats`; nothing serves it yet | `/api/hero-base-stats` |
| `Endpoints.HandoffExchange` | Redeems the launch URI's one-time code for a Firebase custom token | `/api/extractor/handoff/exchange` |
| `Endpoints.Logout` | Revokes the session server-side, for "sign out everywhere" only | `/api/auth/logout` |

User preferences the app writes back live in `%LOCALAPPDATA%\RslCompanion\settings.json`
([UserSettings.cs](UserSettings.cs)) — *not* in `appsettings.json`, which is install-time config next
to the exe and part of the installer's signed file set.

**Uninstall removes `settings.json` and a downloaded `hero_base_stats.json`, and deliberately
nothing else in that folder.** The answers it
holds are consent — stay signed in, log level, may-we-check-for-updates — and consent should not
outlive the app that asked for it; leaving it meant a reinstall inherited the answers and never
re-asked. Its neighbour `calibrated-offsets.json` stays: it is minutes of scanning per game build,
keyed by build hash so it survives reinstalls and game updates correctly, and no part of the
uninstall's job. A downloaded `hero_base_stats.json` goes for the opposite reason to the one that
keeps the offsets: it is a ~2.2 MB catalog the app *fetched* rather than work the user paid for, and
every install ships its own copy beside the exe — so a reinstall loses nothing and a leftover is only
an orphan. A `dirifempty` entry takes the folder only when that leaves nothing behind.

## Build & release

```
dotnet build RslCompanionUploader.csproj
```

Installer: `installer/setup.iss` (Inno Setup 6). Releases: push a `v*` tag —
`.github/workflows/release.yml` builds (with submodule), compiles the installer, and attaches
it + SHA-256 checksum to a GitHub Release. CI needs the `EXTRACTION_REPO_TOKEN` secret (PAT
with read access to the private extraction repo) to fetch the submodule.
