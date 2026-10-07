# 2026-10-07 — W2-11: content pages and the contact enquiry (GLB-10)

Branch `feature/W2-11-content-pages`, cut from `origin/main` at `006a0c9`. Not pushed.

## Done

**The six system pages, and they ship unpublished with empty bodies.** `content.ContentPages` and
`content.ContactSubmissions` exist as doc 08 §3.7 shapes them, in one migration
(`20261007055218_ContentPagesAndContactSubmissions`). The seeder writes six shells keyed on
`SystemKey` — About, Contact, Returns, Shipping, Privacy, Terms — unpublished, with empty bodies and
no `PublishedAtUtc`. **Nothing in this repository writes their copy.** Doc 11 P13 is explicit that it
is the client's, and Privacy and Terms need a PDPA review; a seeded placeholder policy would put words
the client never agreed to on their own shop. `ContentPageEndpointTests` asserts the emptiness, so a
later change that "helpfully" fills them in goes red.

**The sanitiser.** `IHtmlSanitiser` (Application port) and `HtmlSanitiser` (Infrastructure, over
`HtmlSanitizer`/AngleSharp — MIT, parser-based). Allow-list in both directions; the exact list is in
the type's own documentation and in `HtmlSanitiserTests`, which is 91 table-driven cases over real
payloads. It reports what it removed, and the editor is told even on a successful save.

**The storefront.** One `:slug` route behind a guard over the published-page index, with #30's three
verdicts: known → the page, live-and-absent → a real 404, index-never-read → 503 with `Retry-After`.
The body is bound with `[innerHTML]` and never `bypassSecurityTrustHtml`, so Angular's sanitiser runs
over the already-cleaned markup a second time. The Contact page carries the form, the hours, the
address, the map and WhatsApp, all from Settings, identified by `systemKey` so a renamed slug keeps
them. The footer links a policy page only once it is published.

**The admin.** Content pages (list, editor, publish, remove) under `Content.View/Create/Edit/Delete`;
the enquiry inbox under `Customers.View/Edit/Delete`, which is doc 08 §3.7's G-06 resolution — a
Content Editor can rewrite the Contact page and cannot read a single enquiry.

**ADM-22 part 2.** `Contact.Phone/Email/Address/OpeningHours/MapEmbedUrl` join the existing
Contact+Social save unit, replacing the TODO on `DEFAULT_SITE_CONFIG`. The map URL is checked against a
five-host allow-list on write, on read and again in the browser, because the storefront frames it.

## Four real defects this found

1. **The audit trail's body description was the change detection.** `ContentAuditTrail` describes a page
   body by its character count, and the first version put that description into the snapshots the diff
   reads. Two bodies of the same length produced no diff, so the handler concluded nothing had changed
   and issued no statement — after assigning the new body to the entity. The editor's copy was thrown
   away and the screen said "Saved". Found on the first run of the absent-field test the brief asked
   for, whose fixture happened to swap `<p>Old copy.</p>` for `<p>New copy.</p>`: sixteen characters
   either way. The comparison is now on real values and the redaction happens on the way into the row.

2. **Every enquiry deletion answered 500.** `ContactEnquiryStore` opened a user-initiated transaction,
   and the context is configured with `EnableRetryOnFailure` — a retrying execution strategy refuses one
   outright. Now wrapped in `CreateExecutionStrategy().ExecuteAsync`, as `CartStore`,
   `NumberSeriesAllocator`, `ProductWriteStore` and `StaffUserRoleManagementStore` all are. **A unit
   test could not see this**: a fake store has no execution strategy.

3. **The page body was fetched twice and the second fetch could destroy the first.** The index was
   handed over to the browser; the body was not. So hydration re-fetched it, and a failed second fetch
   renders the missing state — a transient blip replaces a policy page the shopper has just read with
   "We could not find that page". This is `ServerHandover`'s own recorded lesson, one feature over. Now
   handed over per slug, with an e2e asserting the browser makes no second call.

4. **The body's styles could never reach it.** Content injected with `[innerHTML]` carries no
   `_ngcontent` attribute, so every `.page__body table` rule in the component matched nothing and the
   body rendered unstyled. Moved to the global sheet's `.rich` block — which already said "GLB-10
   content pages next" for exactly this reason.

Also: `queueMicrotask` was too early to re-home focus (a signal write schedules change detection as a
task, so the element did not exist yet and focus stayed on `<body>` — the harm the re-homing exists to
prevent); the `:slug` routes needed `pathMatch: 'full'` or `/about/x` prefix-matched and ran the content
guard for every deeper address on the origin.

## Tests

**Measured on the merged tree, after the rebase onto W3-03 (`c8b8c81`).** The figures include bank
transfer's own tests, which is why they are higher than this branch alone; the pre-rebase numbers were
unit 1,692 · integration 1,060 · Vitest 1,397/385/24 · Playwright 867.

| Suite | Result |
|---|---|
| xUnit unit | **1,726 passed**, 0 failed, 8 s |
| xUnit integration, real SQL Server | **1,124 passed**, 0 failed, 13 m 38 s |
| Vitest storefront | **1,404 passed** (54 files) |
| Vitest admin | **407 passed** (32 files) |
| Vitest shared | **24 passed**, unchanged |
| Playwright, whole suite | **891 passed, 0 failed** at the committed `workers: '25%'` on an idle machine, 6.2 min. (Measured once at 866/1 while the SQL suite was running beside it — `search.e2e.ts`'s suggestions overlay, which then passed **246 of 246** on its own at `--repeat-each=3 --workers=1`. That is the load sensitivity the cap is about, and the reason not to measure the browser suite and the database suite at the same time.) |

New: **138** sanitiser cases, 50 content-page write cases, 35 contact-enquiry cases, 14
system-page-copy cases, 11 reserved-slug
cases (which read `app.routes.ts`, so the refusal list cannot drift from the route table), 10
admin-model shape cases, 28 integration cases, 27 browser cases in `content-pages.e2e.ts`, and 13
rewritten in four existing browser specs. Of the sanitiser's 138, **105 are the payload table** — not
the 108 an earlier version of this entry claimed, which the tester counted correctly — 16 are the nesting
bound round two added, and 17 are round three's breakout cases and the two residual-shaped ones beside
them.

The browser-run numbers in the table above were superseded by round two — see **Major 5** below for
what they were measuring and what they are now.

Every new test was checked for its ability to fail. The sanitiser's three control surfaces were mutated
individually: the library's permissive defaults turn 6 red, a widened scheme list 12, an emptied
code-bearing-element list 2.

**Ports.** Every `E2E_*_PORT` was overridden into 4860–4872, scanned free first. Nothing on 4502, 4515,
4767, 4432/4433, 5080 or 4683 was touched.

**Migration.** Snapshot diff against `origin/main` is 156 insertions and **0 deletions**.
`has-pending-model-changes` clean after a rebuild. Up → Down → Up verified on a throwaway database
created with `Latin1_General_100_CI_AS_SC` (49 tables after `Down`, 51 after `Up`), with all five
declared indexes created under their declared names. (Five, not the seven an earlier version of this
entry claimed: the migration declares `UX_ContentPages_Slug`, `UX_ContentPages_SystemKey`,
`IX_ContentPages_Published_Title`, `IX_ContactSubmissions_Status_Created` and
`IX_ContactSubmissions_CreatedAtUtc`. The two primary keys are not indexes I declared.)

## Decisions worth knowing

- **An unpublished Contact page means no contact form.** The strict reading of "a draft must not leak"
  applies to all six, so on day one a shopper's only route is WhatsApp. **The Contact page needs no copy
  from the client** — its content is the form and the Settings details — so it can be published
  immediately, and the admin screen says so. Worth the owner's confirmation.
- **The editor takes HTML rather than offering a rich-text widget.** A widget's output has to be cleaned
  by the same allow-list anyway, and it would hide from the client the thing the screen has to be honest
  about: the shop accepts a short list of tags and removes everything else. The hint names the list and
  the save reports what went. A widget drops in later behind the same field.
- **A reserved-slug list.** A page saved at `/bag` would exist, be listed, be linked, be crawled and be
  unopenable, because that route is declared above the content route. Refused on save, with a test that
  reads the route file.
- **The footer loses six links on a fresh shop**, by design. Six links to 404s on every page is worse.
- **`Order Staff` can read an enquiry and cannot move its status.** That is doc 08 §9's list, faithfully
  — but it reads like an oversight, and the people answering enquiries may well be Order Staff. One for
  the owner; widening a default role is N13's versioned seeder step, not a line added while building an
  inbox.
- **`Customers.Edit`, `Content.Create` and `Content.Delete` are new permission values.** They reach
  default roles on a **fresh** database only: `AdminSeedHostedService` creates a role's claims once and
  never touches an existing role (doc 08 §9, N13). On `SpencerDenim_Dev` a Store Manager will not have
  them until the versioned upgrade step that seeder's own docblock describes is built. Super Admin is
  unaffected.

## The client progress page

`client-progress/progress-data.js`: "Store pages: About, Contact, Returns, Shipping, Privacy & Terms"
moved from **planned 0%** to **in-progress 85%**, and `lastUpdated` to 2026-10-07. Not `done`: CLAUDE.md
says done only after it passes testing, and the independent tester has not looked at this yet. The
remaining 15% is the client's own copy for six pages plus the PDPA review on two of them, which is
outstanding on their side rather than ours — and that is a conversation for the owner rather than a line
on a tracker that carries features and percentages only. **Not published**: publishing is the
owner's own step from their machine (docs/10).

## The bottom-right corner (docs/04 §4)

The standing rule is that anything placed in a viewport corner is checked with
`document.elementFromPoint` across scroll offsets before it lands. The Contact page adds three
tappable things (Send message, the WhatsApp call-to-action, the telephone and email links), and the
answer was to keep them out of the float's column rather than to measure a collision and accept it:
the actions are left-aligned and `width: fit-content`, so they never enter it at any width.
`content-pages.e2e.ts` runs the same scan `quick-view.e2e.ts` and `wishlist.e2e.ts` use, over those
three selectors, at 320x720 and 390x844 — 41 scroll offsets each, asserting the element hit **is** the
control. Making the submit button full-width makes it fail, which is how the left alignment is known to
be the thing doing the work.

## Residues the owner should know about

- **A content page may link to, and load an image from, any `http(s)` host.** That is inherent in
  GLB-10 — a client pastes a link and a picture — and it is what the scheme allow-list exists to
  permit. The consequence is that whoever can edit a content page can make every visitor's browser
  issue a request to a third party, which leaks the visitor's address and user-agent to it. Closing it
  means an image-host allow-list, which is a decision about the shop rather than about the sanitiser:
  the media library is the supported way to put a picture on a page, and an allow-list would also
  refuse a CDN a client may legitimately use. Stated in `HtmlSanitiserTests` beside the two cases that
  prove it is accepted.
- **`<plaintext>` is not in the code-bearing list**, so pasting one turns the rest of the body into
  visible text rather than dropping it. That is the better of the two outcomes — showing is honest,
  dropping is data loss — and it is noted so the next reader does not take the omission for an
  oversight.
- **A single mistyped segment answers 503 rather than 404 during a content-feed outage.** That is the
  deliberate side of #30 and the right way round — a live policy page matters more than a typo — but it
  does mean a scanner's probes get 503 for the length of an outage.

## Not done, deliberately

- **The retention purge job** (enquiries 2 years, IPs 90 days). There is no nightly job host yet. The
  schema allows both as single set-based statements and an integration test runs each one; the index the
  date-keyed purge needs exists, because adding one to a large table later is the expensive half.
- **A rich-text editor.** See above.
- **The client's copy**, for all six pages. That is the point.

---

## Round two: what the tester found, and what it cost

Verdict was **FAIL** — four Major product findings, one Major test-integrity finding, nine minors. Every
one of them was real. The sanitiser itself survived 143 payloads over two rounds with nothing getting
through, including the DOMPurify `form/math/mtext/mglyph/style` bypass, foster-parented tables, null
bytes in tag *and* attribute names, `javascript&colon;`, a fullwidth colon and zero-width prefixes. The
things that failed were the parts around it.

### Major 1 — a content-page save terminated the API process

`Inspect()` read `body.TextContent`, and walking a DOM that way recurses once per level. **A body of
`"<b>"` repeated 8,500 times is 25,501 characters**, inside the 200,000 a page may hold and inside any
length check that could be written, and it overflowed the stack. A stack overflow in .NET cannot be
caught: the runtime terminates the process and every request in flight with it. Measured: 8,000 levels
cleaned in 556 ms, 8,500 killed the host, while the library's own `Sanitize` survived 60,000. So the
failure was entirely mine, in the wrapper.

The length check in `CheckBody` could never have caught it, and the docblock there now says so: the cost
of walking a tree is set by its **shape**, not its size, and three characters buy a level. The bound is
therefore on depth, it is lexical, and it runs before the parser. The walk is now iterative with its own
stack and re-measures the real depth of the real tree, so an input the lexical pre-scan reads as
shallower than the parser builds it is still refused one step later.

**It was a read-path defect too, and that is the half worth remembering.** `AdminContentReader`
re-sanitises the stored body on every content list and detail read, so a row written outside the API —
the support script, data fix or restore that `IHtmlSanitiser`'s own remarks call out — terminated the
process on every attempt to open the one screen a staff member could have used to repair it, for
everyone including the owner. Reading such a row now reports zero images; publishing it is refused with
`body_not_readable`, because a gate that cannot check must not pass; unpublishing still works, or the
page would be stuck on the shop.

Mutation proof: raising `MaxNestingDepth` to 100,000 turns the four refusal tests red **and the run
survives 60,000 levels**, which is the iterative walk proving itself independently of the bound.

**And the first version of the pre-scan had a hole, found by re-reading it rather than by a test.** It
treated a trailing slash as self-closing, which is wrong for every HTML element that is not void: a
browser ignores the slash and opens the element. So `"<b/>"` repeated is four characters a level and
would have been counted as depth zero — 400,000 characters of it reaching the parser 100,000 deep, past
the 60,000 the library was measured at. `"<b x=/>"` does the same thing deliberately. The slash is now
honoured only inside `<svg>` or `<math>`, where a browser honours it too, which also stops a pasted SVG
full of `<path/>` being read as three hundred levels deep and refused. Three tests, two for the hole and
one for that false positive.

### Major 2 — the map could not render, and a test held it there

`sandbox=""` applies *every* restriction including scripts, and both permitted providers draw their
tiles with JavaScript. The comment above it said "a map embed needs neither", which was simply false.
Measured by the tester against the live endpoints: 0 images and 0 tiles from either provider with
`sandbox=""`; 36 images, 7 tiles and "Map data ©2026" from Google and 1 tile and "© OpenStreetMap
contributors" from OSM with `allow-scripts`. And `content-pages.e2e.ts` asserted the empty value, so the
defect had a test defending it.

Now `sandbox="allow-scripts"` and nothing else, asserted as an exact value with each dangerous token
named individually, because the risk runs both ways: too few tokens and the map is blank, too many and a
frame on the shop's own page gains same-origin access, top-level navigation or a form. What makes
`allow-scripts` safe to grant is that the `src` host has been through the allow-list three times, so the
script that runs is Google's or OpenStreetMap's.

### Major 3 — the task's defining guarantee had no test that could fail

`The_six_system_pages_are_seeded_unpublished_with_no_copy_in_them` called `RestoreSystemPagesAsync`
first, and that helper writes the empty body, the unpublished flag, the null published date, the slug,
the title and both null SEO fields — **exactly the seven fields the test then asserted**. It tested its
own helper. The tester proved it by changing the seeder to write a 14-day returns policy into all six
pages: the class stayed 12/12 green and the whole unit suite stayed 1,643/1,643 green, because that one
assertion was the only one in the repository on a seeded body. So an agent inventing a returns policy, a
privacy policy or terms — legal text needing a PDPA review, the thing this whole task is organised
around — shipped green. This is W1-03's vacuous-assertion class, on the one constraint that mattered
most.

Two fixes, because one was not enough. The integration test now hard-deletes the system rows and runs
`CatalogSeedRunner`, so nothing in the method writes a value it then asserts; hard-deleted rather than
soft-deleted because the seeder looks for what is present with `IgnoreQueryFilters` and would read a
soft-deleted shell as already there and insert nothing — vacuous again, more quietly. And
`SystemPageCopyTests` puts the signal in the **fast** suite, where a developer actually works: it reads
the seeder's source and allows exactly one right-hand side for a body assignment, `string.Empty`, which
is an allow-list rather than a hunt for policy phrases, so plain text or a constant held elsewhere
cannot slip past.

### Major 4 — a fresh shop published an invented address, email, phone and opening hours

On every page: `Colombo` / `Sri Lanka`, `hello@` at the real domain, `+94 77 000 0000`, "Monday to
Friday, 9am – 6pm". **This branch added those fallbacks**, and the footer's own comment argued for them
while the Contact panel's docblock three files away argued against them — "a shopper can drive to an
invented address". The second argument is the right one, and it is also exactly the call ADM-22 made
when it *deleted* three placeholder social links: there is no placeholder value for this shop's
telephone number that is not a lie. A shopper emails an address nobody reads, rings a number that is not
answered, and the shop never learns they tried.

The placeholder block is gone from `SITE_CONFIG` entirely rather than left unused for somebody to wire
back up, and the `TODO(ADM-22 part 2)` the tester correctly pointed out was still there verbatim is
replaced by what actually happened. Each field renders only when set; the `<address>` and the opening
hours go when empty; the whole column including its heading goes when there is nothing in it, because a
"Contact" heading over empty space is its own small lie and an empty `<address>` is an accessibility
defect rather than a neutral blank. The social list lives in that column and counts as content, so a
shop with only an Instagram address still gets it. The new tests name the exact placeholder strings
rather than asserting emptiness, so a *different* invented value cannot pass them.

### Major 5 — the suite was non-deterministic on demand

`fullyParallel` is on, and all 21 tests in `content-pages.e2e.ts` mutate module-level state in the
shared stub. The tester measured the full suite at 856/7 and then 859/4 **with a different set of
failures**, against 863 passed / 0 failed at `--workers=1`, and reproduced interference between
`content-pages.e2e.ts` and `site-branding.e2e.ts` on demand. No product regression was hidden by any of
it — but my report had blamed #114/#115, which is wrong: those are established `admin-*` harness races
and these were storefront specs.

The spec now has its own pair of servers and runs serially. That is the config's sixth pair's reasoning
applied a second time and the convention it already set — a stub with a runtime switch gets its own
process, and the spec that turns the switch runs serially against it. Keying the switches to a cookie so
state could be per-test would have to survive the **server** render, which is where the storefront reads
both feeds, so the cookie would need forwarding by the SSR server: a bigger change to the shell than the
problem justifies. The cost of serial mode is that a failure skips the tests after it, which the file's
own docblock now warns about.

**And that was not enough on its own.** With content-pages isolated, the suite still scored 863 passed /
2 failed in parallel against 865/0 at `--workers=1`, with both failures in `product-page.e2e.ts` and
`search.e2e.ts` — specs that pass 159/159 on their own and render the header and footer from
`/api/site/settings`. The last writer left on the shared stub was `site-branding.e2e.ts` (ADM-22), which
switches the logo, the social addresses and the WhatsApp number and was already `mode: 'serial'` — which
orders its own tests and does nothing about the files running beside it. So it moved onto an eighth pair
as well, and **the main pair now has no test that writes to it**. Both runs after that were 865/0.

### The test the tester wrote that the repository did not have

Every content-page fixture here was already-clean HTML, because the API cleans on save — so **nothing
exercised the render layer against hostile input**, which is the layer `IHtmlSanitiser`'s remarks say
exists for exactly the support-script case. The tester stood up nine raw unsanitised bodies through the
real SSR server and real hydrated Chromium and measured 0 rendering dangerously. That fixture is now in
the repository, on the dedicated stub, checking both the hydrated DOM and the bytes a crawler gets with
scripting off — the second because a stored `<script>` would already have run by the time the hydrated
DOM is inspected.

Writing it measured two things worth recording:

- **Angular keeps `srcset`**, including a `javascript:` candidate, in the server render and in the
  browser. Nothing executes — a `srcset` candidate is parsed as an image URL and `javascript:` is not a
  valid one, and the counter the payloads increment stays at zero — but it is the one attribute the
  server's allow-list refuses that survives this layer. Pinned with the reasoning rather than filtered
  out of the assertion.
- **A `javascript:` href is rewritten to `unsafe:javascript:` rather than removed.** So the check cannot
  be a plain "does not contain `javascript:`": it would fail on the neutralised form, and the obvious
  way to make it pass would be to delete the check.

### The minors, and the two that were really defects

- **`focusDetail` and `focusStatus` never fired.** Both used `queueMicrotask`, which runs before the
  render the signal write scheduled, so `getElementById` found nothing. The contact form had the same
  bug and was fixed with `setTimeout(0)`; the tester pointed out that this races the scheduler rather
  than waiting for it, so all three now use `afterNextRender`.
- **`Order Staff` could read every enquiry and never clear one.** Marking Read required
  `Customers.Edit`, which that role does not have; the screen fires the request as it opens a message
  and swallows the failure on purpose, so the New badge counted the same messages for ever and nothing
  said why — on the screen that role is most likely to live in. Doc 11 P15 makes *reading* the thing
  that marks it Read, so the permission to read has to be enough to record it. Replied and Spam stay
  behind `Customers.Edit`: those are claims a staff member makes about what they did.
- **`<picture>` lost its `<img>`** — the likeliest real paste in the feature, since every CDN and editor
  emits `<picture><source><img></picture>`.
- **"We usually reply within one working day"** was a service level the client never agreed to, on the
  component whose docblock says nothing is hardcoded, for a system that sends nothing.
- **The no-email guard held five needles and none matched "we will send you an email"**, the most
  natural phrasing of the failure it exists to prevent.
- **The subject input measured 71–103px against its siblings' 48** from 768px up: a grid container
  stretches its implicit rows and its row-mate carries a two-line hint. Every tap-target check was a
  floor, so none of them could see a control that is too *tall*; the e2e now asks that question too.
- **A delete refused twice for the same reason announced once.** `role="status"` announces a change, and
  setting a region to the text it already holds is not one. ADM-22 fixed its version by giving the save
  two genuinely different sentences; this path has no in-progress state to describe, so the region is
  emptied and the sentence put back after the render — not a counter, because a screen reader reads it.
- **The removal report stopped at twenty without saying so**, so an editor who fixed those twenty would
  believe they were done.
- **The sanitiser's docblock claimed it cleans every `Text/HTML` settings value.** It does not; no
  settings path calls it. A security control believed to cover a field it never sees is worse than one
  whose scope is written down.

### Corrections to my own report

- My test counts were wrong three ways. The sanitiser table was **105** cases, not 108 or 91.
- One mutation proof was understated: emptying the code-bearing list turns **7** red, not 2.
- The migration declares **5** indexes, not 7 (corrected above).
- `playwright.config.ts`'s defaults include 4432/4433, so overriding every port variable is mandatory
  rather than good practice: a bare `npx playwright test` would seize ports that are not this session's.

### Still not fixed, knowingly

- The contact rate limit is 120 per ten minutes per address, which is ~17,000 a day from one address.
  The brief's instruction on spam protection was docs/11 §7 G-06 "and no more", and a second slower
  bucket is more than that says. Worth the owner's decision rather than mine.
- `srcset` surviving the render layer (above). The server refuses it, so only an out-of-band row carries
  one, and the residue is one more third-party request from a page whose images the attacker already
  controls.
- The residues in the section above all still stand.

---

## Round three: two findings, four notes, and the Contact decision

### M1 was not closed — the exception I added for SVG was the same vector

Round two bounded nesting and the crash went away, which the tester re-verified: no process
termination, and with `MaxNestingDepth` raised to 100,000 it parsed 60,000-deep trees and survived. But
the foreign-content exception — honouring a trailing slash inside `<svg>` or `<math>`, because a browser
honours it — reopened the hole it was added to close.

`<b>`, `<div>`, `<span>` and `<ul>` are **breakout** elements: inside `<svg>` the parser leaves foreign
content and opens real nesting, while my scan still had `svg` on its stack and went on skipping. Measured
by the tester against the real `Sanitise`:

| payload | chars | scan read | real depth | outcome |
|---|---|---|---|---|
| `<svg>` + `<b/>` × 90,000 | 360 KB | ~1 | 90,000 | refused after **44,035 ms** |
| `<svg>` + `<div/>` × 66,000 | 396 KB | ~1 | 66,000 | **accepted** after **41,112 ms** |
| `<svg>` + `<div/>` × 40,000 | 240 KB | ~1 | 40,000 | **accepted** after **11,258 ms** |
| control: `<b>` × 133,330 | 400 KB | 133,330 | — | refused in **3 ms** |

A five-character prefix turned a 3 ms refusal into 42 seconds at 100% CPU, and for unwrapped elements the
save *succeeded* after paying it. `AdminContentReader` re-sanitises every row on every content-list load,
so one poisoned 396 KB row taxed every admin list view by the same 42 seconds — the amplification pattern
from round two, in its third form: **the screen that would clear the row is the screen nobody can open.**
Reachable with `Content.Edit`, the role doc 08 §3.7's G-06 split keeps least trusted.

My own `A_trailing_slash_does_not_hide_the_nesting` used `<b/>` with no `<svg>` wrapper, which is exactly
why it did not cover this.

The scan now pops the foreign subtree when a breakout start tag appears, which is not an approximation of
the parser but what the parser does. The element list is the HTML standard's own, oddities like `nobr` and
`listing` included, because a list that leaves one out has a hole in precisely this shape. **Each case
asserts the clock as well as the verdict**, because the first of them was refused before the fix too —
forty-four seconds later — so a verdict-only test would have passed against the defect. Mutation proof:
disabling the pop turns 4 of 5 cases red and takes the filtered run from 1 second to 1 minute 29.

Writing it corrected an expectation of mine. `<math><p/>` × 40,000 is **accepted**, and that is right: `p`
breaks out and then closes the previous `p`, so they are forty thousand siblings one level deep, which is
what a browser builds. The test failing is what made me check; asserting the refusal would have pinned a
false positive against every page that pastes unclosed paragraphs. Five more tests pin the case the
exception exists for — `path`, `circle`, `rect`, `g`, `use` do not break out.

### The WhatsApp number was the last invented value a shopper could see

`whatsAppUrl` fell back to `94770000000` and so could never return null, so a fresh shop carried a
floating button on **every page** and four links on the Contact page, all opening a chat with nobody.
`SITE_CONFIG`'s docblock argued for keeping it: GLB-08 says the float is site-wide, and removing it over
an unset setting would take a scoped feature off the shop. That is wrong in the same way M4's footer
argument was wrong — a float that reaches nobody is not the feature, and a shopper who messages it
concludes the shop does not answer.

It now returns `string | null`, and null is the point of the signature: "there is no WhatsApp yet" becomes
a state every caller has to handle. The float, the mobile-menu row and the panel's "WhatsApp is the
fastest way" sentence all hide; the panel's two links already did. The float also **drops its landmark**
when it has no button, because a named `complementary` region with nothing in it is a worse defect than
the one the landmark was added to fix (#65).

**A grep of `dist/` is not empty, and what it finds is correct.** `dist/admin` holds the settings form's
**input placeholders** — `+94 11 234 5678`, `hello@yourshop.lk`, `Your shop\nColombo 03\nSri Lanka`,
`Monday to Friday, 9am – 6pm`, the map-embed URL shape — each an `attr.placeholder` on a box that is
**empty**, showing the client the format expected. They are admin-only: `grep -rl` over `dist/storefront`
finds none of them, and none is stored or rendered to a shopper. A format hint inside an empty field is
categorically different from the shop stating it as fact, which is what M4 was about.

Written down because it *will* surface in anybody's future grep for the placeholder strings, and finding
it with no explanation would read as a miss. Re-verified on the merged tree after the W3-03 rebase, where
bank transfer's own `Example Bank PLC` / `0000 0000 0000` turn out to be the same thing in the same file —
two tasks arriving independently at the same convention, which is the most reassuring way to find one.
Neither task's real-looking strings reach the storefront bundle.

One thing that fix made load-bearing: **the stub's shop now has a WhatsApp number unless a test asks for
none.** `quick-view`, `wishlist` and `content-pages` scan the bottom-right corner with `elementFromPoint`
*because the float owns it*, so with no number those scans would pass by measuring a corner with nothing
in it. A content-pages occlusion test caught exactly that, which is why the switch distinguishes an absent
`whatsApp` parameter from `whatsApp=none`.

### Four notes, from the coordinator and worth keeping

1. **`ng lint` is in CI** (`ci.yml:145`). The six errors round two found survived only because **the
   branch was never pushed**, so CI never ran on it. The gate is correctly placed; a never-pushed branch
   bypasses it by definition. Nothing to fix in the pipeline — but it is the argument for pushing early.
2. **The suite starts 17 web servers, not sixteen.**
3. **865/0 is the no-flake outcome, not a guarantee.** #88 can fire at any worker count, and a clean
   `--workers=1` run tripped it once in the named test (`admin-catalog`, keyboard reorder — a spec this
   branch does not touch). Expect 864/1 or 865/0.
4. **`workers: '25%'` is not the whole answer.** The 17 servers are a *fixed* baseline load whatever the
   worker count, so on a 16–32-core runner 25% could re-oversubscribe them. Whoever owns CI should
   consider an absolute cap as well as the percentage.

### Three residuals, recorded rather than coded

- **M3's guard covers the seeder only.** Both tests read `HomeContentSeeder` and the seeded rows, so copy
  written out-of-band — by a migration, or a data fix — is caught by neither. That is arguably outside the
  claim ("nothing in this repository writes their copy"), and it is a line here rather than code because
  the honest check would be a constraint on the column, which cannot distinguish the client's own copy
  from an agent's.
- **`srcset` survives the render layer**, re-verified by the tester through the real SSR build and
  hydrated Chromium: it is kept verbatim, `window.__xss` stays `undefined`, and a `javascript:` href is
  neutralised to `unsafe:`. Correctly a residue — and the note to carry forward is that **any future
  image-host allow-list must cover `srcset` too**, because the two layers disagree about whether the
  attribute exists at all.
- **HTML integration points are the remaining edge of the self-closing exception, and the pop does not
  reach them.** This is the third residual and the one a reader is most likely to assume away, because
  the breakout list looks exhaustive and is not.

  An *integration point* re-enters HTML parsing **inside** foreign content without being a breakout
  element, so `BreaksOutOfForeignContent` never fires and `svg`/`math` stays on the scan's stack:
  `<svg><foreignObject>`, `<svg><desc>`, `<svg><title>`, `<math><annotation-xml
  encoding="text/html">`, `<math><mtext>` and `<math><mi>`, each followed by a **non**-breakout
  self-closing element such as `<q/>` or `<section/>`. At n=300 the real parsed depth is 302 while the
  scan reads about 2.

  **What it costs, measured at the full 400 KB cap:** `<svg><foreignObject>` + `<section/>` × 39,000 is
  the worst case at **8,777 ms**, with empty output and no crash. Round two's numbers for the same class
  of payload were 41 s accepted and 121 s refused, so this is a 5–14× reduction and the headline vector
  — the one a Content Editor could use to take the API down, and to make the admin content list
  unopenable — is gone.

  **Recorded rather than coded, deliberately.** Closing it means modelling the integration-point rules as
  well as the breakout list, which is a second faithful slice of the HTML parsing algorithm living in a
  lexical pre-scan whose own docblock already says it is an approximation and need not be exact. The
  cost/benefit flipped once the result was "slow, bounded, and it refuses" rather than "fast, unbounded,
  and it accepts": 8.8 seconds of one core on a 400 KB request is a nuisance an editor can cause by
  pasting nonsense, not an outage. What matters is that the next reader knows **where the exception's
  edge actually is** instead of assuming the pop closed everything — and that if this ever needs closing,
  the place is the pre-scan's model of foreign content, not another entry on the breakout list.

---

## Contact ships published

Owner decision, after round three. **Contact is seeded with `IsPublished = true` and an empty body**; the
other five stay drafts.

The reasoning, because it is the opposite of the rule the rest of this task follows. Contact has nothing
to wait for: its content *is* the enquiry form plus whatever the client has typed into the five
`Contact.*` settings, so there is no copy whose absence makes it wrong. And leaving it unpublished costs
more than it saves — now that the WhatsApp float correctly hides itself until a number is set, a brand-new
shop with Contact unpublished would have **no contact route at all**. An empty-bodied Contact page is a
working contact form; an unpublished one is a shop a customer cannot reach. The tester measured the
published page as finished rather than broken at every width: 200, no horizontal overflow, the form
renders, every invented detail gone.

**The empty body is not relaxed for it.** Published and empty is exactly the intended state, and both
guards still assert the body of all six is empty.

`ContentPageSeed` carries a `PublishedOnInstall` flag rather than the seeder special-casing a key, so the
decision sits beside the page it applies to. `SystemPageCopyTests` asserts the record's property list
whole — now `PublishedOnInstall`, `Slug`, `SystemKey`, `Title` — and asserts that flag is a `bool`,
because **a boolean cannot carry a returns policy**: that is what keeps this from becoming a door for
seeded copy. A new test asserts "exactly one page ships on the shop and it is Contact", so a seventh seed
published by default has to argue for itself.

`PublishedAtUtc` is stamped for it, from `IClock` like the sibling `DemoCatalogueSeeder`. Null beside
`IsPublished = true` would be an inconsistent row, and `ContentPage.SetPublished` assigns with `??=`, so
the first unpublish-and-republish would otherwise record a date months after the page was first visible.

**The re-seed rule needed proving, not changing.** The seeder only ever inserts keys that are absent, so a
client who takes Contact off the shop keeps it off — but that is now carrying a decision it was not
carrying before, and "it happens to work because of how the query is written" is exactly what a later
refactor breaks for free. The announcement protection a few sections up exists because that went wrong
once already. So there are two tests, in both directions: Contact stays off once the client takes it off
(and gains no duplicate shell), and a page the client published stays published with its copy and its
original publish date. **Mutation proof:** making the seeder enforce the seed on existing rows turns both
red.

One rename came with it. The fixture that put all six pages into the draft state was called
`RestoreSystemPagesAsync`, which was misleading in round two — the seed test asserted the seven fields it
had just written — and became wrong outright here, because drafting Contact is no longer restoring
anything. It is `DraftEverySystemPageAsync` now, and its own docblock says it is a fixture rather than a
restore, and points at the test that does assert the seed.

---

## The rebase onto W3-03

`main` moved to `c8b8c81` (bank transfer, PAY-01/PAY-03) while this was finishing, and GitHub **will not
compute checks for a conflicting PR** — so #127 reported `DIRTY` with zero checks rather than a failure.
Rebased onto it; merge base was `006a0c9`.

**The migration needed nothing.** Bank transfer added none at all, so
`20261007055218_ContentPagesAndContactSubmissions` is still the latest and nothing was renumbered or
regenerated. Verified three ways on the merged tree, because `AppDbContextModelSnapshot.cs` merges without
conflict and a wrong merge there is silent: `has-pending-model-changes` clean after a full rebuild, the
snapshot diff against `origin/main` still **156 insertions and 0 deletions**, and `grep '^-'` over that
diff empty.

**Two conflicts in 36 replayed commits**, both resolved by keeping both sides:

- `Program.cs` — both throttles and all three endpoint registrations.
- `SettingCatalogTests.cs` — three regions. Both of bank transfer's tests and mine now stand side by
  side, and I took **main's** test name: it had renamed
  `The_ten_keys_this_build_populates_…` to drop the count, with the reasoning "a name that counts is a
  name that has to be renamed", and my side was about to add the eleventh. The `<remarks>` arithmetic is
  corrected to five → ten → fifteen.

**The other twelve overlapping files auto-merged, and each was read rather than assumed.** Both settings
groups survive whole: 15 catalogue definitions (Store 1, Contact 6, Social 3, Bank 5), both request
records, 9 contact-and-social fields beside 5 bank fields, three settings panels in the order
`admin-settings.e2e.ts` asserts exhaustively (Logo, Contact and social, Bank transfer), both admin route
sets, and both stub blocks — bank transfer's `Example Bank PLC` fixture beside my contact settings and
the `whatsAppFrom` helper. Bank transfer's widening of the `GET /` guard to
`RequireAnySettingsPermission()` is intact; my changes never touched that line.

**Every deletion in `git diff origin/main HEAD` was inspected** and each is a widening of the same thing:
`ContactAndSocialFields` 4 → 9, `buildSiteSettings(logo, social, whatsApp)` → `(…, contact)`, the settings
payload's member list `["logo","social","whatsAppNumber"]` → `["contact","logo","social","whatsAppNumber"]`.
Nothing of main's was lost.

**One thing found that is not mine to fix**, reported to the coordinator rather than edited: the
`AdminSettingsEndpoints` summary still opens "Every route is behind `Settings.Edit`" and says the guard
"is what has to widen when the payments group lands". Bank transfer widened both, so that paragraph now
describes the pre-W3-03 world — and it is **identical on `origin/main`**, so this is a pre-existing
defect rather than a rebase casualty. It is a security docblock, which is the worst kind to leave stale;
rewriting another task's reasoning without asking is worse.

**And the WhatsApp hiding interacts with bank transfer in one way worth recording.** On main, an unset
`Contact.WhatsApp` still produced a float, because the storefront fell back to the placeholder — so the
corner scans in `quick-view`, `wishlist`, `content-pages` and now `bank-transfer.e2e.ts` were all
measuring a real float by accident. After this branch an unset number means **no float**, which would
have turned all of them into scans of an empty corner. The stub's default shop therefore has a WhatsApp
number, and `whatsApp=none` is how a test asks for the unconfigured state — the reason that default is
load-bearing rather than convenience.
