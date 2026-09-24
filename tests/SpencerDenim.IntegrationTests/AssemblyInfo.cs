using SpencerDenim.IntegrationTests.Infrastructure;

[assembly: AssemblyFixture(typeof(SqlServerDatabaseFixture))]

// One migrated and seeded database is shared by the whole assembly, and a few tests mutate it on
// purpose (the soft-delete guard, the audit columns, the check constraints). Running test classes in
// parallel would let a listing assertion observe that half-applied state, so the assembly runs
// sequentially. The whole suite still finishes in seconds.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
