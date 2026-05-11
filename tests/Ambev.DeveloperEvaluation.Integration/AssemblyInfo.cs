using Xunit;

// Integration collections each own a Testcontainers Postgres instance and
// set process-level env vars (ConnectionStrings__DefaultConnection,
// RateLimit__PermitLimit, etc.) to satisfy Program.Main's early config
// reads. Running collections in parallel would race on those env vars and
// either tear down each other's hosts or corrupt the rate-limit config.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
