using SpencerDenim.IntegrationTests.Infrastructure;

[assembly: AssemblyFixture(typeof(SqlServerDatabaseFixture))]

// One migrated and seeded database is shared by the whole assembly, and a number of tests mutate it on
// purpose (the soft-delete guard, the audit columns, the check constraints, five `ops.Settings` keys).
// Running test classes in parallel lets an assertion over shared state observe another class's
// half-applied fixture, which is what this attribute is here to stop.
//
// **It does not currently stop it, and that is measured rather than suspected.** W3-04 reproduced a
// cross-class settings race at about one run in three with a four-class filter while the same test passed
// twenty times alone — so classes in this assembly do interleave despite this line. The attribute still
// compiles on xunit.v3 3.2.2; whatever it did on v2, the observable behaviour here is parallel. The
// previous version of this comment asserted "the assembly runs sequentially", and a wrong comment about
// the harness is why #114's family keeps being diagnosed as a leak.
//
// **Do not read this as licence to assert global state.** The rule that actually holds is the one the
// suites already follow: a test owns what it changes and restores it, and an assertion over a shared
// table either scopes itself to rows it owns or wraps its mutation in a rolled-back transaction (see
// `SettingsSchemaAndSeedTests.The_seeder_writes_a_missing_keys_catalogued_seed_value`). Turning
// parallelism off properly — via `xunit.runner.json`'s `parallelizeTestCollections` — is a harness-wide
// decision with a runtime cost and belongs with #114 rather than with a feature branch.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
