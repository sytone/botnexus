// Disk-backed config mutation tests share a process-wide BOTNEXUS_HOME environment variable and
// drive the real filesystem plus the process-static PlatformConfigWriter write lock. Running them
// in parallel would let one test's temporary home leak into another's resolution. The suite is
// small and I/O bound, so serialising the whole assembly is the honest, deterministic choice.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
