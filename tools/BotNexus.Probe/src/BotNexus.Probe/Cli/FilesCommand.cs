namespace BotNexus.Probe.Cli;

public static class FilesCommand
{
    public static Task<int> RunAsync(CliOptions options, string[] args, CancellationToken cancellationToken)
    {
        _ = args;
        _ = cancellationToken;

        if (!Directory.Exists(options.LogsPath))
        {
            var payload = new { status = "empty", count = 0, items = Array.Empty<object>() };
            CliOutput.Write(options, payload, () => "No log files found.");
            return Task.FromResult(2);
        }

        var files = Directory.EnumerateFiles(options.LogsPath, "*.log*", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => new
            {
                name = file.Name,
                fullPath = file.FullName,
                size = file.Length,
                modified = new DateTimeOffset(file.LastWriteTimeUtc),
                from = new DateTimeOffset(file.CreationTimeUtc),
                to = new DateTimeOffset(file.LastWriteTimeUtc)
            })
            .ToList();

        var payloadResult = new
        {
            status = files.Count > 0 ? "ok" : "empty",
            count = files.Count,
            items = files
        };

        CliOutput.Write(options, payloadResult, () =>
        {
            if (files.Count == 0)
            {
                return "No log files found.";
            }

            var lines = files.Select(file =>
                $"{file.name}  size={file.size}  modified={file.modified:yyyy-MM-dd HH:mm:ss}");
            return $"📁 Log Files ({files.Count} results)\n━━━━━━━━━━━━━━━━━━━━\n{string.Join(Environment.NewLine, lines)}";
        });

        return Task.FromResult(files.Count > 0 ? 0 : 2);
    }
}
