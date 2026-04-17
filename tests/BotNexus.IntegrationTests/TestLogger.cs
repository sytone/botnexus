namespace BotNexus.IntegrationTests;

/// <summary>
/// Dual-output logger: writes to console + a per-scenario log file.
/// </summary>
public sealed class TestLogger : IDisposable
{
    private readonly string _logDir;
    private readonly StreamWriter _summaryWriter;
    private StreamWriter? _scenarioWriter;
    private readonly object _lock = new();

    public TestLogger(string logDir)
    {
        _logDir = logDir;
        Directory.CreateDirectory(logDir);

        var summaryPath = Path.Combine(logDir, "summary.log");
        _summaryWriter = new StreamWriter(summaryPath, append: false) { AutoFlush = true };
        Write($"=== BotNexus Integration Test Log — {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC ===");
    }

    public string LogDir => _logDir;

    /// <summary>
    /// Start a new per-scenario log file.
    /// </summary>
    public void StartScenario(string scenarioName)
    {
        lock (_lock)
        {
            _scenarioWriter?.Dispose();
            var safeName = string.Join("_", scenarioName.Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(_logDir, $"{safeName}.log");
            _scenarioWriter = new StreamWriter(path, append: false) { AutoFlush = true };
            _scenarioWriter.WriteLine($"=== {scenarioName} — {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC ===");
        }
    }

    public void Write(string message)
    {
        var line = $"[{DateTimeOffset.UtcNow:HH:mm:ss.fff}] {message}";
        lock (_lock)
        {
            Console.WriteLine($"    {line}");
            _summaryWriter.WriteLine(line);
            _scenarioWriter?.WriteLine(line);
        }
    }

    public void WriteHeader(string scenarioName)
    {
        var separator = new string('─', 60);
        lock (_lock)
        {
            var header = $"\n{separator}\n▶ {scenarioName}\n{separator}";
            Console.Write(header);
            _summaryWriter.Write(header);
        }
        StartScenario(scenarioName);
    }

    public void WriteResult(string scenarioName, bool passed, string? error = null)
    {
        lock (_lock)
        {
            if (passed)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(" PASS");
                Console.ResetColor();
                _summaryWriter.WriteLine($" PASS");
                _scenarioWriter?.WriteLine($"RESULT: PASS");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(" FAIL");
                Console.ResetColor();
                _summaryWriter.WriteLine($" FAIL");
                _scenarioWriter?.WriteLine($"RESULT: FAIL");
                if (error is not null)
                {
                    Console.WriteLine($"    ❌ {error}");
                    _summaryWriter.WriteLine($"    ❌ {error}");
                    _scenarioWriter?.WriteLine($"ERROR: {error}");
                }
            }
        }
    }

    public void Dispose()
    {
        _scenarioWriter?.Dispose();
        _summaryWriter.Dispose();
    }
}
