# Attribute a champion's skills to the right form, at the right ascension

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

## Why the residual exists

`champions{}` carries **one entry per champion**, holding the **max-ascension** kit. That is
deliberate: it is what "this champion's skills" means to a reader, and it is the complete kit, since
skills *unlock* with ascension.

But ascension does not only add. It also **replaces**. A copy sitting below max ascension can hold a
skill id that does not appear anywhere in the max-ascension kit, and that skill attributes to nothing:

```
Apothecary  (base 30)     asc0–2: 301, 302, 303      asc3–6: 301, 302, 304
Abbess      (base 2310)   asc0–2: 23101, 23102, 23104  asc3–6: 23101, 23102, 23103
Skavag      (base 23100)  asc0:   …, 231002, …        asc1+:  …, 231005, …
                          asc6 additionally gains 200009, 200010
```

**374 of the 1,355 champions** in the 11.70.0 catalog have at least one skill that exists below max
ascension and is gone by max ascension. For the other 981 the max-ascension kit is a superset, so the
`champions{}` lookup is already correct for them.

## The fix

`types[]` carries **every ascension variant separately**, keyed by `typeId`, with its own `forms[]`.
Attribute against the variant matching the copy's own ascension, and keep `champions{}` as the
fallback:

```ts
// heroes[].baseTypeId + heroes[].ascensionLevel identify the exact variant.
const variantTypeId = hero.baseTypeId + hero.ascensionLevel;

const forms =
  catalog.types?.find(t => t.typeId === variantTypeId)?.forms
  ?? catalog.champions[String(hero.baseTypeId)]?.forms
  ?? [];

const form = forms.find(f => f.skillTypeIds.includes(skill.typeId));
```

The fallback is not decoration. A variant can be missing from `types[]` (the catalog only sees what
the client had loaded), and `types` is absent entirely from one of the two published catalog shapes.

## Which catalog file — this part matters

The catalog ships in **two shapes from the same extraction**:

| Shape | `champions{}` | `types[]` | Where |
| --- | --- | --- | --- |
| **slim** (~0.5 MB) | yes | **no** | bundled in the uploader installer, `{app}\exports\champion_index.json` |
| **full** (~4.5 MB) | yes | yes | `RslCompanionMetadata/exports/champion_index.json` |

`types[]` is 3.98 MB of the 4.47 MB full catalog and nothing in the uploader reads it, so the
installer ships the slim shape. **RaidTools needs the full one** — take it from the metadata repo,
not from an uploader install.

A missing `types` key means *"this file cannot answer per-ascension questions"*, **not** *"this
champion has no ascension variants"*. Do not silently degrade to the `champions{}` answer for a
catalog that simply is not the right file; log it, because every below-max copy will be misattributed
and the output still looks plausible.

## Traps

- **Do not derive form membership arithmetically.** `typeId === baseTypeId * 10 + slot` holds for
  3,027 of 3,082 skills and then quietly fails: a champion with a second form also carries that
  form's whole block at `800000 + own id`, and a skill can sit in another champion's block outright
  (Ezio Auditore `10270` and Edward Kenway `10280` both carry `102505`). Membership in
  `forms[].skillTypeIds` is the only correct test.
- **Slots are not dense.** A real, complete Kael reports `15101 / 15103 / 15104`. A gap is not a
  missing skill.
- **Base ids and skill ids look alike and are different id spaces.** Skavag's *champion* base id is
  `23100`; Abbess's first *skill* id is `23101`. Never compare across the two.
- **`forms[].role` is nullable and `null` ≠ `0`** — `0` is Attack, so the field has no spare sentinel.
- **`skills[].level` is 1-based** (1 = un-upgraded), and the per-skill cap is not in the payload *or*
  in this catalog. Do not render "maxed" from `level` alone.

## Regenerating after a game update

Both shapes come out of one run of `tools/ChampionIndexExporter` in the metadata repo, with Raid
running and the champion **index** screen opened. Generating them separately is what would let them
drift, so don't. See that tool's README.
