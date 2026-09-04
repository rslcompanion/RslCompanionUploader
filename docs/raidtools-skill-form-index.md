# Read `skills[].formIndex` from the payload instead of deriving it

Consumer-side prompt for **RaidTools**, written to be handed straight to an agent: paste it into a
Claude Code session opened on that repo.

It is a *summary*. [`export-schema.md`](export-schema.md) / [`.json`](export-schema.json) are the
contract for the payload side; where this file disagrees with them, they win, and this one is stale
and should be fixed.

---

## What changed on the producer

Schema 20 (uploader v1.18.0) adds one field to every skill on every owned copy:

```jsonc
"skills": [
  { "typeId": 86301,  "level": 6, "formIndex": 0 },
  { "typeId": 886301, "level": 5, "formIndex": 1 }
]
```

`formIndex` is which of the champion's forms the skill is on — `0` the base form, `1` a
transformation's second form — matching `forms[].index` in `champion_index.json`. It is **absent,
never `0`, when the producer could not resolve it**.

The uploader resolves it from the live game process: a copy's `Hero._type` points at that copy's own
**ascension variant** of the champion, so `HeroType.Forms[].SkillTypeIds` read off it is already the
kit that copy actually has. Copies whose shared `HeroType` the client never hydrated fall back to the
bundled champion catalog evaluated at the copy's own `ascensionLevel`. On a real 915-champion roster
that closed the split for 3,005 of 3,005 skills.

## Why RaidTools should switch to it

RaidTools already derives this, correctly, in
`RaidTools.Api/Controllers/RaidApiControllers.cs` — `GetChampionCopies`, the block commented *"A
transformation champion's two forms arrive as ONE flat skill list on the copy"*. That derivation is
the thing this change makes redundant on current payloads, and there are three reasons to prefer the
field:

1. **The producer's answer cannot be behind the game.** RaidTools' derivation reads
   `ChampionIdentity.SkillsAtThisAscension`, off a `champion_index.json` revision that is refreshed on
   its own schedule. The uploader reads the running client. When the two disagree — a champion or a
   reworked kit newer than the metadata revision — the payload is right.
2. **It answers for single-form champions too.** The current block is gated on `formCount > 1` and
   leaves `FormIndex` null everywhere else, which is correct-but-silent: a consumer cannot tell "form
   0" from "nobody said". The payload states `0` on every ordinary champion.
3. **It removes a catalog read from a request path.** `GetChampionCatalogAsync` is fetched in that
   endpoint *only* for the form split (the comment says so); once the field is stored, that read is
   needed only for the pre-schema-20 fallback.

**Do not delete the derivation.** Uploader installs update opt-in, so pre-20 payloads keep arriving
from older installs indefinitely — the same reason the `payload.champions ?? payload.heroes` fallback
outlived the producer-side removal of `heroes[]`. The target shape is *payload first, catalog second*.

## The change, end to end

There are four touch points. The stored shape is the wire shape verbatim, so most of this is
plumbing one property through.

1. **`RaidTools.Api/Dtos/RaidDtos.cs` — `ChampionSkillDto`.** Add:

   ```csharp
   /// <summary>
   /// Which of the champion's forms this skill is on, from schema 20 onward: 0 the base form, 1 a
   /// transformation's second form, matching forms[].index in champion_index.json.
   ///
   /// NULL, NEVER 0, when the payload did not carry it — a pre-schema-20 uploader, or a copy the
   /// producer could not resolve. 0 is the base form, a real answer, so this field has no spare
   /// sentinel: coalesce with the catalog derivation, never with 0.
   /// </summary>
   [JsonPropertyName("formIndex")]
   public int? FormIndex { get; set; }
   ```

   `SyncManager.SerializeSkills` serializes the DTO list wholesale, so this reaches
   `ChampionDocument.SkillsJson` with no further change. Confirm the serializer is not configured to
   drop nulls in a way that matters — an absent property and a `null` one must both read back as
   "not stated".

2. **`RaidTools.Api/Services/SkillBookProgress.cs` — `Build`.** It parses `SkillsJson` property by
   property; read `formIndex` the same way `level` is read, and set it on the `SkillProgressDto` it
   constructs. Absent property → leave `FormIndex` null. The XML doc on `SkillProgressDto.FormIndex`
   currently says *"Not computed here … the caller that has the catalog stamps it"* — that sentence
   becomes wrong and must be rewritten: it is now read here when the feed states it, and stamped by
   the caller only when the feed did not.

3. **`RaidTools.Api/Controllers/RaidApiControllers.cs` — `GetChampionCopies`.** Turn the existing
   block into the fallback:

   - Skip the catalog work entirely when **every** skill on the copy already has a `FormIndex`. That
     is the common case on a current payload, and it is what removes the catalog read.
   - Otherwise run the existing derivation, but apply it **only to skills whose `FormIndex` is null**.
     Never overwrite a value the payload stated: the payload read the running client and the catalog
     revision may not have.
   - Keep the `formCount > 1` gate on the *fallback* only. Do not extend the derivation to
     single-form champions to make it match the payload — that is inventing an answer the old feed
     did not give. A pre-20 payload legitimately leaves form unstated there.

4. **The frontend.** Anywhere the skill list is grouped or labelled by form, `formIndex == null` must
   render as ungrouped/unknown, not as form 0. Search for existing `formIndex` consumers and check
   each for a `?? 0`, `|| 0`, or a truthiness test — `formIndex` of `0` is falsy in JavaScript, which
   is the specific bug this field's nullability invites. Use `formIndex ?? null` and `!= null` tests.

## Verification

- **A schema-20 payload:** every skill on a multi-form champion (Alaz the Sunbearer, base `8630`,
  reports `86301…86305` and `886301…886305`) comes back split, and the response is identical to what
  the catalog derivation produced before the change. That equivalence is the actual test — if the two
  disagree on a champion, the payload is right and the metadata revision is stale, which is worth a
  log line rather than a silent preference.
- **A pre-schema-20 payload** (any stored snapshot from before this ships): behaviour is unchanged —
  multi-form champions still split via the catalog, single-form champions still report null.
- **A copy the catalog cannot place at all** stays unplaced in both paths. It must never land in form 0.

## Traps

- **`0` is a real form and JavaScript disagrees.** `if (skill.formIndex)` is false for the base form.
  This is the same trap `roleId` carries (`0` is Attack), and it is now in a second place.
- **Do not derive form membership arithmetically.** `typeId === baseTypeId * 10 + slot` holds for
  3,027 of 3,082 skills and then quietly fails — a champion with a second form carries that form's
  whole block at `800000 + own id`, and a skill can sit in another champion's block outright (Ezio
  Auditore `10270` and Edward Kenway `10280` both carry `102505`).
- **The catalog fallback must evaluate the ascension span**, at the copy's own `ascensionLevel` —
  `ChampionIdentity.SkillsAtThisAscension` already does this and must keep doing it. The full
  reasoning, and why a plain membership test is wrong in both directions, is in
  [`raidtools-skill-attribution.md`](raidtools-skill-attribution.md).
- **Levels are still 1-based and caps are still not in the payload.** Nothing about this change makes
  "maxed" derivable without the skill catalog.
