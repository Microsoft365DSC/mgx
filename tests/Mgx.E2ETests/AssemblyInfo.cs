using Mgx.E2ETests.Infrastructure;

[assembly: AssemblyFixture(typeof(WireMockGraphFixture))]

// Every Mgx cmdlet reaches process-wide static state, so these tests cannot run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
