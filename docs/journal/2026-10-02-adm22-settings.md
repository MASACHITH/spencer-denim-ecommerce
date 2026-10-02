# 2026-10-02 — ADM-22 / GLB-02: `ops.Settings`, the logo upload, and the social links

Branch `feature/ADM-22-settings-branding`, worktree `worktrees/sd-sizeguide`, from `main` at `f96f9b8`.
Not pushed, no PR. `MEMORY.md` and `TASKS.md` deliberately untouched (doc 14, clarified today).

## Why

The client's delivered logo files are JPEGs with a mockup background, so they cannot go in the header, and
nobody has sent the social profile addresses. Both had been blocking for days. **The owner's decision was to
stop waiting:** build the screen where the client uploads the logo themselves and replaces it whenever they
like, and take the social links on the same screen when they arrive. In the owner's words — *the logo will
change in future, so the admin upload option should let them remove the existing one and add a new one.*

## Done

**The table.** `ops.Settings` exactly as doc 08 §3.8 specifies, `[RV]` included: `Key varchar(100)` PK
(dotted), `GroupKey varchar(50)` indexed, `ValueType tinyint` with its CHECK, `Value nvarchar(max)` null,
`IsSecret bit` with the biconditional CHECK, `RequiredPermission varchar(64)`, `Description nvarchar(300)`,
`UpdatedAtUtc`/`UpdatedByUserId`, `RowVersion`. **The whole value-type range (1–8) is allowed although every
seeded key is a 1 or an 8**, so #53 and W3 add rows rather than a migration.

**Seeded, values null:** `Store.LogoAssetId` (type 8 `MediaAssetRef`), `Contact.WhatsApp`,
`Social.FacebookUrl`, `Social.InstagramUrl`, `Social.TikTokUrl`. Nothing else — see *Left out* below.

**The logo** is tracked in `media.MediaUsages` with `OwnerType = Setting`, `OwnerId` the setting key and
`UsageRole = Logo`, so W1-04's delete guard refuses to delete a logo that is in use; and a replacement
releases the previous asset's row in the same transaction, which is the half the owner actually asked for.

**The storefront** reads the logo, the WhatsApp number and the social links on the server on every page
(`GET /api/site/settings`), through the SSR handover `AnnouncementService` established, behind doc 08 §3.8's
sixty-second cache, evicted on every successful save.

**The admin screen** is `/settings`: a logo panel (upload, replace, remove, with a chequerboard preview so a
JPEG's background is obvious *before* it reaches the shop) and a contact-and-social form.

## One migration

`20261002111130_OpsSettingsTable`. **It creates exactly one table and touches nothing else:** `ops.Settings`
with the ten columns above, `PK_Settings` on `Key`, three CHECK constraints
(`CK_Settings_ValueType` 1–8, `CK_Settings_IsSecret` for the biconditional, and
`CK_Settings_RequiredPermission` requiring a non-empty permission — the last is not in doc 08 and is
explained in the configuration), and one non-unique index `IX_Settings_GroupKey`. No foreign key, no seed
data, no `EnsureSchema` (the initial migration created `ops` for the history table and `AuditLogs`), and **no
alteration to any other table** — the `MediaUsages` rows fit the table W1-04 already built, because doc 08
§3.3 specified `OwnerType = Setting` from the start. `Down` drops the table.

Nothing in it can collide with `sales` (W2-09, an hour earlier) or with #53's tables.

## Decisions, with reasons

**The shop's name is not a setting, and I did not make it one.** Doc 08 §3.8 lists `Name` in the `Store`
group and doc 11 P14 step 11 lists "Store name" under `Settings.Edit` — both predate the owner's decision of
2026-10-02 (docs/02 §I2, W1-15), which put the name in one constant per application with a convention test
behind it. A settings row would give it two sources of truth, one of which the server renders the storefront
from before any database is touched. `SettingCatalogTests.The_shop_name_is_not_a_setting` is the guard, and it
is phrased as "no key may be the shop's name" so `Branding.StoreName` fails it too —
`BrandNameConventionTests` would **not** have caught it, because seeding from `StoreBrand.Name` reads the
constant rather than writing the literal.

**The header fallback is the normal state, not an error state.** With no logo uploaded the header keeps the
text wordmark; there is no second rendering for "the settings read failed", because the service answers the
same empty payload for a timeout, a 500, a malformed body and an unconfigured shop. That equivalence is the
point: the failure path has already been looked at by everyone who has looked at the normal one. The
`<img>` carries explicit `width`/`height` (CLS) and its `alt` is never blank — it falls back to the shop's
name, because this image *is* the site's name.

**The logo is sized by the header, not by the file**, with `height` fixed and `max-width` capped. That is what
lets a client upload any image without breaking the layout, and it protects `--header-height` in tokens.css,
which anchor jumps and focused controls clear — if the logo could set its own height, every upload would
silently invalidate that token and the symptom would appear three screens away from the cause.

**URL validation is an allow-list, written as one.** W1-12's banner-link validator was defeated three times by
deciding what to reject. These are *absolute external* URLs, so the rule differs — what is accepted is: no
backslash, control character or whitespace anywhere; parses as absolute; scheme exactly `http` or `https`
(which is what refuses `javascript:`, `data:`, `vbscript:`, `file:` and every scheme nobody has thought of);
a non-empty host (an `href` with none resolves against the current page, so the footer's "Instagram" icon
would link back into the shop); and no credentials (`https://instagram.com@evil.example/` reads as one
destination and goes to another). **The same predicate runs on the way out**, so a value written by an earlier
build or a database edit disappears instead of going live, and the browser-side check asks `new URL` —
which is the question the `href` will ask.

**Unset means hidden, never a placeholder.** The footer's three icons used to point at `facebook.com`,
`instagram.com` and `tiktok.com` — the networks' front pages, not this shop's profiles. A dead social icon on
a live shop is worse than no icon, so `SITE_CONFIG.social` is gone and an unset address shows nothing; the
whole `<ul>` disappears rather than leaving an empty row.

**The WhatsApp number is the one exception, deliberately.** `Contact.WhatsApp` *overrides*
`SITE_CONFIG.whatsAppNumber` rather than deciding whether GLB-08's button exists. An unset social address
hides one footer icon; removing the floating button would take a scoped feature off every page over a setting
nobody has filled in. **The placeholder `94770000000` predates this task and is still there** — it is the
thing to replace with the client's real number, and it is flagged here rather than quietly dropped as part of
a task about logos.

**Secrets: three independent floors, built while there is nothing to lose by getting it wrong.** (1) The admin
read model never returns a secret's value **to anybody** — stronger than doc 08's permission rule, because the
weaker rule needs a check to be right on every future read path and gets one wrong eventually; `hasValue` is
all a screen needs. (2) The anonymous storefront contract is a hand-written allow-list of three named members
with no general-purpose value, so a secret added tomorrow has *nowhere to appear*; a reflection test pins the
member list. (3) `SettingSecretProtector` uses doc 08's purpose `SpencerDenim.Settings.v1`, swallows
cryptographic failures rather than logging them (a Data Protection message can carry the payload it failed
on), and the audit trail logs `[secret changed]` driven by the row's own flag so a handler cannot opt out.

**RBAC and audit.** `Settings.Edit` already existed as a constant; the read filters **per row** against the
caller's *effective* permissions, so a group they may not read is **absent rather than empty** — an empty
`Bank` group would confirm bank details exist. One audit row per changed key (doc 08 §3.8), action
`SettingChange`, through `IAuditLogWriter.Enlist` so the trail and the change commit together (#59).

## Two defects I found and fixed, with the evidence

**1. Every valid contact-and-social save answered 409.** The read model published a concurrency token *per
group*; the save compared it against the rows of *both* groups, because one form saves both. Two files, nothing
making them agree.

- **Red:** 7 integration failures, `Expected: NoContent / Actual: Conflict`; then a unit-level version,
  `SettingsRoundTripTests` — 4 failed / 8 passed, `Assert.IsType() Failure … Expected: Succeeded, Actual:
  Conflict`.
- **Fix:** `SettingCatalog.SaveUnits` — one declaration both sides read. A group is a unit of *permission and
  display* (doc 08 keeps `Contact` and `Social` separate, respected); a save unit is a unit of *concurrency*.
- **Green:** backend unit 1088/1088, settings integration 74/74.
- The test that would have caught it is `SettingsRoundTripTests`: take the token the read model published,
  send it straight back, require the save to accept it — phrased over every group, so a third group added to a
  form fails there too. Plus `SettingsSaveUnitTests` as the structural pin.

**2. With a logo set, the header's home link shrank to 32 px wide.** Under the 44 px minimum (WCAG 2.5.8 and
this project's own rule), on the shop's primary link, on every page.

- **Red:** `site-branding.e2e.ts` — `tap targets at 1024px … A.wordmark site-header__wordmark, width 32,
  height 44`.
- **Cause, which I would not have predicted:** the anchor is only as wide as the drawn image, and the drawn
  width comes from the file's *intrinsic* ratio once it loads — the `width`/`height` attributes supply a ratio
  only beforehand. A square logo, or a small one the encoder clamped rather than upscaled, gives a 32×32 box.
  `min-width: 0` was right while the content was text that had to shrink inside `minmax(0, 1fr)`; it is wrong
  now that the content is an image whose size the client chooses.
- **Fix:** `min-width: var(--tap)` on the wordmark anchor. **Green:** 38/38 in that file.

Three further corrections, each a test that was measuring the wrong thing, each recorded in the test itself:

- `http:///profile` — I asserted the browser refuses it. It does not: the WHATWG parser normalises it to host
  `profile`, while .NET's RFC 3986 `Uri` gives it an *empty* host and refuses it server-side. The server is the
  stricter of the two; the test now records which is which instead of the version that read better.
- A control character at the **end** of a URL is trimmed and the value accepted — `Clean`/`trim` removes
  whitespace, and TAB/CR/LF/VT are whitespace. The dangerous position is the **middle**, which `Trim` does not
  touch. A NUL is refused wherever it sits, because it is a control character and not whitespace.
- Two settings tests inserted a non-catalogued key and then looked for it in the payload. The read model
  projects only catalogued keys (correct — the catalogue is the allow-list), so the tests were **vacuous**:
  they would have passed with the redaction removed. They now flip a *catalogued* key to `Secret`, which is the
  only way to get a secret row onto the read model's own path.

Also: `page.route` cannot fault the settings read, because the storefront fetches it during the **server**
render and hands the answer over with the page — a routed test faults a request the browser never makes. The
fault modes live on the stub (`/__test__/site-settings?status=|delay=`), like the departments fault.

## Tests

| Suite | Total | Failures |
|---|---|---|
| Backend unit | **1088** | 0 |
| Backend integration (real SQL Server, LocalDB) | **663** | 0 |
| Frontend unit — shared / storefront / admin | **21 / 1033 / 232** | 0 |
| Playwright (full, `CI=1`) | **643** | 0 of mine; see flakes |
| Release build | — | 0 warnings, 0 errors |
| `ng lint` | — | clean |

Added: ~150 backend unit tests across 6 files, 72 integration tests across 3 files, ~69 storefront unit tests
and 20 admin unit tests, and 38 Playwright tests across `site-branding.e2e.ts` and `admin-settings.e2e.ts`
(605 → 643).

**Breakpoints** 320 / 375 / 390 / 768 / 1024 / 1280 / 1440 / 1920 — storefront header with and without a logo,
footer social icons, and the admin settings screen: no horizontal scroll, ≥44 px targets, visible focus rings
measured by photograph (`edgesWithoutRing`, lesson L4). Every `undersized` call site is guarded **per clause**
first, because it returns `[]` both when everything passes and when the selector matched nothing (#80, not
fixed here).

**axe** (serious + critical, docs/06 §3's gate): nothing on the storefront header with a logo set, nothing on
the admin settings screen. Heading order is checked by hand, because `heading-order` is a *moderate* and the
gate cannot see it; an any-impact annotation test records the rest.

**Flakes, none mine:** `storefront-shell.e2e.ts` reduced-motion drawer (**#66**, named in MEMORY, same test,
file untouched by this work beyond the header's `min-width`) and a `search.e2e.ts` recent-searches read — which
flaked on a *different* test (`:634`) on re-run than in the full run (`:725`), the signature of a flake rather
than a regression. Both passed on retry. One `accessibility.e2e.ts` ECONNRESET through a route handler, also
retried green.

Ports: a private, verified-free block **5620–5632** with `CI=1` (so none of the 13 `webServer` entries reuses
anything). No process or database left behind; no `SpencerDenim_Test_*` remains.

## Left out, on purpose

Everything doc 08 §3.8 lists that needs something that does not exist: `Bank.*` and `Payments.PayPal.*` (need
`Settings.Payments` and a payments screen — #53, W3); `Email.Smtp.*`, `Security.*` and
`Privacy.RetainInvoiceBillTo` (need `Settings.Security`, the sensitive-change alert and `ops.AdminAlerts`);
delivery methods, zone rates and couriers (need `sales.*` tables #53 creates). `Social.YouTubeUrl` is not
seeded either: the shared icon set has no YouTube glyph, so a filled-in value would have nowhere to render.

Also absent: doc 08 §3.8 N2's sensitive-change alert (it fires only for keys this build cannot write), the
HTML sanitiser for `ValueType 2` (nothing writes one), and the rest of the `Contact` group — phone, email,
address, opening hours, map — which stay as `SITE_CONFIG` placeholders and are rows away.

## Things the next person should know

- **`ops.DataProtectionKeys` and `ProtectKeysWithCertificate` (doc 08 DB30) do not exist**, so
  `SettingSecretProtector` uses the framework's default filesystem key ring. **Corrected after round 1: this
  was understated.** There is no Data Protection configuration anywhere in `src/backend`, and the default key
  ring is *already load-bearing today* — it protects the admin auth cookie and the antiforgery tokens on these
  very routes. In Docker without a mounted volume it is ephemeral, so every container restart invalidates every
  admin session. "Before the first secret goes in" sets the bar a notch too low: it wants closing **before the
  next deploy**. Filed by the coordinator; not this task's to fix.
- The admin stub's derived port band starts at 4500, and **4502 is held by `charon-svc.exe` (a VPN service) on
  this machine** — a worktree whose hash lands there would collide with something that is not a Playwright
  server at all. Not hit today because this run overrode the port.
- A pre-existing scratch database `SpencerDenim_W113_...` is on this LocalDB instance. Not mine; left alone.

## Where the brief and the documents disagree

Reported rather than resolved:

1. **The brief calls the logo's group "Branding"; doc 08 §3.8 calls it `Store` and names the key
   `LogoAssetId`.** The brief itself says doc 08 is binding, so I used doc 08's names and read "Branding" as
   the brief's informal name for GLB-02's branding. If "Branding" was meant literally, it is a rename of one
   constant and one seeded group key.
2. **Doc 08 §3.8 and doc 11 P14 step 11 both still list the store *name* as a setting**, which the owner's
   decision of 2026-10-02 (docs/02 §I2) overrode. The brief is right and the two documents are stale; they
   want a line, and `StoreBrand`'s own doc comment — which says this constant "becomes the fallback for
   `Branding.StoreName`" when ADM-22 lands — wants correcting too. I have not edited them: doc 02 §I2 is the
   owner's record and doc 08 is Tharanga's working document.
3. **The brief says the social links are placeholders on `https://example.invalid/PLACEHOLDER/<network>`.**
   Those are the *homepage social gallery tiles* (HOME-08), in `ClientAssetImporter` — sd-rename's territory,
   untouched. The **footer** links were `https://www.facebook.com/` and friends, and that is what this slice
   replaced. **Corrected after round 1: I had the severity the wrong way round.** The HOME-08 tiles are the
   *milder* defect — `.invalid` is RFC 2606-reserved and cannot resolve, so those tiles are visibly dead —
   where the old footer links went to a real page that is not this shop, which is the one a shopper cannot tell
   from a working link. They still want the same treatment, just less urgently than I implied.

## Shared files touched

`MediaService.LoadOwnerLabelsAsync` gained a `Setting` branch — twelve lines. It had to come *before* the
integer parse, because a setting's `OwnerId` is the key rather than a number (doc 08 §3.3 says so), and
without it the 409 refusing to delete the live logo read "Setting Store.LogoAssetId" with no label at all, on
the exact screen the owner asked for so they could remove an old logo. `MediaUsage.cs` gained
`OwnerTypes.Setting` and `Roles.Logo`. **The media importer and the seeded catalogue were not touched**
(sd-rename). `catalog-api-stub.mjs` and `admin-catalog-stub.mjs` gained settings routes, additively.

---

# Round 1 — tester FAIL on 2 majors and 6 minors, all addressed

Nothing in the shipped feature was broken: the tester could not defeat the secret guard, the media-usage
guard, the concurrency fix, the schema, the anonymous endpoint, or the URL validator beyond three exotic rows.
The FAIL was for **test integrity and two comments that promised a protection the code did not have** — the
same standard I had applied three times to my own vacuous tests inside round 0, which is why it is the right
call. Every count matched.

## Major 1 — both logo-shape fixtures served a 1x1 pixel

`buildSiteSettings` declared `wide` as 600x150 and `tall` as 200x600 while the stub answered every
`/media/public/` path with one 34-byte 1x1 WebP. So **every logo drawn anywhere in the 643-test suite was one
1x1 square**, and the two tests that exist to prove the header sizes the logo rather than the file could not
fail for the reason they state: `max-width: 220px` was never reached, `height`/`object-fit` were never
exercised, and `expect(drawn.height).toBeLessThanOrEqual(40)` passed trivially on a 32 px box. The stub's own
comment — a real URL is served "so a broken one would show up as a broken one" — made the gap read as covered.
That is the property the journal named as the whole safety argument for handing this screen to a client.

**Fixed by generating real images in the stub.** A ~40-line PNG encoder (`solidPng`, IHDR + deflated IDAT +
CRC) produces `wide` 2000x100 (899 B), `tall` 200x600 (1,591 B) and `square` 512x512 (1,882 B) — three
distinct files, each the size its payload declares. They are served from `public/originals/logo-*.png` with
`image/png`, which is a real production shape: `MediaUrlBuilder` returns the original's key for an asset with
no renditions. PNG rather than WebP because a lossless WebP encoder is a few hundred lines and the property
under test — intrinsic size — is format-independent.

**And the `square` state is kept deliberately.** The tester proved the tap-target gate works by removing
`min-width: var(--tap)` and watching the suite go red with my exact reported output — but it only worked
*because* the 1x1 made every logo square. Fixing the fixtures naively would have un-gated my own fix. So
`square` is now a named state, the loop that uses it says why, and the rationale comment in `site-header.ts`
says it too.

**Evidence, with the fixtures mutated back to 1x1:**

| Test | Red output under the 1x1 mutation |
|---|---|
| `the wide logo fixture really is 2000x100` | `"width": 2000, "height": 100` expected, `1 / 1` received |
| `a wide logo leaves the navigation in place…` | `the logo is not capped at 1024px — Expected: 220, Received: 32` |
| `a tall logo does not grow the header` | `drawn 32px, expected ~10.67px` (21.33 off a tolerance of 2) |
| `…with the logo square` | **still passes** — a 1x1 *is* square, which is the point |

**And with the real fixtures, `min-width: var(--tap)` removed:**

| Test | Result |
|---|---|
| `…with the logo square` | **red**: `width: 32, height: 44` — the original defect, still gated |
| `…with the logo wide` | **passes** — a wide logo is capped at 220 px, so it cannot see this |

Three new fixture-integrity tests assert `naturalWidth`/`naturalHeight` against the declared values, which is
the browser's own answer about the decoded bytes and the one number a declared dimension cannot fake.

## Major 2 — the write path had no per-row permission check, and two comments claimed it did

`UpdateContactAndSocialCommandHandler`'s doc said *"the permission is still per row"*; `grep RequiredPermission`
found it only in the read handler. The read model carefully implemented a control the write model claimed to
share.

And `toForm`'s comment said a key the server did not send *"stays null, which is correct"* — while the code
did the opposite of what the comment described: the key stayed `null`, the save PUT all four fields, and
PUT-replace means the API is told to **clear** them. The moment a `Settings.Edit`-without-`Social` role exists
— which is what #53 adds — that caller's first WhatsApp save silently wipes the client's social links. W1-12's
Blocker, one feature over.

**Both halves fixed rather than the comments deleted.**

*Server.* Both write handlers now read `Setting.RequiredPermission` through `IEffectivePermissionsProvider`
(from roles, never from principal claims — doc 08 §3.1 R4), **after** the diff so an unchanged key is never a
refusal, and **before** any mutation so a refusal leaves nothing half applied. A refused key is a new
`SettingsWriteResult.Forbidden` → **403** with `code: setting_forbidden` and the keys named. Silently skipping
was rejected: a save that reports success and does not do what it was asked is worse than a refusal.

*Absent vs null.* The endpoint now reads the body as a `JsonObject` and tells **absent** ("I am not writing
this key") from **null** ("clear it"). The command carries a dictionary of the fields the request actually
sent. The logo route goes further and **refuses** an absent `logoAssetPublicId` with a 400, because that
route's whole job is an explicit decision and a screen that stopped sending the field would otherwise delete
the shop's logo. A side benefit: a field of the wrong JSON type is now a named field error instead of
"malformed JSON" — the right side of issue #47's family.

*Client.* `toForm` omits a key the payload did not contain, `ContactAndSocialValues` fields are optional, and
the screen renders the WhatsApp box only when the `Contact` group came back and the three social boxes only
when `Social` did — so the screen, `toForm` and the server all agree about what is being written.

**Red before, green after:** `toForm`'s three omission tests failed with
`expected [ 'whatsApp', 'facebookUrl', …(2) ] to deeply equal [ 'whatsApp' ]` (4 failed / 230 passed); after
the fix, 237 passed. The ordering discovery on the way: the integration test for the 403 first came back
`Expected: Forbidden / Actual: Conflict`, because changing `RequiredPermission` is an UPDATE that moves the
rowversion — so **concurrency is checked before permission**, which is the right order (tell a stale form to
reload before telling it about permissions) and is now written down in both tests.

## The six minors

1. **Store the normalised URL.** The old rule kept the raw text, reasoning that normalising "would show a
   client something other than what they pasted" — which was backwards and created a homograph hole. U+3002
   (`instagram.com。evil.example`) was accepted, and both .NET's `IdnHost` and Chromium map it to an ordinary
   dot, so **the stored text read `instagram.com` while the link went to `evil.example`**. U+FF20 and U+FF0F
   were accepted server-side and *throw* in the browser, so the client got a 204, no error anywhere, and an
   icon that never appeared. `TryNormaliseProfileUrl` now substitutes `IdnHost` and stores
   `UriBuilder(...).Uri.AbsoluteUri`. **Measured first, because the obvious fix is wrong:** `AbsoluteUri`
   alone lower-cases the host, drops a default port and fills an empty path but **leaves the host's Unicode as
   typed**, so the ASCII host has to be substituted deliberately. The reader re-normalises on the way out, and
   the storefront's own `href` is now `URL.href` rather than the raw text, so the value checked and the value
   followed cannot differ.
2. **Comparers aligned to `OrdinalIgnoreCase`**, matching `Latin1_General_100_CI_AS_SC`. A row scripted in as
   `social.facebookurl` was found by SQL, "realigned" by the seeder (which reported success), and then missed
   by the ordinal dictionaries — so the box vanished from the screen and every save answered `NotSeeded`,
   which is a **500, for ever**, with the advertised repair path reporting nothing wrong.
3. **The handover moved below the vetting.** `tap(give)` sat upstream of `timeout`/`catchError`, so a failed or
   slow read handed over nothing and the browser re-fetched — and if that succeeded the logo popped in after
   hydration, the flash `site-header.ts` claims to prevent. W2-07's Major 3, one feature over. **Mutation-
   checked:** putting the old order back fails the new test with `expected [] to deeply equal [ {…} ]`.
4. **`SiteSettingsReader` now reads `IsSecret`.** The tester's correction to the brief was right: it was two
   floors plus a shape, not three. One `.Where(s => !s.IsSecret)` makes the third real, and the new test uses
   a value that *passes* format validation so it cannot pass for the old reason.
5. **`OwnerLabel` is a name again.** It was a 185-character `Description` in a field doc 08 §3.3 documents as
   "a name a person recognises". `SettingDefinition` gained a short `Label` (≤ 60 chars, asserted), read from
   the catalogue rather than the row — so no query, no column and no second migration, and it labels correctly
   even on a database whose row was scripted in with different casing.
6. **Dead code and slips:** the unread `WhatsAppMaxLength` is gone (it promised a message that never happened);
   the `min-width` rationale now sits above its own declaration; `AdminSettingsWriteShapeTests` →
   `AdminSettingsShapeTests`; and the doc 11 reference is P13 step 13 (line 1419) plus P14's permission table
   (line 1494).

**A third over-promising comment, found by my own probe rather than reported:** both the backend rule and the
storefront service claimed .NET gives `http:///profile` an *empty host*. It does not — `Uri.TryCreate` returns
false for it outright. The outcome (refused) was always right; the stated reason was not, which is the kind of
thing the next person builds on. Corrected in three places with a test that pins the real behaviour.

## Counts after round 1

| Suite | Round 0 | Round 1 | Failures |
|---|---|---|---|
| Backend unit | 1088 | **1126** | 0 |
| Backend integration (real SQL Server) | 663 | **674** | 0 |
| Frontend unit — shared / storefront / admin | 21 / 1033 / 232 | **21 / 1042 / 237** | 0 |
| Playwright (full, `CI=1`) | 643 | **647** | 0 of mine (one `bag.e2e.ts` flake, retried green) |
| Release build | clean | **clean** | 0 warnings |
| `ng lint` | clean | **clean** | — |

Ports: the same private verified-free block **5620–5632** with `CI=1`. No process, port or database left behind.

## What I would still flag

- The frontend's URL rule and the backend's are now two legible implementations of one rule, but they are
  still two. A contract test driving both from one table of inputs would be better than two tables that agree
  today.
- `SettingsGroupVersion` embeds each row's key verbatim, so its token technically depends on the stored casing
  of the keys. Both sides read the same rows so they cannot disagree, and the ordering is now
  case-insensitive — but normalising the key inside the token would remove the thought entirely.
