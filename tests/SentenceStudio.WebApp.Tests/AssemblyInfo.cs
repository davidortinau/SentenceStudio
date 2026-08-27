// These tests boot the real WebApp host, and the host reads its connection string, API base
// address, and signing key from environment variables — the only configuration source that is in
// place before minimal hosting validates its container. Environment variables are process-wide, so
// two factories alive at once would read each other's settings.
//
// Serialising the assembly is the right trade here anyway: each test owns a PostgreSQL database
// and a loopback listener, and running those concurrently buys little.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
