// #2833: SqliteStoreIdentityGuard installs a PROCESS-WIDE identity, because a world identity is a
// property of the running process rather than of a call site. Parallel test collections in this
// assembly would therefore observe each other's Configure/Reset, so parallelisation is disabled
// here. The project is small (a few dozen SQLite tests) and the serial cost is negligible next to
// the flakiness a shared static would otherwise introduce.
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
