# RaidTools: moving a shared account to the profile that plays it

A prompt for an agent working on RaidTools (API + frontend). It describes what the uploader needs
from the server so a user can **move** ("migrate") an account that someone else shared with them
into their own profile, and so a moved account can be **deleted** only after a 7-day grace period.
Same standing as the other `raidtools-*.md` notes: a summary of the contract, written to be handed
over. The server's code and docs are the record once it ships.

**Superseded the same day by one record per game account.** The owner's call (2026-11-21): several
copies of one game account make no sense, so in RaidTools an ordinary upload of an account another
profile holds *is* the move, shared or not, and the `migrate` route and `migratable` field below were
removed. Champions are matched by instance id **and** type. A reclaim deletes the new owner's copy and
bars that profile from uploading the account again. RaidTools `docs/account-migration.md` is the
record. Other differences from this prompt, from the first build (`09640c9`):

- **Nothing moves at the start.** The previous owner's copy is only hidden for the 7 days, and their
  plans and saved snapshots change hands at finalization. Re-owning their snapshots at once (step 2
  below) would not survive the new owner's next update, which purges outgoing snapshots.
- **No `migratedAway` on the account list.** Moves out are served by `GET /api/accounts/migrations`
  (`incoming` / `outgoing`), and the site shows the Reclaim notice from that.
- **No names of the other side.** The notice says "another RSL Companion profile"; `previousOwner` /
  `newOwner` are not sent.

Read `docs/account-sharing.md` in RaidTools first. This builds on its pins, grants and the rule
that ownership is a uid filter.

---

## Why

Today one game account can sit in RSL Companion two ways:

- **Owned.** Snapshots under the importer's uid (`accounts.FirebaseUid`). Only the owner can update,
  revert or delete it.
- **Shared with me.** A `SharedAccountPin`. `GET /api/accounts` lists it with `shared: true`. The
  viewer can only unpin it (`DELETE /api/shared-accounts/{accountId}`).

A player often ends up as the *viewer* of their own game account. A clanmate or a previous holder
imported it, then shared it. They play it, but their RSL Companion profile only links to someone
else's copy. If they press Update in the uploader, they get a **second, independent copy** with no
history. The other person's copy, saved snapshots, plans and clan record stay where they were.

Migration fixes that: the person who can prove they play the account takes it over, history
included. The previous owner gets 7 days to undo it.

## What the uploader does (1.62)

- A shared tile's link reads **Unlink**. It calls `DELETE /api/shared-accounts/{id}` and says no
  data is deleted.
- An owned tile's link reads **Remove**. It calls `DELETE /api/accounts/{id}?syncMethod=ConsolidatedJson`
  and says the data is deleted immediately, with no grace period.

## What the uploader will do once the server supports it

- On a **shared** tile for the account **currently open in Raid**: a **Move to my profile** action.
  It reads the live game and sends that read to the migrate endpoint below. It is offered only when
  the list item says `migratable: true`. That is how the uploader detects a server that supports
  migration, so either side can ship first.
- Unlinking a shared tile while that account is open in Raid asks which one the user means:
  **Unlink** (drop the link) or **Move to my profile**. The dialog says a moved account can be
  deleted permanently only 7 days after the move.
- On an **owned tile still in its grace period**: Remove offers **Give it back** (undo the move).
  **Delete permanently** is greyed out, with the date it becomes available (`deletableFrom`).
- After the grace period, Remove is the ordinary delete.

---

## Proof of ownership: the live read

The server cannot see the game. The uploader can, so a migration carries a **fresh consolidated
payload read from the running game**. It has the same body, headers and gzip handling as
`POST /api/sync/consolidated/raw`.

Be honest in the code comments about what this proves. It is **evidence of possession, not a
cryptographic proof**: a crafted request can carry any in-game id. The limits below are what make
that acceptable:

1. **Only an account the caller can already see.** The caller must hold a **live** pin on it (the
   grant re-derived as on every shared read). A migration can never reach an account that was never
   shared with the caller.
2. **The payload must name the account.** `accountId` in the body must equal the route's `{id}`.
   Otherwise answer 409 `account_mismatch`.
3. **Fresh.** The payload's `timestamp` must be within 10 minutes of the server's clock. Otherwise
   answer 409 `stale_read`.
4. **Recommended: it must look like the same account.** At least 90% of the payload's champion
   `instanceId`s must appear in the previous owner's active snapshot. A read-only viewer can list
   those ids, so this only raises the bar. Say so in the comment.
5. **The 7-day grace period is the real safeguard.** Nothing of the previous owner's is destroyed
   until it ends, and they can undo the move until then.

---

## API

### `POST /api/accounts/{id}/migrate`

The body is a consolidated payload, as for `/api/sync/consolidated/raw`. Requirements:

- Signed in, with Extractor access on this environment, as uploads require.
- The caller holds a live pin on `{id}`. Otherwise 403 `{ code: "not_shared_with_you" }`.
- Checks 2–4 above.
- No other migration of `{id}` is in its grace period. Otherwise 409 `{ code: "migration_pending",
  deletableFrom }`.
- The import counts against the daily upload limit like any upload. A 429 is answered the same way.

It does the following, **in this order**, so a failure part-way leaves the previous owner whole:

1. Imports the payload under the **caller's** uid, exactly as an upload does. The caller's copy is
   now the active one.
2. Re-owns the previous owner's documents for `{id}` by changing their `FirebaseUid` to the
   caller's: snapshots of any status (`active` becomes `backup`), their child documents, saved
   snapshots and `account_misc_data` (plans and saved inputs). It records **exactly which document
   ids moved** in a manifest, so the move can be undone precisely.
3. Writes a migration record (below) with `migratedAt = now` and `deletableFrom = now + 7 days`.
4. Deletes the caller's own pin on `{id}`. They own it now, and `GET /api/accounts` already drops
   a pin the caller also owns.
5. Sharing: the previous owner's `AccountShareSettings` are **not** transferred. Who may see an
   account is the new owner's decision. The new owner starts all-off, as every import does. Other
   viewers' pins are left to stop resolving (the existing "revoked pins are filtered, not deleted"
   rule), so they come back if the new owner shares the account again. Clan membership follows the
   import's own clan rule (`SyncClanLinkAsync`).

The response is `200 { userId, name, migratedAt, deletableFrom }`.

If the caller **already owns a copy** of `{id}`, still allow the migration: step 1 refreshes their
copy, and step 2 brings the previous owner's history in beside it.

### `POST /api/accounts/{id}/migration/undo` (new owner) and `/migration/reclaim` (previous owner)

Both work only during the grace period, and both reverse the manifest: every moved document goes
back to the previous owner with its original status. The account then reappears in the previous
owner's list as it was. Whatever the new owner uploaded themselves stays theirs, since they could
always import their own copy. After the grace period, both answer 409 `{ code: "migration_final" }`.

- `undo` is the new owner's **Give it back**.
- `reclaim` is the previous owner's way back, for an account taken by mistake or by someone who
  should not have it. It also restores the previous owner's share settings, which were never moved.

### `DELETE /api/accounts/{id}` gains one rule

While the caller's copy of `{id}` came from a migration in its grace period, answer
`409 { code: "migration_grace", message, deletableFrom }`. Do not delete anything. The previous
owner's documents are part of that account and must stay recoverable until `deletableFrom`. After
that, the delete runs as it does today.

### `GET /api/accounts` gains three fields

| Field | On | Meaning |
|-------|----|---------|
| `migratable` | shared items | `true` when this server supports migration and the caller's pin is live. The uploader decides whether to offer the action from this field alone. Never send `false` from a server that cannot migrate: omit the field. |
| `migration` | owned items | `{ migratedAt, deletableFrom, previousOwner }` while a migration into this account is in its grace period. Absent otherwise. `previousOwner` is a display name, never an email. |
| `migratedAway` | previous owner | A list entry for an account moved out during its grace period: `{ userId, name, migratedAt, reclaimableUntil, newOwner }`. It is not an account: it carries nothing to browse, only a way back. |

---

## Data

A new collection, `account_migrations`:

| Field | |
|-------|-|
| `AccountId` | in-game id |
| `FromFirebaseUid`, `ToFirebaseUid` | |
| `MigratedAt`, `DeletableFrom` | `DeletableFrom = MigratedAt + 7 days`; one setting, not a literal spread across the code |
| `Status` | `grace`, `undone`, `reclaimed`, `final` |
| `Moved` | the manifest: per collection, the document ids that changed uid |

Index on `(AccountId, Status)`. A record in `grace` past `DeletableFrom` becomes `final` either
lazily (on the next read that meets it) or in the same startup sweep `PruneOrphanShareSettingsAsync`
runs in. Keep the records as an audit trail. Do not delete them.

## Frontend (rslcompanion.com)

- **Previous owner:** a notice on the dashboard for each `migratedAway` entry: "{name} was moved to
  {newOwner}'s profile on {date}." Its one button is **Reclaim**, available until `reclaimableUntil`.
  It is informative, one sentence, the same rule as the uploader's notices.
- **New owner:** the account card shows "Moved from {previousOwner} · can be deleted from {date}"
  during the grace period. The dashboard's delete reads the 409 `migration_grace` and shows the date
  rather than a generic error. **Give it back** calls `undo`.
- The site needs no Move action of its own: only the uploader can read the game, so only the
  uploader can migrate.

## Tests worth having

- Migration without a live pin → 403. Payload id ≠ route id → 409. Stale timestamp → 409.
- After a migration, the previous owner's `GET /api/accounts` no longer lists the account as owned,
  and does list it under `migratedAway`.
- `undo` and `reclaim` restore exactly the manifest, statuses included, and leave the new owner's
  own uploads alone.
- `DELETE` during the grace period → 409 with `deletableFrom`. After it → 204, and the moved
  documents are gone.
- A failure after step 1 leaves the previous owner's documents untouched (a crash mid-move must not
  lose data). Either use a transaction, or order the writes so the manifest is written before any
  uid changes and replayed on recovery.

## Open decisions for the owner

- What `previousOwner` / `newOwner` show: the display name, or "another RSL Companion user".
- Whether the previous owner also gets an email, if the site has a mail channel.
- Whether a migration also needs check 4 (the champion overlap), or the grace period is enough.
