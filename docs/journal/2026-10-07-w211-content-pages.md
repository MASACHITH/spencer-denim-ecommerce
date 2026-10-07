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

| Suite | Result |
|---|---|
| xUnit unit | **1,679 passed**, 0 failed, 11 s |
| xUnit integration, real SQL Server | **1,058 passed**, 0 failed, 14 m 41 s |
| Vitest storefront | **1,389 passed** (53 files) |
| Vitest admin | **385 passed** (354 before, +31) |
| Vitest shared | **24 passed**, unchanged |
| Playwright, whole suite | **865 passed, 0 failed** at `--workers=1` and at `--workers=2`; **864/1** at the capped default, the one being the known `admin-catalog` flake |

New: **121** sanitiser cases, 50 content-page write cases, 35 contact-enquiry cases, 12
system-page-copy cases, 11 reserved-slug
cases (which read `app.routes.ts`, so the refusal list cannot drift from the route table), 10
admin-model shape cases, 28 integration cases, 27 browser cases in `content-pages.e2e.ts`, and 13
rewritten in four existing browser specs. Of the sanitiser's 121, **105 are the payload table** — not
the 108 an earlier version of this entry claimed, which the tester counted correctly — and 16 are the
nesting bound round two added.

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

## Round three: two findings, and four notes for whoever owns this next

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

### The WhatsApp number was the last invented value in the bundle

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
the one the landmark was added to fix (#65). A grep of `dist/` now finds no invented value in either
bundle.

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

### Two residuals, recorded rather than coded

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
