using SpencerDenim.IntegrationTests.Infrastructure;

[assembly: AssemblyFixture(typeof(SqlServerDatabaseFixture))]

// One migrated and seeded database is shared by the whole assembly, and a number of tests mutate it on
// purpose (the soft-delete guard, the audit columns, the check constraints, five `ops.Settings` keys).
// Running test classes in parallel lets an assertion over shared state observe another class's
// half-applied fixture, which is what this attribute is here to stop.
//
// **It does stop it, and that is now measured rather than assumed in either direction.** Four probe
// classes — two of them taking `SqlServerDatabaseFixture` — each occupying three seconds, logged start and
// end times with zero overlap and ran strictly one after another; a probe without the fixture behaved the
// same, which rules the fixture out as the thing doing the serialising. There is no `xunit.runner.json`
// anywhere in the repository to override this attribute. On xunit.v3 3.2.2, in this assembly, it works.
//
// **A W3-04 round-2 comment here claimed the opposite and called it measured. It was wrong, and the
// comment it replaced was right.** What that round actually reproduced was a *permanent leak*, not a
// window: round-1 `AdminSettingsEndpointTests.RestoreAsync` set every catalogued key to NULL, including
// `Orders.AutoDeliverAfterDays`, whose catalogued `SeedValue` is "7" — so the key stayed NULL for the rest
// of the run, and an assertion about its resting value failed if and only if it happened to run *after*
// that class. The non-determinism was xUnit varying class **order**, which is permitted and expected;
// about one run in three is what that looks like, and it is also why the test passed twenty times alone.
// Recorded at length because the wrong diagnosis was briefly load-bearing in four files.
//
// **The rule that holds, unchanged by any of this.** A test owns what it changes and restores it to the
// *catalogue's* resting value rather than to a guessed one — a restore that invents NULL for a key the
// seeder gives a value is a leak that no amount of serialisation saves you from. An assertion over a
// shared table should still scope itself to rows it owns or wrap its mutation in a rolled-back
// transaction (see `SettingsSchemaAndSeedTests.The_seeder_writes_a_missing_keys_catalogued_seed_value`),
// because that is what keeps a test honest if parallelism is ever turned on — which is #114's to decide,
// and is now a question about unsafe *fixtures* rather than about this attribute.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
