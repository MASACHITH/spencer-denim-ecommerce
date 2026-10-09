# 2026-10-09 — #122 and #113: a seeder that is correct for a fresh database and incomplete for an existing one

Branch `fix/seed-existing-databases`, worktree `worktrees/sd-seed`, cut from `origin/main` at `31e6aa6`.
Scope: **doc 08 §9's versioned default-role permission step (#122) and the per-key decision for every
`ops.Settings` key §3.8 specifies and the catalogue does not seed (#113)**.
**Not pushed, not a PR. No migration — none was needed.**

Both issues are the same defect wearing two hats: a seeder that creates a thing only when the thing is
absent is correct on a fresh database and silently incomplete on one that already exists. Nothing has been
deployed, so neither has ever bitten. The client's database is about to be created.

---

## 1. Launch-day checklist (the part to read if you read nothing else)

**Nothing manual is required on a database created from this build or later.** The step runs on every
startup, unattended, inside the reference-data phase. What to do anyway:

1. **Create the database, apply the migrations, start the API** — doc 08 §10's three steps, unchanged.
2. **Read the startup log for one line.** On a correct deploy there is nothing to read: the step logs at
   `Debug` when there is no drift. If it logs `Default role '<name>' is missing N permission(s) …` at
   **Warning**, that line names the permissions. Grep the deploy log for `is missing` once.
3. **If that Warning appears on the client's first deploy**, something is wrong with the seeding, not with
   the client's choices — they have had no chance to remove anything. Compare the named permissions with
   `DefaultRoleDefinitions` and grant them. There is no role screen yet (#105 is blocked), so the grant is
   SQL: for each named permission,
   ```sql
   INSERT INTO identity.RoleClaims (RoleId, ClaimType, ClaimValue)
   SELECT r.Id, 'perm', N'<Permission>'
   FROM identity.Roles r
   WHERE r.Name = N'<Role>'
     AND NOT EXISTS (SELECT 1 FROM identity.RoleClaims c
                     WHERE c.RoleId = r.Id AND c.ClaimType = 'perm' AND c.ClaimValue = N'<Permission>');
   ```
   `Settings.Security` must never be inserted: `CK_RoleClaims_NoSecurity` refuses it and GrantsAll is the
   only route to it.
4. **If the same Warning appears on a later deploy**, it is probably the client's own edit and doc 08 N13
   says to leave it alone. Ask them before granting anything back.
5. **Staff do not need to sign out.** `EffectivePermissionsProvider` caches per `(userId, SecurityStamp)`
   with a five-minute TTL, so a permission granted at startup is in force within five minutes — and a
   deploy restarts the process, which empties the in-memory cache anyway.
6. **`Seed:Admin:Enabled` can be turned off after go-live without breaking the upgrade step.** That is why
   the step is not inside `AdminSeedHostedService`. Turning it off does stop a *new default role* being
   created, which is correct.

**What an operator must never do:** edit the `Seed.Version` row in `ops.Settings`. Lowering it re-offers
increments the client may have deliberately removed; raising it suppresses increments that have not been
applied. It is deliberately absent from `SettingCatalog`, so no screen offers it and the admin API cannot
reach it.

---

## 2. #122 — the versioned step

Option 1 of the four in the issue, with the startup check. Options 2, 3 and 4 are not implemented; role
CRUD (#105) is untouched.

### The crux: why only additions are applied

You cannot in general tell *"the client removed this permission"* from *"the role never had it"* — both
are an absent `RoleClaims` row. The mechanism does not need to. **A permission introduced in a version
above the one a database records cannot have been removed by that database's owner, because it did not
exist for them to remove.** So:

- `DefaultRoleSeedVersions.BaselinePermissions` is the **frozen** version-1 set, taken from commit
  `4a32705` (W1-08), which is the first and only release of `DefaultRoleDefinitions` before `31f3b27`.
- `DefaultRoleSeedVersions.All` is one increment per later version: the **additions only**.
- `DefaultRoleSeedUpgrader` applies `Since(recorded)` and nothing else. It never deletes a claim, never
  creates a role, never touches `GrantsAllPermissions` or `IsSystem`, and never compares a role against
  its whole shipped definition.

Version 2 is W2-11/GLB-10 (`31f3b27`, 2026-10-07): `git diff 4a32705 31f3b27` on that file is exactly
`+Customers.Edit, +Content.Create, +Content.Delete` on Store Manager and `+Content.Create,
+Content.Delete` on Content Editor — **three permission values, five role-permission pairs**, which is
what issue #122's table says.

### The one honest hole, which is closed from here on

A database with **no** `Seed.Version` row is treated as version 1. Such a database could in principle be
one seeded at the current version whose owner then removed `Content.Delete` — and the step would put it
back. That cannot have happened here: nothing is deployed, the three permissions are two days old, and the
only marker-less databases are development and test ones. Every database created from this build onwards
carries a marker, so the ambiguity does not recur. It is recorded in
`DefaultRoleSeedVersions.Baseline`'s own remarks rather than left for somebody to rediscover.

### The startup check, and its log level

`DefaultRoleSeedUpgradeResult.MissingPermissions` compares the shipped definitions against what each
existing default role actually holds and the step logs the difference **by name**, at **Warning**.

- Not `Error`: a difference is the *expected* state of a correctly configured shop whose owner removed a
  permission on purpose — N13 exists to let them — and this codebase has already paid for an Error on a
  healthy startup once (`CatalogSeedHostedService`'s lock timeout printed a stack trace and the wrong
  remedy against a database that was fine).
- Not `Information`: that is where the startup noise lives, and this is the one line that distinguishes
  "the client chose this" from "#122 happened again".
- `Serilog:MinimumLevel:Default` is `Information` in `appsettings.json`, so Warning is emitted.
- It names the permissions. "3 missing" tells nobody which button will refuse.

A grant is news and gets `Information`. A clean run gets `Debug`, so the quiet case is quiet.

### Where it runs, and what happens when things go wrong

It is a step of `CatalogSeedRunner.RunReferenceDataAsync`, after `SettingsSeeder` — doc 08 §9 puts
"default roles and their RoleClaims" in the same idempotent step as the rest of the reference data, and
the step records its progress in an `ops.Settings` row, so that table has to be reachable first.

| Failure | What happens |
|---|---|
| **Fails halfway** | The claims and the marker are one `SaveChangesAsync`, so they commit together or not at all. A throw leaves the marker at the old version, readiness goes red with a reason, and the phase's 10 s retry timer tries again with the host up and answering. Nothing is half-applied. |
| **Two or three instances start at once** | The phase holds `sp_getapplock` on `SpencerDenim:ReferenceDataSeed` (5 s timeout, 3 attempts, then the retry timer), so the step is serialised. `UX_RoleClaims_Role_Type_Value` and `PK_Settings` are the database's own backstop under that; a unique violation there is contention, which the phase already retries at `Information` rather than reporting a broken shop. Tested with three hosts released together. |
| **Runs against a database mid-migration** | `ops.Settings` or `identity.RoleClaims` not being there is a `SqlException` out of the step, which lands in the phase's deliberately broad catch: readiness red, the reason logged, the retry timer self-heals once the migration lands. In practice `SettingsSeeder` throws first, one step earlier. The host still starts, so liveness and the OpenAPI document still explain the problem. |
| **Roles do not exist yet** | The ordinary state of a fresh database at this point. Nothing sets `HostOptions.ServicesStartConcurrently`, so hosted services start one at a time in registration order and `AddHostedService<CatalogSeedHostedService>()` comes before `AddAdminIdentity()`; this step therefore finds no roles and grants nothing, and `AdminSeedHostedService` then creates each role from the **whole current** definition. **The safety does not rest on that order**: role creation is always complete-at-current, and the grant is idempotent per row with `UX_RoleClaims_Role_Type_Value` underneath, so a concurrent start costs at worst a unique violation the phase already retries. |
| **Marker above this build's version** | Left alone, logged at Warning, nothing granted. Lowering it would make a later upgrade re-offer increments the client may since have removed. |
| **Marker not a number** | Treated as the baseline: every increment re-offered (idempotent) and the value rewritten. Logged at Warning. Refusing to run would leave the roles short for as long as the bad value sat there. |

### What stops the next three tasks recreating this

`DefaultRoleSeedVersionsTests` asserts that the frozen baseline plus every increment reproduces
`DefaultRoleDefinitions` **in both directions**. A permission added to a role's definition without a
matching increment is a red build naming the role and the permission, before the branch is pushed — not a
staff user being refused by a screen that is there. `DefaultRoleDefinitions` now carries a comment saying
so at the point where someone would add one.

---

## 3. #113 — the settings keys

### The count in the issue is out of date

#113 says the seeder ships **5** keys. `SettingCatalog.All` has **20** (counted: `grep -c "^        new("`
on `SettingCatalog.cs`). The 5 was measured before W2-11 added five `Contact.*` keys, W3-03 added the five
`Bank.*` keys and W3-04 added four `Store.*` keys and `Orders.AutoDeliverAfterDays`.

Doc 08 §3.8's "Groups" table names **59** keys across **15** groups — both counted by parsing the table,
not by arithmetic. §9's "~50" is the approximation. So **39 keys are absent**, and which ones they are is
the finding.

Three files said "sixteen groups" (`SettingKeys`, `SettingCatalog`, `SiteSettingsEndpoints`). The table
has fifteen rows. Sixteen is §3.1's **module** count, which is a different list. All three corrected, and
`SettingKeySpecificationTests` now counts the groups from the document so the claim cannot drift again.

### The decision for each of the 39

**None can be correctly seeded today, and one will never be.** `DeferredSettingKeys` records all 39 with
the reason per key; the test suite diffs that list and `SettingCatalog` against the document, so the
decisions are source rather than prose in a report.

| Keys | Decision |
|---|---|
| `Store.Name` | **Withdrawn, permanently.** The shop's name is a build-time constant in one place per application (`StoreBrand.Name`, `BRAND`) by the owner's decision of 2026-10-02 (docs/02 §I2), with `BrandNameConventionTests` enforcing it. A row would give it a second source of truth, one of which the server renders the storefront from before any database is read. |
| `Store.Email`, `Store.FaviconAssetId`, `Store.DefaultOgImageAssetId` | Nothing reads them. The invoice's seller block reads `LegalName`/`Address`/`BRNumber`/`VatNumber` and prints no email; the favicon is a static asset; `PageMetadata` builds `og:image` from the page's own media. |
| `Social.YouTubeUrl` | `@spencer-denim/shared`'s `IconName` has no YouTube glyph, so a value would have nowhere to render. A row and an icon away — worth a one-line follow-up. |
| `Content.PdpReturnsInfoHtml`, `Content.PdpDeliveryInfoHtml` | PDP-05's panels do not exist. **The sanitiser blocker named in `SettingKeys` is stale** — W2-11 built `IHtmlSanitiser` — so the blocker is the panel, not N10. |
| `Orders.BankTransferExpiryHours` | Read from `Orders__BankTransferExpiryHours` (`BankTransferOptions`, default 48 = §3.8's). Seeding without moving the reader would give the payment deadline two sources that could disagree. Owed to ADM-22. |
| `Orders.PayPalExpiryMinutes` | No PayPal provider to expire anything. |
| `Inventory.DefaultLowStockThreshold`, `Catalog.NewArrivalDays`, `Catalog.BestSellerTopN` | `CatalogOptions`, defaults 5 / 30 / 10, all §3.8's own figures. |
| `Reviews.RequireApproval` | No review submission or moderation path; nothing reads it at all. |
| `Delivery.DefaultZone` | Checkout prices per district and asks for one before quoting, so no path needs a zone with no district. R7's use needs customer accounts (W3-01). |
| `Privacy.RetainInvoiceBillTo` | Nothing reads it; the PDPA anonymisation path is not built. Also needs N2's sensitive-change alert, every `Settings.Security` key being on N2's trigger list. |
| `Traffic.RawRetentionDays`, `Traffic.BotRetentionDays` | The analytics schema does not exist: no `PageViews` set, no migration creating `analytics.PageViews`. |
| `Payments.PayPal.*` (11) | No provider (W4-01), credentials not held, and `ClientSecret` is the first `Secret` row — whose write needs N2's alert. |
| `Email.Smtp.*` (7) | **#113's guess that the email gap explains these is out of date**: `ops.EmailOutbox` (#133) and `ops.AdminAlerts` (#138) both exist now. Two things still block them: the transport reads `IOptions<EmailOptions>`, so a row would configure nothing; and N2's dual send — the previous SMTP configuration carried as ciphertext in `EmailOutbox.TransportOverrideProtected`, so that a hijacked SMTP change cannot silence its own alert — does not exist. Making the keys editable first would **remove** a control. |
| `Security.StaffLockoutMaxFailures`, `StaffLockoutMinutes` | Applied to Identity's lockout options at startup from `AdminSecurityOptions`, defaults 5 and 15 = §3.8's. A live-editable row needs the options rebuilt per sign-in attempt, plus N2. |
| `Security.CustomerLockoutMaxFailures`, `CustomerLockoutMinutes` | No customer sign-in exists (W3-01, ACC-01). |

### The check that mattered more than the count

#113's second clause — *"anything reading a missing key falls back to whatever the code's default is,
which may not be what §9 specifies"* — is the half that would have been a defect. **It is not one here.**
Every deferred key that has a code-side fallback falls back to §3.8's own figure: 5 / 30 / 10 / 48 / 5 /
15. `SettingKeySpecificationTests` pins all six, with the expected numbers parsed out of the document's
own cells, so this is a doc-against-code check and not two copies of a constant.

### Invented data

Nothing is seeded with content. All 20 catalogue keys are seeded `NULL` except
`Orders.AutoDeliverAfterDays = 7`, which is a figure doc 08 §3.8 states. The one new row this branch adds
is `Seed.Version`, which is the seeder's own bookkeeping. An integration test asserts the *whole table* on
a fresh deployment holds a value only for the keys whose default a document states, so a future
placeholder is a red test rather than a reviewer noticing.

The empty-handling on the seeded side was checked rather than assumed: `BankTransferProvider` treats an
incomplete bank set as "instructions not available yet", `InvoiceSellerDetails`/`OrderSettingsReader`
return null fields that print as nothing, the footer renders no icon for an empty social address, and the
Contact page shows no map and no email address for an empty value. All four are existing behaviour with
existing tests.

---

## 4. Migration

**None.** Checked rather than assumed:

- `Seed.Version` is a **row** in `ops.Settings`, whose columns and CHECK constraints already exist
  (`20261005063301_OpsSettingsTable`). It needs `GroupKey` and `RequiredPermission` to be non-empty —
  `Seed` and `Settings.Security` — and `ValueType` in 1..8.
- The grants are **rows** in `identity.RoleClaims`.
- No identity schema was touched, so nothing here collides with customer accounts (W3-01).
- `dotnet ef migrations has-pending-model-changes` is clean after a rebuild.

---

## 5. Owed, or worth a line in somebody else's issue

1. **`Social.YouTubeUrl`** needs a glyph in `@spencer-denim/shared`'s `IconName` and then a catalogue
   row. Small, frontend, out of #113's scope.
2. **`SettingKeys`' "what is deliberately absent" prose** is now a summary of `DeferredSettingKeys`
   rather than the record. If the two ever disagree, the machine-checked list is the one to trust.
3. **Doc 08 §3.1 line 340** promises "a startup check logs unknown values" for `RoleClaims`. There is
   still no such check — `PermissionCatalog` exists, nothing compares the table against it. It is one
   query away from the step this branch adds and was left out as outside #122's scope.
4. **#105 (role CRUD)** remains the only screen that would let a Super Admin fix drift themselves. Until
   it ships, §1 step 3's SQL is the remedy.
