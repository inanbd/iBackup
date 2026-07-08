using Xunit;

// Each test class boots its own WebApplicationFactory over the same Program.
// Host interception via HostFactoryResolver is not safe when several factories
// start the entry point concurrently, and the classes share one database, so
// run integration test classes sequentially. (Concurrency is exercised inside
// individual tests, e.g. ConcurrencyTests, using parallel clients.)
[assembly: CollectionBehavior(DisableTestParallelization = true)]
