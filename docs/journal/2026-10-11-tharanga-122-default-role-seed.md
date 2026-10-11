# 2026-10-11 — #122: a new permission reaches the default roles of a database that already exists

Branch `fix/122-default-role-permissions-seed`, worktree `worktrees/sd-roleseed`, cut from `origin/main` at
`31e6aa6`. Issue #122's option 1 (a versioned seeder step, which is what doc 08 §9 / N13 anticipate) plus its
"make it visible" part. **Not pushed, not a PR.** **No database migration** — the applied version lives in
`ops.Settings`.

## What was wrong

`AdminSeedHostedService` creates Store Manager, Order Staff and Content Editor only when they do not exist and,
per N13, never touches one that does. So a permission added to `DefaultRoleDefinitions` later reached fresh
databases only. W2-11 had already added three (`Customers.Edit`, `Content.Create`, `Content.Delete` — five
role grants), and every remaining permission-adding task would add more. The symptom is the worst kind: the
screen is there, the button refuses, the developer who built it tested as Super Admin on a fresh database, and it
arrives as a support call from the client's Store Manager.

## What it does now

On every start, in the reference-data phase, `DefaultRolePermissionSync`:

1. takes a transaction-scoped application lock (`SpencerDenim:DefaultRoleSeed`, 15 s) and reads
   `ops.Settings` `Seed.Version`;
2. for each default role that **exists, is a system role and is not GrantsAll**, inserts only the permissions
   `DefaultRoleSeedManifest` says were introduced after that version (and that the build still ships for that
   role), skipping any the role already holds;
3. writes a `PermissionChange` audit row per role that gained something (no acting user, before/after lists) and
   regenerates the `SecurityStamp` of that role's holders;
4. records the current version, **monotonically** (never lowers it), and commits;
5. then compares every default role with its shipped definition and logs the differences by name.

It never removes a permission, never touches a role's other permissions, never creates a role, never touches Super
Admin (or any `GrantsAllPermissions` role), a custom role, or a role that only shares a default role's name
without being the seeded system role (that one gets a Warning).

### The manifest and the baseline — the part to check

`DefaultRoleSeedManifest` (Domain, framework-free):

- `BaselineVersion = 1` — what the three roles held when they **first shipped, in W1-08 (#102, 2026-10-05)**.
  `DefaultRoleDefinitions.cs` was created by that commit; before it only Super Admin was seeded, so no database
  anywhere holds an older shape (a database from before W1-08 gets the roles created complete by the admin seed).
  I read the file at `4a32705` to get the version-1 sets: Store Manager 37, Order Staff 6, Content Editor 18.
- **Version 2 = what W2-11 (#127, `31f3b27`) added**: Store Manager `Customers.Edit`, `Content.Create`,
  `Content.Delete`; Content Editor `Content.Create`, `Content.Delete`. Order Staff gains nothing.
- `CurrentVersion` = the highest version listed (2). Anything not listed is a baseline permission, so the manifest
  stays five lines long; the frozen version-1 record lives in the unit test as the witness (see below).

**An absent `Seed.Version` is treated as the baseline (1).** That is the choice that makes the three known-missing
permissions arrive on existing databases (they are version 2, and the database is at 1) while a role that
deliberately lacks a version-1 permission is not changed. In the code that is almost free: the manifest lists only
post-baseline introductions, so a baseline permission is never a candidate for granting whatever the stored value
(I confirmed this by mutation — setting the default to 0 changes no test, because it changes no behaviour; it is
an equivalent mutant, and `BaselineVersion` is kept for what it says).

**The one thing the baseline cannot know**, written in the manifest's remarks: a database created *after* a later
introduction, from which somebody removed that permission *by hand in SQL* before this step existed, gets it back
on the first start (nothing is recorded to say it was removed). There is no role editor yet (#105), so only a
developer with SQL access could have done that; from that first start on, the recorded version protects every
later removal. On `SpencerDenim_Dev` and on every test database the first start simply fills the gap, which is the
point.

### Where it runs, and why not in `AdminSeedHostedService`

That service does nothing unless `Seed:Admin:Enabled` is on, and the deployment guidance is to turn it off once the
real Super Admins exist. A step living there would **never run on the client's live database** — the one database
this exists for. So it is called from `CatalogSeedRunner.RunReferenceDataAsync` (last, after the settings seeder),
which runs on every start of every environment, already sits inside the reference-data seed lock and retries a late
database. Doc 08 §9 lists "default roles and their RoleClaims" under that same idempotent seeding step. `AdminSeed`
still creates a missing role, with its full current definition; nothing else creates one.

It uses a scope of its own (own `DbContext`, own connection), so it does not share a change tracker or a transaction
with the seeders before it. A failure is logged at Error and **swallowed** (`TryRunAsync`): the reference data it
follows is what lets the shop take an order, and a permissions reconciliation that cannot finish must not make a
working shop report itself unready. The price is that a failure is retried only on the next start; it is logged
with that consequence.

### Fresh database

No special case. Either order of the two seeders is right: if the admin seed ran first the roles are complete and
the step finds nothing to add and records the current version; if the step ran first there is no role to touch and
it records the current version anyway, because a role created afterwards is created complete. Tested through the
real host.

### Concurrency

Two overlapping starts serialise on the lock (`sp_getapplock`, `Transaction` owner, so the commit releases it —
the pattern `StaffUserRoleManagementStore` and `CatalogSeedHostedService` already use). The second re-reads the
version the first committed and does nothing. A lock wait that runs out is Information, not a failure: the holder
is doing the same job. `UX_RoleClaims_Role_Type_Value` stays underneath as the floor; no violation is expected and
the test releases eight runs at once and sees none. **I removed the lock to prove the test can fail: it did.**

### Storage of the version: `ops.Settings` `Seed.Version`, deliberately outside `SettingCatalog`

Doc 08 §9 names the setting (`Seed.Version`) and `ops.Settings` already exists, so there is no migration. The
catalogue is "the allow-list in both directions" — the keys a screen offers and a write path can reach — and this
is system-owned, so it is **not in it**: every read model and write handler works from the catalogue's keys, so
none can see or edit the row, and `SettingsSeeder` leaves rows it does not know alone (a "withdrawn key" is an
anticipated state). `RequiredPermission` is `Settings.Security` (GrantsAll-only, grantable to no role), the strictest
the schema allows, in case a future generic reader ever lists every row. Group `Seed`; type Int.

The cost, honestly: `FreshDeploymentTests.The_seed_lock_is_released_before_the_demo_catalogue_is_written` used
`COUNT(ops.Settings) == SettingCatalog.All.Count` as its marker for "the reference phase is done", and the extra
row stopped it ever matching ("the demo catalogue never took long enough to observe"). I changed the marker to
`All.Count + 1` with a comment. That is the only existing test touched. Doc 08 §3.8's groups table has no `Seed`
group; the owner may want a line saying it is system-owned and uncatalogued.

I chose the doc's key name over a more specific one (`Seed.DefaultRolesVersion`). It is the version of the
default-role manifest and nothing else; if another seeder ever needs a version it must not reuse this key.

### Signed-in users and the permission cache

`EffectivePermissionsProvider` caches per `(userId, SecurityStamp)` for five minutes, and a role's claims changing
moves **nobody's** stamp. So a grant alone is invisible to a signed-in user until the cache entry expires — and in a
rolling deployment an older instance keeps its entry. At a **single-instance** start the cache is empty and
permissions are read from the database per request (never from the cookie, R4), so the new permission is visible
at once; but **sessions survive a restart** (cookies, persisted data-protection keys), so "nobody is signed in" is
not actually true, and the admin SPA's permission list is whatever it fetched at its last load.

So the step does what doc 08 §3.1 DB4 rule 5 prescribes for any role or permission change: it regenerates the
`SecurityStamp` (and `ConcurrencyStamp`) of every holder of a role that **gained** something, in the same
transaction. That changes the cache key on every instance, and asks the session to revalidate within the stamp
interval (one minute), so the user signs in again and the SPA refetches. A role that gained nothing moves nobody's
stamp. **Trade-off to veto if you want:** staff in an affected role are signed out about a minute after the deploy
that grants them something, once per release that bumps the version. Removing it is one call in `ApplyAsync`; the
test `A_user_holding_the_role_sees_the_new_permission_on_the_next_request` then has to wait out the cache instead
(I removed the call to prove the test fails without it: it did).

## What an operator reads

Category `DefaultRolePermissionSync`, ids 1501–1510, once per start, role and permission names only (no user, no
request, no secret):

- one Information line when nothing differs — `Default roles are in step with the shipped definitions: 3 of 3
  default role(s) checked at default-role seed version 2, 0 permission(s) granted this start.`;
- Information per role per grant — `Default role 'Store Manager' was granted 3 permission(s) added after
  default-role seed version 1 (now at 2): Customers.Edit, Content.Create, Content.Delete.`;
- **Warning** per role for permissions the shipped definition includes and the role lacks, which the version gate
  declined to re-grant (removed after the fact), by name;
- Information per role for permissions the role holds beyond the definition (added by a Super Admin);
- Information for an absent role; Warning for a same-named non-system role, a recorded version newer than the
  build, and an unreadable recorded value; Error if the whole step failed.

One line per role per direction rather than one per permission, so a role a client has deliberately trimmed is one
line, not thirty. **That Warning will repeat on every start for as long as a client's deliberate removal stands**;
there is no way to silence it short of restoring the permission, and I think that is right for a "somebody should
know" line, but it is the thing most likely to be called noisy.

## Tests

Unit (`DefaultRoleSeedManifestTests`, 19 cases; `DefaultRolePermissionSyncFailureTests`, 3). The rule that stops
this recurring: **every permission a default role ships has a version in 1..Current, and one that no release
introduced must be in the frozen version-1 record** (written out in the test, not derived from the code under
test). Add `Reports.View` to Order Staff without a manifest entry and it fails, with the steps to take in the
message — I did exactly that to prove it. Also: Current equals the highest version; versions run from 2 to Current
without a gap; every entry names a default role that ships that permission; no duplicates; nothing introduced at or
below the baseline; nothing that cannot be granted to a role (`Settings.Security`); version 2 pinned to W2-11's five
grants; Order Staff introduced to nothing; role names match case-insensitively.

Integration (`DefaultRolePermissionSyncTests`, 16) on **a database of their own** (`DefaultRoleSyncDatabase`, an
xUnit class fixture that takes the assembly fixture): the shared one asserts the exact permission set of the same
three roles, and a test that puts them into old states and restores them is a test that leaks when it fails
half-way. They cover: fresh database through the real host; the old roles get exactly the five grants and the
version, with the audit rows; a second start changes nothing (same claim row ids, same stamp, same row version, one
log line); a permission removed after its version was applied stays removed; a baseline permission on a database
with no version stays absent; an added permission is kept and reported; Super Admin, a custom role and a look-alike
non-system role untouched; a missing role not created; no roles at all still records the version; eight
overlapping runs; a user holding the role sees the permission on the next request through the real
`IEffectivePermissionsProvider` and its cache, and a user in Order Staff keeps their stamp; a newer recorded
version; an unreadable one; the reference-data phase brings the roles up with no admin-seed flag; the row is not a
catalogue setting.

Mutations proven to fail: stamp refresh removed (1 test), lock removed (the overlap test), permission added without a
manifest entry (the rule). Equivalent mutant noted above.

## Not done, and things for the owner

- **`docs/15-deployment-runbook.md` is not on `main`** — it exists only on `feature/W0-15-docker-packaging`, which I
  must not touch. The "adding a permission" note, the log lines and the **launch-day check** are in
  `docs/06-engineering-process.md` §5a instead (with lesson L7). When W0-15 merges, the three launch-day checks
  (`Seed.Version` equals the manifest's current version; the log shows the in-step or granted line and no Error;
  sign in as a Store Manager and a Content Editor and open a screen guarded by the newest permission) belong in
  the runbook's launch-day list, and the hand-grant SQL as option 2's safety net.
- **A brand-new default role** is created only by the admin seed behind `Seed:Admin:Enabled`; this step never
  creates one, as briefed. If a later release adds a fourth default role, the client's live database will not get
  it unless that flag is on for one start. Worth a sentence in the runbook.
- **#105 (role CRUD)** inherits this: it must not touch `Seed.Version`; a role editor that changes a default
  role's permissions should regenerate the holders' stamps the way this step does (that is #105's "stale
  permission snapshot" blocker, and this is the same mechanism, not a second one); and an `IsSystem` role must not
  be deletable, or a recreated role would be offered the baseline's later grants again.
- **Doc 08 §9** says the version is recorded in `Seed.Version`; that is what the code uses, and §3.8's groups table
  has no `Seed` group (see above). I did not edit doc 08.
- The first start on an existing database with no recorded version re-grants a later-version permission that was
  removed **by hand in SQL** before this existed (see the baseline section). Nothing else can be removed by hand
  today.
