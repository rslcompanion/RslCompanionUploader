# RaidTools: "Update Data" names the account it was clicked on

> **Done** — RaidTools `d9976b9` ("Update Data names its card to the Extractor; launch copy matches the
> app"), on prod by 2026-10-09: `buildSyncUri` appends `&account=` for a positive id, only
> `updateViaExtractor` passes one, and the post-click message no longer says "Export account". Kept as
> the record of why.

A prompt for a RaidTools session, written from the uploader side. **This is a summary, never the
contract.** The launch URI is described in this repo's README and CLAUDE.md, and the uploader's
parser (`ProtocolHandler.TryGetHandoff`) is what actually reads it.

## Why

Since uploader **1.41**, "Update Data" on a dashboard account card does the whole update. The
Extractor comes forward, signs in with the handoff code, and runs the export as soon as Raid's
account is readable. Nobody clicks anything in the app.

The Extractor can only read **the account open in Raid**. On a profile with two accounts, "Update
Data" on card A while the game is on account B syncs B. The owner decided (2026-10-04) that this is
**reported, not blocked**: the update still runs, and the app tells the user which account was
synced and how to update the other. To do that the app has to know which card was clicked, and
today the URI doesn't say.

## The change

### 1. Put the card's in-game id on the launch URI

`ExtractorLauncherService.launchSync()` builds:

```
rslcompanion-extractor://sync?code=<code>&api=<api base URL>
```

Give it an optional account id, and append it when present:

```
rslcompanion-extractor://sync?code=<code>&api=<api base URL>&account=<in-game id>
```

- **The value is the in-game account id**, the one `GET /api/accounts` reports as both `id` and
  `userId`. It is the same id the uploader already puts on its own "Open RSL Companion" link
  (`?account=<id>`, read by `ActiveAccountService`).
- **Only "Update Data" passes it** (`DashboardPageComponent.updateViaExtractor(acc)`). "Sync New
  Account" (`launchExtractor`) and `/connect-extractor` pass nothing. A new account has no id yet,
  and `/connect-extractor` is only a sign-in.
- **Order doesn't matter, and no version gate is needed.** Every uploader since 1.8.0 ignores
  unknown parameters, so the site can ship this before or after 1.42. Before 1.42 the parameter is
  simply unused.

### 2. Fix the message shown after the click

`updateViaExtractor` currently flashes:

> RSL Companion is opening. With Raid running, click "Export account" there to update {name}, then
> refresh your accounts.

That has been wrong since 1.41: there is no "Export account" button, and the update starts by
itself. Suggested wording:

> The Extractor is updating your data. Keep Raid open on {name}; the card refreshes once the upload
> finishes.

The page has no way to tell whether the app is ≥ 1.41. If that matters, keep a short fallback
clause: "If nothing happens, press Update user data in the Extractor."

## Traps

- **Don't treat `account` as a target or a permission.** The app never acts on it. It only uses it
  to name the requested account in a message. Nothing about security changes, and the `api`
  allow-list stays the only boundary on that URI.
- **Use the in-game id, not the RSL Companion row id**, if those ever diverge. The app compares it
  with the id it reads from the running game.
- **Encode it like `code`** (`encodeURIComponent`), even though it is numeric today.
- **"Launched" still comes from `/api/extractor/handoff/status`**, unchanged. The extra parameter
  doesn't affect the code's redemption.

## How to verify

With uploader ≥ 1.42 open, signed in and Raid on account B:

1. Click "Update Data" on account **B**'s card. The upload runs, and no notice appears in the app.
2. Click "Update Data" on account **A**'s card. The upload still runs (B's data). The app shows a
   notice: "You asked to update A, but Raid is signed in to B, so B is the account being synced…".
