using System.Globalization;
using System.IO.Pipes;

// This executable has no gateway dependencies, config, HTTP listener, or shared stores.
// Pipe EOF also ends its lifetime if the test host disappears without disposing the fixture.
using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
await pipe.ConnectAsync(deadline.Token);
using var reader = new StreamReader(pipe);
using var writer = new StreamWriter(pipe) { AutoFlush = true };
await writer.WriteLineAsync($"ready:{Environment.ProcessId}");
// Once ready, only an explicit command or parent pipe EOF ends the child lifetime.
var command = await reader.ReadLineAsync();
return command is not null && command.StartsWith("exit:", StringComparison.Ordinal)
    ? int.Parse(command[5..], CultureInfo.InvariantCulture)
    : 0;
