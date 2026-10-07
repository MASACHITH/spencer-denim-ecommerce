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
| xUnit unit | **1,619 passed**, 0 failed |
| xUnit integration (real SQL Server) | **1,057 passed**, 0 failed, ~16 min |
| Vitest storefront | **1,302 passed** before the two new specs, **+83** in them |
| Vitest admin / shared | **354** / **24** passed, unchanged |
| Playwright `content-pages.e2e.ts` | **27 passed** |

New: 91 sanitiser payload cases, 47 content-page write cases, 35 contact-enquiry cases, 11
reserved-slug cases (which read `app.routes.ts`, so the refusal list cannot drift from the route table),
28 integration cases, 27 browser cases.

Every new test was checked for its ability to fail. The sanitiser's three control surfaces were mutated
individually: the library's permissive defaults turn 6 red, a widened scheme list 12, an emptied
code-bearing-element list 2.

**Ports.** Every `E2E_*_PORT` was overridden into 4860–4872, scanned free first. Nothing on 4502, 4515,
4767, 4432/4433, 5080 or 4683 was touched.

**Migration.** Snapshot diff against `origin/main` is 156 insertions and **0 deletions**.
`has-pending-model-changes` clean after a rebuild. Up → Down → Up verified on a throwaway database
created with `Latin1_General_100_CI_AS_SC` (49 tables after `Down`, 51 after `Up`), with all seven
declared indexes created under their declared names.

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

## Not done, deliberately

- **The retention purge job** (enquiries 2 years, IPs 90 days). There is no nightly job host yet. The
  schema allows both as single set-based statements and an integration test runs each one; the index the
  date-keyed purge needs exists, because adding one to a large table later is the expensive half.
- **A rich-text editor.** See above.
- **The client's copy**, for all six pages. That is the point.
