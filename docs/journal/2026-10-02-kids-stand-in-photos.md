# 2026-10-02 — Kids stand-in photographs (Sachith, worktree `sd-rename`)

A third file for the same day, named for the work rather than the day: `2026-10-02-sachith.md` is
W1-15's and `2026-10-02-w207-search.md` is W2-07's, and `docs/14` §5 exists because two sessions
appending to one file conflict on every merge. **`MEMORY.md` and `TASKS.md` deliberately untouched** —
`docs/14` was clarified today: the owner folds these in, a feature branch writes only its journal.

**Done:** the ten kids products borrow the nearest delivered adult garment's photograph, so the Kids
listing and the ten product pages can be seen and tested before the real kids photographs arrive
(owner decision, 2026-10-02). Branch `feature/kids-stand-in-photos`, **not pushed**: it goes to the
tester.

**In progress:** nothing else.

**Blocked:** nothing. **The real kids photographs are still owed by the client**, and that is the
point of this entry more than anything in it.

---

## What this is, and what it is not

It is **test data to unblock a section**, not a feature, and nothing presents it as finished:

- `client-progress/progress-data.js` is **not touched**. It says "Kids products still show marked
  placeholder images until those photographs are supplied", and the photographs are still
  outstanding, so that stays. See *One client-facing string to watch* below.
- No migration, no change to the catalogue's shape, no change to any kids product's name, price or
  category. One new tool, one new mapping file, one documented README section, three test files.
- Every row of the mapping grades its own match honestly, and two of the ten are recorded as a bad
  match rather than dressed up (`poor` and `none`).

## The mapping, and why each one

In one place: [`tools/kids-stand-in-photos.csv`](../../tools/kids-stand-in-photos.csv), with a reason
per row. `KidsStandInMappingTests` holds every slug in it against `DemoCatalogueData`, so a renamed
product — or an eleventh kids product — fails the build instead of quietly keeping its placeholder.

| Kids product | Borrows | Match |
|---|---|---|
| Girls Cotton Sundress | Tiered Midi Dress | fair — nearest light one-piece dress; an adult woman in frame |
| Girls Printed Tee | Oversized Graphic Tee | good — the only *printed* cotton jersey tee delivered |
| Girls Jersey Leggings | Seamless Studio Leggings | good — same garment; performance knit, not cotton jersey |
| Boys Checked Shirt | Oxford Button-Down Shirt | fair — same construction and collar; nothing checked was delivered |
| Boys Cotton Tee Two-Pack | Heavyweight Crew Tee | good — same garment and fabric; one tee, not two |
| Boys Pull-On Shorts | Linen Drawstring Shorts | good — the pull-on closure matches; linen, not cotton blend |
| Baby Ribbed Bodysuit Set | Ribbed Scoop-Neck Tee | **poor** — only the rib carries over; no babywear was delivered |
| Baby Muslin Bib Set | Everyday Canvas Tote | **none** — nothing is close; a flat cotton accessory, not a garment on an adult |
| Kids Canvas Sneakers | Court Canvas Sneakers | exact — same shoe, same materials |
| Girls Denim Pinafore | Denim Wrap Mini Skirt | fair — washed denim, short A-line; no bib and no straps |

The `none` row is the honest answer and is left in rather than left out: with it the Kids listing has
no marked placeholder on it at all, which is what the decision asked for, and the CSV says in its own
words to treat that row as a gap.

## Decisions

- **One asset, two usages — not a copy, and not a code path around the index.**
  `UX_MediaAssets_Checksum_Ready` (W1-04) means the same bytes cannot become a second asset however
  many filenames carry them, and that is correct. So the stand-in is the *same* `media.MediaAssets`
  row with a second `media.MediaUsages` row and a second `catalog.ProductMedia` row — which is what
  doc 08 §3.3 and `UX_MediaUsages_Asset_Owner_Role` are for. Measured on a real import: **zero new
  assets and zero new files** for all twenty stand-in files.

- **A second drop, not more files in the client's.** `tools/kids-stand-in-photos.ps1` builds a
  throwaway drop (`.local-kids-stand-ins/`, git-ignored) of byte-copies under kids filenames and
  imports *that*. Two reasons. It keeps borrowing an operator's own act rather than something the
  client's delivery does — so
  `ClientAssetImportTests.Kids_products_keep_their_placeholders_rather_than_borrowing_an_adult_photograph`
  goes on meaning exactly what it says, unchanged and still green. And a drop is already a
  first-class concept with its own `--from`, so nothing new had to be invented.

- **The order of the two imports is structural, not documentary, and that cost a test to prove.**
  Alt text lives on the **asset**, and the pipeline answers a repeat upload with the asset it already
  has — so whichever import *creates* the asset writes the sentence both products afterwards read
  out. Run the stand-ins first and the shared asset carries "Girls Cotton Sundress, front view", and
  the women's dress reads that out too: a false sentence on a page the client has already signed off.
  The script therefore runs `import-client-assets` over the client's delivery first (idempotent, so
  it costs nothing) and over the stand-ins second.
  `KidsStandInPhotoTests.Importing_the_stand_ins_first_puts_the_kids_name_on_the_donors_photograph`
  asserts the bad outcome deliberately: the day the importer stops depending on the order, that test
  fails and the first step can go.

- **Alt text names the adult garment, and that is the honest answer.** A girls' sundress card reads
  "Tiered Midi Dress, front view". Four reasons, in order of weight:
  1. It is **true of the photograph**, which is all alt text promises. "Girls Cotton Sundress" would
     be false.
  2. There is **only one sentence available**. Alt text is a column on the shared asset, so writing
     the kids name would put it on the *women's* product too.
  3. ~~An override would make the listing card and the product page disagree.~~ **Struck — this was
     wrong, and the tester caught it.** `product-card.ts:91` and `:105` hard-code `alt=""` on both the
     card and the hover image, inside an `aria-hidden="true"` anchor, because the photograph repeats
     the link on the product name. That is the correct decorative-image treatment and has nothing to
     do with this change — but it means the asset's sentence **never reaches the listing at all**
     (measured: 0 distinct non-empty `alt` values on the whole `/kids` page), so the listing and the
     product page cannot disagree. The honest summary is **disclosed on the product page, silent on
     the listing**, not "the same sentence everywhere".
  4. Decisively, and the only structural argument left: **`PlaceProductMedia` does not clear
     `AltTextOverride` when a photograph replaces another.** An override written now would survive the
     real photographs arriving and describe the wrong garment for ever — exactly the archaeology this
     task exists to prevent. The tester verified this line by line rather than taking it.

  The mismatch between "Girls Cotton Sundress" in the heading and "Tiered Midi Dress" in the alt text
  is therefore the **disclosure on the product page**, and it costs nothing to remove. There is a
  second one for free: the seeded placeholder frames behind the card and hover (sort orders 1..4,
  colour-scoped, untouched by the import) still read "Placeholder image — no photograph of Girls
  Cotton Sundress in Blush Pink has been supplied yet", exactly as every women's and men's product's
  do since W1-13. The gallery says the photographs are outstanding in its own words.

- **The reversal is `--replace`, which already existed.** Real kids photographs go into the client's
  own drop as `<kids-slug>-1.jpg` / `-2.jpg` and `import-client-assets --from <drop> --replace` puts
  them in. Without `--replace` the run lists the kids slots under *NOT APPLIED*, exits **3** and
  changes nothing **a shopper sees** — but the photographs do enter the library: the bytes go through
  the pipeline before the slot is consulted, so a refused run leaves 2 assets and 8 files stored with
  no usage row pointing at them (956 → 964, measured). Litter rather than loss, and nothing purges it
  automatically. I had written "changes nothing", which was true of the slots and false of the library;
  the test now asserts both halves.
  `The_real_photographs_replace_a_stand_in_and_leave_the_donor_alone` asserts the refusal *and* the
  replacement, and that the superseded stand-in leaves the kids gallery rather than sitting beside the
  real photograph.

- **Deleting a product's row from the CSV is tidiness, not a guard.** The tester checked what happens
  if the row is left in and the script re-run with the real photographs already in place: exit 3 and a
  Conflict on every kids slot. The protection is the importer's conflict rule, which is stronger than
  my README implied, and the README now says so.

## Found: the import report asserted alt text it had not written

**Fixed, and it is not cosmetic.** `ImportProductsAsync` printed the alt text it *offered* for every
file — `alt text "Girls Cotton Sundress, front view"` — regardless of what the library actually
stored. For a photograph already in the library the pipeline answers `Duplicate` and keeps the earlier
sentence, so the report told the operator a sentence that is nowhere in the database, about the one
thing on this task that most needs to be checked. Before the stand-ins this could only happen for two
identical files inside one drop, where the line said something else anyway; now it is the normal case.

`StoredAsset` carries the sentence the library holds, `FindByChecksumAsync` reads it for a dry run,
and the report says which sentence was kept and which was not written:

```
[Imported] girls-cotton-sundress-1.jpg -> girls-cotton-sundress — the card image,
    alt text "Tiered Midi Dress, front view" — the sentence already on this photograph, kept rather
    than overwritten with "Girls Cotton Sundress, front view", because it is one asset and one
    sentence, shared with every product that shows it
```

## One client-facing string to watch (not changed, and settled)

`client-progress/progress-data.js` line 135: *"Kids products still show marked placeholder images
until those photographs are supplied."*

**It stays exactly as it is, and the reason is better than the one I first gave.** I argued it was
"false on an environment where the script has been run". The tester's reason is the right one: that
line sits inside `updates: [` in a **dated entry** describing the delivery landing — it is **history,
not status** — and editing a dated note because somebody later ran a local test-data script would
falsify the log. The Kids rows in `features` claim nothing about photography, so nothing there is
wrong either.

Also established, and worth having written down: **no production code reads the mapping CSV at all**,
the seeder never applies the stand-ins, and `import-client-assets` is a CLI verb that never starts the
web host — so no environment the client is shown can carry stand-ins unless somebody deliberately ran
the script there. If that ever happens the answer is a **new dated entry**, not an edit to this one.

## Tester round 1: FAIL on 2 majors and 5 minors, all fixed

Rebased onto `origin/main` (`f96f9b8`) by the owner first, so W2-09/CART-01/02 is underneath this now.

- **Major — `-Out` deleted whatever was at that path, recursively, with no guard.** The one
  destructive operation on the branch, on an operator-supplied path, and `-ClientAssets` is the
  adjacent parameter an operator always types. The stakes are the part I had not thought through:
  **`client-assets/` is untracked in every repository here**, so it is the project's only copy of the
  delivered photographs, and one transposed argument destroyed it with no prompt and exit 0. It was
  below the standard of the file it shipped beside — `ClientAssetImporter` refuses to overwrite "a
  picture somebody chose" and exits 3 rather than guess, under its own comment that "a missed file is
  litter; a deleted file is data loss". `Assert-SafeOutputFolder` now refuses a filesystem root, the
  repository, anything containing or contained by `-ClientAssets`, and any existing folder that is not
  wholly a generated drop of this mapping's own files — "everything here is mine", not "something here
  is mine". `-Remove` goes through the same gate. Eight tests in
  `KidsStandInDropGuardTests`, driving the real script through `pwsh` against decoy folders under the
  test's own temp directory; **six of them failed before the guard existed**, including the delivery
  being deleted outright.

- **Major — the first dry run stated the exact false sentence this task's central decision exists to
  prevent.** `-DryRun` passes `--dry-run` to *both* steps, so step 1 commits nothing, step 2's
  `FindByChecksumAsync` cannot find the donor's bytes, and the old `stored.AltText ?? alt` fell back to
  the offered kids sentence. On a freshly seeded database — exactly where the documented order starts
  an operator — all 20 lines read `alt text "Girls Cotton Sundress, front view"` as fact. A plan that
  cannot know has to say so, which is the rule this command already applies to refusals ("0 refused"
  means NOT CHECKED). The line is now marked `PROVISIONAL` with the reason, and `-DryRun` says the same
  thing in prose at the end.

- **Minor — a `Conflict` still carried the "kept rather than overwritten" clause**, claiming a sentence
  had been preserved on a slot the run had just declined to touch. `altNote` was built before the
  outcome was consulted; the detail is now an outcome `switch` and `Conflict` reports the slot only.

The other three minors are corrections to things I wrote, fixed where I wrote them: reason #3 of the
alt-text decision (struck — see above), "exits 3 and changes nothing" (false of the library), and the
menswear count. The alt-text decision itself stands, and the tester verified its decisive argument
directly rather than accepting it.

### Worth knowing: a stale build made a test lie to me, twice

Chasing the `Conflict` fix I "restored" the patched importer with `Copy-Item`, which **preserves the
source file's LastWriteTime** — so the restored file was older than the compiled DLL, `dotnet build`
reported success without recompiling, and `dotnet test --no-build` ran the *old* code. I spent several
minutes reading correct source that could not possibly produce the output I was seeing. Dumping the
actual report entries settled it in one run. If you swap a file in or out to compare behaviour, `touch`
it afterwards, or build with `--no-incremental`; and when source and behaviour disagree, print the
behaviour before re-reading the source.

## Also found, not mine, not touched

- **`SpencerDenim_W113_04a380124da34326a6efb0a6ebf74cee` is still on LocalDB**, created
  2026-10-01 19:19 by the W1-13 session. `ClientAssetWorld` names its throwaway databases
  `SpencerDenim_W113_*` rather than `SpencerDenim_Test_*`, so a sweep for the documented prefix does
  not find it. This session's own five were created and dropped cleanly.
- **The storefront e2e servers still sit on fixed ports 4420–4429 and 4432–4433** while the admin
  one derives a port per worktree. `playwright.config.ts` says so itself and says it is not that
  task's job to move them; it is not this one's either.
- **The brief for this task called the delivered photographs "the womenswear photographs".** The
  delivery is men's and women's and accessories (`SD-M-*` and `SD-W-*`), and the split is **five
  menswear and five womenswear**, not four and six as I first reported — men's are
  `oversized-graphic-tee`, `oxford-button-down-shirt`, `heavyweight-crew-tee`,
  `linen-drawstring-shorts` and `court-canvas-sneakers`. The substance of the correction stands and the
  menswear matches are the better ones: a boys' checked shirt on a men's oxford and a boys' tee on a
  men's crew tee are closer than anything in womenswear.

**Next:** tester. Nothing here is Done until it passes.
