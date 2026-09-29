// These tests boot the real WebApp host, and the host reads its connection string, API base
// address, and signing key from environment variables — the only configuration source that is in
// place before minimal hosting validates its container. Environment variables are process-wide, so
// two factories alive at once would read each other's settings.
//
// Serialising the assembly is the right trade here anyway: the opt-in integration tests each
// create a database on an explicitly owned disposable cluster and use a loopback listener.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
