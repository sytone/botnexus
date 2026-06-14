using Xunit;

// The BotNexus.Cli.Tests assembly exercises real CLI commands, which share two pieces of
// process-global state that are fundamentally not safe to touch from concurrent test classes:
//
//   1. The static Spectre.Console.AnsiConsole.Console writer. Command code writes status and
//      error output through AnsiConsole.MarkupLine; tests that capture output swap the static
//      writer and dispose it afterward. When classes run in parallel, one class can dispose the
//      writer while another is mid-write, producing
//      "ObjectDisposedException: Cannot write to a closed TextWriter".
//
//   2. Loopback HttpListener sockets (MockHttpServer and inline listeners). Parallel classes
//      racing to bind "free" ports surface intermittent "Address already in use".
//
// Per-class [Collection("AnsiConsole")] tagging was an incomplete guard: every class that
// either writes through AnsiConsole or binds a socket has to be tagged, and any new test that
// forgets the attribute silently reintroduces the race. Because the classes that dominate the
// runtime (the real-timeout "unreachable gateway" tests and the git/process UpdateCommand
// tests) are all in that console-touching set, they get serialized either way — so tagging buys
// almost no wall-clock back while staying fragile. Disabling cross-class parallelization for the
// whole assembly is the durable fix: it cannot be defeated by a forgotten attribute, and the
// extra wall time on this one project is negligible inside the multi-minute CI test job.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
