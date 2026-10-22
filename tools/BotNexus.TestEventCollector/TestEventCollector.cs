using System.Xml;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollection;

namespace BotNexus.TestEventCollector;

/// <summary>Records test start/end events directly from VSTest's out-of-process data collection contract.</summary>
[DataCollectorFriendlyName("BotNexus Test Events")]
[DataCollectorTypeUri("datacollector://BotNexus/TestEvents/1")]
public sealed class TestEventCollector : DataCollector
{
    private EventJournal? journal;

    /// <summary>Opens a run-scoped journal and subscribes before any test starts.</summary>
    public override void Initialize(XmlElement? configurationElement, DataCollectionEvents events,
        DataCollectionSink dataSink, DataCollectionLogger logger, DataCollectionEnvironmentContext? environmentContext)
    {
        var directory = configurationElement?["Directory"]?.InnerText;
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Test event evidence directory is required.");
        Directory.CreateDirectory(directory);
        journal = new EventJournal(Path.Combine(directory, $"events-{Environment.ProcessId}-{Guid.NewGuid():N}.jsonl"));
        events.TestHostLaunched += (_, e) =>
        {
            using var host = System.Diagnostics.Process.GetProcessById(e.TestHostProcessId);
            journal.RecordHost(e.TestHostProcessId, host.StartTime.ToUniversalTime());
        };
        events.TestCaseStart += (_, e) => Record("TestCaseStart", e.TestElement);
        events.TestCaseEnd += (_, e) => Record("TestCaseEnd", e.TestElement);
        events.SessionEnd += (_, _) => journal.Dispose();
    }

    private void Record(string eventName, TestCase? test)
    {
        if (test is null) throw new InvalidOperationException("Test event identity is missing.");
        if (journal is null) throw new InvalidOperationException("Collector was not initialized.");
        journal.Record(eventName, test.Id, test.FullyQualifiedName, test.DisplayName, test.Source);
    }
}
