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
  3. `ProductMedia.AltTextOverride` cannot fix that. `ProductCatalogReader` takes a **card's** alt
     text straight from `CardImageAsset.AltText` with no override to consult (line 538), so an
     override would make the listing card and the product page disagree about one picture — which is
     W1-13's stated reason for leaving it null.
  4. Decisively: **`PlaceProductMedia` does not clear `AltTextOverride` when a photograph replaces
     another.** An override written now would survive the real photographs arriving and describe the
     wrong garment for ever — exactly the archaeology this task exists to prevent.

  The mismatch between "Girls Cotton Sundress" in the heading and "Tiered Midi Dress" in the alt text
  is therefore the **disclosure**, it costs nothing to remove, and it is the same sentence everywhere.
  There is a second one on the page for free: the seeded placeholder frames behind the card and hover
  (sort orders 1..4, colour-scoped, untouched by the import) still read "Placeholder image — no
  photograph of Girls Cotton Sundress is supplied", exactly as every women's and men's product's do
  since W1-13. The gallery says the photographs are outstanding in its own words.

- **The reversal is `--replace`, which already existed.** Real kids photographs go into the client's
  own drop as `<kids-slug>-1.jpg` / `-2.jpg` and `import-client-assets --from <drop> --replace` puts
  them in. Without `--replace` the run lists the kids slots under *NOT APPLIED*, exits **3** and
  changes nothing, because a stand-in is a picture somebody chose and the importer cannot tell it from
  an admin's upload. `The_real_photographs_replace_a_stand_in_and_leave_the_donor_alone` asserts the
  refusal *and* the replacement, and that the superseded stand-in leaves the kids gallery rather than
  sitting beside the real photograph.

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

## One client-facing string to watch (not changed, reported)

`client-progress/progress-data.js` line 135: *"Kids products still show marked placeholder images
until those photographs are supplied."* **True about the delivery and about any environment where the
script has not been run; false about one where it has.** The stand-ins are applied only by a manual
command, never by the seeder and never at startup, so a client looking at a staging site that has had
the script run would see kids products with adult photographs under a page saying placeholders.
Changing it is the owner's call and would mean telling the client their kids photographs are no longer
needed, which is the opposite of true — so the line is left exactly as it is and flagged here.

## Also found, not mine, not touched

- **`SpencerDenim_W113_04a380124da34326a6efb0a6ebf74cee` is still on LocalDB**, created
  2026-10-01 19:19 by the W1-13 session. `ClientAssetWorld` names its throwaway databases
  `SpencerDenim_W113_*` rather than `SpencerDenim_Test_*`, so a sweep for the documented prefix does
  not find it. This session's own five were created and dropped cleanly.
- **The storefront e2e servers still sit on fixed ports 4420–4429 and 4432–4433** while the admin
  one derives a port per worktree. `playwright.config.ts` says so itself and says it is not that
  task's job to move them; it is not this one's either.
- **The brief for this task called the delivered photographs "the womenswear photographs".** The
  delivery is men's and women's and accessories (`SD-M-*` and `SD-W-*`), and four of the ten
  stand-ins are **menswear** — which is better, not worse: a boys' checked shirt matched to a men's
  oxford and a boys' tee to a men's crew tee are closer matches than anything in womenswear.

**Next:** tester. Nothing here is Done until it passes.
