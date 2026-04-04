using Xunit;

// Several unit tests modify the process-global BOTNEXUS_HOME environment variable.
// Parallel execution causes race conditions where one test resets the variable while
// another resolves BotNexusHome paths, creating directories in ~/.botnexus/.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
