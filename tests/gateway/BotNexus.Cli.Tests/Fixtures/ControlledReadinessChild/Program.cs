using System.Globalization;
using System.IO.Pipes;

// This executable has no gateway dependencies, config, HTTP listener, or shared stores.
if (args[0].StartsWith("build-output-", StringComparison.Ordinal))
{
    switch (args[0])
    {
        case "build-output-unknown-failure":
            Console.WriteLine("opaque stdout [red]");
            Console.Error.WriteLine("opaque stderr [red]");
            break;
        case "build-output-huge-line":
            Console.WriteLine(new string('x', 1_000_000));
            Console.Error.WriteLine(new string('y', 1_000_000));
            break;
        case "build-output-locked":
            Console.Error.WriteLine("file.cs(1,2): error MSB3027: locked file");
            break;
    }
    return 17;
}
// Pipe EOF also ends its lifetime if the test host disappears without disposing the fixture.
using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
await pipe.ConnectAsync(deadline.Token);
using var reader = new StreamReader(pipe);
using var writer = new StreamWriter(pipe) { AutoFlush = true };
// Build-streamer modes exercise redirected pipes without a shell or gateway.
var mode = args.Length > 1 ? args[1] : string.Empty;
if (mode is not ("build-continuous" or "build-one-pipe"))
    await writer.WriteLineAsync($"ready:{Environment.ProcessId}");
if (mode == "build-root-exits-first")
{
    // The test owns the descendant control pipe. Root exit cannot close that pipe or
    // cooperatively release the inherited stdout/stderr handles under test.
    var info = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false };
    info.ArgumentList.Add(typeof(Program).Assembly.Location);
    info.ArgumentList.Add(args[0] + "-descendant");
    info.ArgumentList.Add("build-silent");
    using var child = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("Child failed to start.");
    await reader.ReadLineAsync(deadline.Token);
    return 0;
}
if (mode == "build-descendant")
{
    var info = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false };
    info.ArgumentList.Add(typeof(Program).Assembly.Location);
    info.ArgumentList.Add(args[0] + "-descendant");
    info.ArgumentList.Add("build-silent");
    using var descendantPipe = new NamedPipeServerStream(args[0] + "-descendant", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    using var child = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("Child failed to start.");
    await descendantPipe.WaitForConnectionAsync(deadline.Token);
    using var descendantReader = new StreamReader(descendantPipe);
    await descendantReader.ReadLineAsync(deadline.Token);
    await writer.WriteLineAsync($"child:{child.Id}");
    await reader.ReadLineAsync(deadline.Token);
    return 0;
}
if (mode == "build-one-pipe")
{
    Console.Out.Close();
    Console.Error.WriteLine("stderr remains open");
    await writer.WriteLineAsync($"ready:{Environment.ProcessId}");
}
if (mode == "build-continuous")
{
    await Console.Out.WriteLineAsync("continuous stdout started");
    await Console.Error.WriteLineAsync("continuous stderr started");
    await writer.WriteLineAsync($"ready:{Environment.ProcessId}");
    using var outputCancellation = new CancellationTokenSource();
    var output = Task.Run(async () =>
    {
        try
        {
            while (!outputCancellation.IsCancellationRequested)
            {
                await Console.Out.WriteLineAsync("continuous stdout");
                await Console.Error.WriteLineAsync("continuous stderr");
            }
        }
        catch (IOException) { }
    });
    await reader.ReadLineAsync(deadline.Token);
    outputCancellation.Cancel();
    await output;
    return 0;
}
// Once ready, only an explicit command or parent pipe EOF ends the child lifetime.
var command = await reader.ReadLineAsync();
return command is not null && command.StartsWith("exit:", StringComparison.Ordinal)
    ? int.Parse(command[5..], CultureInfo.InvariantCulture)
    : 0;
