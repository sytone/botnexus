using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Xml;
using BotNexus.TestEventCollector;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollection;

namespace BotNexus.Architecture.Tests;

/// <summary>Candidate-source contracts run in remote CORE, independently of image helpers.</summary>
public sealed class RunnerDiagnosticArchitectureTests : ArchitectureTest
{
    [Theory]
    [InlineData("RunnerDiagnostic.Tests.ps1")]
    [InlineData("RunnerDiagnosticDeadline.Tests.ps1")]
    [InlineData("RunnerDiagnosticCleanup.Tests.ps1")]
    public async Task Diagnostic_CandidateSourceFixtures_PassWithoutGatewayOrTesthost(string script)
    {
        var start = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = Repository.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(Repository.Path("infra", "buildtest", "runner", "tests", script));
        using var process = Process.Start(start);
        process.ShouldNotBeNull("PowerShell is required; missing harness must fail closed.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        var output = await stdout + await stderr;
        process.ExitCode.ShouldBe(0, output);
        output.ShouldContain("failed=0");
    }

    [Fact]
    public void Collector_ActualObjectModelEvents_AreSubscribedAndDurablyRecorded()
    {
        var root = Path.Combine(Path.GetTempPath(), $"collector4777-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var collector = new TestEventCollector.TestEventCollector();
        try
        {
            var xml = new XmlDocument();
            var configuration = xml.CreateElement("Configuration");
            var directory = xml.CreateElement("Directory");
            directory.InnerText = root;
            configuration.AppendChild(directory);
            var events = new CollectorEvents();
            var test = new TestCase("Example.ActualEvent", new Uri("executor://example"), "Example.Tests.dll")
            { Id = Guid.NewGuid(), DisplayName = "Actual collector fixture" };
            var context = new DataCollectionContext(test);
            collector.Initialize(configuration, events, new Sink(), new Logger(), new DataCollectionEnvironmentContext(context));
            using var current = Process.GetCurrentProcess();
            events.Host(new TestHostLaunchedEventArgs(context, current.Id));
            events.Start(new TestCaseStartEventArgs(context, test));
            // Read before SessionEnd: no direct EventJournal call or source-string assertion.
            var path = Directory.GetFiles(root, "events-*.jsonl").ShouldHaveSingleItem();
            var first = ReadLiveJournal(path);
            first.Length.ShouldBe(2);
            using var host = JsonDocument.Parse(first[0]);
            host.RootElement.GetProperty("event").GetString().ShouldBe("TestHostLaunched");
            host.RootElement.GetProperty("testHostProcessId").GetInt32().ShouldBe(current.Id);
            host.RootElement.GetProperty("testHostStartUtc").GetString().ShouldBe(current.StartTime.ToUniversalTime().ToString("o"));
            var expectedStartToken = OperatingSystem.IsLinux()
                ? ReadLinuxStartToken(current.Id)
                : current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            host.RootElement.GetProperty("testHostStartToken").GetString().ShouldBe(expectedStartToken);
            using var begin = JsonDocument.Parse(first[1]);
            begin.RootElement.GetProperty("id").GetGuid().ShouldBe(test.Id);
            begin.RootElement.GetProperty("event").GetString().ShouldBe("TestCaseStart");
            begin.RootElement.GetProperty("source").GetString().ShouldBe(test.Source);
            events.End(new TestCaseEndEventArgs(context, test, TestOutcome.Passed));
            var lines = ReadLiveJournal(path);
            lines.Length.ShouldBe(3);
            using var end = JsonDocument.Parse(lines[2]);
            end.RootElement.GetProperty("event").GetString().ShouldBe("TestCaseEnd");
            end.RootElement.GetProperty("id").GetGuid().ShouldBe(test.Id);
            end.RootElement.GetProperty("sequence").GetInt64().ShouldBe(3);
            events.Finish(new SessionEndEventArgs(context));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string[] ReadLiveJournal(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string ReadLinuxStartToken(int processId)
    {
        var stat = File.ReadAllText($"/proc/{processId}/stat");
        return stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19];
    }

    private sealed class CollectorEvents : DataCollectionEvents
    {
        public override event EventHandler<TestHostLaunchedEventArgs>? TestHostLaunched;
        public override event EventHandler<SessionStartEventArgs>? SessionStart;
        public override event EventHandler<SessionEndEventArgs>? SessionEnd;
        public override event EventHandler<TestCaseStartEventArgs>? TestCaseStart;
        public override event EventHandler<TestCaseEndEventArgs>? TestCaseEnd;
        public void Host(TestHostLaunchedEventArgs e) => TestHostLaunched?.Invoke(this, e);
        public void Start(TestCaseStartEventArgs e) { SessionStart?.Invoke(this, new SessionStartEventArgs()); TestCaseStart?.Invoke(this, e); }
        public void End(TestCaseEndEventArgs e) => TestCaseEnd?.Invoke(this, e);
        public void Finish(SessionEndEventArgs e) => SessionEnd?.Invoke(this, e);
    }

    private sealed class Sink : DataCollectionSink
    {
        public override event AsyncCompletedEventHandler? SendFileCompleted;
        public override void SendFileAsync(FileTransferInformation fileTransferInformation) => SendFileCompleted?.Invoke(this, new AsyncCompletedEventArgs(null, false, null));
    }

    private sealed class Logger : DataCollectionLogger
    {
        public override void LogError(DataCollectionContext context, string text) => throw new InvalidOperationException(text);
        public override void LogError(DataCollectionContext context, string text, Exception exception) => throw new InvalidOperationException(text, exception);
        public override void LogWarning(DataCollectionContext context, string text) => throw new InvalidOperationException(text);
    }
}
