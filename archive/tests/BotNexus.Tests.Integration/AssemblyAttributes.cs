using Xunit;

// All integration tests that modify BOTNEXUS_HOME must be serialized because
// environment variables are process-global. Parallel tests would race on the value.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
