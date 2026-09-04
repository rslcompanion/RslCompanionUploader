# Attribute a champion's skills to the right form, at the right ascension

> **Superseded for schema ≥ 20 payloads, and still required for older ones — read this line before the
> rest of the file.** Since schema 20 the uploader resolves the answer itself and every skill arrives
> carrying `formIndex`, so on a current payload none of the work below is needed: read the field. What
> keeps this note alive is that uploader installs update **opt-in**, so pre-20 payloads keep arriving
> from older installs indefinitely, and for those the join described here is still the only way to get
> the split. The correct consumer shape is therefore `skill.formIndex ?? <the lookup below>`, and
> **`?? 0` is wrong** — `0` is the base form, a real answer, so absent must stay absent.
> See `export-schema.md` → `champions[].skills` for the producer's side of this.

Consumer-side note for **RaidTools**, written to be handed straight to an agent: paste it into a
Claude Code session opened on that repo.

It is a *summary*. [`export-schema.md`](export-schema.md) / [`.json`](export-schema.json) are the
contract for the payload side; where this file disagrees with them, they win, and this one is stale
and should be fixed. The champion catalog is not part of the payload contract at all — see
"Which catalog file" below.

---

## The problem

An owned copy reports `heroes[].skills[] = [{typeId, level}, …]` and nothing else about what those
skills *are*. Names, caps and form membership come from the champion catalog, joined on `typeId`.

Attributing a copy's skills to a form using `champions{}[baseTypeId].forms[]` works for **97.4%** of
owned skills (2,720 of 2,792 on the reference account, 2026-08-09). The residual 2.6% are not noise
and not a bad read — they are a structural mismatch, and this note is how to close it.

> **Updated 2026-08-12 for the reshaped catalog.** The fix below used to be a lookup into a
> `types[]` array of per-ascension variants. That array is retired and the answer now lives on the
> skill itself. If you are reading a file that still has `types[]`, it predates 2026-08-11 — get a
> current one. The old `skillTypeIds[]` and the current `skills[]` are not interchangeable.

## Why the residual existed

`champions{}` carries **one entry per champion**. It used to hold only the **max-ascension** kit,
on the reasoning that skills *unlock* with ascension and the top kit is therefore complete.

That reasoning is wrong, and it is the whole problem: ascension does not only add, it also
**replaces**. A copy sitting below max ascension can hold a skill id that does not appear anywhere
in the max-ascension kit, and that skill attributes to nothing:

```
Apothecary  (base 30)     asc0–2: 301, 302, 303      asc3–6: 301, 302, 304
Abbess      (base 2310)   asc0–2: 23101, 23102, 23104  asc3–6: 23101, 23102, 23103
Skavag      (base 23100)  asc0:   …, 231002, …        asc1+:  …, 231005, …
                          asc6 additionally gains 200009, 200010
```

**336 of the 1,034 playable champions** have at least one skill that exists below max ascension and
is gone by max ascension. For the other 698 the max-ascension kit is a superset, so the
`champions{}` lookup is already correct for them.

The swap is at ascension 3 for 335 of the 336 — but **not all of them**, so do not special-case the
number 3. Deathless (1590) keeps `15903` through ascension 3 and gains `15904` at 4. Evaluate the
range; that is what it is for.

(Older notes here said "374 of 1,355, the other 981". That counted bosses in — 336 playable plus 38
bosses — from before `boss_index.json` split them out. Against the roster this document is about,
which is the playable one, it is 336 and 698.)

## The fix

Each skill now carries **the span of ascensions it is actually on the champion for**, so the
champion's own row answers every ascension and there is no second lookup:

```jsonc
"skills": [
  { "typeId": 301, "fromAscension": 0 },                    // whole life
  { "typeId": 303, "fromAscension": 0, "toAscension": 2 },  // replaced at asc 3
  { "typeId": 304, "fromAscension": 3 }                     // the replacement
]
```

`toAscension` absent means "through max ascension", which is the common case.

```ts
const forms = catalog.champions[String(hero.baseTypeId)]?.forms ?? [];

const isActiveAt = (s, asc) =>
  s.fromAscension <= asc && (s.toAscension == null || s.toAscension >= asc);

const form = forms.find(f =>
  f.skills.some(s => s.typeId === skill.typeId && isActiveAt(s, hero.ascensionLevel)));
```

**Match on the range, not just on membership.** Dropping `isActiveAt` and testing `typeId` alone
re-creates the original bug in the other direction: it credits an un-ascended copy with the
post-swap skill it does not have yet. Both halves of a swapped pair are in the list — that is the
point of the list — and only the range separates them.

It also cannot be done arithmetically. The upgraded id is *usually* the higher one and sometimes is
not: Abbess goes `23104` → `23103`.

## Which catalog file — this part matters

**There is one file and one shape.** The slim/full pair this section used to describe is gone:
`types[]` was 89% of the old catalog, folding it away made the single file smaller than the old
slim copy, and the `--slim-out` flag that wrote the second one was deleted with it.

| File | Holds | Where |
| --- | --- | --- |
| `champion_index.json` | 1,034 **playable champions** | produced in `RslCompanionMetadata/exports/`; a verbatim copy is bundled at `{app}\exports\champion_index.json` |
| `boss_index.json` | 321 bosses + location-only entries | `RslCompanionMetadata/exports/` only — **not** in the uploader install |

Take either from the metadata repo; the uploader's copy is a copy, refreshed by copying. The two
files never share a key, and bosses are separate because nothing in a roster can own one — see
`RslCompanionMetadata/docs/champion-index-contract.md`.

A file that still carries a `types[]` array predates the reshape. Do not read it as "this champion
has no ascension variants" — it means the file cannot answer the question at all. Log it, because
every below-max copy will be misattributed and the output still looks plausible.

## Traps

- **Do not derive form membership arithmetically.** `typeId === baseTypeId * 10 + slot` holds for
  3,027 of 3,082 skills and then quietly fails: a champion with a second form also carries that
  form's whole block at `800000 + own id`, and a skill can sit in another champion's block outright
  (Ezio Auditore `10270` and Edward Kenway `10280` both carry `102505`). Membership in
  `forms[].skills[]`, at the copy's ascension, is the only correct test.
- **Slots are not dense.** A real, complete Kael reports `15101 / 15103 / 15104`. A gap is not a
  missing skill.
- **Base ids and skill ids look alike and are different id spaces.** Skavag's *champion* base id is
  `23100`; Abbess's first *skill* id is `23101`. Never compare across the two.
- **`forms[].role` is nullable and `null` ≠ `0`** — `0` is Attack, so the field has no spare sentinel.
- **`skills[].level` is 1-based** (1 = un-upgraded), and the per-skill cap is not in the payload *or*
  in this catalog. Do not render "maxed" from `level` alone.

## Regenerating after a game update

`champion_index.json` and `boss_index.json` come out of **one run** of `tools/ChampionIndexExporter`
in the metadata repo, with Raid running and the champion **index** screen opened. Generating them
separately is what would let them drift, so don't. Every copy elsewhere is refreshed by copying that
output, never by a separate run. See that tool's README.
