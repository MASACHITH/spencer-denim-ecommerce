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

**One caveat worth knowing rather than hiding.** If this step is the thing that fails, readiness says "The
reference data could not be established, so this host cannot take an order: …" plus the exception. For a
role-claims failure that over-states the problem — the shop *can* take an order; a staff user is short a
permission. The phase's wording is shared with the rest of the reference data, and
`FreshDeploymentTests.A_connectable_database_with_no_migrations_applied_is_not_ready` asserts that exact
phrase is present while the lock-contention test asserts it is absent, so it was left alone rather than
loosened for one step. In practice everything that can
throw here — a missing table, an unreachable server, a permission — would already have thrown in
`SettingsSeeder` one step earlier, where the wording is right.

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
| `Store.Email`, `Store.FaviconAssetId`, `Store.DefaultOgImageAssetId` | Nothing reads them. The invoice's seller block (`IOrderSettingsReader.ReadInvoiceSellerAsync`) reads `LegalName`/`Address`/`BRNumber`/`VatNumber` and prints no email; the favicon is a static asset (`index.html`'s `link rel=icon`); `PageMetadata` takes `og:image` from the page's own image and removes the tag when there is none. |
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
| `Email.Smtp.*` (7) | **#113's guess that the email gap explains these is out of date**: `ops.EmailOutbox` (#133) and `ops.AdminAlerts` (#138) both exist now. Two things still block them: the transport is handed the bound `EmailOptions` singleton (`Email__Smtp__*`, read once at startup), so a row would configure nothing; and N2's dual send — the previous SMTP configuration carried as ciphertext in `EmailOutbox.TransportOverrideProtected`, so that a hijacked SMTP change cannot silence its own alert — does not exist. Making the keys editable first would **remove** a control. |
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
is `Seed.Version`, which is the seeder's own bookkeeping. Two tests hold the line, and they are deliberately
different: a unit test asserts the **catalogue** carries a seed value only where doc 08 §3.8's own cell
states the default (and then that exact value), and an integration test asserts the **whole table** on a
fresh deployment holds a value only where the catalogue does. The first catches an invented default; the
second catches anything between the catalogue and the table inventing one. Neither alone is enough — see
§5.

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

## 5. The tests, and the mutation that killed each one

Every new test was proven able to fail. The mutations were applied one at a time, with a build and a run
each, and reverted (`git checkout -- src tests`; never `git stash`, the stack is shared).

| Test | Mutation that killed it |
|---|---|
| `Baseline_plus_increments_reproduces_the_shipped_definition` (Store Manager) | `Customers.Edit` removed from version 2's increment while `DefaultRoleDefinitions` keeps it |
| …(Order Staff) | the baseline's `Order Staff` key renamed to `Order Staffs` |
| …(Content Editor) | `Content.Delete` removed from the Content Editor increment |
| `Current_is_the_highest_declared_increment_version` | `Current` raised to 3 with no version-3 increment |
| `Every_increment_is_above_the_baseline_version` | an increment declared at version 1 |
| `No_increment_repeats_a_baseline_permission` | `Content.Edit` added to version 2's increment |
| `Every_permission_in_the_history_is_grantable_to_a_role` | `Settings.Security` added to an increment |
| `Every_named_role_is_a_default_role` | the baseline's `Order Staff` key renamed |
| `Since_returns_the_increments_above_the_recorded_version_and_no_others` | `>` changed to `>=` |
| `Version_two_grants_the_three_permissions_issue_122_names` | `Customers.Edit` removed from the increment |
| `The_parse_of_doc_08_section_3_8_finds_the_keys_it_is_supposed_to` | the parser made to return an empty list |
| `Every_specified_key_is_either_seeded_or_recorded_as_deferred` | `Store.Name` misspelt in `DeferredSettingKeys` |
| `No_key_is_both_seeded_and_recorded_as_deferred` | `Contact.Phone` added to `DeferredSettingKeys` |
| `Nothing_seeded_or_deferred_is_absent_from_doc_08` | `Store.Nickname` added to `DeferredSettingKeys` |
| `Every_deferral_carries_a_reason_and_appears_once` | `Email.Smtp.Host` listed twice; separately, a reason shortened to "Not built yet." |
| `Every_deferred_key_with_a_code_fallback_falls_back_to_the_documented_default` | `CatalogOptions.NewArrivalDays` changed from 30 to 31 |
| `A_seeded_value_exists_only_where_doc_08_states_the_default` | `Bank.BankName` given `SeedValue: "Some Bank PLC"` |
| `The_seed_version_marker_is_not_a_catalogue_key` | `Seed.Version` added to `SettingCatalog` |
| `A_permission_added_after_seeding_reaches_a_role_that_already_exists` | `Since(recorded)` made to yield nothing |
| `A_permission_the_client_removed_is_not_put_back` | the grant loop made to apply the role's whole shipped definition instead of the increment's additions |
| `The_step_reports_what_it_will_not_grant_by_name` | the drift scan made to report nothing |
| `Running_the_step_again_grants_nothing_and_leaves_one_row_per_permission` | `Since(recorded)` made to yield nothing |
| `A_newer_marker_is_left_alone_and_an_unreadable_one_re_offers_every_increment` | the `recorded > Current` guard removed; separately, an unreadable marker treated as `Current`; separately, the per-row idempotence guard removed |
| `Three_hosts_starting_at_once_grant_each_permission_exactly_once` | `Since(recorded)` made to yield nothing |
| `A_fresh_deployment_seeds_every_catalogue_key_and_no_deferred_key` | `SettingsSeeder` made to skip the first catalogue key |
| `Only_the_keys_whose_default_a_document_states_are_seeded_with_a_value` | `SettingsSeeder` made to write `SeedValue ?? "TBC"` |

### Two of them could not fail when first written, and the mutations are why that is known

1. **`A_permission_the_client_removed_is_not_put_back`.** It removed a baseline permission with the marker
   already at the current version, so `Since()` returned nothing and the grant loop never ran. The
   mutation that replaced `increment.AddedPermissions` with the role's whole shipped definition — the
   tempting wrong fix, the one N13 forbids — **passed it unchanged**. It now also winds the marker back, so
   the step is granting on that very role in that very pass and still has to leave the removed permission
   alone.
2. **`Only_the_keys_whose_default_a_document_states_are_seeded_with_a_value`.** Its expected set was
   computed from `SettingDefinition.SeedValue`, so giving `Bank.BankName` a plausible bank name mutated
   both sides of the comparison and the test **passed**. It is the right check for the *seeder* and cannot
   be the check for the *catalogue*; the catalogue half is now a unit test whose permitted values come
   from doc 08 §3.8's own cells.

### One existing test caught a regression this branch caused

`FreshDeploymentTests.The_seed_lock_is_released_before_the_demo_catalogue_is_written` watched for
`Settings.Count() == SettingCatalog.All.Count` as "the reference phase has finished". The new step runs
after the settings seeder and commits the `Seed.Version` row, so that equality now holds only for the
microseconds between two saves and the 100 ms poll never saw it. It failed with "the demo catalogue never
took long enough to observe", which is precisely what that message is for. The marker is now the
`Seed.Version` row itself — whatever the phase writes last, which is what the test always meant.

### Numbers

| Suite | Before | After |
|---|---|---|
| Unit (xUnit) | 1,980 | **1,998** (18 new) |
| Integration (xUnit, real SQL Server) | 1,357 | **1,365** (8 new) |
| Vitest | 24 files / 1,417 / 495 | unchanged — no frontend file is in this branch's diff |
| Playwright | 974 | unchanged |

Both backend figures were measured, not taken from the brief: the first whole-assembly integration run
reported 1,365 total, and 1,365 − 8 = 1,357 matches what main was said to have. Likewise 1,998 − 18 =
1,980. `dotnet ef migrations has-pending-model-changes` answers "No changes have been made to the model
since the last migration" after a rebuild.

Test databases: every one this branch created is a `SpencerDenim_Deploy_<guid>` made and dropped by
`FreshDeployment`, plus the fixture's own `SpencerDenim_Test_<guid>`. A listing of `sys.databases`
afterwards shows **no `SpencerDenim_Deploy_*` left behind**. Nothing else was touched —
`SpencerDenim_Dev`, the eight `SpencerDenim_W113_*`, `SpencerDenim_W103_Tester` and the
`SpencerDenim_Test_*` databases were left alone, including any that an interrupted run of this branch's
own fixture may have orphaned: they are indistinguishable from other sessions' and dropping one on a guess
is how somebody else's work disappears.

**Two notes for whoever runs this next.** `--filter-class` passed after `--` is ignored here — the project
runs under VSTest, so the filter is `--filter "FullyQualifiedName~X"`, and the two classes this branch
adds plus `FreshDeploymentTests` run in **1 m 15 s** against the 17-plus minutes of the whole assembly.
And the suite really does look hung: a mid-run check of `sys.dm_exec_sessions` showed every session
`sleeping`, which is normal — the work is in managed code between queries. Read the wall clock, not the
appearance; one run of it was stopped here on a misjudgement of elapsed time and had been perfectly
healthy.

## 6. Owed, or worth a line in somebody else's issue

1. **`Social.YouTubeUrl`** needs a glyph in `@spencer-denim/shared`'s `IconName` and then a catalogue
   row. Small, frontend, out of #113's scope.
2. **`SettingKeys`' "what is deliberately absent" prose** is now a summary of `DeferredSettingKeys`
   rather than the record. If the two ever disagree, the machine-checked list is the one to trust.
3. **Doc 08 §3.1 line 340** promises "a startup check logs unknown values" for `RoleClaims`. There is
   still no such check — `PermissionCatalog` exists, nothing compares the table against it. It is one
   query away from the step this branch adds and was left out as outside #122's scope.
4. **#105 (role CRUD)** remains the only screen that would let a Super Admin fix drift themselves. Until
   it ships, §1 step 3's SQL is the remedy.
